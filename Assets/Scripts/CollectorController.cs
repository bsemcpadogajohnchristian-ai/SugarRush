// CollectorController.cs — Sugar Rush (UPDATED: Smoke Grenade auto-camera fix)
//
// ── WHAT CHANGED FROM PREVIOUS VERSION ───────────────────────────────────
//   BUG FIX — Smoke grenade never fired because playerCamera was null:
//
//   • Awake() now auto-finds the Camera component in children (includeInactive=true)
//     if playerCamera is not manually assigned in the Inspector.
//     This means you no longer NEED to wire the camera reference by hand,
//     but you still CAN if you want to be explicit.
//
//   • HandleSmokeGrenade() gained a PlayerRole.Collector guard so it can never
//     fire if the role somehow flips mid-game.
//
//   • No throw-direction logic changed — pressing 4 already threw toward the
//     camera forward. That behaviour is preserved exactly.

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

    [Header("Smoke Grenade")]
    [Tooltip("Drag your SmokeGrenade prefab here.")]
    public GameObject smokeGrenadePrefab;

    [Tooltip("Optional — leave empty and the script will find the Camera in children automatically.")]
    public Camera playerCamera;

    [Tooltip("How fast the grenade travels forward (m/s).")]
    public float smokeThrowForce    = 14f;

    [Tooltip("Upward component added to the throw velocity for the arc.")]
    public float smokeThrowArc      = 5.5f;

    [Tooltip("Maximum number of smoke grenades before the cooldown starts.")]
    public int   smokeMaxCharges    = 2;

    [Tooltip("Seconds to recharge both smoke grenades after both are used.")]
    public float smokeGrenadeCooldown = 25f;

    [Header("HUD events")]
    public UnityEvent<int>   onCandyCountChanged   = new();
    public UnityEvent<float> onSuperSpeedCooldown  = new();
    public UnityEvent<float> onDecoyCooldown       = new();
    public UnityEvent<int>   onDecoyChargesChanged = new();
    public UnityEvent<float> onDecoyWindow         = new();
    public UnityEvent        onDecoyFired          = new();

    public UnityEvent<float> onSmokeGrenadeCooldown = new();
    public UnityEvent<int>   onSmokeChargesChanged  = new();
    public UnityEvent        onSmokeGrenadeFired    = new();

    public NetworkVariable<int> carriedCount = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<bool> superSpeedActive = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private PlayerController _pc;
    private PlayerStats      _stats;

    private float _superSpeedTimer;
    private float _decoyTimer;
    private float _decoyWindowTimer;
    private bool  _inDecoyWindow;
    private int   _decoyCharges;
    private bool  _superSpeedActive;
    private float _currentPenalty = 1f;

    private int   _smokeCharges;
    private float _smokeTimer;

    private readonly List<Candy> _carriedCandies = new();

    private void Awake()
    {
        _pc    = GetComponent<PlayerController>();
        _stats = GetComponent<PlayerStats>();

        // ── FIX: Auto-find the camera if not wired in the Inspector ──────────
        // includeInactive:true is required because PlayerSetup disables the
        // camera GameObject for non-owners before this script runs.
        if (playerCamera == null)
            playerCamera = GetComponentInChildren<Camera>(true);

        if (playerCamera == null)
            Debug.LogWarning("[CollectorController] Could not find a Camera in children. " +
                             "Assign playerCamera manually in the Inspector if smoke grenade still fails.");
    }

    public override void OnNetworkSpawn()
    {
        carriedCount.OnValueChanged += OnCarriedCountChanged;
        if (IsServer) _stats.onDeath.AddListener(OnDied);
        if (!IsOwner) { enabled = false; return; }

        _decoyCharges = decoyMaxCharges;
        _smokeCharges = smokeMaxCharges;
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
        HandleSmokeGrenade();
        TickCooldowns();
    }

    // ── Pickup ────────────────────────────────────────────────────────────────

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

    // ── Smoke Grenade (Key 4) ─────────────────────────────────────────────────
    //
    // Press 4 → grenade is instantly thrown toward wherever the camera is aimed.
    // No "equip then throw" step; the throw happens in one keypress.

    private void HandleSmokeGrenade()
    {
        // Role guard — only Collectors can throw smoke.
        if (_stats.role.Value != PlayerRole.Collector) return;

        if (!Input.GetKeyDown(KeyCode.Alpha4)) return;
        if (_smokeCharges <= 0 || _smokeTimer > 0f) return;

        // ── Camera null-check with helpful message ────────────────────────────
        if (playerCamera == null)
        {
            Debug.LogError("[CollectorController] HandleSmokeGrenade: playerCamera is still null. " +
                           "The auto-find in Awake() failed — assign it manually in the Inspector.");
            return;
        }

        // ── FIX: Flatten forward for spawn position ───────────────────────────
        //
        // PROBLEM (camera bob when throwing):
        //   The old code used playerCamera.transform.forward directly for the
        //   spawn offset. Camera forward includes vertical pitch. Looking down
        //   even slightly pulls the spawn point DOWN into the CharacterController
        //   capsule. The grenade Rigidbody then depenetrates outward, physically
        //   nudging the CC — causing _isGrounded to flicker for one frame, which
        //   triggers the landing-camera-bob in PlayerController.
        //
        // FIX:
        //   Flatten the forward vector (zero out Y, renormalize) so the spawn
        //   point is always at a fixed height above the player's root regardless
        //   of where the camera is pitched. 1 m forward puts it well outside the
        //   CC capsule (typical radius 0.35–0.5 m). The throw VELOCITY still uses
        //   the real camera forward (with pitch) so the grenade flies toward the
        //   crosshair as expected.
        Vector3 flatForward = playerCamera.transform.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.001f)
            flatForward = transform.forward;   // looking straight up/down fallback
        flatForward.Normalize();

        // Spawn at shoulder height, 1 m in front — always outside the capsule.
        Vector3 spawnPos = transform.position
                         + Vector3.up  * 1.5f
                         + flatForward * 1.0f;

        // Throw direction still follows actual camera aim (pitch included).
        // This is what makes the grenade fly toward wherever you're aiming.
        Vector3 velocity = playerCamera.transform.forward * smokeThrowForce
                         + Vector3.up                     * smokeThrowArc;

        ThrowSmokeGrenadeRpc(spawnPos, velocity);

        _smokeCharges--;
        onSmokeGrenadeFired?.Invoke();
        onSmokeChargesChanged?.Invoke(_smokeCharges);

        // Cooldown only kicks in once ALL charges are spent.
        if (_smokeCharges <= 0)
            _smokeTimer = smokeGrenadeCooldown;
    }

    [Rpc(SendTo.Server)]
    private void ThrowSmokeGrenadeRpc(Vector3 spawnPos, Vector3 velocity)
    {
        if (smokeGrenadePrefab == null)
        {
            Debug.LogError("[CollectorController] smokeGrenadePrefab is not assigned!");
            return;
        }

        Quaternion rot = velocity.sqrMagnitude > 0.01f
            ? Quaternion.LookRotation(velocity.normalized)
            : Quaternion.identity;

        GameObject obj = Instantiate(smokeGrenadePrefab, spawnPos, rot);
        NetworkObject no = obj.GetComponent<NetworkObject>();

        if (no == null)
        {
            Debug.LogError("[CollectorController] smokeGrenadePrefab is missing a NetworkObject!");
            Destroy(obj);
            return;
        }

        no.Spawn(true);

        // ── FIX: Ignore collision between grenade and its thrower ─────────────
        //
        // Belt-and-suspenders safety net on top of the spawn-position fix.
        // If the grenade ever ends up close to the player (e.g. spawning near
        // a wall pushes it back), it must not be able to physically interact
        // with the thrower's own colliders (including the CharacterController,
        // which inherits from Collider). Without this, Rigidbody depenetration
        // can nudge the CC and re-trigger the landing-camera-bob.
        Collider grenadeCol = obj.GetComponent<Collider>();
        if (grenadeCol != null)
            foreach (Collider pc in GetComponentsInChildren<Collider>(true))
                Physics.IgnoreCollision(grenadeCol, pc, true);

        obj.GetComponent<SmokeGrenade>()?.Initialize(velocity, _stats.team.Value);
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

        if (_smokeTimer > 0f)
        {
            _smokeTimer -= Time.deltaTime;
            onSmokeGrenadeCooldown?.Invoke(Mathf.Max(_smokeTimer, 0f));

            if (_smokeTimer <= 0f)
            {
                _smokeTimer   = 0f;
                _smokeCharges = smokeMaxCharges;
                onSmokeGrenadeCooldown?.Invoke(0f);
                onSmokeChargesChanged?.Invoke(_smokeCharges);
            }
        }
    }

    private void OnDied() { if (IsServer) DropAllCandiesServer(); }

    public int  GetCarriedCount()    => carriedCount.Value;
    public bool IsSuperspeedActive() => _superSpeedActive;

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, pickupRadius);
    }
}