// FXPool.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Client-side object pool for short-lived particle FX (bullet impacts, explosions).
// Eliminates the Instantiate/Destroy churn that causes GC spikes during heavy fire.
//
// SETUP:
//   Attach this to any persistent GameObject in your scene (e.g. the one that
//   already has DontDestroyLoader). It survives scene loads automatically.
//
// HOW IT WORKS:
//   • First request for a prefab: instantiates the object normally and tags it
//     with a PooledFXTag component so Return() knows which pool it belongs to.
//   • Subsequent requests: pulls a sleeping object from the queue, repositions
//     it, replays all ParticleSystem children, and starts a coroutine to
//     return it automatically when the particles finish.
//   • No object is ever Destroyed — they just toggle SetActive(false) and
//     go back into the queue.
//
// THREAD SAFETY: Unity main thread only (standard for MonoBehaviour pools).

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FXPool : MonoBehaviour
{
    public static FXPool Instance { get; private set; }

    // One queue per unique prefab asset reference
    private readonly Dictionary<GameObject, Queue<GameObject>> _pools = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch a pooled instance of <paramref name="prefab"/> at the given pose.
    /// Automatically returns the instance to the pool once its particles finish.
    /// Safe to call even if FXPool.Instance is null — returns null gracefully.
    /// </summary>
    public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
    {
        if (prefab == null) return null;

        if (!_pools.TryGetValue(prefab, out Queue<GameObject> queue))
            _pools[prefab] = queue = new Queue<GameObject>();

        GameObject obj;
        if (queue.Count > 0)
        {
            obj = queue.Dequeue();
            obj.transform.SetPositionAndRotation(position, rotation);
            obj.SetActive(true);
        }
        else
        {
            obj = Instantiate(prefab, position, rotation);

            // Tag so we can find the source prefab key on return
            PooledFXTag tag = obj.AddComponent<PooledFXTag>();
            tag.sourcePrefab = prefab;
        }

        // Replay all particle systems (they may be stopped from last use)
        foreach (ParticleSystem ps in obj.GetComponentsInChildren<ParticleSystem>())
        {
            ps.Clear();
            ps.Play();
        }

        // Auto-return after the longest particle finishes
        float lifetime = GetParticleLifetime(obj);
        StartCoroutine(ReturnAfterDelay(obj, lifetime));

        return obj;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private IEnumerator ReturnAfterDelay(GameObject obj, float delay)
    {
        yield return new WaitForSeconds(delay);
        Return(obj);
    }

    private void Return(GameObject obj)
    {
        if (obj == null) return;
        PooledFXTag tag = obj.GetComponent<PooledFXTag>();
        if (tag == null || tag.sourcePrefab == null) { Destroy(obj); return; }

        obj.SetActive(false);

        if (_pools.TryGetValue(tag.sourcePrefab, out var queue))
            queue.Enqueue(obj);
    }

    private static float GetParticleLifetime(GameObject obj)
    {
        float max = 1.5f;
        foreach (ParticleSystem ps in obj.GetComponentsInChildren<ParticleSystem>())
        {
            float dur = ps.main.duration + ps.main.startLifetime.constantMax;
            if (dur > max) max = dur;
        }
        return max;
    }
}

// ── Helper component — attached automatically by FXPool ──────────────────────

/// <summary>
/// Tiny marker component that records which prefab an FX object was created from,
/// so FXPool.Return() can put it back in the correct queue.
/// Never add this manually.
/// </summary>
public class PooledFXTag : MonoBehaviour
{
    [HideInInspector] public GameObject sourcePrefab;
}
