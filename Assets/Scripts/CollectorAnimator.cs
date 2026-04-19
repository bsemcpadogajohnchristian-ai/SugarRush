using UnityEngine;

// ── FIX LOG (this version) ────────────────────────────────────────────────
//
//   BUG 1 FIX — LOCOMOTION_AIRBORNE_BUFFER increased from 0.05 s → 0.15 s.
//     0.05 s = only 3 frames at 60 fps. Terrain seams or mesh-tile joints can
//     keep HasGroundContact() false for 4-6 frames, expiring the buffer and
//     snapping Speed to 0. 0.15 s = 9 frames, matching ShooterAnimator.
//
//   BUG 2 FIX — Owner rawAirborne: HasGroundContact() → IsGrounded().
//     HasGroundContact() uses a 0.08 m (skin-width) radius sphere — designed
//     specifically for zero-frame-accurate landing detection. That precision
//     is the WRONG tool for the IsGrounded animator bool, which drives state
//     machine transitions. A momentary gap between the capsule bottom and the
//     floor (terrain seam, stair tread edge, mesh rounding) returns false for
//     1-3 frames → H_IsGrounded flips false → animator can interrupt the
//     Walk/Run state with a Jump transition → locomotion snaps to idle.
//     IsGrounded() uses the generous 0.3 m sphere from PlayerController,
//     which never flickers on normal walkable terrain.
//
//   BUG 3 FIX — Owner locomotion buffer: mirrors ShooterAnimator exactly.
//     Previous code reset the buffer on !rawAirborne (buggy HasGroundContact
//     source). Now uses isGroundedNV.Value (authoritative 0.3 m sphere from
//     PlayerController._isGrounded) — the same stable source ShooterAnimator
//     has used since its locomotion fix. This eliminates flicker even on
//     uneven terrain.
//
//   BUG 4 FIX — Owner + non-owner speed: input-based / NV-based, not delta.
//     Previous: speed gated by _smoothedHSpeed < walkThreshold (position-delta
//     derived). For the owner this is stable but adds unnecessary lag; for
//     non-owners it oscillates at NetworkTransform update boundaries (30 Hz),
//     causing the blend tree to flicker between Idle and Walk every 2 frames.
//     Fix mirrors ShooterAnimator:
//       • Owner   → direct Input.GetAxis with inputDeadZone guard.
//       • Non-owner → isMovingNV (NV written by PlayerController, never
//                     oscillates) + MOVING_OFF_DEBOUNCE to prevent brief stops.
//
//   BUG 5 FIX — Animator.SetFloat dampTime replaced with explicit EMA.
//     Unity's dampTime overload uses Mathf.SmoothDamp internally, which is a
//     spring-damper that CAN OVERSHOOT. When the Speed float crosses the
//     Idle/Walk threshold (1.0) or Walk/Run threshold (2.0) by even 0.001 for
//     a single frame the blend tree snaps to the adjacent clip; the spring
//     pulls back, overshoots again → flickering between clips.
//     Fix: compute targetSpeed (0, 1, or 2) and apply a plain Mathf.Lerp EMA
//     into _smoothedSpeed. Lerp never overshoots, making threshold crossings
//     smooth and exactly once. Mirrors ShooterAnimator's existing approach.
//
// ─────────────────────────────────────────────────────────────────────────

[DefaultExecutionOrder(50)]    
[RequireComponent(typeof(Animator))]
public class CollectorAnimator : MonoBehaviour
{
    [Header("References (auto-found in Awake if left empty)")]
    public PlayerController    playerController;
    public CollectorController collectorController;
    public PlayerStats         playerStats;

    [Header("Standing speed thresholds")]
    [Tooltip("BUG 4 FIX: owner speed now uses direct input (inputDeadZone below).\n" +
             "Non-owner speed now uses isMovingNV. This field is kept for Inspector\n" +
             "compatibility only and is no longer used.")]
    [HideInInspector]
    public float walkThreshold = 0.5f;   // legacy — no longer read

    [Header("Speed blend")]
    [Tooltip("EMA factor for the Speed float sent to the 1D blend tree.\n" +
             "BUG 5 FIX: replaces Unity's dampTime (SmoothDamp) which could overshoot\n" +
             "blend-tree thresholds and cause Idle/Walk flickering.\n" +
             "12 is a good default; range 8-15.")]
    public float speedSmoothFactor = 12f;

    [Header("Input dead zone (owner only)")]
    [Tooltip("Raw Input.GetAxis dead zone. Axes below this are treated as no-input.\n" +
             "0.15 matches Unity's default Input Manager dead zone.")]
    public float inputDeadZone = 0.15f;

