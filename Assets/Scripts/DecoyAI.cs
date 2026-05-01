// DecoyAI.cs — Sugar Rush
//
// ── SMOKE PUFF ON SPAWN ───────────────────────────────────────────────────────
//   A visual smoke puff now plays at the decoy's position the moment it
//   activates (i.e. when CollectorController deploys it).
//
//   WHAT CHANGED:
//     • Added  public GameObject smokePuffFX  (assign your smoke puff prefab
//       in the Decoy prefab Inspector — any particle-system prefab works).
//     • Added  PlaySpawnFXRpc()  — a ClientsAndHost RPC called from Activate().
//       It uses FXPool.Instance.Spawn() when available, or falls back to a
//       plain Instantiate + auto-destroy, so it works even without FXPool.
//     • Activate() now calls PlaySpawnFXRpc() once, immediately when the decoy
//       starts moving. Nothing else changed.
//
//   SETUP GUIDE → see the step-by-step tutorial comment at the bottom of this
//   file, or follow the Unity Editor steps in the accompanying tutorial document.

using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class DecoyAI : NetworkBehaviour
{
    [Header("Movement")]
    public float lifetime = 5f;
    public float gravity  = -19.62f;

    [Header("Durability")]
    public int maxHits = 10;

    // ── NEW: smoke puff spawned when the decoy activates ─────────────────────
    [Header("Spawn FX")]
    [Tooltip("Particle-system prefab to spawn at the decoy's feet when it appears. " +
             "Any smoke / puff VFX prefab works. Leave empty to skip.")]
    public GameObject smokePuffFX;

    // Synced so ALL clients know the team and can show the right model colour.
    // ownerTeam is kept for server-side friendly-fire checks (TakeHitRpc).
    public NetworkVariable<TeamID> syncedTeam = new(TeamID.TeamA,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    [HideInInspector] public TeamID ownerTeam; // server-only helper, set from syncedTeam on spawn

    [Header("Visuals")]
    [Tooltip("Root of the collector body mesh child. Assign in the Decoy prefab.")]
    public GameObject collectorBody;

    // Animator lives on the collector body child (same controller as the real collector).
    private Animator _animator;

    // ── Animator hashes ──────────────────────────────────────────────────────
    private static readonly int H_Speed  = Animator.StringToHash("Speed");
    private static readonly int H_TeamID = Animator.StringToHash("TeamID");

    private CharacterController _cc;
    private NetworkTransform    _nt;

    private Vector3 _dir;
    private float   _speed;
    private float   _yVelocity;
    private int     _hits;
    private bool    _ready;

    private void Awake()
    {
        _cc = GetComponent<CharacterController>();
        _nt = GetComponent<NetworkTransform>();

        // Grab the Animator from the collector body child if assigned.
        if (collectorBody != null)
            _animator = collectorBody.GetComponentInChildren<Animator>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
        {
            // Clients let NetworkTransform drive position.
            if (_nt != null) _nt.enabled = false;
        }

        // Mirror team into the plain field so server-side TakeHitRpc can read it
        // without going through the NetworkVariable every frame.
        ownerTeam = syncedTeam.Value;

        // Apply visuals immediately and whenever the value changes.
        syncedTeam.OnValueChanged += (_, next) => ApplyTeamVisuals(next);
        ApplyTeamVisuals(syncedTeam.Value);

        if (IsServer && _dir != Vector3.zero)
            Activate();
    }

    // ── Called by CollectorController.SpawnDecoyRpc AFTER Spawn() ───────────
    public void InitializeMovement(Vector3 direction, float speed)
    {
        _dir   = direction.normalized;
        _speed = Mathf.Max(speed, 0f);

        if (IsServer)
        {
            Activate();
            InitStateClientRpc(transform.position, _dir, _speed);
        }
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void InitStateClientRpc(Vector3 spawnPos, Vector3 dir, float speed)
    {
        if (IsServer) return;

        _dir   = dir.normalized;
        _speed = speed;

        _cc.enabled = false;
        transform.position = spawnPos;
        _cc.enabled = true;

        _yVelocity = 0f;
        _ready     = true;

        // Start run animation on clients too.
        SetRunning(true);
    }

    private void Activate()
    {
        if (_ready) return;
        _ready     = true;
        _yVelocity = 0f;
        SetRunning(true);

        // ── NEW: broadcast smoke puff to all clients ─────────────────────────
        PlaySpawnFXRpc(transform.position);

        StartCoroutine(LifetimeRoutine());
    }

    private void Update()
    {
        if (!_ready) return;

        // Always force run — Speed 2f = run in the collector blend tree (0=idle, 1=walk, 2=run).
        if (_animator != null)
            _animator.SetFloat(H_Speed, 2f);

        Vector3 move = _dir * _speed * Time.deltaTime;

        if (_cc.isGrounded && _yVelocity < 0f)
            _yVelocity = -2f;

        _yVelocity += gravity * Time.deltaTime;
        move.y      = _yVelocity * Time.deltaTime;

        _cc.Move(move);
    }

    // ── Visuals ──────────────────────────────────────────────────────────────

    private void ApplyTeamVisuals(TeamID team)
    {
        ownerTeam = team; // keep the plain field in sync on all clients

        if (_animator == null) return;
        _animator.SetInteger(H_TeamID, (int)team);
    }

    // Drives the same Speed float your CollectorAnimator uses so the run clip plays.
    private void SetRunning(bool running)
    {
        if (_animator == null) return;
        _animator.SetFloat(H_Speed, running ? 1f : 0f);
    }

    // ── NEW: Smoke puff RPC ──────────────────────────────────────────────────

    /// <summary>
    /// Plays the smoke puff VFX at <paramref name="pos"/> on every client and the host.
    /// Uses FXPool when available; falls back to a plain Instantiate + auto-destroy.
    /// </summary>
    [Rpc(SendTo.ClientsAndHost)]
    private void PlaySpawnFXRpc(Vector3 pos)
    {
        if (smokePuffFX == null) return;

        if (FXPool.Instance != null)
        {
            FXPool.Instance.Spawn(smokePuffFX, pos, Quaternion.identity);
        }
        else
        {
            GameObject fx = Instantiate(smokePuffFX, pos, Quaternion.identity);
            ParticleSystem ps = fx.GetComponent<ParticleSystem>();
            float lifetime = ps != null
                ? ps.main.duration + ps.main.startLifetime.constantMax
                : 2f;
            Destroy(fx, lifetime);
        }
    }

    // ── Damage ───────────────────────────────────────────────────────────────

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TakeHitRpc(TeamID attackerTeam)
    {
        if (attackerTeam == ownerTeam) return; // no friendly fire

        _hits++;
        if (_hits >= maxHits) Despawn();
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────

    private IEnumerator LifetimeRoutine()
    {
        yield return new WaitForSeconds(lifetime);
        Despawn();
    }

    private void Despawn()
    {
        if (!IsServer) return;
        StopAllCoroutines();
        GetComponent<NetworkObject>()?.Despawn(true);
    }

    // ── Gizmos ───────────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        if (_cc == null) return;
        float bottomOffset = _cc.center.y - _cc.height * 0.5f + _cc.skinWidth;
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position + Vector3.up * bottomOffset, 0.1f);
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(transform.position, _dir * 2f);
    }
}

/*
 * ═══════════════════════════════════════════════════════════════════════════
 *  STEP-BY-STEP SETUP GUIDE — Decoy Smoke Puff
 * ═══════════════════════════════════════════════════════════════════════════
 *
 *  OVERVIEW
 *  --------
 *  When the Collector deploys a decoy, a small smoke puff plays at the
 *  decoy's feet on EVERY client (server, host, and all remote clients).
 *  The effect is purely visual — it does not affect gameplay.
 *
 * ───────────────────────────────────────────────────────────────────────────
 *  STEP 1 — Replace DecoyAI.cs
 * ───────────────────────────────────────────────────────────────────────────
 *  Replace your existing Assets/Scripts/DecoyAI.cs with this file.
 *  Save and let Unity recompile.
 *
 * ───────────────────────────────────────────────────────────────────────────
 *  STEP 2 — Create the Smoke Puff particle prefab
 * ───────────────────────────────────────────────────────────────────────────
 *  Option A — Use your existing SmokeGrenade VFX (easiest):
 *    a. In the Project window, locate the smoke particle prefab you already
 *       use for SmokeGrenade / SmokeCloud visuals.
 *    b. Duplicate it (Ctrl/Cmd + D) and rename it "FX_DecoySmokePuff".
 *    c. Open the duplicate. In the ParticleSystem component:
 *         • Duration          → 0.6
 *         • Start Lifetime    → 0.8
 *         • Start Size        → 1.5  (smaller than a full smoke cloud)
 *         • Max Particles     → 20
 *         • Emission > Rate over Time → 0
 *         • Emission > Bursts → add one burst: Time=0, Count=15
 *         • Shape             → Sphere, Radius=0.3
 *         • Color over Lifetime → fade from 60% alpha → 0% alpha
 *    d. Drag the prefab into Assets/Prefabs/FX/ (or wherever you keep FX).
 *
 *  Option B — Start from scratch:
 *    a. In the Hierarchy right-click → Effects → Particle System.
 *    b. Configure the ParticleSystem as described above.
 *    c. Drag it from the Hierarchy into the Project window to create a prefab.
 *    d. Name it "FX_DecoySmokePuff".
 *    e. Delete the Hierarchy instance; keep only the prefab.
 *
 *  IMPORTANT: The prefab must NOT have a NetworkObject component — it is
 *  a local VFX, not a networked object. FXPool manages its lifetime.
 *
 * ───────────────────────────────────────────────────────────────────────────
 *  STEP 3 — Assign the prefab to the Decoy prefab
 * ───────────────────────────────────────────────────────────────────────────
 *  a. In the Project window, open your Decoy prefab
 *     (the same one CollectorController.decoyPrefab points to).
 *  b. Select the root GameObject of the prefab.
 *  c. In the Inspector, find the "DecoyAI" component.
 *  d. You will see a new field called "Smoke Puff FX".
 *  e. Drag "FX_DecoySmokePuff" from the Project window into that slot.
 *  f. Click "Apply" / save the prefab (Ctrl/Cmd + S).
 *
 * ───────────────────────────────────────────────────────────────────────────
 *  STEP 4 — Verify FXPool is in your scene (optional but recommended)
 * ───────────────────────────────────────────────────────────────────────────
 *  FXPool recycles particle objects so you don't allocate a new GameObject
 *  every time a puff plays. If you already have an FXPool singleton in your
 *  scene or a DontDestroyOnLoad object, you're done.
 *
 *  If not:
 *    a. Create an empty GameObject in your GameScene, name it "FXPool".
 *    b. Add the FXPool component to it.
 *    c. (Optional) Add a DontDestroyOnLoad wrapper so it survives scene loads.
 *       Your existing DontDestroyLoader script can do this.
 *
 *  The code falls back to a plain Instantiate + Destroy if FXPool is absent,
 *  so this step is optional but improves performance.
 *
 * ───────────────────────────────────────────────────────────────────────────
 *  STEP 5 — Test in Play Mode
 * ───────────────────────────────────────────────────────────────────────────
 *  a. Start a multiplayer session (Host + Client, or ParrelSync).
 *  b. Play as the Collector role.
 *  c. Press Q (or your decoy key) to deploy a decoy.
 *  d. You should see a small smoke puff appear at the decoy's feet the
 *     instant it starts running — visible on BOTH the host and client windows.
 *
 * ───────────────────────────────────────────────────────────────────────────
 *  TROUBLESHOOTING
 * ───────────────────────────────────────────────────────────────────────────
 *  • No puff visible at all
 *      → Check that "Smoke Puff FX" is assigned in the Decoy prefab Inspector.
 *      → Make sure the ParticleSystem's "Play On Awake" is ON, or that your
 *        burst is set up (Step 2 Option B).
 *
 *  • Puff visible only on host, not on clients
 *      → Verify you are using THIS updated DecoyAI.cs (check that the
 *        PlaySpawnFXRpc method exists in the compiled MonoScript).
 *
 *  • Puff appears but is too large / wrong colour
 *      → Tweak Start Size, Color over Lifetime in the FX_DecoySmokePuff
 *        prefab's ParticleSystem component (no code changes needed).
 *
 *  • Compile error: "FXPool does not exist"
 *      → Make sure FXPool.cs is in your project (it was in your original
 *        script set — it should already be there).
 * ═══════════════════════════════════════════════════════════════════════════
 */