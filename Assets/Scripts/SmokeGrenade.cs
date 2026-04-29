// SmokeGrenade.cs — Sugar Rush
//
// ── WHAT CHANGED (Physics Sync Fix) ──────────────────────────────────────────
//
//   ROOT CAUSE OF THE BUG:
//     The grenade had no NetworkTransform, so its position was NEVER replicated
//     after the initial spawn. Every machine ran its own independent Rigidbody
//     simulation. When the server called Despawn(), clients saw the grenade vanish
//     from wherever THEIR local simulation had placed it — which could be anywhere.
//     Additionally, if the Rigidbody was non-kinematic in the prefab, it fell with
//     gravity on the client between spawn and SyncThrowClientRpc arriving, sometimes
//     drifting back into the thrower's capsule before IgnoreThrowerCollisions ran.
//
//   THE FIX — Two components added to the SmokeGrenade prefab:
//     1. NetworkTransform  — replicates the server's authoritative position to all
//                            clients every network tick (with interpolation for smooth visuals).
//     2. NetworkRigidbody  — automatically sets the Rigidbody to kinematic on all
//                            non-owner (non-server) clients, preventing them from running
//                            their own independent physics simulation.
//
//   With these two components, the architecture is:
//     SERVER  → runs all Rigidbody physics, owns the simulation.
//     CLIENTS → Rigidbody is kinematic (no local physics), position comes from
//               NetworkTransform interpolation. They always see the same trajectory
//               as the server, so the grenade never "disappears" from the wrong spot.
//
//   CODE CHANGES:
//     • Added [RequireComponent] for NetworkTransform and NetworkRigidbody.
//     • Added OnNetworkSpawn() to explicitly disable client-side physics as a
//       safety net (in case NetworkRigidbody is not yet on the prefab).
//     • Removed SyncThrowClientRpc entirely — it is no longer needed.
//       NetworkTransform handles all position sync. Clients no longer need to
//       receive a velocity value because they don't simulate physics.
//     • IgnoreThrowerCollisions() is now server-only (no point running it on
//       kinematic-Rigidbody clients).
//     • Initialize() no longer calls SyncThrowClientRpc.
//     • OnCollisionEnter() has an explicit IsServer guard at the top so it is
//       completely inert on clients (belt-and-suspenders alongside the kinematic flag).
//
//   PREFAB SETUP REQUIRED — see Step-by-Step Tutorial in the project docs.
//     1. Add NetworkTransform component → set Interpolate = true.
//     2. Add NetworkRigidbody component (leave defaults).
//     3. On the Rigidbody component → set Is Kinematic = TRUE.
//        (Initialize() sets it to false server-side; clients stay kinematic via NetworkRigidbody.)
//
// ── WHAT CHANGED (Throw Shove Fix) ───────────────────────────────────────────
//
//   ROOT CAUSE — Character moves when grenade is thrown:
//     In Unity's physics engine, a KINEMATIC Rigidbody that moves into a
//     CharacterController WILL push it — this is documented Unity behaviour
//     ("Kinematic Rigidbodies affect CharacterControllers, not other Rigidbodies").
//     A DYNAMIC Rigidbody does NOT push a CC; only kinematic ones do.
//
//     On non-server clients, NetworkRigidbody makes the grenade's Rigidbody
//     kinematic. When the grenade spawns and NetworkTransform replicates its
//     initial position, the kinematic Rigidbody's Collider can briefly overlap
//     the thrower's CharacterController on the client, shoving them sideways.
//
//     IgnoreThrowerCollisions() does NOT help here: CharacterController does not
//     inherit from Collider in Unity's type system, so it is never returned by
//     GetComponentsInChildren<Collider>() and therefore never passed to
//     Physics.IgnoreCollision(). The CC's internal physics capsule stays visible
//     to the kinematic grenade.
//
//   THE FIX:
//     Cache the grenade's Collider in Awake(). In OnNetworkSpawn(), on non-server
//     clients only, immediately disable the Collider and schedule ClientArmCollider()
//     after armDelay seconds to re-enable it.
//
//     By that point the server physics (replicated by NetworkTransform) has moved
//     the grenade well clear of the thrower — at 14 m/s forward the grenade covers
//     ~2.8 m in 0.2 s, safely past any reasonable character capsule radius.
//
//     On the server the grenade is DYNAMIC (isKinematic = false after Initialize()).
//     Dynamic Rigidbodies do not push CCs, so the server needs no change.
//     IgnoreThrowerCollisions() still handles all standard Collider components on
//     the player hierarchy for the server physics scene.
//
//   CODE CHANGES (this fix only):
//     • Added _col field — cached in Awake().
//     • OnNetworkSpawn() — if !IsServer, disables _col and Invokes ClientArmCollider.
//     • Added ClientArmCollider() — re-enables _col after armDelay. Client-only path.
//     • No prefab changes required.

