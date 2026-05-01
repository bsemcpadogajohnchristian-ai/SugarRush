// CollectorController.cs — Sugar Rush
//
// ── MAGNET ABILITY ADDED ──────────────────────────────────────────────────────
//   New ability: press R to activate a candy magnet for magnetDuration seconds.
//   While active, the nearest on-ground candy within magnetRadius is auto-picked
//   up every magnetPickupRate seconds (up to maxCarryCapacity).
//   After the duration expires, a magnetCooldown countdown begins.
//
//   NEW FIELDS:
//     • magnetRadius, magnetDuration, magnetCooldown, magnetPickupRate (Inspector)
//     • onMagnetCooldown   UnityEvent<float>  — drives HUD cooldown fill
//     • onMagnetActiveChanged UnityEvent<bool> — drives HUD active indicator
//     • onMagnetActivated  UnityEvent          — fires animation trigger (FP + 3P)
//     • magnetActive       NetworkVariable<bool> — replicated for 3P animator
//     • _magnetCooldownTimer, _magnetActive, _magnetPickupTimer (private state)
//
//   NEW METHODS:
//     • HandleMagnet()         — reads R key, starts coroutine
//     • MagnetRoutine()        — coroutine: active duration then cooldown flag
//     • TryMagnetPickup()      — finds nearest candy in range, calls PickupCandyRpc
//     • TickCooldowns() gains  — magnet cooldown tick + charge restore

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
    public float      decoyCooldown       = 20f;
    public float      decoyWindowDuration = 5f;
    public int        decoyMaxCharges     = 2;

    // ── Magnet ────────────────────────────────────────────────────────────────
    [Header("Magnet")]
    [Tooltip("Auto-pickup radius while the magnet is active (metres).")]
    public float magnetRadius     = 8f;
    [Tooltip("How long the magnet stays active after pressing R (seconds).")]
    public float magnetDuration   = 6f;
    [Tooltip("Cooldown before R can be pressed again (seconds).")]
    public float magnetCooldown   = 25f;
    [Tooltip("Seconds between each automatic candy pickup while magnet is active.")]
    public float magnetPickupRate = 0.25f;

    [Header("HUD events")]
    public UnityEvent<int>   onCandyCountChanged   = new();
    public UnityEvent<float> onSuperSpeedCooldown  = new();
    public UnityEvent<float> onDecoyCooldown       = new();
    public UnityEvent<int>   onDecoyChargesChanged = new();
    public UnityEvent<float> onDecoyWindow         = new();
    public UnityEvent        onDecoyFired          = new();

    // ── Magnet HUD / animation events ─────────────────────────────────────────
    /// <summary>Remaining cooldown seconds — 0 when ready. Drives HUD fill.</summary>
    public UnityEvent<float> onMagnetCooldown      = new();
    /// <summary>True while the magnet is actively pulling candy.</summary>
    public UnityEvent<bool>  onMagnetActiveChanged = new();
    /// <summary>Fires once on activation — triggers FP + 3P animations.</summary>
    public UnityEvent        onMagnetActivated     = new();

    // ── Network variables ─────────────────────────────────────────────────────

    public NetworkVariable<int> carriedCount = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<bool> superSpeedActive = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>
    /// Replicated so CollectorAnimator on remote clients can drive the 3P
    /// magnet animation, mirroring the superSpeedActive pattern.
    /// </summary>
    public NetworkVariable<bool> magnetActive = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ── Private state ─────────────────────────────────────────────────────────

    private PlayerController _pc;
    private PlayerStats      _stats;

    private float _superSpeedTimer;
    private float _decoyTimer;
    private float _decoyWindowTimer;
    private bool  _inDecoyWindow;
    private int   _decoyCharges;
    private bool  _superSpeedActive;
    private float _currentPenalty = 1f;

    // Magnet
    private float _magnetCooldownTimer;
    private bool  _magnetActive;
    private float _magnetPickupTimer;

    private readonly List<Candy> _carriedCandies = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

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

        _decoyCharges = decoyMaxCharges;
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
        HandleMagnet();
        TickCooldowns();
    }

    // ── Manual Pickup ─────────────────────────────────────────────────────────

    private void HandlePickup()
    {
        if (_stats.role.Value != PlayerRole.Collector) return;
        if (!Input.GetMouseButtonDown(0)) return;

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

    // ── Drop / Deliver ────────────────────────────────────────────────────────

    public void DropAllCandiesServer()
    {
        if (!IsServer) return;

        int count = _carriedCandies.Count;
        for (int i = 0; i < count; i++)
        {
            if (_carriedCandies[i] == null) continue;

            int   ring      = i / 8;
            int   ringSlot  = i % 8;
            int   ringCount = Mathf.Min(count - ring * 8, 8);
            float radius    = 1.8f + ring * 1.4f;
            float angle     = (2f * Mathf.PI / ringCount) * ringSlot;
            Vector3 offset  = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

            _carriedCandies[i].DropServer(transform.position + offset);
        }

        _carriedCandies.Clear();
        carriedCount.Value = 0;
    }

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

    // ── Candy count change ────────────────────────────────────────────────────

    private void OnCarriedCountChanged(int prev, int next)
    {
        if (!IsOwner) return;
        _currentPenalty = Mathf.Max(1f - next * speedPenaltyPerCandy, 0.3f);
        if (!_superSpeedActive) _pc.speedMultiplier = _currentPenalty;
        onCandyCountChanged?.Invoke(next);
    }

    // ── Super Speed ───────────────────────────────────────────────────────────

    private void HandleSuperSpeed()
    {
        if (Input.GetKeyDown(KeyCode.E) && _superSpeedTimer <= 0f && !_superSpeedActive)
            StartCoroutine(SuperSpeedRoutine());
    }

    private IEnumerator SuperSpeedRoutine()
    {
        _superSpeedActive      = true;
        superSpeedActive.Value = true;
        _pc.speedMultiplier    = _currentPenalty * superSpeedMultiplier;

        yield return new WaitForSeconds(superSpeedDuration);

        _pc.speedMultiplier    = _currentPenalty;
        _superSpeedActive      = false;
        superSpeedActive.Value = false;
        _superSpeedTimer       = superSpeedCooldown;
    }

    // ── Decoy ─────────────────────────────────────────────────────────────────

    private void HandleDecoy()
    {
        if (Input.GetKeyDown(KeyCode.Q) && _decoyCharges > 0 && _decoyTimer <= 0f)
        {
            SpawnDecoyRpc(transform.position, transform.forward,
                _stats.speedMultiplier * _pc.sprintSpeed);

            _decoyCharges--;
            onDecoyFired?.Invoke();
            onDecoyChargesChanged?.Invoke(_decoyCharges);

            if (_decoyCharges > 0)
            {
                _inDecoyWindow    = true;
                _decoyWindowTimer = decoyWindowDuration;
                onDecoyWindow?.Invoke(_decoyWindowTimer);
            }
            else
            {
                _inDecoyWindow    = false;
                _decoyWindowTimer = 0f;
                _decoyTimer       = decoyCooldown;
                onDecoyWindow?.Invoke(0f);
            }
        }
    }

    [Rpc(SendTo.Server)]
    private void SpawnDecoyRpc(Vector3 pos, Vector3 dir, float speed)
    {
        if (decoyPrefab == null) return;

        Vector3 roughPos  = pos + dir.normalized * 1.5f;
        Vector3 groundPos = ComputeGroundPos(roughPos, GetComponent<Collider>());

        GameObject obj = Instantiate(decoyPrefab, groundPos, Quaternion.LookRotation(dir));

        DecoyAI decoyAI = obj.GetComponent<DecoyAI>();
        if (decoyAI != null) decoyAI.ownerTeam = _stats.team.Value;

        obj.GetComponent<NetworkObject>()?.Spawn(true);

        if (decoyAI != null)
            decoyAI.syncedTeam.Value = _stats.team.Value;

        decoyAI?.InitializeMovement(dir, speed);
    }

    private Vector3 ComputeGroundPos(Vector3 fromPos, Collider skipCollider)
    {
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
            return new Vector3(fromPos.x, pivotY, fromPos.z);
        }

        Debug.LogWarning("[DecoySpawn] ComputeGroundPos: no surface found — spawning at rough position.");
        return fromPos;
    }

    // ── Magnet ────────────────────────────────────────────────────────────────

    private void HandleMagnet()
    {
        // Activate on R press when ready and not already active.
        if (Input.GetKeyDown(KeyCode.R) && !_magnetActive && _magnetCooldownTimer <= 0f)
            StartCoroutine(MagnetRoutine());

        // Tick the auto-pickup timer while magnet is running.
        if (_magnetActive)
        {
            _magnetPickupTimer -= Time.deltaTime;
            if (_magnetPickupTimer <= 0f)
            {
                _magnetPickupTimer = magnetPickupRate;
                TryMagnetPickup();
            }
        }
    }

    private IEnumerator MagnetRoutine()
    {
        // ── Activate ──────────────────────────────────────────────────────────
        _magnetActive         = true;
        magnetActive.Value    = true;          // replicated → 3P animator
        _magnetPickupTimer    = 0f;            // fire immediately on first frame
        onMagnetActiveChanged?.Invoke(true);
        onMagnetActivated?.Invoke();           // FP + 3P animation trigger

        yield return new WaitForSeconds(magnetDuration);

        // ── Deactivate ────────────────────────────────────────────────────────
        _magnetActive         = false;
        magnetActive.Value    = false;
        onMagnetActiveChanged?.Invoke(false);
        _magnetCooldownTimer  = magnetCooldown;
    }

    /// <summary>
    /// Finds the nearest on-ground candy inside magnetRadius and requests a pickup.
    /// Called every magnetPickupRate seconds while the magnet is active.
    /// </summary>
    private void TryMagnetPickup()
    {
        if (carriedCount.Value >= maxCarryCapacity) return;

        Collider[] hits = Physics.OverlapSphere(transform.position, magnetRadius, candyLayer);

        // Find nearest on-ground candy.
        NetworkObject best      = null;
        float         bestDist  = float.MaxValue;

        foreach (Collider hit in hits)
        {
            Candy candy = hit.GetComponent<Candy>();
            if (candy == null || !candy.IsOnGround()) continue;

            NetworkObject no   = hit.GetComponent<NetworkObject>();
            if (no == null) continue;

            float dist = Vector3.Distance(transform.position, hit.transform.position);
            if (dist < bestDist)
            {
                bestDist = dist;
                best     = no;
            }
        }

        if (best != null)
            PickupCandyRpc(best.NetworkObjectId);
    }

    // ── Cooldown ticks ────────────────────────────────────────────────────────

    private void TickCooldowns()
    {
        if (_superSpeedTimer > 0f)
        {
            _superSpeedTimer -= Time.deltaTime;
            onSuperSpeedCooldown?.Invoke(Mathf.Max(_superSpeedTimer, 0f));
        }

        if (_inDecoyWindow)
        {
            _decoyWindowTimer -= Time.deltaTime;
            onDecoyWindow?.Invoke(Mathf.Max(_decoyWindowTimer, 0f));

            if (_decoyWindowTimer <= 0f)
            {
                _inDecoyWindow    = false;
                _decoyWindowTimer = 0f;
                _decoyTimer       = decoyCooldown;
                onDecoyWindow?.Invoke(0f);
            }
        }

        if (_decoyTimer > 0f)
        {
            _decoyTimer -= Time.deltaTime;
            onDecoyCooldown?.Invoke(Mathf.Max(_decoyTimer, 0f));

            if (_decoyTimer <= 0f)
            {
                _decoyTimer   = 0f;
                _decoyCharges = decoyMaxCharges;
                onDecoyCooldown?.Invoke(0f);
                onDecoyChargesChanged?.Invoke(_decoyCharges);
            }
        }

        // ── Magnet cooldown ───────────────────────────────────────────────────
        if (_magnetCooldownTimer > 0f)
        {
            _magnetCooldownTimer -= Time.deltaTime;
            onMagnetCooldown?.Invoke(Mathf.Max(_magnetCooldownTimer, 0f));

            if (_magnetCooldownTimer <= 0f)
            {
                _magnetCooldownTimer = 0f;
                onMagnetCooldown?.Invoke(0f);   // signal "Ready!"
            }
        }
    }

    // ── Death ─────────────────────────────────────────────────────────────────

    private void OnDied()
    {
        if (IsServer) DropAllCandiesServer();
    }

    // ── Public helpers ────────────────────────────────────────────────────────

    public int  GetCarriedCount()    => carriedCount.Value;
    public bool IsSuperspeedActive() => _superSpeedActive;
    public bool IsMagnetActive()     => _magnetActive;

    // ── Gizmos ────────────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        // Manual pickup radius
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, pickupRadius);

        // Magnet radius
        Gizmos.color = _magnetActive
            ? new Color(0.2f, 0.8f, 1f, 0.9f)
            : new Color(0.2f, 0.8f, 1f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, magnetRadius);
    }
}