    [Header("Crouch blend normalisation")]
    [Tooltip("BUG 4 FIX: non-owner crouch direction now uses localMoveDir NV and no\n" +
             "longer needs speed normalisation. Kept for Inspector compat only.")]
    [HideInInspector]
    public float crouchMaxSpeed = 3.0f;  // legacy — no longer read

    [Header("Airborne detection")]
    [Tooltip("Smoothed Y velocity (m/s) above which a non-owner is considered airborne.")]
    public float airborneYThreshold = 0.6f;

    [Tooltip("Seconds to hold IsAirborne = true after the signal drops (non-owners only).\n" +
             "Bridges NT dead frames (30 Hz) so Jump_Start does not stutter.")]
    public float airborneHoldTime = 0.05f;

    [Header("Velocity smoothing")]
    [Tooltip("EMA factor for horizontal speed — used ONLY for non-owner airborne detection.\n" +
             "No longer drives walk/idle switching (that now uses isMovingNV).")]
    public float hSpeedSmoothFactor = 12f;

    [Tooltip("EMA factor for Y velocity — non-owner airborne detection. ~20 recommended.")]
    public float yVelSmoothFactor = 20f;

    [Tooltip("EMA factor for CrouchMoveX/Y on owner AND non-owner.\n" +
             "6-8 is a good range. Too high = shaking; too low = laggy direction.")]
    public float crouchSmoothFactor = 7f;

    [Header("Pick-up")]
    [Tooltip("Duration (seconds) the PickUpItem clip plays after a successful pickup.")]
    public float pickupDuration = 0.6f;

    
    private static readonly int H_Speed        = Animator.StringToHash("Speed");
    private static readonly int H_CrouchX      = Animator.StringToHash("CrouchMoveX");
    private static readonly int H_CrouchY      = Animator.StringToHash("CrouchMoveY");
    private static readonly int H_IsCrouching  = Animator.StringToHash("IsCrouching");
    private static readonly int H_IsSuperspeed = Animator.StringToHash("IsSuperspeed");
    private static readonly int H_IsGrounded   = Animator.StringToHash("IsGrounded");
    private static readonly int H_IsPickingUp  = Animator.StringToHash("IsPickingUp");
    private static readonly int H_IsDead       = Animator.StringToHash("IsDead");
    private static readonly int H_Die          = Animator.StringToHash("Die");
    private static readonly int H_JumpTrigger  = Animator.StringToHash("Jump");

    
    private Animator  _anim;
    private Transform _root;

    private Vector3 _prevPos;

    // BUG 5 FIX: explicit EMA — replaces Animator.SetFloat dampTime.
    // Plain Lerp never overshoots, so blend-tree thresholds are crossed
    // smoothly and exactly once per locomotion state change.
    private float _smoothedSpeed;

    private float   _smoothedHSpeed;   // non-owner airborne detection only
    private float   _smoothedYVel;
    private float   _smoothedLocalX;
    private float   _smoothedLocalZ;
    private float   _airborneBuffer;
    private float   _pickupTimer;
    private int     _lastCarriedCount;
    private bool    _wasDead;
    private bool    _subscribedToDeath;
    private bool    _subscribedToRespawn;
    private int     _lastJumpSequence;
    private bool    _wasSuperspeed;
    private bool    _wasAirborne;

    private float       _jumpForceAirborneTimer;
    private const float JUMP_FORCE_AIRBORNE_TIME = 0.15f;

    private float _prevRawVelY;
    private float _nonOwnerLandLatch;
    private const float NON_OWNER_LAND_LATCH_TIME = 0.15f;

    private const float TELEPORT_THRESHOLD = 3f;

    // BUG 1 FIX: increased from 0.05 s → 0.15 s.
    // 0.05 s = 3 frames at 60 fps; terrain seams last 4-6 frames, expiring
    // the buffer and snapping Speed to 0. 0.15 s = 9 frames matches
    // ShooterAnimator and is robust against all normal ground irregularities.
    private float _ownerLocomotionAirborneBuffer;
    private float _nonOwnerLocomotionAirborneBuffer;
    private const float LOCOMOTION_AIRBORNE_BUFFER = 0.15f;

    // BUG 4 FIX: debounces for non-owner NV-based movement/sprint detection.
    // Mirrors the same constants in ShooterAnimator.
    private float       _movingOffDebounce;
    private const float MOVING_OFF_DEBOUNCE = 0.12f;

