// SmokeCloud.cs — Sugar Rush
// Lingering smoke cloud spawned by SmokeGrenade.Deploy().
//
// ── FEATURES ──────────────────────────────────────────────────────────────
//   • Particle-system visuals grow to full radius over growDuration seconds.
//   • Server polls every 0.1 s to detect which players are inside (consistent
//     with DeliveryZone's approach — avoids CharacterController trigger quirks).
//   • Players who enter the smoke cloud receive a targeted RPC that tells
//     their HUDManager to show a screen-space smoke overlay (tinted fog).
//   • Players who leave (or the cloud despawns) receive the remove RPC.
//   • Auto-despawns after smokeDuration seconds.
//
// ── PREFAB REQUIREMENTS ───────────────────────────────────────────────────
//   • NetworkObject
//   • SphereCollider  (set to trigger — this script sets isTrigger at runtime)
//   • ParticleSystem  with a Sphere shape module (assign in smokeParticles)
//   • Set playerLayer to the layer your Player GameObjects use

using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class SmokeCloud : NetworkBehaviour
{
    // ── Inspector ────────────────────────────────────────────────────────────

    [Header("Duration & Size")]
    [Tooltip("How long (seconds) the smoke lingers before despawning.")]
    public float smokeDuration  = 8f;

    [Tooltip("Radius of the fully-grown smoke sphere (metres).")]
    public float smokeRadius    = 5.5f;

    [Header("Grow-in")]
    [Tooltip("Seconds for the cloud to expand from 0 to smokeRadius.")]
    public float growDuration   = 1.4f;

    [Header("Visuals")]
    [Tooltip("ParticleSystem that plays the smoke VFX. " +
             "Its Shape module should be set to Sphere.")]
    public ParticleSystem smokeParticles;

    [Header("Player Detection")]
    [Tooltip("Set this to the layer your Player GameObjects are on.")]
    public LayerMask playerLayer;

    [Tooltip("How often (seconds) to check for players inside the cloud.")]
    public float checkInterval  = 0.1f;

    // ── Runtime ──────────────────────────────────────────────────────────────

    private SphereCollider          _col;
    private TeamID                  _ownerTeam;
    private readonly HashSet<ulong> _insideClients = new();
    private float                   _checkTimer;

    // ── NGO lifecycle ─────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        _col           = GetComponent<SphereCollider>();
        _col.isTrigger = true;
        _col.radius    = 0f;   // grows via GrowRoutine

        if (smokeParticles != null) smokeParticles.Play();

        StartCoroutine(GrowRoutine());

        if (IsServer)
        {
            _checkTimer = checkInterval;
            StartCoroutine(LifetimeRoutine());
        }
    }

    /// <summary>Server-side init — called by SmokeGrenade.Deploy() after Spawn().</summary>
    public void InitializeCloud(TeamID ownerTeam) => _ownerTeam = ownerTeam;

    // ── Update (server-side player detection) ────────────────────────────────

    private void Update()
    {
        if (!IsServer) return;

        _checkTimer -= Time.deltaTime;
        if (_checkTimer > 0f) return;
        _checkTimer = checkInterval;

        CheckPlayersInside();
    }

    private void CheckPlayersInside()
    {
        // Find all players currently within smokeRadius of this cloud.
        Collider[] hits = Physics.OverlapSphere(
            transform.position, _col.radius, playerLayer);

        // Build set of client IDs currently inside.
        var currentlyInside = new HashSet<ulong>();
        foreach (Collider hit in hits)
        {
            PlayerStats ps = hit.GetComponent<PlayerStats>();
            if (ps == null || ps.IsDead()) continue;
            currentlyInside.Add(ps.OwnerClientId);
        }

        // Newly entered players → show overlay.
        foreach (ulong id in currentlyInside)
        {
            if (_insideClients.Add(id))
                NotifyOverlayRpc(true, RpcTarget.Single(id, RpcTargetUse.Temp));
        }

        // Players who left → hide overlay.
        var exited = new List<ulong>();
        foreach (ulong id in _insideClients)
            if (!currentlyInside.Contains(id))
                exited.Add(id);

        foreach (ulong id in exited)
        {
            _insideClients.Remove(id);
            NotifyOverlayRpc(false, RpcTarget.Single(id, RpcTargetUse.Temp));
        }
    }

    // ── Grow coroutine ────────────────────────────────────────────────────────

    private IEnumerator GrowRoutine()
    {
        float elapsed = 0f;

        while (elapsed < growDuration)
        {
            elapsed += Time.deltaTime;
            float t  = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / growDuration));
            float r  = Mathf.Lerp(0f, smokeRadius, t);

            if (_col != null) _col.radius = r;

            if (smokeParticles != null)
            {
                var shape  = smokeParticles.shape;
                shape.radius = r;
            }

            yield return null;
        }

        // Ensure final values are exact.
        if (_col != null) _col.radius = smokeRadius;
        if (smokeParticles != null)
        {
            var shape  = smokeParticles.shape;
            shape.radius = smokeRadius;
        }
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────

    private IEnumerator LifetimeRoutine()
    {
        yield return new WaitForSeconds(smokeDuration);
        DespawnCloud();
    }

    private void DespawnCloud()
    {
        if (!IsServer) return;

        // Notify anyone still inside before despawning so their overlay clears.
        foreach (ulong id in _insideClients)
            NotifyOverlayRpc(false, RpcTarget.Single(id, RpcTargetUse.Temp));

        _insideClients.Clear();
        GetComponent<NetworkObject>()?.Despawn(true);
    }

    // ── Targeted RPC — only the affected client receives this ────────────────
    //
    // SendTo.SpecifiedInParams with RpcTarget.Single sends to exactly one client.
    // Using this instead of a ClientRpc means we don't broadcast to all clients
    // just to tell one player their screen should change.

    [Rpc(SendTo.SpecifiedInParams)]
    private void NotifyOverlayRpc(bool isInside, RpcParams rpcParams = default)
        => HUDManager.Instance?.SetSmokeOverlay(isInside);

    // ── Safety cleanup on despawn ─────────────────────────────────────────────

    public override void OnNetworkDespawn()
    {
        // Belt-and-suspenders: clear any lingering overlay on THIS client.
        // (Handles cases where the player was inside when the cloud was force-despawned.)
        HUDManager.Instance?.SetSmokeOverlay(false);
        base.OnNetworkDespawn();
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.7f, 0.7f, 0.7f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, smokeRadius);
    }
}
