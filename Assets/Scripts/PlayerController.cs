// PlayerController.cs
// Sugar Rush
// Unity 6.3 LTS + Netcode for GameObjects v2.1+
//
// Handles local player movement, look, jump, crouch.
// Owner-only. Non-owners are disabled immediately.
//
// ── SCRIPT EXECUTION ORDER ────────────────────────────────────────────────
//
//   [DefaultExecutionOrder(-50)] ensures PlayerController.Move() — and
//   critically the _ccContact update at the END of Move() — always runs
//   BEFORE CollectorAnimator.Update() in the same frame.
//
//   Without this guarantee, CollectorAnimator could read HasGroundContact()
//   BEFORE PlayerController has processed the current frame's collision,
//   causing a 1-frame delay on the landing animation trigger.
//
//   CollectorAnimator uses [DefaultExecutionOrder(50)] to run after.
//
// ── ZERO-FRAME LANDING DELAY FIX (_ccContact placement) ──────────────────
//
//   PREVIOUS BEHAVIOUR:
//     _ccContact was computed at the TOP of Move(), using _lastMoveFlags from
//     the PREVIOUS frame's CC.Move(). On the exact landing frame:
//       • _lastMoveFlags(N-1) had no CollisionFlags.Below (was falling)
//       • CheckSphere might not reach the floor yet (0.08 m radius)
//     → HasGroundContact() returned false on the landing frame itself.
//     → CollectorAnimator saw IsGrounded = false for 1 extra frame (~16 ms).
//
//   FIX:
//     _ccContact is now computed at the BOTTOM of Move(), AFTER _cc.Move()
//     and the post-move velocity snaps. It now includes:
//       • _lastMoveFlags from THIS frame's CC.Move() (CollisionFlags.Below
//         is set on the exact landing frame)
//       • CheckSphere at the character's NEW position after moving
//     → HasGroundContact() returns true on the SAME frame the capsule
//        contacts the floor. Zero-frame delay. ✓
//
//   NOTE: _isGrounded (the generous 0.3 m sphere used for jump/coyote logic)
//   remains at the TOP of Move() — intentional, that generous tolerance is
//   correct for jump/coyote/movement logic and must not be changed.
//
// ── JUMP SYSTEM (Professional 3-part fix) ────────────────────────────────
//
//   PROBLEM — Floating glitch when spam-jumping:
//     Physics.CheckSphere (_isGrounded) still overlaps the ground for 1–2
//     frames after jumping because the character hasn't risen far enough yet.
//     During those frames _velocity.y > 0 so the -2f snap guard is false,
//     but _isGrounded is STILL true → pressing Jump again fires a SECOND
//     (third, fourth…) jump from the same position → stacked launch velocity
//     → character floats upward. Each extra jump resets _velocity.y to the
//     full launch speed, so gravity never gets a chance to pull them down.
//
//   PROBLEM — Other clients see float animation:
//     jumpSequence was incrementing on every spam-press (no guard), sending
//     rapid SetTrigger(Jump) calls to remote clients → rapid state restarts
//     → animator flickered into Jump state repeatedly while floating.
//
//   FIX 1 — Jump Lock (_hasJumped):
//     Set true the instant a jump fires. Blocks ALL further jumps until the
//     player is truly grounded with downward (or zero) velocity.
//     This is the CORE fix that kills the floating glitch.
//
//   FIX 2 — Jump Buffer (_jumpBufferTimer):
//     Records jump input for JUMP_BUFFER_TIME seconds (0.15 s). If the
//     player presses Jump just before landing, the buffer keeps the intent
//     alive and fires the moment they touch ground. Makes the jump feel
//     responsive without allowing spam-floating.
//     Common in: Celeste, Hollow Knight, all precision platformers.
//
//   FIX 3 — Coyote Time (_coyoteTimer):
//     For COYOTE_TIME seconds (0.10 s) after walking off a ledge, the
//     player can still jump even though _isGrounded is false. Prevents the
//     frustrating "I was on the edge and it didn't jump" feeling.
//     Common in: every well-regarded platformer since the 1990s.
//
//   ANIMATION SIDE EFFECT:
//     _hasJumped prevents spam from generating multiple jumpSequence
//     increments, so remote clients receive one clean SetTrigger(Jump)
//     per actual jump — no more rapid-fire trigger restarts causing float
//     animation on other clients.
//
// ── STUCK-AFTER-JUMP FIX (terminal velocity) ─────────────────────────────
//
//   PROBLEM — Character gets stuck or shudders on landing with high gravity:
//     With gravity = -25 (or steeper), _velocity.y can accumulate to extreme
//     negative values between grounded frames. Passing a very large downward
//     displacement to CharacterController.Move() in a single tick causes the
//     CC's internal CCD solver to overshoot, clip into floor geometry, or
//     enter a stuck-against-collider state. The result is a visible 1-3 frame
//     freeze where the character can't move immediately after landing.
//
//   FIX — Terminal velocity cap:
//     Clamp _velocity.y to terminalVelocity every frame after gravity is
//     applied. This prevents the CC from ever receiving a displacement large
//     enough to confuse its solver, regardless of how long the player was
//     airborne. Default -30 works well for -25 gravity fast-paced games.
//     Raise the magnitude for "floaty" games; lower for faster-falling ones.
//
// ── LANDING CAMERA BOB ────────────────────────────────────────────────────
//
//   Adds a physical "impact squish" feel when landing from a jump or fall.
//   The camera briefly dips down by an amount proportional to fall speed,
//   then springs back to normal position over landingBobRecovery seconds.
//   This is purely cosmetic and local to the owning client — no networking.
//
//   HOW IT WORKS:
//     On the exact landing frame (_wasGroundedLastFrame is false and
//     _isGrounded is now true, with sufficient downward velocity),
//     _landingBobOffset is set negative (camera dips). Crouch() lerps it
//     toward 0 every frame using landingBobRecovery as the spring speed.
//
//   TUNING:
//     landingBobAmount    = peak dip in metres. 0.06–0.12 feels natural.
//     landingBobRecovery  = spring speed. 10 = slow, 14 = snappy, 20 = instant.
//     LANDING_BOB_THRESHOLD = minimum fall speed (m/s, negative) to trigger.
//       -3 ignores tiny steps; -5 only fires on real intentional jumps.
//
// ── STAIR ANIMATION FIX (single CC.Move) ─────────────────────────────────
//
//   PROBLEM — Walk animation stops on stairs:
//     The old code called CC.Move() TWICE per frame — once for horizontal
//     movement, once for vertical (gravity). _lastMoveFlags only captured
//     the second call's CollisionFlags.
//     When CC auto-steps up a stair tread on the FIRST horizontal move,
//     CollisionFlags.Below is set there — but that result was DISCARDED.
//     The second (gravity-only) move after the step may not contact the
//     floor yet (character is 1 mm above the new tread for one frame).
//     Result: _lastMoveFlags has no Below → _isGrounded = false for 1-2
//     frames → IsAirborne() = true → CollectorAnimator step 10 sets Speed=0
//     → walk animation stops on every stair tread.
//
//   FIX — Single combined Move:
//     Horizontal and vertical displacement are combined into one Vector3 and
//     passed to CC.Move() in a single call. CollisionFlags.Below is now
//     correctly set on the exact frame the capsule contacts a stair tread,
//     keeping _isGrounded stable throughout stair climbing.
//
// ── NETWORK VARIABLE WRITE GUARD ─────────────────────────────────────────
//
//   _stats.isSprinting.Value is now only written when the value changes.
//   Without this guard it was written every frame while sprinting (~60/s),
//   dirtying the NetworkVariable and generating unnecessary replication
//   traffic. Owner-writable NVs still have a replication cost even from the
//   owner side. The guard reduces that to 2 writes per sprint (start + stop).