    private float       _sprintOffDebounce;
    private const float SPRINT_OFF_DEBOUNCE = 0.10f;

    
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

        if (_root != null)
            _prevPos = _root.position;

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

    
    private void ResetRuntimeState()
    {
        _smoothedSpeed                    = 0f;
        _smoothedHSpeed                   = 0f;
        _smoothedYVel                     = 0f;
        _airborneBuffer                   = 0f;
        _smoothedLocalX                   = 0f;
        _smoothedLocalZ                   = 0f;
        _wasAirborne                      = false;
        _wasSuperspeed                    = false;
        _jumpForceAirborneTimer           = 0f;
        _prevRawVelY                      = 0f;
        _nonOwnerLandLatch                = 0f;
        _ownerLocomotionAirborneBuffer    = 0f;
        _nonOwnerLocomotionAirborneBuffer = 0f;
        _movingOffDebounce                = 0f;
        _sprintOffDebounce                = 0f;
    }

    
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

        
        if (playerStats.IsDead())
        {
            _anim.SetBool(H_IsDead,       true);
            _anim.SetFloat(H_Speed,       0f);
            _anim.SetFloat(H_CrouchX,     0f);
            _anim.SetFloat(H_CrouchY,     0f);
            _anim.SetBool(H_IsGrounded,   true);
            _anim.SetBool(H_IsCrouching,  false);
            _anim.SetBool(H_IsSuperspeed, false);
            _anim.SetBool(H_IsPickingUp,  false);
            _smoothedSpeed = 0f;
            _wasSuperspeed = false;
            return;
        }
        _anim.SetBool(H_IsDead, false);

        bool isOwner = playerStats.IsOwner;

        
        if (collectorController != null)
        {
            int now = collectorController.GetCarriedCount();
            if (now > _lastCarriedCount) _pickupTimer = pickupDuration;
            _lastCarriedCount = now;
        }
        if (_pickupTimer > 0f) _pickupTimer -= UnityEngine.Time.deltaTime;
        _anim.SetBool(H_IsPickingUp, _pickupTimer > 0f);

        
        bool isSuperspeed = collectorController != null
            && collectorController.superSpeedActive.Value
            && _smoothedHSpeed >= 0.5f;  // 0.5 keeps using velocity here (non-critical, superspeed is a known state)

        _anim.SetBool(H_IsSuperspeed, isSuperspeed);

        
        bool jumpJustFired = playerStats.jumpSequence.Value != _lastJumpSequence;

        if (jumpJustFired)
        {
            _jumpForceAirborneTimer           = JUMP_FORCE_AIRBORNE_TIME;
            _nonOwnerLandLatch                = 0f;
            _ownerLocomotionAirborneBuffer    = 0f;
            _nonOwnerLocomotionAirborneBuffer = 0f;
        }
        else if (_jumpForceAirborneTimer > 0f)
        {
            _jumpForceAirborneTimer -= UnityEngine.Time.deltaTime;
        }

        // ── BUG 2 FIX: rawAirborne uses IsGrounded() (0.3 m) instead of
        //   HasGroundContact() (0.08 m). The tight 0.08 m radius misses the
        //   floor on terrain seams for 1-3 frames, flipping H_IsGrounded false
        //   and interrupting the Walk/Run state machine. The 0.3 m sphere from
        //   IsGrounded() is stable across all normal walkable terrain.
        bool rawAirborne;
        if (isOwner && playerController != null)
        {
            rawAirborne = !playerController.IsGrounded()
                       || _jumpForceAirborneTimer > 0f;
        }
        else
        {
            
            _smoothedYVel = UnityEngine.Mathf.Lerp(_smoothedYVel, worldVel.y,
                yVelSmoothFactor * UnityEngine.Time.deltaTime);

            bool fastLanding = _prevRawVelY      < -airborneYThreshold
                            && worldVel.y >= -airborneYThreshold * 0.4f;

            if (fastLanding)
                _nonOwnerLandLatch = NON_OWNER_LAND_LATCH_TIME;
            else if (_nonOwnerLandLatch > 0f)
                _nonOwnerLandLatch -= UnityEngine.Time.deltaTime;

            _prevRawVelY = worldVel.y;

            rawAirborne = (UnityEngine.Mathf.Abs(_smoothedYVel) > airborneYThreshold
                       ||  _jumpForceAirborneTimer > 0f)
                       && _nonOwnerLandLatch <= 0f;
        }

