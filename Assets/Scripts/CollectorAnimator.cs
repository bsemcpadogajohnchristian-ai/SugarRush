// CollectorAnimator.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Drives the Collector body Animator on ALL clients (owner + non-owner).
//
// ── WHY MonoBehaviour NOT NetworkBehaviour ────────────────────────────────
//   NetworkBehaviour child components (no NetworkObject on the same GO) have
//   NGO lifecycle timing issues: if Body_Collector is inactive for even one
//   frame during the parent NetworkObject's spawn sequence, OnNetworkSpawn
//   may never fire — leaving playerStats null — causing Update() to silently
//   return early on every non-owner client.  Using plain MonoBehaviour with
//   Start() is safe: by the time Start() runs the parent NetworkObject is
//   fully spawned and all NetworkVariables are readable.
//
// ── POSITION DELTA VELOCITY ───────────────────────────────────────────────
//   PlayerController.Update() is disabled on non-owners (they never call
//   CC.Move), so CC.velocity is always ZERO on those clients.  We measure
//   (root.position - prevPos) / deltaTime instead — this works on every
//   client because NetworkTransform updates root.position every frame.
//
// ── ANIMATOR PARAMETERS (exact names, case-sensitive) ─────────────────────
//   Float   "Speed"        — 0=Idle  1=Walk  2=MedRun    (1D Blend Tree)
//   Float   "CrouchMoveX" — local X velocity when crouching  (-1 to 1)
//   Float   "CrouchMoveY" — local Z velocity when crouching  (-1 to 1)
//   Bool    "IsCrouching"  — standing ↔ CrouchMovement blend tree
//   Bool    "IsSuperspeed" — E-skill active → FastRun state
//   Bool    "IsAirborne"   — true while jumping or falling
//   Bool    "IsPickingUp"  — true for pickupDuration after candy pickup
//   Bool    "IsDead"       — locks into Death state
//   Trigger "Die"          — fires once on death
// ─────────────────────────────────────────────────────────────────────────

using UnityEngine;

[RequireComponent(typeof(Animator))]
public class CollectorAnimator : MonoBehaviour
{
    [Header("References (auto-found in Awake if left empty)")]
    public PlayerController    playerController;
    public CollectorController collectorController;
    public PlayerStats         playerStats;

    [Header("Standing speed thresholds (m/s)")]
    [Tooltip("Speed below this = Idle.  0.5 prevents idle-jitter from micro-movements.")]
    public float walkThreshold = 0.5f;

    [Tooltip("Speed at or above this = Medium Run for non-owner clients. " +
             "Set between walk and sprint speeds.  Default 7 works with all carry penalties.")]
    public float runThreshold  = 7f;

    [Header("Crouch blend normalisation")]
    [Tooltip("Max crouch speed in m/s used to normalise CrouchMoveX/Y to -1..1. " +
             "crouchSpeed(2.5) × collectorMult(1.3) ≈ 3.25 — keep slightly above.")]
    public float crouchMaxSpeed = 3.5f;

    [Header("Airborne detection")]
    [Tooltip("Y velocity (m/s) above which a non-owner client is considered airborne. " +
             "Keep small (0.8) to catch the jump start without false triggers from slopes.")]
    public float airborneYThreshold = 0.8f;

    [Header("Pick-up")]
    [Tooltip("Seconds the PickUpItem clip plays after a successful candy pickup.")]
    public float pickupDuration = 0.6f;

    // ── Animator parameter hashes (faster than string every frame) ────────
    private static readonly int H_Speed        = Animator.StringToHash("Speed");
    private static readonly int H_CrouchX      = Animator.StringToHash("CrouchMoveX");
    private static readonly int H_CrouchY      = Animator.StringToHash("CrouchMoveY");
    private static readonly int H_IsCrouching  = Animator.StringToHash("IsCrouching");
    private static readonly int H_IsSuperspeed = Animator.StringToHash("IsSuperspeed");
    private static readonly int H_IsAirborne   = Animator.StringToHash("IsAirborne");
    private static readonly int H_IsPickingUp  = Animator.StringToHash("IsPickingUp");
    private static readonly int H_IsDead       = Animator.StringToHash("IsDead");
    private static readonly int H_Die          = Animator.StringToHash("Die");

    private Animator  _anim;
    private Transform _root;            // Player root — what NetworkTransform moves
    private Vector3   _prevPos;         // root position last frame
    private float     _prevRootY;       // root Y last frame for Y-velocity calculation
    private float     _pickupTimer;
    private int       _lastCarriedCount;
    private bool      _wasDead;
    private bool      _subscribedToDeath;

    // ── Lifecycle ──────────────────────────────────────────────────────────

    private void Awake()
    {
        _anim = GetComponent<Animator>();
        _root = transform.root;   // Body_Collector child → root = Player

        // Auto-find on parent if fields were left empty in Inspector
        if (playerController    == null) playerController    = GetComponentInParent<PlayerController>();
        if (collectorController == null) collectorController = GetComponentInParent<CollectorController>();
        if (playerStats         == null) playerStats         = GetComponentInParent<PlayerStats>();
    }

    private void Start()
    {
        // Subscribe to death NetworkVariable here — by Start() the parent
        // NetworkObject is fully spawned so playerStats.IsOwner etc. are valid.
        TrySubscribeToDeath();

        if (collectorController != null)
            _lastCarriedCount = collectorController.GetCarriedCount();

        if (_root != null)
        {
            _prevPos   = _root.position;
            _prevRootY = _root.position.y;
        }
    }

