// CollectorController.cs — Sugar Rush
//
// ── PAUSE GUARD ADDED ─────────────────────────────────────────────────────────
//   BUG: Left-click candy pickup and all ability inputs (E / Q / R) remained
//   active while PauseMenuUI was open. PlayerController already blocked its
//   Update(), but CollectorController had no matching guard.
//
//   FIX: Added `if (PauseMenuUI.IsPaused) return;` at the top of Update(),
//   immediately after the existing IsOwner / IsDead guard.
//   Blocks HandlePickup(), HandleSuperSpeed(), HandleDecoy(), HandleMagnet(),
//   and TickCooldowns() while the pause overlay is visible.
//   No gameplay logic is changed.
//
//   PauseMenuUI runs at [DefaultExecutionOrder(-100)], CollectorController at
//   the default (0), so IsPaused is already set before this script reads it.
//
// ── ALL OTHER CHANGES ARE UNCHANGED FROM PREVIOUS VERSION ────────────────────

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
    [Tooltip("Set to the layer your AmmoPickup GameObjects are on.")]
    public LayerMask ammoLayer;
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
    public UnityEvent<int>   onCandyCountChanged      = new();
    public UnityEvent<float> onSuperSpeedCooldown      = new();
    public UnityEvent<bool>  onSuperSpeedActiveChanged = new();
    public UnityEvent<float> onDecoyCooldown           = new();
    public UnityEvent<int>   onDecoyChargesChanged     = new();
    public UnityEvent<float> onDecoyWindow             = new();
    public UnityEvent        onDecoyFired              = new();

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

    // ── HUD update throttle ───────────────────────────────────────────────────
    private float _hudTick;
    private const float HUD_TICK_RATE = 0.05f; // 20 Hz

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

        // ── FIX: block all input while the pause overlay is open ──────────────
        // PauseMenuUI runs at [DefaultExecutionOrder(-100)] so IsPaused is
        // already set before this script's default-order Update() reads it.
        if (PauseMenuUI.IsPaused) return;

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

        // ── Candy ─────────────────────────────────────────────────────────────
        Collider[] hits = Physics.OverlapSphere(transform.position, pickupRadius, candyLayer);
        foreach (Collider hit in hits)
        {
            Candy candy = hit.GetComponent<Candy>();
            NetworkObject no = hit.GetComponent<NetworkObject>();
            if (candy != null && candy.IsOnGround() && no != null)
            {
                PickupCandyRpc(no.NetworkObjectId);
                return; // one pickup per click
            }
        }

        // ── Ammo ──────────────────────────────────────────────────────────────
        Collider[] ammoHits = Physics.OverlapSphere(transform.position, pickupRadius, ammoLayer);
        foreach (Collider hit in ammoHits)
        {
            AmmoPickup ammo = hit.GetComponent<AmmoPickup>();
            NetworkObject no  = hit.GetComponent<NetworkObject>();
            if (ammo != null && no != null)
            {
                PickupAmmoRpc(no.NetworkObjectId);
                return; // one pickup per click
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

    [Rpc(SendTo.Server)]
    private void PickupAmmoRpc(ulong ammoId)
    {
        if (_stats.role.Value != PlayerRole.Collector) return;
        if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(ammoId, out var obj)) return;

        AmmoPickup ammo = obj.GetComponent<AmmoPickup>();
        if (ammo == null) return;

        ammo.PickupServer(_stats);
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
        GetComponent<PlayerMatchStats>()?.AddCandies(count);
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
        onSuperSpeedActiveChanged?.Invoke(true);

        yield return new WaitForSeconds(superSpeedDuration);

        _pc.speedMultiplier    = _currentPenalty;
        _superSpeedActive      = false;
        superSpeedActive.Value = false;
        _superSpeedTimer       = superSpeedCooldown;
        onSuperSpeedActiveChanged?.Invoke(false);
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
        if (Input.GetKeyDown(KeyCode.R) && !_magnetActive && _magnetCooldownTimer <= 0f)
            StartCoroutine(MagnetRoutine());

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
        _magnetActive         = true;
        magnetActive.Value    = true;
        _magnetPickupTimer    = 0f;
        onMagnetActiveChanged?.Invoke(true);
        onMagnetActivated?.Invoke();

        yield return new WaitForSeconds(magnetDuration);

        _magnetActive         = false;
        magnetActive.Value    = false;
        onMagnetActiveChanged?.Invoke(false);
        _magnetCooldownTimer  = magnetCooldown;
    }

    private void TryMagnetPickup()
    {
        if (carriedCount.Value >= maxCarryCapacity) return;

        Collider[] hits = Physics.OverlapSphere(transform.position, magnetRadius, candyLayer);

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
        _hudTick -= Time.deltaTime;
        bool pushHud = _hudTick <= 0f;
        if (pushHud) _hudTick = HUD_TICK_RATE;

        // Super Speed
        if (_superSpeedTimer > 0f)
        {
            _superSpeedTimer -= Time.deltaTime;
            if (_superSpeedTimer <= 0f)
            {
                _superSpeedTimer = 0f;
                onSuperSpeedCooldown?.Invoke(0f);
            }
            else if (pushHud) onSuperSpeedCooldown?.Invoke(_superSpeedTimer);
        }

        // Decoy window
        if (_inDecoyWindow)
        {
            _decoyWindowTimer -= Time.deltaTime;
            if (_decoyWindowTimer <= 0f)
            {
                _inDecoyWindow    = false;
                _decoyWindowTimer = 0f;
                _decoyTimer       = decoyCooldown;
                onDecoyWindow?.Invoke(0f);
            }
            else if (pushHud) onDecoyWindow?.Invoke(_decoyWindowTimer);
        }

        // Decoy cooldown
        if (_decoyTimer > 0f)
        {
            _decoyTimer -= Time.deltaTime;
            if (_decoyTimer <= 0f)
            {
                _decoyTimer   = 0f;
                _decoyCharges = decoyMaxCharges;
                onDecoyCooldown?.Invoke(0f);
                onDecoyChargesChanged?.Invoke(_decoyCharges);
            }
            else if (pushHud) onDecoyCooldown?.Invoke(_decoyTimer);
        }

        // Magnet cooldown
        if (_magnetCooldownTimer > 0f)
        {
            _magnetCooldownTimer -= Time.deltaTime;
            if (_magnetCooldownTimer <= 0f)
            {
                _magnetCooldownTimer = 0f;
                onMagnetCooldown?.Invoke(0f);
            }
            else if (pushHud) onMagnetCooldown?.Invoke(_magnetCooldownTimer);
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
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, pickupRadius);

        Gizmos.color = _magnetActive
            ? new Color(0.2f, 0.8f, 1f, 0.9f)
            : new Color(0.2f, 0.8f, 1f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, magnetRadius);
    }
}