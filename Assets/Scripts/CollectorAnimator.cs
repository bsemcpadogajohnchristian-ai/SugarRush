// CollectorAnimator.cs — Sugar Rush
//
// ── MAGNET ABILITY ADDED ──────────────────────────────────────────────────────
//   • Added H_IsMagnet animator hash (bool "IsMagnet").
//   • In Update(), polls collectorController.magnetActive.Value each frame
//     and drives H_IsMagnet — mirrors exactly how H_IsSuperspeed polls
//     superSpeedActive.Value.
//   • No subscription overhead: NV polling is already the established pattern
//     in this file for networked state that all clients need.
//
// ── ANIMATOR SETUP REQUIRED ───────────────────────────────────────────────────
//   In your 3P Collector Animator Controller add:
//     • Bool parameter "IsMagnet" — drive a looping overlay layer or blend tree
//       weight change while the magnet is active (e.g. arms-raised idle overlay).
//   The parameter is optional — the script won't crash if it's absent.

using UnityEngine;

[DefaultExecutionOrder(50)]
[RequireComponent(typeof(Animator))]
public class CollectorAnimator : MonoBehaviour
{
    [Header("References (auto-found in Awake if left empty)")]
    public PlayerController    playerController;
    public CollectorController collectorController;
    public PlayerStats         playerStats;

    [Header("Standing speed thresholds")]
    [HideInInspector]
    public float walkThreshold = 0.5f;   // legacy — no longer read

    [Header("Speed blend")]
    [Tooltip("EMA factor for the Speed float sent to the 1D blend tree.")]
    public float speedSmoothFactor = 12f;

    [Header("Input dead zone (owner only)")]
    [Tooltip("Raw Input.GetAxis dead zone. 0.15 matches Unity's default.")]
    public float inputDeadZone = 0.15f;

    [Header("Crouch blend normalisation")]
    [HideInInspector]
    public float crouchMaxSpeed = 3.0f;  // legacy — no longer read

    [Header("Velocity smoothing")]
    public float hSpeedSmoothFactor = 12f;
    public float crouchSmoothFactor = 7f;

    [Header("Pick-up")]
    [Tooltip("Duration (seconds) the PickUpItem clip plays after a successful pickup.")]
    public float pickupDuration = 0.6f;

    // ── Animator parameter hashes ─────────────────────────────────────────────

    private static readonly int H_Speed        = Animator.StringToHash("Speed");
    private static readonly int H_CrouchX      = Animator.StringToHash("CrouchMoveX");
    private static readonly int H_CrouchY      = Animator.StringToHash("CrouchMoveY");
    private static readonly int H_IsCrouching  = Animator.StringToHash("IsCrouching");
    private static readonly int H_IsSuperspeed = Animator.StringToHash("IsSuperspeed");
    private static readonly int H_IsPickingUp  = Animator.StringToHash("IsPickingUp");
    private static readonly int H_IsDead       = Animator.StringToHash("IsDead");
    private static readonly int H_Die          = Animator.StringToHash("Die");
    private static readonly int H_JumpTrigger  = Animator.StringToHash("Jump");
    private static readonly int H_IsMagnet     = Animator.StringToHash("IsMagnet"); // ← NEW

    // ── Runtime state ─────────────────────────────────────────────────────────

    private Animator  _anim;
    private Transform _root;
    private Vector3   _prevPos;

    private float _smoothedSpeed;
    private float _smoothedHSpeed;
    private float _smoothedLocalX;
    private float _smoothedLocalZ;
    private float _pickupTimer;
    private int   _lastCarriedCount;
    private bool  _wasDead;
    private bool  _subscribedToDeath;
    private bool  _subscribedToRespawn;
    private int   _lastJumpSequence;
    private bool  _wasSuperspeed;

    private const float TELEPORT_THRESHOLD = 3f;

    private float       _movingOffDebounce;
    private const float MOVING_OFF_DEBOUNCE = 0.12f;

    private float       _sprintOffDebounce;
    private const float SPRINT_OFF_DEBOUNCE = 0.10f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        _anim = GetComponent<Animator>();
        _root = transform.root;

