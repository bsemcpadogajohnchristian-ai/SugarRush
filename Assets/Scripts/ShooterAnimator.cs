// ShooterAnimator.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Drives the Shooter body Animator on ALL clients (owner + non-owner).
// Attach to bodyShooter — the child GameObject that holds the Shooter mesh
// and Animator component.
//
// ── FIX LOG ───────────────────────────────────────────────────────────────
//
//   FIX A — inputDeadZone applied to crouch-walk direction.
//   FIX B — Owner standing locomotion is input-based (no position-delta lag).
//   FIX C — Reload animation sync via isReloadingNV.
//   BUG 1 — Speed dampTime added for airborne/crouch snap-to-zero.
//   BUG 2 — Non-owner hysteresis prevents walk/idle oscillation.
//   BUG 3 — Owner CrouchMoveX/Y now goes through the same EMA as non-owner.
//
//   BUG 5 FIX (this version) — Animator.SetFloat dampTime replaced with
//     explicit _smoothedSpeed EMA:
//
//     The previous code passed speedParam (0, 1, or 2) to Animator.SetFloat
//     with the dampTime overload. Unity's dampTime internally calls
//     Mathf.SmoothDamp, which is a spring-damper — it can OVERSHOOT.
//
//     When Speed overshoots the 1.0 threshold (Idle↔Walk) or the 2.0
//     threshold (Walk↔Run) by even 0.001 for a single frame, the animator
//     blend tree snaps to the adjacent clip. The spring then pulls back,
//     overshoots again the other direction, and the blend tree flickers
//     between clips for several frames. This is the primary cause of
//     walk/run twitching on the owner.
//
//     Fix: mirror FPShooterAnimator exactly — compute a targetSpeed float
//     (0, 1, or 2), apply a plain Mathf.Lerp EMA into _smoothedSpeed, then
//     call SetFloat(H_Speed, _smoothedSpeed) with no dampTime. Lerp never
//     overshoots, making threshold crossings smooth and exactly once.
//
//     _smoothedSpeed is updated every frame regardless of airborne/crouch
//     state (target = 0 in those cases) so it is always in sync when the
//     character returns to normal locomotion.
//
//   BUG 4 FIX (previous version) — Speed dampTime increased for Walk↔Run.
//     Kept in spirit by speedSmoothFactor = 12f (≈ 0.083s to converge).
//
//   ANIMATOR SETUP NOTE:
//     The Crouch Movement blend tree MUST use "2D Simple Directional", NOT
//     "2D Freeform Directional". Freeform Directional uses gradient band
//     interpolation designed for multiple clips in the same direction at
//     different speeds. With 5 single-direction clips it causes "magnetic"
//     blending — the blend point snaps toward the nearest sample as
//     CrouchMoveX/Y cross zero, producing a visible twitch.
//
// ─────────────────────────────────────────────────────────────────────────

using UnityEngine;

[DefaultExecutionOrder(50)]
[RequireComponent(typeof(Animator))]
public class ShooterAnimator : MonoBehaviour
{
    [Header("References (auto-found in Awake if left empty)")]
    public PlayerController playerController;
    public PlayerStats      playerStats;

    [Header("Standing speed thresholds")]
    [Tooltip("NON-OWNER walk detection is now NV-based (isMovingNV) and no longer\n" +
             "uses position-delta velocity. This field is kept for reference only\n" +
             "and is no longer read by the animator.")]
    [HideInInspector]
    public float walkThreshold = 0.5f;   // legacy — unused, kept for Inspector compat

    [Header("Crouch blend normalisation")]
    [Tooltip("Non-owner crouch direction is now NV-based (localMoveDir) and no longer\n" +
             "requires speed normalisation. Kept for Inspector compat only.")]
    [HideInInspector]
    public float crouchMaxSpeed = 3.0f;  // legacy — unused, kept for Inspector compat

