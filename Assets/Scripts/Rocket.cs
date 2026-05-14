// Rocket.cs — Sugar Rush
//
// ── BUG FIXES ─────────────────────────────────────────────────────────────────
//
//   FIX 1 — NOT VISIBLE TO CLIENTS
//     Root cause: Rigidbody.linearVelocity was set only on the server.
//     Without a NetworkTransform on the prefab, clients never received the
//     position updates — the rocket appeared frozen at the spawn point.
//     Fix: Movement is now driven by transform.position += _velocity * dt in
//     FixedUpdate on BOTH the server and clients. The server broadcasts the
//     initial velocity via LaunchClientVisualRpc immediately after spawning, so
//     every client independently mirrors the trajectory for visual purposes.
//     No NetworkTransform prefab component is required.
//
//   FIX 2 — NOT DAMAGING THE OTHER TEAM (two stacked causes)
//     a) explosionMask defaulted to 0 (Nothing):
//        Unity's LayerMask fields default to value 0 when left unassigned in
//        the Inspector. Physics.OverlapSphere with mask=0 returns zero results,
//        so no players were ever found in the blast.
//        Fix: If mask.value == 0, fall back to Physics.DefaultRaycastLayers.
//     b) OnCollisionEnter never fires against CharacterControllers:
//        Unity's CharacterController does not generate Rigidbody collision
//        events on incoming projectiles (Unity documentation limitation).
//        Rockets flew straight through players without Explode() ever running.
//        Fix: Replaced OnCollisionEnter entirely with a Physics.SphereCast
//        sweep inside FixedUpdate (server-only). SphereCast reliably detects
//        both CharacterControllers and static geometry every physics tick,
//        with no tunneling on fast-moving projectiles.
//
//   FIX 3 — DIRECT DAMAGE NEVER APPLIED
//     _directDamage was stored in Initialize() but Explode() only used splash
//     damage, so a point-blank hit dealt the same as a near-miss (80 instead
//     of 120). The player struck directly is now dealt directDamage first and
//     is then excluded from the splash pass to avoid double-counting.
//
// ── PREFAB NOTES ──────────────────────────────────────────────────────────────
//   • The Rigidbody on the rocket prefab is now forced kinematic at runtime.
//     You may leave it on the prefab or remove it — either works.
//   • You do NOT need to add a NetworkTransform component; the RPC approach
//     handles visual sync without it.
//   • Assign explosionMask in BazookaWeapon's Inspector (Player + Ground
//     layers). Even if left unset the fallback keeps things working, but an
//     explicit mask gives you finer control over what rockets can hit.