using Unity.Netcode;
using UnityEngine;

[DefaultExecutionOrder(-50)]   // must run before CollectorAnimator (order +50)
[RequireComponent(typeof(CharacterController))]
public class PlayerController : NetworkBehaviour
{
    [Header("Movement speeds")]
    public float walkSpeed   = 5f;
    public float sprintSpeed = 9f;
    public float crouchSpeed = 2.5f;

    [Header("Jump & gravity")]
    public float jumpHeight       = 1.5f;
    public float gravity          = -25f;   // intentionally high for fast-paced feel

    // ── TERMINAL VELOCITY FIX ─────────────────────────────────────────────────
    // Caps the downward component of _velocity so CharacterController.Move()
    // never receives a single-frame displacement large enough to confuse Unity's
    // CC CCD solver — which causes the 1-3 frame "stuck" freeze after landing
    // when gravity is steep (≤ -20).
    public float terminalVelocity = -30f;

    [Header("Crouch")]
    public float standHeight        = 2f;
    public float crouchHeight       = 1f;
    public float crouchLerp         = 8f;
    [Tooltip("How far the camera drops when crouching (negative = down). " +
             "Match this to roughly half the difference between standHeight and crouchHeight.")]
    public float crouchCameraOffset = -0.55f;

    [Header("Ground detection")]
    public Transform groundCheck;
    [Tooltip("Sphere radius for grounded detection. Used for jump/coyote logic. " +
             "Keep at ~0.3 for generous ground tolerance. " +
             "Landing animation uses HasGroundContact() (updated post-Move) for zero-frame accuracy.")]
    public float     groundRadius = 0.3f;
    public LayerMask groundMask;

