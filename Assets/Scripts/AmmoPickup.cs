// AmmoPickup.cs — Sugar Rush
//
// ── PURPOSE ───────────────────────────────────────────────────────────────────
//   A networked ammo pickup that spawns on the map (via AmmoSpawner).
//   ONLY the Collector role can pick it up via LEFT CLICK — exactly like candy.
//   When collected, it refills ALL of the Collector's teammate Shooter's
//   weapon magazines and reserves.
//
// ── PICKUP FLOW (mirrors Candy pickup pattern) ────────────────────────────────
//   1. Collector presses left mouse button.
//   2. CollectorController.HandlePickup() does OverlapSphere on ammoLayer.
//   3. CollectorController.PickupAmmoRpc() fires → server validates.
//   4. Server calls AmmoPickup.PickupServer(collectorStats).
//   5. PickupServer() finds the teammate Shooter, calls RefillAllAmmo(),
//      notifies the collector client, tells AmmoSpawner, then despawns.
//
// ── PREFAB REQUIREMENTS ───────────────────────────────────────────────────────
//   1. NetworkObject component
//   2. A Collider (set isTrigger = false — CollectorController uses OverlapSphere,
//      no physics collision needed. But a collider IS required so OverlapSphere
//      can detect this object. A SphereCollider radius ~0.4 is fine.)
//   3. Assign the prefab's layer to match CollectorController.ammoLayer
//   4. Register the prefab in NetworkManager → Network Prefabs list

using System.Collections;
using Unity.Netcode;
using UnityEngine;

public class AmmoPickup : NetworkBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Visual Animation")]
    [Tooltip("How many units the pickup bobs up and down.")]
    public float bobAmplitude = 0.15f;
    [Tooltip("Speed of the bob cycle.")]
    public float bobSpeed     = 2f;
    [Tooltip("Degrees per second the pickup spins.")]
    public float rotateSpeed  = 60f;

    [Header("Lifetime")]
    [Tooltip("Seconds this pickup lingers before auto-despawning if uncollected.")]
    public float lifetime = 30f;

    // ── Runtime ───────────────────────────────────────────────────────────────

    private Vector3 _startPos;
    private bool    _pickedUp;

    // ── NGO lifecycle ─────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        _startPos = transform.position;

        if (IsServer)
            StartCoroutine(LifetimeRoutine());
    }

    // ── Per-frame: visual only ────────────────────────────────────────────────

    private void Update()
    {
        // Bob up/down and spin — purely cosmetic, runs on all clients.
        float y = _startPos.y + Mathf.Sin(Time.time * bobSpeed) * bobAmplitude;
        transform.position = new Vector3(transform.position.x, y, transform.position.z);
        transform.Rotate(Vector3.up, rotateSpeed * Time.deltaTime);
    }

    // ── Server: called by CollectorController.PickupAmmoRpc ──────────────────

    /// <summary>
    /// Called on the SERVER by CollectorController after the owner clicks.
    /// Finds the teammate Shooter, refills their weapons, notifies the collector,
    /// then despawns this pickup.
    /// </summary>
    public void PickupServer(PlayerStats collector)
    {
        if (!IsServer || _pickedUp) return;
        _pickedUp = true;

        // Find this collector's teammate Shooter and refill their weapons.
        TeamID team = collector.team.Value;

        foreach (PlayerStats ps in FindObjectsByType<PlayerStats>(FindObjectsSortMode.None))
        {
            if (ps.team.Value != team)               continue;
            if (ps.role.Value != PlayerRole.Shooter) continue;

            ShooterController sc = ps.GetComponent<ShooterController>();
            if (sc != null)
            {
                sc.RefillAllAmmo();
                Debug.Log($"[AmmoPickup] {team} Shooter resupplied by Collector " +
                          $"(client {collector.OwnerClientId}).");
            }
            break;
        }

        // Tell the collector client they successfully resupplied their shooter.
        NotifyCollectorRpc(RpcTarget.Single(collector.OwnerClientId, RpcTargetUse.Temp));

        // Inform the spawner so it can schedule a replacement.
        AmmoSpawner.Instance?.NotifyPickedUp(this);

        GetComponent<NetworkObject>()?.Despawn(true);
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────

    private IEnumerator LifetimeRoutine()
    {
        yield return new WaitForSeconds(lifetime);

        if (_pickedUp || !IsServer) yield break;

        AmmoSpawner.Instance?.NotifyPickedUp(this);
        GetComponent<NetworkObject>()?.Despawn(true);
    }

    // ── RPC: feedback to the collector ───────────────────────────────────────

    [Rpc(SendTo.SpecifiedInParams)]
    private void NotifyCollectorRpc(RpcParams rpcParams = default)
    {
        // Optional: HUDManager.Instance?.ShowBanner("Shooter Resupplied!");
        Debug.Log("[AmmoPickup] You resupplied your Shooter's ammo!");
    }

    // ── Editor gizmo ──────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.85f, 0f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, 0.4f);
    }
}