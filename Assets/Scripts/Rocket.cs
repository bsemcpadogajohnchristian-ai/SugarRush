// Rocket.cs — Sugar Rush
//
// ── KILL FEED CHANGE ─────────────────────────────────────────────────────────
//   _shooterClientId field added — stored via Initialize().
//   SpawnRocketServerRpc in ShooterController must pass OwnerClientId as an
//   extra argument (see tutorial Step 4).
//   Splash damage now calls TakeDamageFrom() instead of TakeDamage()
//   so the kill feed shows "Rocket" as the weapon and the correct killer.

using Unity.Netcode;
using UnityEngine;

public class Rocket : NetworkBehaviour
{
    private const float ARM_DELAY = 0.15f;

    private float      _speed;
    private float      _splashRadius;
    private float      _splashDamage;
    private float      _directDamage;
    private LayerMask  _mask;
    private GameObject _impactFX;
    private TeamID     _firingTeam;
    private ulong      _shooterClientId;   // ← NEW: for kill attribution
    private bool       _exploded;
    private bool       _armed;
    private Rigidbody  _rb;

    private void Awake() => _rb = GetComponent<Rigidbody>();

    /// <param name="shooterClientId">OwnerClientId of the player who fired this rocket.</param>
    public void Initialize(float speed, float splashRadius, float splashDamage,
        float directDamage, LayerMask mask, GameObject fx, TeamID team,
        ulong shooterClientId = 0)
    {
        _speed            = speed;
        _splashRadius     = splashRadius;
        _splashDamage     = splashDamage;
        _directDamage     = directDamage;
        _mask             = mask;
        _impactFX         = fx;
        _firingTeam       = team;
        _shooterClientId  = shooterClientId;   // ← NEW

        if (_rb != null) _rb.linearVelocity = transform.forward * _speed;

        Invoke(nameof(Arm), ARM_DELAY);
        Invoke(nameof(TimeoutExplode), 10f);
    }

    private void Arm() => _armed = true;

    private void OnCollisionEnter(Collision col)
    {
        if (!IsServer || _exploded || !_armed) return;
        Explode(col.contacts[0].point);
    }

    private void TimeoutExplode()
    {
        if (!IsServer) return;
        Explode(transform.position);
    }

    private void Explode(Vector3 point)
    {
        _exploded = true;
        ShowExplosionRpc(point);

        Collider[] cols = Physics.OverlapSphere(point, _splashRadius, _mask);
        foreach (Collider c in cols)
        {
            PlayerStats ps = c.GetComponentInParent<PlayerStats>();
            if (ps == null || ps.team.Value == _firingTeam) continue;
            float falloff = 1f - Mathf.Clamp01(Vector3.Distance(point, c.transform.position) / _splashRadius);
            // ── KILL FEED CHANGE: use TakeDamageFrom so the kill is attributed ──
            ps.TakeDamageFrom(_splashDamage * falloff, _shooterClientId, "Rocket");
        }

        GetComponent<NetworkObject>()?.Despawn(true);
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void ShowExplosionRpc(Vector3 point)
    {
        if (_impactFX == null) return;
        if (FXPool.Instance != null)
            FXPool.Instance.Spawn(_impactFX, point, Quaternion.identity);
        else
        {
            GameObject fx = Instantiate(_impactFX, point, Quaternion.identity);
            ParticleSystem ps = fx.GetComponent<ParticleSystem>();
            Destroy(fx, ps != null ? ps.main.duration + ps.main.startLifetime.constantMax : 2f);
        }
    }
}