    [Header("Look")]
    public Transform cameraHolder;
    public float     mouseSensitivity = 2f;

    [Header("Landing feel")]
    [Tooltip("Peak camera dip on landing, in metres.\n" +
             "Scales with fall speed — gentle step barely dips; long fall hits full amount.\n" +
             "Set 0 to disable entirely.")]
    public float landingBobAmount   = 0.08f;

    [Tooltip("Spring-back speed after the landing dip.\n" +
             "10 = slow recovery, 14 = snappy, 20 = nearly instant.")]
    public float landingBobRecovery = 14f;

    // Used by CollectorController to apply candy penalty / superspeed on top
    [HideInInspector] public float speedMultiplier = 1f;

    private CharacterController _cc;
    private PlayerStats         _stats;

    private Vector3 _velocity;
    private float   _xRot;
    private bool    _isGrounded;
    private bool    _isCrouching;
    private bool    _isSprinting;
    private float   _airSpeed;

    // CollisionFlags from the CURRENT frame's combined CC.Move.
    // Kept as a field so _ccContact can be updated at the END of Move()
    // without needing to pass it as a return value.
    private CollisionFlags _lastMoveFlags;

    // ── Zero-frame landing contact ─────────────────────────────────────────────
    // Updated at the BOTTOM of Move(), AFTER CC.Move() runs.
    // This ensures HasGroundContact() returns true on the exact landing frame —
    // no one-frame delay. CollectorAnimator reads this via HasGroundContact().
    // DO NOT read this before CC.Move() runs (it reflects post-move collision).
    private bool _ccContact;

    // Stores the camera's local position when standing so we always lerp
    // back to the exact same place regardless of where cameraHolder starts.
    private Vector3 _camDefaultLocalPos;

    // FIX (sinking): stores the CharacterController's center while standing
    // so Crouch() can shift it DOWN when crouching to keep the capsule bottom
    // pinned to the floor (prevents the "sinking" visual artifact).
    private Vector3 _standingCCCenter;

    // ── Professional Jump System ──────────────────────────────────────────────
    private const float JUMP_BUFFER_TIME = 0.15f;
    private const float COYOTE_TIME      = 0.10f;

    // ── Tight-radius contact threshold ────────────────────────────────────────
    // Much smaller than groundRadius so the Land animation trigger fires on the
    // exact frame the capsule bottom visually touches the floor, not 0.3 m early.
    // Set to ~CC skinWidth (default 0.08 m).
    // Only used for HasGroundContact() — NOT for jump/coyote logic.
    private const float LANDING_CONTACT_RADIUS = 0.08f;

