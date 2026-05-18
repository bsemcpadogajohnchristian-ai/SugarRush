// AmmoSpawner.cs — Sugar Rush
//
// ── PURPOSE ───────────────────────────────────────────────────────────────────
//   Server-authoritative spawner that keeps a fixed number of AmmoPickup items
//   alive on the map at all times. When a pickup is collected or expires,
//   AmmoPickup calls NotifyPickedUp() and a new one spawns after respawnDelay.
//
// ── DESIGN ────────────────────────────────────────────────────────────────────
//   Unlike CandySpawner (wave-based), AmmoSpawner maintains a steady pool.
//   This means there are always `maxAmmoPickups` packs available — creating a
//   constant resource for both teams' Collectors to race for.
//
// ── SETUP ─────────────────────────────────────────────────────────────────────
//   1. Create an empty GameObject in GameScene named "AmmoSpawner".
//   2. Attach this script AND a NetworkObject component to it.
//   3. Set all Inspector fields (see below).
//   4. NetworkGameManager.StartMatch() calls StartSpawning() automatically
//      once you add the one-line hook described in the tutorial.
//
// ── SURFACE VALIDATION ────────────────────────────────────────────────────────
//   Uses identical raycast + normal-dot + headroom + CheckSphere logic as
//   CandySpawner so pickups always land on walkable, unobstructed surfaces.