    [Header("Airborne detection")]
    [Tooltip("Smoothed Y velocity (m/s) above which a non-owner is airborne.")]
    public float airborneYThreshold = 0.6f;

    [Tooltip("Seconds to hold IsGrounded = false after signal drops (non-owners).\n" +
             "Bridges 30 Hz NT dead frames so Jump_Start does not stutter.")]
    public float airborneHoldTime = 0.05f;

    [Header("Velocity smoothing")]
    [Tooltip("EMA factor for the Speed float sent to the 1D blend tree.\n" +
             "Higher = snappier walk/run transitions. 12 recommended.\n" +
             "BUG 5 FIX: replaces Unity's dampTime (SmoothDamp) which\n" +
             "could overshoot blend-tree thresholds and cause flickering.")]
    public float speedSmoothFactor   = 12f;

    [Tooltip("EMA factor for horizontal speed — used ONLY for non-owner airborne detection.\n" +
             "No longer drives walk/idle switching (that now uses isMovingNV). 10–15 recommended.")]
    public float hSpeedSmoothFactor  = 12f;

    [Tooltip("EMA factor for Y velocity — non-owner airborne detection. ~20 recommended.")]
    public float yVelSmoothFactor    = 20f;

    [Tooltip("EMA factor for CrouchMoveX/Y (owner AND non-owner). 6–8 recommended.\n" +
             "BUG 3 FIX: owner now uses this factor too (was bypassing EMA before).")]
    public float crouchSmoothFactor  = 7f;

    [Header("Input dead zone (owner only)")]
    [Tooltip("Raw Input.GetAxis dead zone (0–1). Prevents idle jitter from micro-inputs.\n" +
             "Default 0.15 matches Unity's Input Manager default.")]
    public float inputDeadZone = 0.15f;

    // ── Animator parameter hashes ─────────────────────────────────────────
    private static readonly int H_Speed       = Animator.StringToHash("Speed");
    private static readonly int H_CrouchX     = Animator.StringToHash("CrouchMoveX");
    private static readonly int H_CrouchY     = Animator.StringToHash("CrouchMoveY");
    private static readonly int H_WeaponType  = Animator.StringToHash("WeaponType");
    private static readonly int H_IsCrouching = Animator.StringToHash("IsCrouching");
    private static readonly int H_IsGrounded  = Animator.StringToHash("IsGrounded");
    private static readonly int H_IsDead      = Animator.StringToHash("IsDead");
    private static readonly int H_IsReloading = Animator.StringToHash("IsReloading");
    private static readonly int H_Jump        = Animator.StringToHash("Jump");
    private static readonly int H_Fire        = Animator.StringToHash("Fire");
    private static readonly int H_Reload      = Animator.StringToHash("Reload");
    private static readonly int H_Die         = Animator.StringToHash("Die");
    private static readonly int H_Respawn     = Animator.StringToHash("Respawn");

    // ── Runtime state ─────────────────────────────────────────────────────
    private Animator  _anim;
    private Transform _root;

    private Vector3 _prevPos;

    // BUG 5 FIX: explicit EMA for the Speed parameter.
    // Replaces Animator.SetFloat dampTime (which used SmoothDamp and could
    // overshoot blend-tree thresholds). Plain Lerp never overshoots.
    private float _smoothedSpeed;

    private float   _smoothedHSpeed;   // used only for non-owner airborne detection
    private float   _smoothedYVel;
    private float   _smoothedLocalX;
    private float   _smoothedLocalZ;
    private float   _airborneBuffer;
    private bool    _wasAirborne;
    private bool    _wasDead;
    private bool    _wasReloading;
    private bool    _subscribedToDead;
    private bool    _subscribedToRespawn;
    private int     _lastJumpSequence;
    private int     _lastFireSequence;
    private int     _lastWeaponIndex = -1;

    private float       _jumpForceAirborneTimer;
    private const float JUMP_FORCE_AIRBORNE_TIME = 0.15f;

    private float       _prevRawVelY;
    private float       _nonOwnerLandLatch;
    private const float NON_OWNER_LAND_LATCH_TIME = 0.15f;

