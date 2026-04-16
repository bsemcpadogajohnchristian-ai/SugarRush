using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class CandySpawner : NetworkBehaviour
{
    public static CandySpawner Instance { get; private set; }

    [Header("Spawning")]
    public GameObject  candyPrefab;
    public int         candiesPerWave       = 5;
    public float       waveDuration         = 10f;
    public float       spawnExclusionRadius = 6f;
    [Tooltip("Minimum distance between any two candy pieces in the same wave. " +
             "Prevents candies from spawning on top of each other.")]
    public float       minCandySpacing      = 3f;
    public Transform[] teamBases;

    [Header("Map bounds")]
    public Vector3 mapCenter = Vector3.zero;
    public float   mapSize   = 30f;

    [Header("Surface validation")]
    [Tooltip("How flat a surface must be to spawn candy on it. " +
             "1.0 = perfectly flat floor only. " +
             "0.9 = allows very slight slopes. " +
             "0.7 = allows ramps. Default 0.9 is recommended.")]
    [Range(0.7f, 1.0f)]
    public float minSurfaceDot = 0.9f;

    [Tooltip("Only spawn candy on these layers. Set to your Ground/Floor layer. " +
             "Leave as Default to hit everything and rely on the normal check.")]
    public LayerMask spawnableLayers = Physics.DefaultRaycastLayers;

    private readonly List<Candy> _waveCandy = new();
    private Coroutine _loop;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public void StartSpawning()
    {
        if (!IsServer) return;
        _loop = StartCoroutine(WaveLoop());
    }

    public void StopSpawning()
    {
        if (!IsServer) return;
        if (_loop != null) StopCoroutine(_loop);
        DespawnAll();
    }

    private IEnumerator WaveLoop()
    {
        while (true)
        {
            SpawnWave();
            yield return new WaitForSeconds(waveDuration);
            DespawnRemaining();
        }
    }

    private void SpawnWave()
    {
        _waveCandy.Clear();

        
        List<Vector3> placedPositions = new();

        for (int i = 0; i < candiesPerWave; i++)
        {
            Vector3 pos = GetValidPosition(placedPositions);
            if (pos == Vector3.zero) continue;

            placedPositions.Add(pos);

            GameObject obj = Instantiate(candyPrefab, pos, Quaternion.identity);
            obj.GetComponent<NetworkObject>()?.Spawn(true);
            Candy c = obj.GetComponent<Candy>();
            if (c != null) _waveCandy.Add(c);
        }
    }

    private Vector3 GetValidPosition(List<Vector3> placedPositions)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            float x = Random.Range(mapCenter.x - mapSize, mapCenter.x + mapSize);
            float z = Random.Range(mapCenter.z - mapSize, mapCenter.z + mapSize);

            Vector3 rayOrigin = new Vector3(x, mapCenter.y + 20f, z);

            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 40f, spawnableLayers))
            {
                
                
                float surfaceDot = Vector3.Dot(hit.normal, Vector3.up);
                if (surfaceDot < minSurfaceDot) continue;

                Vector3 pos = hit.point + Vector3.up * 0.5f;

                
                bool tooClose = false;
                if (teamBases != null)
                    foreach (Transform t in teamBases)
                        if (Vector3.Distance(pos, t.position) < spawnExclusionRadius)
                        { tooClose = true; break; }

                if (tooClose) continue;

                
                foreach (Vector3 placed in placedPositions)
                    if (Vector3.Distance(pos, placed) < minCandySpacing)
                    { tooClose = true; break; }

                if (tooClose) continue;

                
                if (Physics.Raycast(pos, Vector3.up, 1.5f,
                    spawnableLayers, QueryTriggerInteraction.Ignore))
                    continue;

                
                if (Physics.CheckSphere(pos, 0.35f,
                    spawnableLayers, QueryTriggerInteraction.Ignore))
                    continue;

                return pos;
            }
        }

        Debug.LogWarning("[CandySpawner] GetValidPosition: failed to find valid spot after 30 attempts.");
        return Vector3.zero;
    }

    private void DespawnRemaining()
    {
        foreach (Candy c in _waveCandy)
            if (c != null && c.IsOnGround())
                c.GetComponent<NetworkObject>()?.Despawn(true);
        _waveCandy.Clear();
    }

    private void DespawnAll()
    {
        foreach (Candy c in _waveCandy)
            if (c != null) c.GetComponent<NetworkObject>()?.Despawn(true);
        _waveCandy.Clear();
    }

    public void NotifyCandyPickedUp(Candy candy) => _waveCandy.Remove(candy);
}
