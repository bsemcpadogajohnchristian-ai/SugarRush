// SmokeGrenade.cs — Sugar Rush
// A physics grenade that arcs through the air and deploys a SmokeCloud on
// impact (or after its hard-cap fuse timer expires).
//
// ── HOW IT WORKS ──────────────────────────────────────────────────────────
//   1. CollectorController.ThrowSmokeGrenadeRpc() instantiates this prefab
//      on the server, calls Initialize(), then broadcasts the throw velocity
//      to all clients so each machine can run physics locally.
//   2. On first collision after armDelay seconds the post-bounce fuse starts.
//      If the grenade never hits anything it deploys after maxLifetime.
//   3. Deploy() instantiates a SmokeCloud prefab, spawns it over the network,
//      calls SmokeCloud.InitializeCloud(), then despawns the grenade.
//
// ── PREFAB REQUIREMENTS ───────────────────────────────────────────────────
//   • Rigidbody  (interpolate = On,  collision detection = Continuous)
//   • NetworkObject
//   • A small SphereCollider  (radius ≈ 0.12) for bouncing
//   • Assign smokePrefab (your SmokeCloud prefab) in the Inspector
//   • Optional: AudioSource + bounce / deploy AudioClips

using System.Collections;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class SmokeGrenade : NetworkBehaviour
{
    // ── Inspector ────────────────────────────────────────────────────────────

    [Header("Fuse")]
    [Tooltip("Seconds after the FIRST bounce before smoke deploys.")]
    public float fuseAfterBounce = 0.35f;

    [Tooltip("Hard-cap lifetime. Smoke deploys even if grenade never bounces.")]
    public float maxLifetime = 4.5f;

    [Tooltip("Seconds post-spawn during which collisions are ignored (prevents " +
             "instantly deploying on the thrower's own collider).")]
    public float armDelay = 0.20f;

    [Header("References")]
    [Tooltip("Drag your SmokeCloud prefab here.")]
    public GameObject smokePrefab;

    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip   bounceSound;
    public AudioClip   deploySound;

    // ── Runtime ──────────────────────────────────────────────────────────────

    private Rigidbody _rb;
    private TeamID    _ownerTeam;
    private bool      _armed;
    private bool      _deployed;

    private void Awake() => _rb = GetComponent<Rigidbody>();

    // ─────────────────────────────────────────────────────────────────────────
    // Public API — call server-side immediately after Spawn()
    // ─────────────────────────────────────────────────────────────────────────

    /// <param name="velocity">World-space throw velocity (direction × force + upward arc).</param>
    /// <param name="team">Team of the collector who threw the grenade.</param>
    public void Initialize(Vector3 velocity, TeamID team)
    {
        _ownerTeam = team;

        if (_rb != null)
        {
            _rb.isKinematic     = false;
            _rb.linearVelocity  = velocity;
            // Tumble: looks natural, purely cosmetic
            _rb.angularVelocity = Random.insideUnitSphere * 9f;
        }

        // Clients run their own physics simulation from the same starting
        // conditions — close enough for a short-lived grenade.
        SyncThrowClientRpc(velocity);

        Invoke(nameof(Arm),    armDelay);
        Invoke(nameof(Deploy), maxLifetime); // safety timeout
    }

    // ── Client-side kick-start ────────────────────────────────────────────────

    [Rpc(SendTo.ClientsAndHost)]
    private void SyncThrowClientRpc(Vector3 velocity)
    {
        if (IsServer) return; // server already set this in Initialize()

        if (_rb != null)
        {
            _rb.isKinematic    = false;
            _rb.linearVelocity = velocity;
        }
    }

    // ── Arm ───────────────────────────────────────────────────────────────────

    private void Arm() => _armed = true;

    // ── Collision ─────────────────────────────────────────────────────────────

    private void OnCollisionEnter(Collision col)
    {
        PlayBounceSoundRpc();

        if (!IsServer || !_armed || _deployed) return;

        // Cancel the hard-cap timeout and start the short post-bounce fuse.
        CancelInvoke(nameof(Deploy));
        Invoke(nameof(Deploy), fuseAfterBounce);
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void PlayBounceSoundRpc()
    {
        if (bounceSound != null)
            audioSource?.PlayOneShot(bounceSound, 0.55f);
    }

    // ── Deploy ────────────────────────────────────────────────────────────────

    private void Deploy()
    {
        if (!IsServer || _deployed) return;
        _deployed = true;
        CancelInvoke(nameof(Deploy));

        PlayDeploySoundRpc();

        if (smokePrefab != null)
        {
            GameObject cloud = Instantiate(smokePrefab, transform.position, Quaternion.identity);
            NetworkObject no  = cloud.GetComponent<NetworkObject>();
            if (no != null)
            {
                no.Spawn(true);
                cloud.GetComponent<SmokeCloud>()?.InitializeCloud(_ownerTeam);
            }
            else
            {
                Debug.LogError("[SmokeGrenade] smokePrefab is missing a NetworkObject component!");
                Destroy(cloud);
            }
        }
        else
        {
            Debug.LogWarning("[SmokeGrenade] smokePrefab is not assigned — no smoke will appear.");
        }

        GetComponent<NetworkObject>()?.Despawn(true);
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void PlayDeploySoundRpc()
    {
        if (deploySound != null)
            audioSource?.PlayOneShot(deploySound);
    }

    // ── Gizmos ───────────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.white;
        Gizmos.DrawWireSphere(transform.position, 0.12f);
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(transform.position, transform.forward * 0.5f);
    }
}
