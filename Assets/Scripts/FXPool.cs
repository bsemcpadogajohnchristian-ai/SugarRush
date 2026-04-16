using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FXPool : MonoBehaviour
{
    public static FXPool Instance { get; private set; }

    
    private readonly Dictionary<GameObject, Queue<GameObject>> _pools = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    
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

            
            PooledFXTag tag = obj.AddComponent<PooledFXTag>();
            tag.sourcePrefab = prefab;
        }

        
        foreach (ParticleSystem ps in obj.GetComponentsInChildren<ParticleSystem>())
        {
            ps.Clear();
            ps.Play();
        }

        
        float lifetime = GetParticleLifetime(obj);
        StartCoroutine(ReturnAfterDelay(obj, lifetime));

        return obj;
    }

    
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


public class PooledFXTag : MonoBehaviour
{
    [HideInInspector] public GameObject sourcePrefab;
}