    // Called every time Body_Collector is re-enabled by PlayerSetup.ApplyRole
    private void OnEnable() => TrySubscribeToDeath();

    private void OnDestroy()
    {
        if (_subscribedToDeath && playerStats != null)
            playerStats.isDead.OnValueChanged -= OnDeadChanged;
    }

    private void TrySubscribeToDeath()
    {
        if (_subscribedToDeath || playerStats == null) return;
        playerStats.isDead.OnValueChanged += OnDeadChanged;
        _subscribedToDeath = true;
    }

    // ── Per-frame update ───────────────────────────────────────────────────

    private void Update()
    {
        if (_anim == null) return;

        // Late-find playerStats if Awake ran before NGO fully set up the parent
        if (playerStats == null)
        {
            playerStats = GetComponentInParent<PlayerStats>();
            TrySubscribeToDeath();
            if (playerStats == null) return;
        }

        if (_root == null)
        {
            _root      = transform.root;
            _prevPos   = _root.position;
            _prevRootY = _root.position.y;
        }

        // ── 1. Position-delta velocity ────────────────────────────────────────
        Vector3 worldVel  = (_root.position - _prevPos) / Time.deltaTime;
        float   hSpeed    = new Vector3(worldVel.x, 0f, worldVel.z).magnitude;
        float   yVelocity = (_root.position.y - _prevRootY) / Time.deltaTime;
        _prevPos   = _root.position;
        _prevRootY = _root.position.y;

        // ── 2. Dead: freeze all other logic ───────────────────────────────────
        if (playerStats.IsDead())
        {
            _anim.SetBool(H_IsDead, true);
            return;
        }
        _anim.SetBool(H_IsDead, false);

        bool isOwner = playerStats.IsOwner;

        // ── 3. Pickup detection via carriedCount NetworkVariable ──────────────
        // NetworkVariable is replicated to all clients so the pickup animation
        // plays on every machine, not only on the owner.
        if (collectorController != null)
        {
            int now = collectorController.GetCarriedCount();
            if (now > _lastCarriedCount) _pickupTimer = pickupDuration;
            _lastCarriedCount = now;
        }
        if (_pickupTimer > 0f) _pickupTimer -= Time.deltaTime;
        _anim.SetBool(H_IsPickingUp, _pickupTimer > 0f);

        // ── 4. Superspeed E-skill → FastRun state ─────────────────────────────
        bool isSuperspeed = collectorController != null && collectorController.IsSuperspeedActive();
        _anim.SetBool(H_IsSuperspeed, isSuperspeed);

        // ── 5. Airborne (jump + fall) ─────────────────────────────────────────
        // Owner:     read directly from PlayerController (exact physics state)
        // Non-owner: PlayerController is disabled, infer from Y position delta
        bool isAirborne = isOwner && playerController != null
            ? playerController.IsJumping() || playerController.IsFalling()
            : Mathf.Abs(yVelocity) > airborneYThreshold;

        _anim.SetBool(H_IsAirborne, isAirborne);

        // ── 6. Crouch + 2D blend tree ─────────────────────────────────────────
        // Owner path is exact.  Non-owner path has no reliable way to read the
        // crouch flag so it falls back to a speed-based heuristic.
        bool isCrouching = isOwner && playerController != null
            ? playerController.IsCrouching()
            : hSpeed > walkThreshold && hSpeed < crouchMaxSpeed && !isSuperspeed && !isAirborne;

        _anim.SetBool(H_IsCrouching, isCrouching);

        if (isCrouching)
        {
            // Local axes: X = strafe left/right, Z = forward/back
            Vector3 local = _root.InverseTransformDirection(worldVel);
            _anim.SetFloat(H_CrouchX, Mathf.Clamp(local.x / crouchMaxSpeed, -1f, 1f), 0.1f, Time.deltaTime);
            _anim.SetFloat(H_CrouchY, Mathf.Clamp(local.z / crouchMaxSpeed, -1f, 1f), 0.1f, Time.deltaTime);
        }

        // ── 7. Standing Speed parameter (1D blend tree) ───────────────────────
        // Reset to 0 while airborne so the blend tree doesn't play a walk/run
        // clip during the jump — the Airborne/Jump state handles that.
        float speedParam;
        if (hSpeed < walkThreshold || isAirborne || isCrouching)
        {
            speedParam = 0f;
        }
        else if (isOwner && playerController != null)
        {
            speedParam = playerController.IsSprinting() ? 2f : 1f;
        }
        else
        {
            speedParam = hSpeed >= runThreshold ? 2f : 1f;
        }
        _anim.SetFloat(H_Speed, speedParam, 0.1f, Time.deltaTime);
    }

    // ── Death callback — fires on ALL clients when isDead changes ─────────

    private void OnDeadChanged(bool prev, bool next)
    {
        if (next && !_wasDead)
        {
            _anim.SetTrigger(H_Die);
            _anim.SetBool(H_IsDead, true);
            _wasDead = true;
        }
        else if (!next)
        {
            _anim.ResetTrigger(H_Die);
            _anim.SetBool(H_IsDead, false);
            _wasDead = false;
        }
    }
}