    private const float TELEPORT_THRESHOLD = 3f;

    private float       _ownerLocomotionBuffer;
    private const float LOCOMOTION_AIRBORNE_BUFFER = 0.05f;  // owner locomotion buffer only

    // ── isMovingNV stop-debounce ──────────────────────────────────────────────
    //
    // Belt-and-suspenders guard for the non-owner locomotion path.
    // Even after the PlayerController fix (removing _isGrounded from isMovingNV),
    // a brief 1-frame false NV value can still arrive due to network packet
    // timing or a host-frame edge case. Without debounce, that single false frame
    // pulls targetSpeed to 0, _smoothedSpeed dips, and the blend tree flickers.
    //
    // Pattern: when isMovingNV goes false, hold the "moving" signal for
    // MOVING_OFF_DEBOUNCE seconds before letting targetSpeed drop to 0.
    // When isMovingNV is true the timer is refreshed every frame — so it only
    // fires on genuine stop events, not 1-frame glitches.
    private float       _movingOffDebounce;
    private const float MOVING_OFF_DEBOUNCE = 0.12f;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    private void Awake()
    {
        _anim = GetComponent<Animator>();
        _root = transform.root;

        if (playerController == null) playerController = GetComponentInParent<PlayerController>();
        if (playerStats      == null) playerStats      = GetComponentInParent<PlayerStats>();
    }

    private void Start()
    {
        TrySubscribeToDead();
        TrySubscribeToRespawn();

        if (_root != null) _prevPos = _root.position;

        if (playerStats == null) return;
        _lastJumpSequence = playerStats.jumpSequence.Value;
        _lastFireSequence = playerStats.shootFireSequence.Value;
        _lastWeaponIndex  = playerStats.equippedWeaponIndex.Value;
        _anim.SetInteger(H_WeaponType, _lastWeaponIndex);

        _wasReloading = playerStats.isReloadingNV.Value;
        _anim.SetBool(H_IsReloading, _wasReloading);

        if (playerStats.IsDead()) ApplyDeathState();
    }

    private void OnEnable()
    {
        TrySubscribeToDead();
        TrySubscribeToRespawn();

        if (_root == null && transform.root != null) _root = transform.root;
        if (_root != null) _prevPos = _root.position;

        ResetRuntimeState();

        if (playerStats != null)
        {
            _lastJumpSequence = playerStats.jumpSequence.Value;
            _lastFireSequence = playerStats.shootFireSequence.Value;
            _lastWeaponIndex  = playerStats.equippedWeaponIndex.Value;
            _anim.SetInteger(H_WeaponType, _lastWeaponIndex);

            _wasReloading = playerStats.isReloadingNV.Value;
            _anim.SetBool(H_IsReloading, _wasReloading);

            if (playerStats.IsDead()) ApplyDeathState();
        }
    }

    private void OnDisable()
    {
        if (playerStats == null) return;
        if (_subscribedToDead)
        {
            playerStats.isDead.OnValueChanged -= OnDeadChanged;
            _subscribedToDead = false;
        }
        if (_subscribedToRespawn)
        {
            playerStats.onRespawn.RemoveListener(OnRespawn);
            _subscribedToRespawn = false;
        }
    }

    private void OnDestroy()
    {
        if (playerStats == null) return;
        if (_subscribedToDead)    playerStats.isDead.OnValueChanged -= OnDeadChanged;
        if (_subscribedToRespawn) playerStats.onRespawn.RemoveListener(OnRespawn);
    }

    private void TrySubscribeToDead()
    {
        if (_subscribedToDead || playerStats == null) return;
        playerStats.isDead.OnValueChanged += OnDeadChanged;
        _subscribedToDead = true;
    }

    private void TrySubscribeToRespawn()
    {
        if (_subscribedToRespawn || playerStats == null) return;
        playerStats.onRespawn.AddListener(OnRespawn);
        _subscribedToRespawn = true;
    }

