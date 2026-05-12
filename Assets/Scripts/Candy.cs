using System.Collections;
using Unity.Netcode;
using UnityEngine;

public enum CandyState { OnGround, Carried, Delivered }

public class Candy : NetworkBehaviour
{
    [Header("Animation")]
    public float bobAmplitude = 0.2f;
    public float bobSpeed     = 1.5f;
    public float rotateSpeed  = 90f;

    public NetworkVariable<CandyState> state = new(CandyState.OnGround,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<ulong> carrierId = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<int> slotIndex = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    [Header("Dropped candy")]
    public float droppedLifetime = 10f;

    private Vector3   _startPos;
    private Collider  _col;
    private Renderer  _rend;
    private Coroutine _despawnRoutine;

    // ── Carrier cache (lag fix) ───────────────────────────────────────────────
    // The original Update() called NetworkManager.SpawnManager.SpawnedObjects
    // .TryGetValue() every frame per candy — a dictionary lookup inside the
    // NGO spawn table. With 10 carried candies that is 600 lookups/second.
    // Fix: cache the carrier's Transform when carrierId changes; clear on drop.
    private Transform _carrierTransform;

    private void Awake()
    {
        _col  = GetComponent<Collider>();
        _rend = GetComponentInChildren<Renderer>();
    }

    public override void OnNetworkSpawn()
    {
        _startPos = transform.position;
        state.OnValueChanged += (_, next) =>
        {
            bool onGround = next == CandyState.OnGround;
            _col.enabled  = onGround;
            if (_rend) _rend.enabled = onGround;
        };
        bool startOnGround   = state.Value == CandyState.OnGround;
        _col.enabled         = startOnGround;
        if (_rend) _rend.enabled = startOnGround;

        // Cache carrier transform whenever carrierId changes — avoids per-frame
        // SpawnedObjects dictionary lookup in Update().
        carrierId.OnValueChanged += (_, newId) =>
        {
            if (newId == 0 || state.Value != CandyState.Carried)
            {
                _carrierTransform = null;
                return;
            }
            if (NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(newId, out var obj))
                _carrierTransform = obj.transform;
        };
    }

    private void Update()
    {
        if (state.Value == CandyState.OnGround)
        {
            float y = _startPos.y + Mathf.Sin(Time.time * bobSpeed) * bobAmplitude;
            transform.position = new Vector3(transform.position.x, y, transform.position.z);
            transform.Rotate(Vector3.up, rotateSpeed * Time.deltaTime);
        }
        else if (state.Value == CandyState.Carried)
        {
            // FIX: use cached _carrierTransform instead of SpawnedObjects lookup every frame
            if (_carrierTransform != null)
            {
                int idx = slotIndex.Value;
                transform.position = _carrierTransform.position
                    + Vector3.up * (1.5f + idx * 0.25f)
                    + _carrierTransform.right * (idx * 0.3f - 1f);
            }
            else if (NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(carrierId.Value, out var obj))
            {
                // Fallback: cache was not set yet (e.g. late join) — populate now
                _carrierTransform = obj.transform;
            }
        }
    }

    
    public void PickupServer(ulong collectorId, int slot)
    {
        if (!IsServer) return;
        
        if (_despawnRoutine != null) { StopCoroutine(_despawnRoutine); _despawnRoutine = null; }
        state.Value     = CandyState.Carried;
        carrierId.Value = collectorId;
        slotIndex.Value = slot;
    }

    public void DropServer(Vector3 dropPos)
    {
        if (!IsServer) return;
        state.Value     = CandyState.OnGround;
        carrierId.Value = 0;
        slotIndex.Value = 0;
        transform.position = dropPos + Vector3.up * 0.5f;
        _startPos = transform.position;

        
        if (_despawnRoutine != null) StopCoroutine(_despawnRoutine);
        _despawnRoutine = StartCoroutine(DroppedLifetimeRoutine());
    }

    private IEnumerator DroppedLifetimeRoutine()
    {
        yield return new WaitForSeconds(droppedLifetime);
        if (IsServer && state.Value == CandyState.OnGround)
            GetComponent<NetworkObject>()?.Despawn(true);
    }

    public void DeliverServer()
    {
        if (!IsServer) return;
        state.Value = CandyState.Delivered;
        GetComponent<NetworkObject>()?.Despawn(true);
    }

    public bool IsOnGround() => state.Value == CandyState.OnGround;
    public bool IsCarried()  => state.Value == CandyState.Carried;
}