        if (playerController    == null) playerController    = GetComponentInParent<PlayerController>();
        if (collectorController == null) collectorController = GetComponentInParent<CollectorController>();
        if (playerStats         == null) playerStats         = GetComponentInParent<PlayerStats>();
    }

    private void Start()
    {
        TrySubscribeToDeath();
        TrySubscribeToRespawn();

        if (collectorController != null)
            _lastCarriedCount = collectorController.GetCarriedCount();

        if (_root != null) _prevPos = _root.position;

        if (playerStats != null && playerStats.IsDead())
            ApplyDeathState();

        if (playerStats != null)
            _lastJumpSequence = playerStats.jumpSequence.Value;
    }

    private void OnEnable()
    {
        TrySubscribeToDeath();
        TrySubscribeToRespawn();

        if (_root != null) _prevPos = _root.position;
        else if (transform.root != null) { _root = transform.root; _prevPos = _root.position; }

        ResetRuntimeState();

        if (playerStats != null)
            _lastJumpSequence = playerStats.jumpSequence.Value;

        if (_anim != null)
            _anim.ResetTrigger(H_JumpTrigger);

        if (playerStats != null && playerStats.IsDead() && _anim != null)
            ApplyDeathState();
    }

    private void OnDestroy()
    {
        if (playerStats == null) return;
        if (_subscribedToDeath)   playerStats.isDead.OnValueChanged -= OnDeadChanged;
        if (_subscribedToRespawn) playerStats.onRespawn.RemoveListener(OnRespawn);
    }

    private void TrySubscribeToDeath()
    {
        if (_subscribedToDeath || playerStats == null) return;
        playerStats.isDead.OnValueChanged += OnDeadChanged;
        _subscribedToDeath = true;
    }

    private void TrySubscribeToRespawn()
    {
        if (_subscribedToRespawn || playerStats == null) return;
        playerStats.onRespawn.AddListener(OnRespawn);
        _subscribedToRespawn = true;
    }

    // ── Runtime reset ─────────────────────────────────────────────────────────

    private void ResetRuntimeState()
    {
        _smoothedSpeed    = 0f;
        _smoothedHSpeed   = 0f;
        _smoothedLocalX   = 0f;
        _smoothedLocalZ   = 0f;
        _wasSuperspeed    = false;
        _movingOffDebounce = 0f;
        _sprintOffDebounce = 0f;
    }

    // ── Update ────────────────────────────────────────────────────────────────

    private void Update()
    {
        if (_anim == null) return;

        if (playerStats == null)
        {
            playerStats = GetComponentInParent<PlayerStats>();
            TrySubscribeToDeath();
            TrySubscribeToRespawn();
            if (playerStats == null) return;
        }

        if (_root == null)
        {
            _root    = transform.root;
            _prevPos = _root.position;
        }

        // Teleport detection.
        float jumpDist = UnityEngine.Vector3.Distance(_root.position, _prevPos);
        if (jumpDist > TELEPORT_THRESHOLD)
        {
            _prevPos = _root.position;
            ResetRuntimeState();
        }

        UnityEngine.Vector3 worldVel = (_root.position - _prevPos) / UnityEngine.Time.deltaTime;
        _prevPos = _root.position;

        float rawHSpeed = new UnityEngine.Vector3(worldVel.x, 0f, worldVel.z).magnitude;
        _smoothedHSpeed = UnityEngine.Mathf.Lerp(_smoothedHSpeed, rawHSpeed,
            hSpeedSmoothFactor * UnityEngine.Time.deltaTime);

        // ── Dead ──────────────────────────────────────────────────────────────
        if (playerStats.IsDead())
        {
            _anim.SetBool(H_IsDead,       true);
            _anim.SetFloat(H_Speed,       0f);
            _anim.SetFloat(H_CrouchX,     0f);
            _anim.SetFloat(H_CrouchY,     0f);
            _anim.SetBool(H_IsCrouching,  false);
            _anim.SetBool(H_IsSuperspeed, false);
            _anim.SetBool(H_IsPickingUp,  false);
            _anim.SetBool(H_IsMagnet,     false);   // ← NEW: clear on death
            _smoothedSpeed = 0f;
            _wasSuperspeed = false;
            return;
        }
        _anim.SetBool(H_IsDead, false);

        bool isOwner = playerStats.IsOwner;

        // ── Pick-up pulse ─────────────────────────────────────────────────────
        if (collectorController != null)
        {
            int now = collectorController.GetCarriedCount();
            if (now > _lastCarriedCount) _pickupTimer = pickupDuration;
            _lastCarriedCount = now;
        }
        if (_pickupTimer > 0f) _pickupTimer -= UnityEngine.Time.deltaTime;
        _anim.SetBool(H_IsPickingUp, _pickupTimer > 0f);

        // ── Super-speed ───────────────────────────────────────────────────────
        bool isSuperspeed = collectorController != null
            && collectorController.superSpeedActive.Value
            && _smoothedHSpeed >= 0.5f;
        _anim.SetBool(H_IsSuperspeed, isSuperspeed);

        // ── Magnet (NEW) ──────────────────────────────────────────────────────
        // Poll the replicated NetworkVariable — identical pattern to superSpeedActive.
        bool isMagnet = collectorController != null && collectorController.magnetActive.Value;
        _anim.SetBool(H_IsMagnet, isMagnet);

        // ── Jump trigger ──────────────────────────────────────────────────────
        int currentSeq = playerStats.jumpSequence.Value;
        if (currentSeq != _lastJumpSequence)
        {
            _lastJumpSequence = currentSeq;
            _anim.ResetTrigger(H_JumpTrigger);
            _anim.SetTrigger(H_JumpTrigger);
        }

        // ── Crouch ────────────────────────────────────────────────────────────
        bool isCrouching = playerStats.isCrouching.Value;
        _anim.SetBool(H_IsCrouching, isCrouching);

        if (isCrouching)
        {
            float targetX, targetZ;

            if (isOwner)
            {
                targetX = UnityEngine.Input.GetAxis("Horizontal");
                targetZ = UnityEngine.Input.GetAxis("Vertical");
            }
            else
            {
                UnityEngine.Vector2 dir = playerStats.localMoveDir.Value;
                targetX = dir.x;
                targetZ = dir.y;
            }

            _smoothedLocalX = UnityEngine.Mathf.Lerp(_smoothedLocalX, targetX,
                crouchSmoothFactor * UnityEngine.Time.deltaTime);
            _smoothedLocalZ = UnityEngine.Mathf.Lerp(_smoothedLocalZ, targetZ,
                crouchSmoothFactor * UnityEngine.Time.deltaTime);

            _anim.SetFloat(H_CrouchX, _smoothedLocalX);
            _anim.SetFloat(H_CrouchY, _smoothedLocalZ);
        }
        else
        {
            _smoothedLocalX = 0f;
            _smoothedLocalZ = 0f;
            _anim.SetFloat(H_CrouchX, 0f);
            _anim.SetFloat(H_CrouchY, 0f);
        }

        // ── Locomotion speed ──────────────────────────────────────────────────
        float targetSpeed;

        if (isCrouching)
        {
            targetSpeed = 0f;
        }
        else if (isOwner && playerController != null)
        {
            float h        = UnityEngine.Input.GetAxis("Horizontal");
            float v        = UnityEngine.Input.GetAxis("Vertical");
            bool  hasInput = UnityEngine.Mathf.Abs(h) > inputDeadZone
                          || UnityEngine.Mathf.Abs(v) > inputDeadZone;

            if (!hasInput)
                targetSpeed = 0f;
            else
                targetSpeed = (playerController.IsSprinting() || isSuperspeed) ? 2f : 1f;
        }
        else
        {
            bool nvIsMoving = playerStats.isMovingNV.Value;
            if (nvIsMoving) _movingOffDebounce = MOVING_OFF_DEBOUNCE;
            else if (_movingOffDebounce > 0f) _movingOffDebounce -= UnityEngine.Time.deltaTime;
            bool isMoving = nvIsMoving || _movingOffDebounce > 0f;

            bool nvIsSprinting = playerStats.isSprinting.Value;
            if (nvIsSprinting) _sprintOffDebounce = SPRINT_OFF_DEBOUNCE;
            else if (_sprintOffDebounce > 0f) _sprintOffDebounce -= UnityEngine.Time.deltaTime;
            bool isSprintingDebounced = nvIsSprinting || _sprintOffDebounce > 0f;

            targetSpeed = isMoving ? ((isSprintingDebounced || isSuperspeed) ? 2f : 1f) : 0f;
        }

        _smoothedSpeed = UnityEngine.Mathf.Lerp(_smoothedSpeed, targetSpeed,
            speedSmoothFactor * UnityEngine.Time.deltaTime);
        _anim.SetFloat(H_Speed, _smoothedSpeed);

        _wasSuperspeed = isSuperspeed;
    }

    // ── Animator Move (empty — root motion not used) ──────────────────────────

    private void OnAnimatorMove() { }

    // ── Death / Respawn callbacks ─────────────────────────────────────────────

    private void OnDeadChanged(bool prev, bool next)
    {
        if (next && !_wasDead)  ApplyDeathState();
        else if (!next)
        {
            _anim.ResetTrigger(H_Die);
            _anim.SetBool(H_IsDead, false);
            _wasDead = false;
        }
    }

    private void ApplyDeathState()
    {
        _anim.SetTrigger(H_Die);
        _anim.SetBool(H_IsDead, true);
        _wasDead = true;
    }

    private void OnRespawn()
    {
        _pickupTimer      = 0f;
        _lastCarriedCount = collectorController != null
            ? collectorController.GetCarriedCount() : 0;

        if (playerStats != null)
            _lastJumpSequence = playerStats.jumpSequence.Value;

        ResetRuntimeState();

        if (_anim != null)
        {
            _anim.ResetTrigger(H_JumpTrigger);
            _anim.SetBool(H_IsMagnet, false);   // ← NEW: clear magnet on respawn
        }
    }
}