    private void ResetRuntimeState()
    {
        _smoothedSpeed            = 0f;   // BUG 5 FIX: must reset so on-enable has no stale value
        _smoothedHSpeed           = 0f;   // airborne detection only
        _smoothedYVel             = 0f;
        _smoothedLocalX           = 0f;
        _smoothedLocalZ           = 0f;
        _airborneBuffer           = 0f;
        _wasAirborne              = false;
        _wasReloading             = false;
        _jumpForceAirborneTimer   = 0f;
        _prevRawVelY              = 0f;
        _nonOwnerLandLatch        = 0f;
        _ownerLocomotionBuffer    = 0f;
        _movingOffDebounce        = 0f;   // clear so a respawn/enable never holds stale "moving" signal
    }

    // ── Per-frame update ──────────────────────────────────────────────────

    private void Update()
    {
        if (_anim == null) return;

        if (playerStats == null)
        {
            playerStats = GetComponentInParent<PlayerStats>();
            TrySubscribeToDead();
            TrySubscribeToRespawn();
            if (playerStats == null) return;
        }

        if (playerStats.role.Value != PlayerRole.Shooter) return;

        if (_root == null)
        {
            _root    = transform.root;
            _prevPos = _root.position;
        }

        // ── 1. Teleport detection ──────────────────────────────────────────
        if (Vector3.Distance(_root.position, _prevPos) > TELEPORT_THRESHOLD)
        {
            _prevPos = _root.position;
            ResetRuntimeState();
        }

        // ── 2. Position-delta velocity (non-owner paths) ──────────────────
        Vector3 worldVel = (_root.position - _prevPos) / Time.deltaTime;
        _prevPos = _root.position;

        float rawHSpeed = new Vector3(worldVel.x, 0f, worldVel.z).magnitude;
        _smoothedHSpeed = Mathf.Lerp(_smoothedHSpeed, rawHSpeed, hSpeedSmoothFactor * Time.deltaTime);

        // ── 3. Dead: freeze all other logic ───────────────────────────────
        if (playerStats.IsDead())
        {
            _anim.SetBool(H_IsDead,      true);
            _anim.SetFloat(H_Speed,      0f);
            _anim.SetFloat(H_CrouchX,   0f);
            _anim.SetFloat(H_CrouchY,   0f);
            _anim.SetBool(H_IsGrounded,  true);
            _anim.SetBool(H_IsCrouching, false);
            _anim.SetBool(H_IsReloading, false);
            _smoothedSpeed  = 0f;
            _wasReloading   = false;
            return;
        }
        _anim.SetBool(H_IsDead, false);

        bool isOwner = playerStats.IsOwner;

        // ── 4. Weapon type ─────────────────────────────────────────────────
        int wi = playerStats.equippedWeaponIndex.Value;
        if (wi != _lastWeaponIndex)
        {
            _lastWeaponIndex = wi;
            _anim.SetInteger(H_WeaponType, wi);
        }

        // ── 5. Fire trigger ────────────────────────────────────────────────
        int fireSeq = playerStats.shootFireSequence.Value;
        if (fireSeq != _lastFireSequence)
        {
            _lastFireSequence = fireSeq;
            _anim.ResetTrigger(H_Fire);
            _anim.SetTrigger(H_Fire);
        }

        // ── 5b. Reload animation ──────────────────────────────────────────
        bool nowReloading = playerStats.isReloadingNV.Value;
        if (nowReloading != _wasReloading)
        {
            _wasReloading = nowReloading;
            _anim.SetBool(H_IsReloading, nowReloading);
            if (nowReloading)
            {
                _anim.ResetTrigger(H_Reload);
                _anim.SetTrigger(H_Reload);
            }
        }

        // ── 6. Airborne detection ──────────────────────────────────────────
        bool jumpJustFired = playerStats.jumpSequence.Value != _lastJumpSequence;
        if (jumpJustFired)
        {
            _jumpForceAirborneTimer   = JUMP_FORCE_AIRBORNE_TIME;
            _nonOwnerLandLatch        = 0f;
            _ownerLocomotionBuffer    = 0f;
        }
        else if (_jumpForceAirborneTimer > 0f)
        {
            _jumpForceAirborneTimer -= Time.deltaTime;
        }

        bool rawAirborne;
        if (isOwner && playerController != null)
        {
            rawAirborne = !playerController.HasGroundContact() || _jumpForceAirborneTimer > 0f;
        }
        else
        {
            _smoothedYVel = Mathf.Lerp(_smoothedYVel, worldVel.y, yVelSmoothFactor * Time.deltaTime);

            bool fastLanding = _prevRawVelY  < -airborneYThreshold
                            && worldVel.y   >= -airborneYThreshold * 0.4f;
            if (fastLanding)
                _nonOwnerLandLatch = NON_OWNER_LAND_LATCH_TIME;
            else if (_nonOwnerLandLatch > 0f)
                _nonOwnerLandLatch -= Time.deltaTime;

            _prevRawVelY = worldVel.y;

            rawAirborne = (Mathf.Abs(_smoothedYVel) > airborneYThreshold
                       ||  _jumpForceAirborneTimer  > 0f)
                       && _nonOwnerLandLatch <= 0f;
        }

        bool isAirborne;
        if (isOwner && playerController != null)
        {
            isAirborne = rawAirborne;
        }
        else
        {
            if (rawAirborne) _airborneBuffer = airborneHoldTime;
            else if (_airborneBuffer > 0f) _airborneBuffer -= Time.deltaTime;
            isAirborne = rawAirborne || _airborneBuffer > 0f;
        }

        // ── 7. IsGrounded ──────────────────────────────────────────────────
        bool justLanded = _wasAirborne && !isAirborne;
        _wasAirborne = isAirborne;
        _anim.SetBool(H_IsGrounded, !isAirborne);

        // ── 8. Jump trigger ────────────────────────────────────────────────
        int currentSeq = playerStats.jumpSequence.Value;
        if (currentSeq != _lastJumpSequence)
        {
            _lastJumpSequence = currentSeq;
            if (!justLanded)
            {
                _anim.ResetTrigger(H_Jump);
                _anim.SetTrigger(H_Jump);
            }
        }

        // ── 9. Crouch ──────────────────────────────────────────────────────
        bool isCrouching = playerStats.isCrouching.Value;
        _anim.SetBool(H_IsCrouching, isCrouching);

        if (isCrouching)
        {
            float targetX, targetZ;
            if (isOwner)
            {
                float rawH = Input.GetAxis("Horizontal");
                float rawV = Input.GetAxis("Vertical");
                targetX = Mathf.Abs(rawH) > inputDeadZone ? rawH : 0f;
                targetZ = Mathf.Abs(rawV) > inputDeadZone ? rawV : 0f;
            }
            else
            {
                // NON-OWNER CROUCH FIX: Read localMoveDir NV written by the owner's
                // PlayerController. This replaces the position-delta estimate
                // (worldVel / crouchMaxSpeed) which oscillated between spike and zero
                // at every NetworkTransform update boundary, causing the crouch blend
                // point to visually snap direction in/out on every other frame.
                Vector2 dir = playerStats.localMoveDir.Value;
                targetX = dir.x;
                targetZ = dir.y;
            }

            // BUG 3 FIX: both owner and non-owner go through the same EMA.
            _smoothedLocalX = Mathf.Lerp(_smoothedLocalX, targetX, crouchSmoothFactor * Time.deltaTime);
            _smoothedLocalZ = Mathf.Lerp(_smoothedLocalZ, targetZ, crouchSmoothFactor * Time.deltaTime);

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

        // ── 10. Standing speed (1D blend tree) ────────────────────────────
        //
        // BUG 5 FIX — replaced dampTime with explicit _smoothedSpeed EMA.
        //
        // We compute a float targetSpeed (0, 1, or 2), then EMA-smooth it
        // into _smoothedSpeed ourselves, then call SetFloat with NO dampTime.
        //
        // Why this matters vs the old SetFloat(id, value, dampTime, dt):
        //   Unity's dampTime uses Mathf.SmoothDamp — a spring-damper. Springs
        //   overshoot. When targetSpeed jumps from 0→1 or 1→2, SmoothDamp
        //   can push _smoothedSpeed past the threshold (e.g. Speed = 1.003)
        //   for a frame or two, causing the blend tree to snap to the adjacent
        //   clip, then spring back, causing visible flicker at the boundary.
        //   Plain Lerp (EMA) is monotone — it never overshoots. The Speed
        //   float approaches the target smoothly and crosses each threshold
        //   exactly once. Flicker eliminated.
        //
        // Additionally: _smoothedSpeed is now updated every frame (target = 0
        // when airborne or crouching) so it is always at 0 when returning to
        // normal locomotion. Previously the stale dampTime value would briefly
        // overshoot on the first frame back from crouching.

        bool isAirborneForLocomotion;
        if (isOwner && playerController != null)
        {
            // Owner: use tight-radius CC ground contact. Buffer resets every
            // grounded frame — a single airborne tick from rough terrain can't
            // expire it and snap the blend tree to Idle.
            if (!rawAirborne) _ownerLocomotionBuffer = LOCOMOTION_AIRBORNE_BUFFER;
            else if (_ownerLocomotionBuffer > 0f) _ownerLocomotionBuffer -= Time.deltaTime;
            isAirborneForLocomotion = rawAirborne && _ownerLocomotionBuffer <= 0f;
        }
        else
        {
            // NON-OWNER AIRBORNE FIX — root cause of remaining "back and forth":
            //
            // The previous code derived isAirborneForLocomotion from position-delta
            // Y velocity (_smoothedYVel). On NT-update frames (~30Hz) the position
            // delta includes a Y component from even slightly uneven ground, spiking
            // worldVel.y above airborneYThreshold (0.6 m/s) for 1-2 frames:
            //
            //   isAirborneForLocomotion = true  → targetSpeed snaps to 0
            //   spike gone, next frame           → targetSpeed back to 1 (isMovingNV)
            //   _smoothedSpeed oscillates 0↔1   → Idle/Walk blend-tree flicker
            //
            // This is WHY the first NV fix helped "a bit" but didn't fully solve
            // it: horizontal speed oscillation was fixed, but the Y-velocity path
            // kept triggering the same snap-to-0 / recover cycle.
            //
            // FIX: drive isAirborneForLocomotion ONLY from _jumpForceAirborneTimer,
            // which is set when jumpSequence changes (owner-write NV, authoritative,
            // never noisy). No Y-velocity involved → no false positives → Speed
            // can never snap to 0 due to terrain artifacts or NT interpolation.
            //
            // The IsGrounded bool (section 7 above) and Jump Start/Land state-
            // machine transitions still use the Y-velocity isAirborne path unchanged
            // — that is the correct source for jump state transitions. Only the
            // LOCOMOTION BLEND TREE is switched to the NV-authoritative timer.
            isAirborneForLocomotion = _jumpForceAirborneTimer > 0f;
        }

        // ── Compute targetSpeed ───────────────────────────────────────────
        float targetSpeed;

        if (isAirborneForLocomotion || isCrouching)
        {
            // Airborne or crouching: drive Speed to 0 so the Locomotion
            // blend tree (if transitioning from/to) shows Idle.
            targetSpeed = 0f;
        }
        else if (isOwner)
        {
            // Owner: input-based (no position-delta startup lag / wall-slide flicker).
            float h        = Input.GetAxis("Horizontal");
            float v        = Input.GetAxis("Vertical");
            bool  hasInput = Mathf.Abs(h) > inputDeadZone || Mathf.Abs(v) > inputDeadZone;

            if (!hasInput)
                targetSpeed = 0f;
            else
                targetSpeed = (playerController != null && playerController.IsSprinting())
                              || playerStats.isSprinting.Value ? 2f : 1f;
        }
        else
        {
            // NON-OWNER LOCOMOTION FIX — use isMovingNV + isSprinting NVs.
            //
            // The previous approach computed _smoothedHSpeed from position-delta
            // velocity and used a hysteresis band (_nonOwnerWalking) to decide
            // whether the character was walking. This caused the "back and forth"
            // flicker because:
            //   • NT updates arrive at ~30 Hz; Update() runs at 60 Hz.
            //   • On NT-update frames, position jumps → rawHSpeed spikes to ~10×.
            //   • On dead frames, rawHSpeed = 0.
            //   • _smoothedHSpeed oscillated around walkThreshold ± WALK_HYST
            //     → _nonOwnerWalking toggled → targetSpeed flipped 0 ↔ 1
            //     → the blend tree snapped between Idle and Walk every few frames.
            //
            // FIX: The owner writes isMovingNV (bool) directly from raw input —
            // zero oscillation possible since it's just a state bit, not a derived
            // velocity. isSprinting.Value is also owner-written the same way.
            // Reading these NVs gives the correct state on every client with no
            // estimation noise.
            //
            // ── STOP DEBOUNCE ─────────────────────────────────────────────────
            // A brief 1-frame false on isMovingNV (network timing edge case) would
            // still snap targetSpeed to 0 and cause a visible flicker even with EMA
            // smoothing. The debounce holds the "moving" signal for MOVING_OFF_DEBOUNCE
            // seconds after isMovingNV drops false — so only genuine stops (held for
            // >0.12 s) propagate to targetSpeed. Instant start (timer refresh on true)
            // is intentional so walk/run begin immediately when the owner starts moving.
            bool nvIsMoving = playerStats.isMovingNV.Value;
            if (nvIsMoving)
                _movingOffDebounce = MOVING_OFF_DEBOUNCE;   // refresh every frame while moving
            else if (_movingOffDebounce > 0f)
                _movingOffDebounce -= Time.deltaTime;

            bool isMoving = nvIsMoving || _movingOffDebounce > 0f;
            targetSpeed = isMoving
                ? (playerStats.isSprinting.Value ? 2f : 1f)
                : 0f;
        }

        // ── Apply EMA and push to animator (no dampTime) ─────────────────
        // Lerp never overshoots → blend tree thresholds are never crossed twice.
        _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, targetSpeed, speedSmoothFactor * Time.deltaTime);
        _anim.SetFloat(H_Speed, _smoothedSpeed);
    }

    // ── Suppress root motion — CC owns all movement ───────────────────────
    private void OnAnimatorMove() { /* intentionally empty */ }

    // ── Death / Respawn ───────────────────────────────────────────────────

    private void OnDeadChanged(bool prev, bool next)
    {
        if (next && !_wasDead) ApplyDeathState();
        else if (!next && _wasDead)
        {
            _anim.ResetTrigger(H_Die);
            _anim.SetBool(H_IsDead, false);
            _anim.ResetTrigger(H_Respawn);
            _anim.SetTrigger(H_Respawn);
            _wasDead = false;
        }
    }

    private void ApplyDeathState()
    {
        _anim.ResetTrigger(H_Respawn);
        _anim.SetTrigger(H_Die);
        _anim.SetBool(H_IsDead, true);
        _anim.SetBool(H_IsReloading, false);
        _smoothedSpeed = 0f;
        _wasReloading  = false;
        _wasDead       = true;
    }

    private void OnRespawn()
    {
        if (playerStats != null)
        {
            _lastJumpSequence = playerStats.jumpSequence.Value;
            _lastFireSequence = playerStats.shootFireSequence.Value;
        }
        ResetRuntimeState();
        if (_anim != null) _anim.ResetTrigger(H_Jump);
    }
}