using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class AmmoSpawner : NetworkBehaviour
{
    public static AmmoSpawner Instance { get; private set; }

    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Prefab")]
    [Tooltip("The AmmoPickup networked prefab. Must be registered in NetworkManager.")]
    public GameObject ammoPickupPrefab;

    [Header("Pool Settings")]
    [Tooltip("Maximum number of ammo packs alive on the map simultaneously.")]
    public int   maxAmmoPickups   = 3;
    [Tooltip("Seconds after a pickup is collected/expired before a replacement spawns.")]
    public float respawnDelay     = 15f;
    [Tooltip("Minimum distance from any team base to prevent spawning inside a base.")]
    public float exclusionRadius  = 6f;
    [Tooltip("Minimum distance between any two ammo packs in the world.")]
    public float minPickupSpacing = 5f;
    [Tooltip("Team base Transforms — ammo won't spawn within exclusionRadius of these.")]
    public Transform[] teamBases;

    [Header("Map Bounds (match your CandySpawner values)")]
    public Vector3 mapCenter = Vector3.zero;
    public float   mapSize   = 30f;

    [Header("Surface Validation")]
    [Tooltip("How flat a surface must be for a pickup to spawn on it. " +
             "1.0 = perfectly flat only. 0.9 = slight slopes allowed.")]
    [Range(0.7f, 1.0f)]
    public float     minSurfaceDot    = 0.9f;
    [Tooltip("Only spawn on these layers. Set to your Ground/Floor layer.")]
    public LayerMask spawnableLayers  = Physics.DefaultRaycastLayers;

    // ── Runtime ───────────────────────────────────────────────────────────────

    private readonly List<AmmoPickup> _activePickups = new();
    private bool _spawning;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    // ── Public API (called by NetworkGameManager) ─────────────────────────────

    /// <summary>
    /// Server: fill the map up to maxAmmoPickups immediately, then maintain
    /// the pool via NotifyPickedUp → RespawnAfterDelay.
    /// </summary>
    public void StartSpawning()
    {
        if (!IsServer) return;
        _spawning = true;

        int toSpawn = maxAmmoPickups - _activePickups.Count;
        for (int i = 0; i < toSpawn; i++)
            SpawnOne();
    }

    /// <summary>
    /// Server: despawn every active pickup (called when the match ends).
    /// </summary>
    public void StopSpawning()
    {
        if (!IsServer) return;
        _spawning = false;
        DespawnAll();
    }

    /// <summary>
    /// Called by AmmoPickup when it is collected or auto-expires.
    /// Removes it from the pool and schedules a replacement if the match is
    /// still running.
    /// </summary>
    public void NotifyPickedUp(AmmoPickup pickup)
    {
        _activePickups.Remove(pickup);

        if (IsServer && _spawning && gameObject.activeInHierarchy)
            StartCoroutine(RespawnAfterDelay());
    }

    // ── Internal: spawn logic ─────────────────────────────────────────────────

    private IEnumerator RespawnAfterDelay()
    {
        yield return new WaitForSeconds(respawnDelay);

        if (_spawning && _activePickups.Count < maxAmmoPickups)
            SpawnOne();
    }

    private void SpawnOne()
    {
        if (ammoPickupPrefab == null)
        {
            Debug.LogError("[AmmoSpawner] ammoPickupPrefab is not assigned!", this);
            return;
        }

        // Build a list of positions already occupied so GetValidPosition can
        // enforce minPickupSpacing between packs.
        List<Vector3> occupied = new();
        foreach (AmmoPickup p in _activePickups)
            if (p != null) occupied.Add(p.transform.position);

        Vector3 pos = GetValidPosition(occupied);
        if (pos == Vector3.zero)
        {
            Debug.LogWarning("[AmmoSpawner] SpawnOne: could not find a valid position " +
                             "after 30 attempts. Map may be too small or crowded.");
            return;
        }

        GameObject obj = Instantiate(ammoPickupPrefab, pos, Quaternion.identity);
        obj.GetComponent<NetworkObject>()?.Spawn(true);

        AmmoPickup pickup = obj.GetComponent<AmmoPickup>();
        if (pickup != null) _activePickups.Add(pickup);
    }

    // ── Surface / spacing validation (mirrors CandySpawner) ──────────────────

    private Vector3 GetValidPosition(List<Vector3> occupied)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            float x = Random.Range(mapCenter.x - mapSize, mapCenter.x + mapSize);
            float z = Random.Range(mapCenter.z - mapSize, mapCenter.z + mapSize);

            Vector3 rayOrigin = new Vector3(x, mapCenter.y + 20f, z);

            if (!Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 40f, spawnableLayers))
                continue;

            // Surface must be flat enough for a player to stand on.
            if (Vector3.Dot(hit.normal, Vector3.up) < minSurfaceDot) continue;

            Vector3 pos = hit.point + Vector3.up * 0.5f;

            // ── Team-base exclusion ────────────────────────────────────────────
            bool tooClose = false;
            if (teamBases != null)
                foreach (Transform t in teamBases)
                    if (Vector3.Distance(pos, t.position) < exclusionRadius)
                    { tooClose = true; break; }

            if (tooClose) continue;

            // ── Pickup-spacing check ───────────────────────────────────────────
            foreach (Vector3 occ in occupied)
                if (Vector3.Distance(pos, occ) < minPickupSpacing)
                { tooClose = true; break; }

            if (tooClose) continue;

            // ── Headroom check (is the spot blocked above?) ────────────────────
            if (Physics.Raycast(pos, Vector3.up, 1.5f,
                spawnableLayers, QueryTriggerInteraction.Ignore))
                continue;

            // ── Overlap check (is something already sitting here?) ─────────────
            if (Physics.CheckSphere(pos, 0.35f,
                spawnableLayers, QueryTriggerInteraction.Ignore))
                continue;

            return pos;
        }

        return Vector3.zero;
    }

    private void DespawnAll()
    {
        foreach (AmmoPickup p in _activePickups)
            if (p != null) p.GetComponent<NetworkObject>()?.Despawn(true);
        _activePickups.Clear();
    }

    // ── Editor gizmo ──────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        // Show spawn bounds
        Gizmos.color = new Color(1f, 0.85f, 0f, 0.15f);
        Gizmos.DrawCube(mapCenter, new Vector3(mapSize * 2f, 1f, mapSize * 2f));

        Gizmos.color = new Color(1f, 0.85f, 0f, 0.5f);
        Gizmos.DrawWireCube(mapCenter, new Vector3(mapSize * 2f, 1f, mapSize * 2f));

        // Show exclusion zones around bases
        if (teamBases == null) return;
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.25f);
        foreach (Transform t in teamBases)
            if (t != null) Gizmos.DrawWireSphere(t.position, exclusionRadius);
    }
}