        bool isAirborne;
        if (isOwner && playerController != null)
        {
            isAirborne = rawAirborne;
        }
        else
        {
            if (rawAirborne)
                _airborneBuffer = airborneHoldTime;
            else if (_airborneBuffer > 0f)
                _airborneBuffer -= UnityEngine.Time.deltaTime;

            isAirborne = rawAirborne || _airborneBuffer > 0f;
        }

        bool justLanded = _wasAirborne && !isAirborne;
        _wasAirborne = isAirborne;

        _anim.SetBool(H_IsGrounded, !isAirborne);

        
        int currentSeq = playerStats.jumpSequence.Value;
        if (currentSeq != _lastJumpSequence)
        {
            _lastJumpSequence = currentSeq;

            if (!justLanded)
            {
                _anim.ResetTrigger(H_JumpTrigger);
                _anim.SetTrigger(H_JumpTrigger);
            }
        }

        
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
                // BUG 4 FIX: use localMoveDir NV (written by PlayerController
                // from raw input). No position-delta oscillation.
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

        // ── BUG 3 FIX: owner locomotion uses isGroundedNV.Value (stable 0.3 m
        //   sphere) instead of rawAirborne (was derived from HasGroundContact).
        //   Mirrors ShooterAnimator's locomotion path exactly.
        bool isAirborneForLocomotion;
        if (isOwner && playerController != null)
        {
            bool groundedForLocomotion = playerStats.isGroundedNV.Value
                                      && _jumpForceAirborneTimer <= 0f;
            if (groundedForLocomotion)
                _ownerLocomotionAirborneBuffer = LOCOMOTION_AIRBORNE_BUFFER;
            else if (_ownerLocomotionAirborneBuffer > 0f)
                _ownerLocomotionAirborneBuffer -= UnityEngine.Time.deltaTime;

            isAirborneForLocomotion = !groundedForLocomotion
                                   && _ownerLocomotionAirborneBuffer <= 0f;
        }
        else
        {
            bool groundedForLocomotion = playerStats.isGroundedNV.Value
                                      && _jumpForceAirborneTimer <= 0f;
            if (groundedForLocomotion)
                _nonOwnerLocomotionAirborneBuffer = LOCOMOTION_AIRBORNE_BUFFER;
            else if (_nonOwnerLocomotionAirborneBuffer > 0f)
                _nonOwnerLocomotionAirborneBuffer -= UnityEngine.Time.deltaTime;

            isAirborneForLocomotion = isAirborne
                                   && _nonOwnerLocomotionAirborneBuffer <= 0f;
        }

        // ── BUG 4 + 5 FIX: compute targetSpeed from stable sources, then
        //   apply a plain Lerp EMA (never overshoots unlike SmoothDamp).
        float targetSpeed;

        if (isAirborneForLocomotion || isCrouching)
        {
            targetSpeed = 0f;
        }
        else if (isOwner && playerController != null)
        {
            // Owner: read input directly — no position-delta lag.
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
            // Non-owner: use isMovingNV (NV written by PlayerController from
            // raw input — never oscillates at NT update boundaries).
            bool nvIsMoving = playerStats.isMovingNV.Value;
            if (nvIsMoving)
                _movingOffDebounce = MOVING_OFF_DEBOUNCE;
            else if (_movingOffDebounce > 0f)
                _movingOffDebounce -= UnityEngine.Time.deltaTime;
            bool isMoving = nvIsMoving || _movingOffDebounce > 0f;

            bool nvIsSprinting = playerStats.isSprinting.Value;
            if (nvIsSprinting)
                _sprintOffDebounce = SPRINT_OFF_DEBOUNCE;
            else if (_sprintOffDebounce > 0f)
                _sprintOffDebounce -= UnityEngine.Time.deltaTime;
            bool isSprintingDebounced = nvIsSprinting || _sprintOffDebounce > 0f;

            targetSpeed = isMoving ? ((isSprintingDebounced || isSuperspeed) ? 2f : 1f) : 0f;
        }

        // BUG 5 FIX: plain Lerp EMA — no SmoothDamp overshoot.
        _smoothedSpeed = UnityEngine.Mathf.Lerp(_smoothedSpeed, targetSpeed,
            speedSmoothFactor * UnityEngine.Time.deltaTime);
        _anim.SetFloat(H_Speed, _smoothedSpeed);

        _wasSuperspeed = isSuperspeed;
    }

    
    private void OnAnimatorMove() {  }

    
    private void OnDeadChanged(bool prev, bool next)
    {
        if (next && !_wasDead)
            ApplyDeathState();
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
            _anim.ResetTrigger(H_JumpTrigger);
    }
}