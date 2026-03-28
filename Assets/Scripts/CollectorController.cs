// CollectorController.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// _carriedCandies is the reverse-lookup list that makes drop/deliver work.
// Without it, candies stay floating after death (the original bug).

using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

public class CollectorController : NetworkBehaviour
{
    [Header("Pickup")]
    public float     pickupRadius         = 1.5f;
    public LayerMask candyLayer;
    public float     speedPenaltyPerCandy = 0.05f;
    public int       maxCarryCapacity     = 10;

    [Header("Superspeed")]
    public float superSpeedMultiplier = 2.0f;
    public float superSpeedDuration   = 10f;
    public float superSpeedCooldown   = 30f;

    [Header("Decoy")]
    public GameObject decoyPrefab;
    public float      decoyCooldown = 20f;

    [Header("HUD events")]
    public UnityEvent<int>   onCandyCountChanged  = new();
    public UnityEvent<float> onSuperSpeedCooldown = new();
    public UnityEvent<float> onDecoyCooldown      = new();

    public NetworkVariable<int> carriedCount = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private PlayerController _pc;
    private PlayerStats      _stats;

    private float _superSpeedTimer;
    private float _decoyTimer;
    private bool  _superSpeedActive;
    private float _currentPenalty = 1f;

    // Server-side reverse lookup: this collector → its held candies
    private readonly List<Candy> _carriedCandies = new();

    private void Awake()
    {
        _pc    = GetComponent<PlayerController>();
        _stats = GetComponent<PlayerStats>();
    }

    public override void OnNetworkSpawn()
    {
        carriedCount.OnValueChanged += OnCarriedCountChanged;
        if (IsServer) _stats.onDeath.AddListener(OnDied);
        if (!IsOwner) { enabled = false; return; }
    }

    public override void OnNetworkDespawn()
    {
        carriedCount.OnValueChanged -= OnCarriedCountChanged;
        if (IsServer) _stats.onDeath.RemoveListener(OnDied);
    }

    private void Update()
    {
        if (!IsOwner || _stats.IsDead()) return;
        HandlePickup();
        HandleSuperSpeed();
        HandleDecoy();
        TickCooldowns();
    }

    // ── Pickup ────────────────────────────────────────────────────────────────

    private void HandlePickup()
    {
        // GDD rule: only Collectors can pick up candy
        if (_stats.role.Value != PlayerRole.Collector) return;
        if (!Input.GetKeyDown(KeyCode.F)) return;

        Collider[] hits = Physics.OverlapSphere(transform.position, pickupRadius, candyLayer);
        foreach (Collider hit in hits)
        {
            Candy candy = hit.GetComponent<Candy>();
            NetworkObject no = hit.GetComponent<NetworkObject>();
            if (candy != null && candy.IsOnGround() && no != null)
            {
                PickupCandyRpc(no.NetworkObjectId);
                break;
            }
        }
    }

    [Rpc(SendTo.Server)]
    private void PickupCandyRpc(ulong candyId)
    {
        // Server-side role guard: reject if caller is not a Collector
        if (_stats.role.Value != PlayerRole.Collector) return;
        if (carriedCount.Value >= maxCarryCapacity) return;
        if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(candyId, out var obj)) return;

        Candy candy = obj.GetComponent<Candy>();
        if (candy == null || !candy.IsOnGround()) return;