using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;   // ← required for NetworkRigidbody
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(NetworkTransform))]   // position replication
[RequireComponent(typeof(NetworkRigidbody))]   // disables client-side physics automatically
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
    private Collider  _col;          // ← NEW: cached for client-side collider disable fix
    private TeamID    _ownerTeam;
    private ulong     _throwerNetworkObjectId;
    private bool      _armed;
    private bool      _deployed;

    private void Awake()
    {
        _rb  = GetComponent<Rigidbody>();
        _col = GetComponent<Collider>(); // ← NEW
    }

    // ── OnNetworkSpawn ────────────────────────────────────────────────────────
    //
    // Safety net: explicitly make the Rigidbody kinematic on clients in case
    // NetworkRigidbody is accidentally removed from the prefab.
    //
    // FIX — Throw Shove:
    //   On non-server clients the grenade Rigidbody is kinematic. Unity's physics
    //   engine allows kinematic Rigidbodies to push CharacterControllers. To prevent
    //   the grenade from shoving the thrower on spawn, we disable the Collider on
    //   all non-server clients for armDelay seconds and re-enable via ClientArmCollider.
    //   By that time the grenade is several metres away and the CC push risk is gone.

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
        {
            // Belt-and-suspenders kinematic guard (NetworkRigidbody normally handles this).
            if (_rb != null)
                _rb.isKinematic = true;

            // ── Throw Shove Fix ───────────────────────────────────────────────
            // Disable the Collider so the kinematic Rigidbody cannot push the
            // thrower's CharacterController during the first armDelay seconds.
            // ClientArmCollider re-enables it once the grenade is safely away.
            if (_col != null)
            {
                _col.enabled = false;
                Invoke(nameof(ClientArmCollider), armDelay);
            }
        }
    }

    // ── ClientArmCollider — non-server clients only ───────────────────────────
    //
    // Re-enables the Collider after armDelay seconds. At this point the grenade
    // has travelled well past the thrower's capsule (≥ 2.8 m at default throw
    // speed), so re-enabling is safe. The Collider is needed on clients so the
    // grenade visually interacts with surfaces for sound/VFX triggers.

    private void ClientArmCollider()
    {
        if (_col != null)
            _col.enabled = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API — call server-side immediately after Spawn()
    // ─────────────────────────────────────────────────────────────────────────

    /// <param name="velocity">World-space throw velocity.</param>
    /// <param name="team">Team of the collector who threw the grenade.</param>
    /// <param name="throwerNetworkObjectId">
    ///     NetworkObjectId of the throwing player. Used to ignore collisions
    ///     on the server's physics scene.
    /// </param>
    public void Initialize(Vector3 velocity, TeamID team, ulong throwerNetworkObjectId = 0)
    {
        // Initialize() is only ever called on the server (from ThrowSmokeGrenadeRpc).
        // Clients receive the grenade's position via NetworkTransform — no RPC needed.

        _ownerTeam              = team;
        _throwerNetworkObjectId = throwerNetworkObjectId;

        // ── IMPORTANT: ignore collisions BEFORE enabling Rigidbody physics ────
        // If isKinematic is set to false first, there is a brief window before
        // IgnoreThrowerCollisions() runs where the active Rigidbody can overlap
        // the player's capsule collider. The physics engine depenetrates them,
        // nudging the player. Ignoring collisions first closes that window entirely.
        IgnoreThrowerCollisions();

        if (_rb != null)
        {
            _rb.isKinematic     = false;           // server owns physics — enable simulation
            _rb.linearVelocity  = velocity;
            _rb.angularVelocity = Random.insideUnitSphere * 9f;
        }

        Invoke(nameof(Arm),    armDelay);
        Invoke(nameof(Deploy), maxLifetime);

        // NOTE: SyncThrowClientRpc has been removed.
        // NetworkTransform replicates the grenade's position to all clients
        // every tick. Clients no longer need velocity — they just follow the
        // server's transform via NetworkTransform interpolation.
    }

    // ── Arm ───────────────────────────────────────────────────────────────────

    private void Arm() => _armed = true;

    // ── Collision ─────────────────────────────────────────────────────────────

    private void OnCollisionEnter(Collision col)
    {
        // Only server handles physics and deploy logic.
        // This guard is belt-and-suspenders alongside the kinematic flag on clients.
        if (!IsServer)
        {
            PlayBounceSoundRpc();   // still play the sound on all machines
            return;
        }

        PlayBounceSoundRpc();

        if (!_armed || _deployed) return;

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
            GameObject    cloud = Instantiate(smokePrefab, transform.position, Quaternion.identity);
            NetworkObject no    = cloud.GetComponent<NetworkObject>();
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

    // ── Collision ignore helper ───────────────────────────────────────────────

    /// <summary>
    /// Server-only. Tells the server's physics scene to ignore collisions between
    /// this grenade and every collider on the throwing player's hierarchy.
    /// Clients no longer need this because their Rigidbody is kinematic.
    ///
    /// NOTE: CharacterController is not a Collider subclass and therefore is NOT
    /// included in GetComponentsInChildren&lt;Collider&gt;(). On the server, the grenade
    /// is a DYNAMIC Rigidbody — dynamic Rigidbodies do not push CharacterControllers
    /// (only kinematic ones do), so this gap is safe to leave. The client-side shove
    /// is handled separately by disabling the Collider in OnNetworkSpawn().
    /// </summary>
    private void IgnoreThrowerCollisions()
    {
        if (!IsServer) return;   // guard: only meaningful on the server
        if (_throwerNetworkObjectId == 0) return;

        if (!NetworkManager.SpawnManager.SpawnedObjects
                .TryGetValue(_throwerNetworkObjectId, out NetworkObject throwerObj))
        {
            Debug.LogWarning($"[SmokeGrenade] IgnoreThrowerCollisions: " +
                             $"NetworkObject {_throwerNetworkObjectId} not found in SpawnedObjects.");
            return;
        }

        Collider grenadeCol = GetComponent<Collider>();
        if (grenadeCol == null) return;

        foreach (Collider pc in throwerObj.GetComponentsInChildren<Collider>(includeInactive: true))
            Physics.IgnoreCollision(grenadeCol, pc, ignore: true);
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