    private float _jumpBufferTimer;
    private float _coyoteTimer;
    private bool  _hasJumped;

    // ── Landing camera bob ────────────────────────────────────────────────────
    private float _landingBobOffset;
    private bool  _wasGroundedLastFrame;

    // Minimum downward velocity (m/s, negative) to trigger any landing bob.
    private const float LANDING_BOB_THRESHOLD  = -3f;

    // Fall speed at which the bob reaches its full landingBobAmount.
    private const float LANDING_BOB_FULL_SPEED = -20f;

    private void Awake()
    {
        _cc    = GetComponent<CharacterController>();
        _stats = GetComponent<PlayerStats>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            if (cameraHolder != null)
            {
                Camera cam = cameraHolder.GetComponentInChildren<Camera>();
                if (cam != null) cam.gameObject.SetActive(false);
                AudioListener al = cameraHolder.GetComponentInChildren<AudioListener>();
                if (al != null) al.enabled = false;
            }
            enabled = false;
            return;
        }

        Cursor.lockState = CursorLockMode.Locked;
        _cc.height = standHeight;
        _standingCCCenter = _cc.center;

        if (cameraHolder != null)
            _camDefaultLocalPos = cameraHolder.localPosition;
    }

    private void Update()
    {
        if (!IsOwner || _stats.IsDead()) return;

        Look();
        Move();
        Crouch();
    }

    private void Look()
    {
        float mx = Input.GetAxis("Mouse X") * mouseSensitivity;
        float my = Input.GetAxis("Mouse Y") * mouseSensitivity;

        _xRot = Mathf.Clamp(_xRot - my, -90f, 90f);
        cameraHolder.localRotation = Quaternion.Euler(_xRot, 0f, 0f);
        transform.Rotate(Vector3.up * mx);
    }

    private void Move()
    {
        // Capture velocity BEFORE any grounded snap so landing bob reads the
        // true fall speed, not the clamped -2f value written below.
        float velYAtFrameStart = _velocity.y;

        // ── Ground check position ─────────────────────────────────────────────
        Vector3 checkPos = groundCheck != null
            ? groundCheck.position
            : transform.position + Vector3.down * (_cc.height * 0.5f);

        // ── _isGrounded: generous 0.3 m sphere ───────────────────────────────
        // Used for: jump execution, coyote time, velocity snap, sprint detection.
        // Intentionally generous — catches the floor even when walking off ledge.
        // Also OR's with PREVIOUS frame's CollisionFlags.Below as a fallback so
        // the first CC contact frame is never missed by the sphere alone.
        _isGrounded = Physics.CheckSphere(checkPos, groundRadius, groundMask,
                          QueryTriggerInteraction.Ignore)
                   || (_lastMoveFlags & CollisionFlags.Below) != 0;

        // ── Landing camera bob — trigger ──────────────────────────────────────
        bool justLanded = _isGrounded && !_wasGroundedLastFrame
                       && velYAtFrameStart < LANDING_BOB_THRESHOLD;

        if (justLanded)
        {
            float t = Mathf.Clamp01(velYAtFrameStart / LANDING_BOB_FULL_SPEED);
            _landingBobOffset = -(t * landingBobAmount);
        }

        _wasGroundedLastFrame = _isGrounded;

        if (_isGrounded && _velocity.y < 0f)
            _velocity.y = -2f;

        // ── Coyote time ───────────────────────────────────────────────────────
        if (_isGrounded && !_hasJumped)
            _coyoteTimer = COYOTE_TIME;
        else if (_coyoteTimer > 0f)
            _coyoteTimer -= Time.deltaTime;

        // ── Jump buffer ───────────────────────────────────────────────────────
        if (Input.GetButtonDown("Jump"))
            _jumpBufferTimer = JUMP_BUFFER_TIME;
        else if (_jumpBufferTimer > 0f)
            _jumpBufferTimer -= Time.deltaTime;

        // ── Jump lock: reset on true landing ──────────────────────────────────
        if (_isGrounded && _velocity.y <= 0f)
            _hasJumped = false;

        // ── Execute jump ──────────────────────────────────────────────────────
        bool canJump    = (_isGrounded || _coyoteTimer > 0f) && !_hasJumped && !_isCrouching;
        bool jumpWanted = _jumpBufferTimer > 0f;

        if (jumpWanted && canJump)
        {
            _velocity.y      = Mathf.Sqrt(jumpHeight * -2f * gravity);
            _jumpBufferTimer = 0f;
            _coyoteTimer     = 0f;
            _hasJumped       = true;
            _stats.jumpSequence.Value++;
        }

        // ── Horizontal movement ───────────────────────────────────────────────
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        bool hasMovementInput = Mathf.Abs(h) > 0.01f || Mathf.Abs(v) > 0.01f;

        // ── Write animation NVs (owner-only) ──────────────────────────────────
        //
        // isMovingNV and localMoveDir let ShooterAnimator drive the 3rd-person
        // body on remote clients without using position-delta velocity, which
        // oscillates at NetworkTransform update boundaries and causes the
        // "animation going back and forth" visual artifact.
        //
        // Both NVs are guarded so they are only written when the value actually
        // changes — avoiding unnecessary replication dirty-marking every frame.

        // isMovingNV — true while the owner has movement input.
        //
        // ── WHY _isGrounded IS REMOVED ────────────────────────────────────────
        // The previous check (hasMovementInput && _isGrounded) caused the
        // "animation going back and forth" bug on non-owner clients:
        //
        //   • _isGrounded uses a generous 0.3 m sphere, which briefly returns
        //     false for 1-2 frames when the capsule rides over slightly bumpy
        //     terrain (small ramps, seams between mesh tiles, etc.).
        //   • Each brief false toggles isMovingNV → NV replicates immediately
        //     → non-owner's ShooterAnimator reads isMovingNV = false
        //     → targetSpeed snaps to 0 → _smoothedSpeed dips toward Idle
        //     → next frame _isGrounded is true again → targetSpeed = 1 or 2
        //     → _smoothedSpeed climbs back → visible flicker on every step.
        //
        // Airborne locomotion suppression on non-owners is already handled in
        // ShooterAnimator via isAirborneForLocomotion (_jumpForceAirborneTimer),
        // which only activates on an actual jumpSequence increment — never on
        // ground-sphere noise. The IsGrounded animator bool and Jump state-
        // machine transitions also remain unaffected (they use separate paths).
        // Removing _isGrounded here eliminates the flicker source entirely.
        bool isCurrentlyMoving = hasMovementInput;
        if (isCurrentlyMoving != _stats.isMovingNV.Value)
            _stats.isMovingNV.Value = isCurrentlyMoving;

        // localMoveDir — raw (h, v) in local space for the crouch blend tree.
        // Written with a 0.05 magnitude guard so micro-jitter on an analog stick
        // does not dirty the NV every frame.
        if (hasMovementInput)
        {
            Vector2 dir = new Vector2(h, v);
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            if (Vector2.Distance(dir, _stats.localMoveDir.Value) > 0.05f)
                _stats.localMoveDir.Value = dir;
        }
        else if (_stats.localMoveDir.Value != Vector2.zero)
        {
            _stats.localMoveDir.Value = Vector2.zero;
        }

        // isGroundedNV — mirrors _isGrounded for ShooterAnimator on remote clients.
        // Guarded so it only dirties the NV on an actual state change (2 writes per
        // ground contact event, not 60/s). ShooterAnimator reads this instead of
        // deriving airborne state from position-delta Y-velocity, which is noisy at
        // NetworkTransform update boundaries and causes IsGrounded to flicker on slopes.
        if (_isGrounded != _stats.isGroundedNV.Value)
            _stats.isGroundedNV.Value = _isGrounded;

        bool wantSprint = _isGrounded && Input.GetKey(KeyCode.LeftShift) && !_isCrouching && hasMovementInput;

        // ── Only write NetworkVariable when sprint state actually changes ──────
        if (wantSprint != _isSprinting)
        {
            _isSprinting             = wantSprint;
            _stats.isSprinting.Value = wantSprint;
        }

        float speed;
        if (_isGrounded)
        {
            speed  = _isCrouching ? crouchSpeed
                   : _isSprinting ? sprintSpeed
                   : walkSpeed;
            speed *= _stats.speedMultiplier * speedMultiplier;
            _airSpeed = speed;
        }
        else
        {
            speed = _airSpeed;
        }

        // ── Gravity ───────────────────────────────────────────────────────────
        _velocity.y += gravity * Time.deltaTime;

        // ── Terminal velocity cap (stuck-after-jump fix) ───────────────────────
        _velocity.y = Mathf.Max(_velocity.y, terminalVelocity);

        // ── Single combined CC.Move (stair fix) ───────────────────────────────
        // Combines horizontal + vertical into one call. CollisionFlags.Below is
        // set on the SAME frame the capsule contacts a stair tread.
        Vector3 motion = (transform.right * h + transform.forward * v) * speed * Time.deltaTime;
        motion.y = _velocity.y * Time.deltaTime;
        _lastMoveFlags = _cc.Move(motion);

        // ── Post-move velocity snaps ───────────────────────────────────────────
        // Floor: prevents the large negative velocity from persisting into the
        //        next frame's gravity accumulation (eliminates depenetration bounce).
        if ((_lastMoveFlags & CollisionFlags.Below) != 0 && _velocity.y < 0f)
            _velocity.y = -2f;

        // Ceiling: zero upward velocity so the character doesn't stick for 1-2 frames.
        if ((_lastMoveFlags & CollisionFlags.Above) != 0 && _velocity.y > 0f)
            _velocity.y = 0f;

        // ── _ccContact: updated HERE, after CC.Move, for zero-frame delay ──────
        //
        // Why here and not at the top of Move():
        //   At the top, _lastMoveFlags is from the PREVIOUS frame. On the exact
        //   landing frame, it has no CollisionFlags.Below yet — so _ccContact
        //   would be false for one extra frame, causing a 1-frame delay in
        //   HasGroundContact() and therefore in CollectorAnimator's IsGrounded.
        //
        //   By updating _ccContact AFTER CC.Move(), we use THIS frame's collision
        //   result. The capsule has already moved to its final position, so:
        //     • CheckSphere at the new position accurately reflects contact
        //     • _lastMoveFlags has CollisionFlags.Below if the floor was hit
        //   → HasGroundContact() returns true on the SAME frame as landing. ✓
        //
        //   _isGrounded (above) stays at the top of Move() — its generous 0.3 m
        //   radius is intentional for jump/coyote logic and is not affected.
        {
            Vector3 contactPos = groundCheck != null
                ? groundCheck.position
                : transform.position + Vector3.down * (_cc.height * 0.5f);

            _ccContact = Physics.CheckSphere(contactPos, LANDING_CONTACT_RADIUS, groundMask,
                             QueryTriggerInteraction.Ignore)
                      || (_lastMoveFlags & CollisionFlags.Below) != 0;
        }
    }

    private void Crouch()
    {
        bool wantCrouch = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C);

        if (wantCrouch && !_isCrouching)
        {
            _isCrouching = true;
            _stats.isCrouching.Value = true;
        }
        else if (!wantCrouch && _isCrouching)
        {
            Vector3 capsuleTop  = transform.position + Vector3.up * (_cc.center.y + _cc.height * 0.5f);
            float   clearNeeded = standHeight - crouchHeight;

            if (!Physics.Raycast(capsuleTop, Vector3.up, clearNeeded))
            {
                _isCrouching = false;
                _stats.isCrouching.Value = false;
            }
        }

        if (_isCrouching)
        {
            _cc.height = crouchHeight;

            float heightDiff = standHeight - crouchHeight;
            _cc.center = new Vector3(
                _standingCCCenter.x,
                _standingCCCenter.y - heightDiff * 0.5f,
                _standingCCCenter.z);
        }
        else
        {
            _cc.height = Mathf.Lerp(_cc.height, standHeight,        crouchLerp * Time.deltaTime);
            _cc.center = Vector3.Lerp(_cc.center, _standingCCCenter, crouchLerp * Time.deltaTime);
        }

        if (cameraHolder != null)
        {
            // ── Landing bob spring ─────────────────────────────────────────────
            _landingBobOffset = Mathf.Lerp(_landingBobOffset, 0f, landingBobRecovery * Time.deltaTime);

            float targetY = _isCrouching
                ? _camDefaultLocalPos.y + crouchCameraOffset
                : _camDefaultLocalPos.y;

            Vector3 target = new Vector3(
                _camDefaultLocalPos.x,
                Mathf.Lerp(cameraHolder.localPosition.y, targetY + _landingBobOffset, crouchLerp * Time.deltaTime),
                _camDefaultLocalPos.z);

            cameraHolder.localPosition = target;
        }
    }

    // ── Public getters ────────────────────────────────────────────────────────

    public bool IsSprinting() => _isSprinting;
    public bool IsCrouching() => _isCrouching;
    public bool IsGrounded()  => _isGrounded;
    public bool IsJumping()   => !_isGrounded && _velocity.y > 0f;
    public bool IsFalling()   => !_isGrounded && _velocity.y < -1f;
    public bool IsAirborne()  => !_isGrounded;

    /// <summary>
    /// True when the tight-radius contact sphere (LANDING_CONTACT_RADIUS = 0.08 m)
    /// overlaps the ground layer, OR when CC.Move() reported a floor collision
    /// this frame (CollisionFlags.Below).
    ///
    /// Updated AFTER CC.Move() so it reflects THIS frame's collision — zero-frame
    /// delay compared to the old placement at the top of Move().
    ///
    /// CollectorAnimator uses this (via [DefaultExecutionOrder(50)], which runs
    /// after this script's [DefaultExecutionOrder(-50)]) so it always reads the
    /// current frame's result. The IsGrounded animator Bool is set from this.
    /// </summary>
    public bool HasGroundContact() => _ccContact;

    // ── Spawn warp ────────────────────────────────────────────────────────────
    [Rpc(SendTo.Owner)]
    public void WarpToSpawnRpc(Vector3 position, Quaternion rotation)
    {
        _cc.enabled = false;
        transform.position = position;
        transform.rotation = rotation;
        _xRot     = 0f;
        _velocity = Vector3.zero;

        _airSpeed      = 0f;
        _lastMoveFlags = CollisionFlags.None;
        _ccContact     = false;   // clear so no spurious landing signal on first frame

        if (_isCrouching)
        {
            _isCrouching             = false;
            _stats.isCrouching.Value = false;
            _cc.height               = standHeight;
            _cc.center               = _standingCCCenter;
            if (cameraHolder != null)
                cameraHolder.localPosition = _camDefaultLocalPos;
        }

        if (_isSprinting)
        {
            _isSprinting             = false;
            _stats.isSprinting.Value = false;
        }

        _hasJumped       = false;
        _jumpBufferTimer = 0f;
        _coyoteTimer     = 0f;

        // Clear animation NVs so remote clients don't see a stale "moving" state
        // on the first frame after a respawn warp.
        _stats.isMovingNV.Value   = false;
        _stats.localMoveDir.Value = Vector2.zero;
        _stats.isGroundedNV.Value = false;   // will be corrected on first Move() frame

        // Clear landing bob so a warp/respawn never plays a spurious dip.
        _landingBobOffset     = 0f;
        _wasGroundedLastFrame = false;

        _cc.enabled = true;
        Debug.Log($"[PlayerController] Warped to {position}, rot {rotation.eulerAngles}");
    }
}