        candy.PickupServer(NetworkObjectId, carriedCount.Value);
        _carriedCandies.Add(candy);
        carriedCount.Value++;
        CandySpawner.Instance?.NotifyCandyPickedUp(candy);
    }

    // ── Drop (on death) ───────────────────────────────────────────────────────

    public void DropAllCandiesServer()
    {
        if (!IsServer) return;

        int count = _carriedCandies.Count;
        for (int i = 0; i < count; i++)
        {
            if (_carriedCandies[i] == null) continue;

            // Evenly distribute around a circle so no two candies land on top of each other.
            // Ring 0 (first 8): radius 1.8 m  |  Ring 1 (next 8): radius 3.2 m  etc.
            int   ring        = i / 8;
            int   ringSlot    = i % 8;
            int   ringCount   = Mathf.Min(count - ring * 8, 8);
            float radius      = 1.8f + ring * 1.4f;
            float angle       = (2f * Mathf.PI / ringCount) * ringSlot;
            Vector3 offset    = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

            _carriedCandies[i].DropServer(transform.position + offset);
        }

        _carriedCandies.Clear();
        carriedCount.Value = 0;
    }

    // ── Deliver ───────────────────────────────────────────────────────────────

    public void DeliverCandiesServer(TeamID scoringTeam)
    {
        if (!IsServer) return;
        int count = _carriedCandies.Count;
        foreach (Candy c in _carriedCandies)
            if (c != null) c.DeliverServer();
        _carriedCandies.Clear();
        carriedCount.Value = 0;
        NetworkGameManager.Instance?.AddScore(scoringTeam, count);
    }

    // ── Speed ─────────────────────────────────────────────────────────────────

    private void OnCarriedCountChanged(int prev, int next)
    {
        if (!IsOwner) return;
        _currentPenalty = Mathf.Max(1f - next * speedPenaltyPerCandy, 0.3f);
        if (!_superSpeedActive) _pc.speedMultiplier = _currentPenalty;
        onCandyCountChanged?.Invoke(next);
    }

    // ── Superspeed ────────────────────────────────────────────────────────────

    private void HandleSuperSpeed()
    {
        if (Input.GetKeyDown(KeyCode.E) && _superSpeedTimer <= 0f && !_superSpeedActive)
            StartCoroutine(SuperSpeedRoutine());
    }

    private IEnumerator SuperSpeedRoutine()
    {
        _superSpeedActive   = true;
        _pc.speedMultiplier = _currentPenalty * superSpeedMultiplier;
        yield return new WaitForSeconds(superSpeedDuration);
        _pc.speedMultiplier = _currentPenalty;
        _superSpeedActive   = false;
        _superSpeedTimer    = superSpeedCooldown;
    }

    // ── Decoy ─────────────────────────────────────────────────────────────────

    private void HandleDecoy()
    {
        if (Input.GetKeyDown(KeyCode.Q) && _decoyTimer <= 0f)
        {
            SpawnDecoyRpc(transform.position, transform.forward,
                _stats.speedMultiplier * _pc.sprintSpeed);
            _decoyTimer = decoyCooldown;
        }
    }

    [Rpc(SendTo.Server)]
    private void SpawnDecoyRpc(Vector3 pos, Vector3 dir, float speed)
    {
        if (decoyPrefab == null) return;

        // Offset 1.5 m in front so the decoy doesn't overlap the collector's capsule.
        Vector3 roughPos = pos + dir.normalized * 1.5f;

        // ── Compute the ground-snapped position BEFORE Instantiate ────────────
        //
        // This is the only reliable way to give NGO the correct initial position:
        //   • Writing transform.position before Spawn() can be ignored or
        //     overwritten by the CharacterController re-enabling itself.
        //   • Writing transform.position after Spawn() may not re-trigger a full
        //     NetworkTransform baseline sync — clients end up with the wrong origin
        //     and see the decoy floating and frozen even while the server moves it.
        //
        // By passing the correct ground position directly to Instantiate(), the
        // GameObject starts at the right Y, Spawn() broadcasts that Y to all clients,
        // and every subsequent CC.Move() delta is cleanly picked up by NetworkTransform.
        // ─────────────────────────────────────────────────────────────────────────
        Vector3 groundPos = ComputeGroundPos(roughPos, GetComponent<Collider>());

        GameObject obj = Instantiate(decoyPrefab, groundPos, Quaternion.LookRotation(dir));

        // Assign team BEFORE Spawn() so the server has it immediately
        // when ShooterController.RegisterHitServerRpc checks it.
        DecoyAI decoyAI = obj.GetComponent<DecoyAI>();
        if (decoyAI != null) decoyAI.ownerTeam = _stats.team.Value;

        obj.GetComponent<NetworkObject>()?.Spawn(true);
        decoyAI?.InitializeMovement(dir, speed);
    }

    /// <summary>
    /// Raycasts downward from <paramref name="fromPos"/> to find the ground surface,
    /// then returns the pivot position that places the CharacterController's capsule
    /// bottom exactly on that surface.
    /// Skips <paramref name="skipCollider"/> and any PlayerStats colliders so we never
    /// snap to a player body or the collector who spawned the decoy.
    /// </summary>
    private Vector3 ComputeGroundPos(Vector3 fromPos, Collider skipCollider)
    {
        // Read CC shape from the prefab so we can compute the pivot offset.
        // This avoids instantiating just to read values.
        CharacterController prefabCC = decoyPrefab.GetComponent<CharacterController>();
        float bottomOffset = prefabCC != null
            ? prefabCC.center.y - prefabCC.height * 0.5f + prefabCC.skinWidth
            : 0f;

        Vector3      origin = fromPos + Vector3.up * 3f;
        RaycastHit[] hits   = Physics.RaycastAll(origin, Vector3.down, 8f,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (RaycastHit h in hits)
        {
            if (skipCollider != null && h.collider == skipCollider)     continue;
            if (h.collider.GetComponentInParent<PlayerStats>() != null) continue;

            float pivotY = h.point.y - bottomOffset;
            Debug.Log($"[DecoySpawn] groundPos pivotY={pivotY:F3}  hit.y={h.point.y:F3}  offset={bottomOffset:F3}");
            return new Vector3(fromPos.x, pivotY, fromPos.z);
        }

        Debug.LogWarning("[DecoySpawn] ComputeGroundPos: no surface found — spawning at rough position.");
        return fromPos;
    }

    // ── Cooldown ticks ────────────────────────────────────────────────────────

    private void TickCooldowns()
    {
        if (_superSpeedTimer > 0f)
        {
            _superSpeedTimer -= Time.deltaTime;
            onSuperSpeedCooldown?.Invoke(Mathf.Max(_superSpeedTimer, 0f));
        }
        if (_decoyTimer > 0f)
        {
            _decoyTimer -= Time.deltaTime;
            onDecoyCooldown?.Invoke(Mathf.Max(_decoyTimer, 0f));
        }
    }

    private void OnDied() { if (IsServer) DropAllCandiesServer(); }

    public int GetCarriedCount() => carriedCount.Value;

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, pickupRadius);
    }
}