using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class Rocket : NetworkBehaviour
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const float ARM_DELAY    = 0.15f;  // seconds before rocket can detonate
    private const float SWEEP_RADIUS = 0.22f;  // SphereCast radius in metres
    private const float TIMEOUT      = 10f;    // auto-detonate if nothing is hit

    // ── Server-authoritative data ─────────────────────────────────────────────
    private float      _speed;
    private float      _splashRadius;
    private float      _splashDamage;
    private float      _directDamage;
    private int        _mask;              // resolved LayerMask value
    private GameObject _impactFX;
    private TeamID     _firingTeam;
    private ulong      _shooterClientId;
    private bool       _exploded;
    private bool       _armed;

    // ── Shared movement state ─────────────────────────────────────────────────
    // Set on server in Initialize(); mirrored on clients via LaunchClientVisualRpc.
    private Vector3 _velocity;
    private bool    _launched;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Force kinematic — position is driven manually via transform.position.
        // OnCollisionEnter is no longer used (see FIX 2b above).
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity  = false;
        }
    }

    // ── Initialize ── called on the SERVER by ShooterController after Spawn() ──

    /// <param name="shooterClientId">OwnerClientId of the firing player (for kill feed).</param>
    public void Initialize(float speed, float splashRadius, float splashDamage,
        float directDamage, LayerMask mask, GameObject fx, TeamID team,
        ulong shooterClientId = 0)
    {
        _speed           = speed;
        _splashRadius    = splashRadius;
        _splashDamage    = splashDamage;
        _directDamage    = directDamage;
        _impactFX        = fx;
        _firingTeam      = team;
        _shooterClientId = shooterClientId;

        // FIX 2a — if explosionMask was left at Nothing (0), hit every layer.
        _mask = (mask.value == 0) ? Physics.DefaultRaycastLayers : mask.value;

        _velocity = transform.forward * _speed;
        _launched = true;

        Invoke(nameof(Arm),            ARM_DELAY);
        Invoke(nameof(TimeoutExplode), TIMEOUT);

        // FIX 1 — broadcast velocity so clients can mirror movement visually.
        // The server already has _velocity from above and doesn't need the RPC.
        LaunchClientVisualRpc(_velocity);
    }

    private void Arm() => _armed = true;

    // ── Movement & collision ──────────────────────────────────────────────────

    private void FixedUpdate()
    {
        if (!_launched) return;

        if (IsServer)
        {
            if (_exploded) return;

            Vector3 move = _velocity * Time.fixedDeltaTime;

            // FIX 2b — SphereCast sweeps the full step each physics tick.
            // Reliably detects CharacterControllers and static geometry;
            // OnCollisionEnter could not be used because CharacterControllers
            // never fire that callback on incoming Rigidbodies.
            if (_armed && Physics.SphereCast(
                    transform.position,
                    SWEEP_RADIUS,
                    _velocity.normalized,
                    out RaycastHit hit,
                    move.magnitude + SWEEP_RADIUS,
                    _mask,
                    QueryTriggerInteraction.Ignore))
            {
                Explode(hit.point, hit.collider);
            }
            else
            {
                transform.position += move;
            }
        }
        else
        {
            // Clients: visual mirror only — damage is handled server-side.
            transform.position += _velocity * Time.fixedDeltaTime;
        }
    }

    // ── Timeout ───────────────────────────────────────────────────────────────

    private void TimeoutExplode()
    {
        if (!IsServer || _exploded) return;
        Explode(transform.position, null);
    }

    // ── Explosion ─────────────────────────────────────────────────────────────

    private void Explode(Vector3 point, Collider directHitCollider)
    {
        if (_exploded) return;
        _exploded = true;
        CancelInvoke(); // cancel pending Arm / TimeoutExplode

        ShowExplosionRpc(point);

        // Track already-damaged players to prevent double-counting.
        var damaged = new HashSet<PlayerStats>();

        // FIX 3 — apply directDamage to the collider the SphereCast contacted.
        // Previously _directDamage (120) was stored but Explode only used
        // _splashDamage (80), so every hit was under-powered.
        if (directHitCollider != null)
        {
            var ps = directHitCollider.GetComponentInParent<PlayerStats>();
            if (ps != null && ps.team.Value != _firingTeam && !ps.IsDead())
            {
                ps.TakeDamageFrom(_directDamage, _shooterClientId, "Rocket");
                damaged.Add(ps);
            }
        }

        // Splash damage with linear distance falloff.
        Collider[] cols = Physics.OverlapSphere(
            point, _splashRadius, _mask, QueryTriggerInteraction.Ignore);

        foreach (Collider c in cols)
        {
            var ps = c.GetComponentInParent<PlayerStats>();
            if (ps == null || damaged.Contains(ps)) continue;
            if (ps.team.Value == _firingTeam || ps.IsDead()) continue;

            float falloff = 1f - Mathf.Clamp01(
                Vector3.Distance(point, c.transform.position) / _splashRadius);
            ps.TakeDamageFrom(_splashDamage * falloff, _shooterClientId, "Rocket");
            damaged.Add(ps);
        }

        GetComponent<NetworkObject>()?.Despawn(true);
    }

    // ── RPCs ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends the launch velocity to every non-server client so they can mirror
    /// the rocket's path locally for visual fidelity. The server already has
    /// _velocity set from Initialize() and handles authoritative movement itself.
    /// </summary>
    [Rpc(SendTo.NotServer)]
    private void LaunchClientVisualRpc(Vector3 velocity)
    {
        _velocity = velocity;
        _launched = true;
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void ShowExplosionRpc(Vector3 point)
    {
        if (_impactFX == null) return;

        if (FXPool.Instance != null)
        {
            FXPool.Instance.Spawn(_impactFX, point, Quaternion.identity);
        }
        else
        {
            GameObject fx = Instantiate(_impactFX, point, Quaternion.identity);
            var ps = fx.GetComponent<ParticleSystem>();
            Destroy(fx, ps != null
                ? ps.main.duration + ps.main.startLifetime.constantMax
                : 2f);
        }
    }
}