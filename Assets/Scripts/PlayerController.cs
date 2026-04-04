// PlayerController.cs
// Sugar Rush
// Unity 6.3 LTS + Netcode for GameObjects v2.1+
//
// Handles local player movement, look, jump, crouch.
// Owner-only. Non-owners are disabled immediately.
//
// INSPECTOR SETUP:
//   groundCheck  — empty child GameObject placed at the player's feet
//   groundMask   — LayerMask set to your "Ground" layer
//   cameraHolder — the child Transform that holds the Camera
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
// ── LAND-FIRES-EARLY FIX (HasGroundContact) ──────────────────────────────
//
//   PROBLEM — Land animation fires before visual feet touch ground:
//     _isGrounded uses Physics.CheckSphere with groundRadius = 0.3 m.
//     The sphere fires when the floor is within 0.3 m of groundCheck —
//     meaning IsAirborne() returns false up to 0.3 m BEFORE the character's
//     feet visually reach the surface. CollectorAnimator reads IsAirborne()
//     for the landing signal, so the Land trigger fires too early.
//
//   FIX — HasGroundContact():
//     Exposes (_lastMoveFlags & CollisionFlags.Below) as a separate getter.
//     The CC's own collision solver sets CollisionFlags.Below on the exact
//     frame the capsule bottom contacts the floor — matching what the player
//     sees. CollectorAnimator uses HasGroundContact() for owner landing
//     detection instead of IsAirborne(), giving frame-perfect Land triggers.
//     CheckSphere (_isGrounded) is kept for jump/coyote/movement logic where
//     the generous radius is a feature, not a bug.

using Unity.Netcode;
using UnityEngine;

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
             "Landing animation uses HasGroundContact() (CC collision only) for frame-perfect accuracy.")]
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
    // Used alongside CheckSphere for _isGrounded, and exposed via
    // HasGroundContact() for frame-perfect landing animation detection.
    private CollisionFlags _lastMoveFlags;

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

    private float _jumpBufferTimer;
    private float _coyoteTimer;
    private bool  _hasJumped;

    // ── Landing camera bob ────────────────────────────────────────────────────
    // _landingBobOffset: current additional camera Y offset.
    //   Set negative on the landing frame, lerped toward 0 every frame (spring).
    // _wasGroundedLastFrame: detects the airborne→grounded transition.
    private float _landingBobOffset;
    private bool  _wasGroundedLastFrame;

    // Minimum downward velocity (m/s, negative) to trigger any landing bob.
    // -3 m/s ignores gentle steps; real jump landings always exceed this.
    private const float LANDING_BOB_THRESHOLD = -3f;

    // Fall speed at which the bob reaches its full landingBobAmount.
    // Falls faster than this are clamped so the camera never over-dips.
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

        // ── Ground check ──────────────────────────────────────────────────────
        // Use BOTH CheckSphere AND last frame's CollisionFlags.Below.
        // CheckSphere alone can miss the landing frame when the sphere just barely
        // doesn't reach the floor (e.g. character 0.31 m up, sphere radius 0.30 m).
        // CollisionFlags.Below fills that gap — if CC.Move hit the floor last frame,
        // we treat this frame as grounded immediately so the velocity snap fires.
        Vector3 checkPos = groundCheck != null
            ? groundCheck.position
            : transform.position + Vector3.down * (_cc.height * 0.5f);

        _isGrounded = Physics.CheckSphere(checkPos, groundRadius, groundMask, QueryTriggerInteraction.Ignore)
                   || (_lastMoveFlags & CollisionFlags.Below) != 0;

        // ── Landing camera bob — trigger ──────────────────────────────────────
        // Fires on the exact frame we transition from airborne → grounded,
        // provided the fall was fast enough to feel like a real landing.
        bool justLanded = _isGrounded && !_wasGroundedLastFrame
                       && velYAtFrameStart < LANDING_BOB_THRESHOLD;

        if (justLanded)
        {
            // Scale the dip proportionally to fall speed.
            // Clamp01 so extremely fast falls don't push the camera into geometry.
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
        _isSprinting = _isGrounded && Input.GetKey(KeyCode.LeftShift) && !_isCrouching && hasMovementInput;

        _stats.isSprinting.Value = _isSprinting;

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

        // ── TERMINAL VELOCITY CAP (stuck-after-jump fix) ──────────────────────
        // Clamps the downward velocity so CharacterController.Move() never
        // receives a displacement large enough to confuse the CC CCD solver.
        _velocity.y = Mathf.Max(_velocity.y, terminalVelocity);

        // ── STAIR FIX: Single combined CC.Move ────────────────────────────────
        //
        // OLD: Two separate CC.Move calls (horizontal first, vertical second).
        //   _lastMoveFlags only captured the second (gravity-only) call.
        //   When the CC auto-stepped up a stair on the first (horizontal) call,
        //   CollisionFlags.Below was set there but DISCARDED. The second call
        //   after the step might not contact the floor yet (1 frame gap while
        //   the character is 1 mm above the new tread). Result: _lastMoveFlags
        //   had no Below → _isGrounded = false for 1-2 frames per stair tread
        //   → IsAirborne() = true → walk animation stopped on every step.
        //
        // FIX: Combine horizontal and vertical into one Vector3, one CC.Move.
        //   CollisionFlags.Below is now correctly set on the SAME frame the
        //   capsule contacts a stair tread, keeping _isGrounded = true
        //   throughout stair climbing and preventing animation flickers.
        Vector3 motion = (transform.right * h + transform.forward * v) * speed * Time.deltaTime;
        motion.y = _velocity.y * Time.deltaTime;
        _lastMoveFlags = _cc.Move(motion);

        // ── Same-frame collision velocity snap ────────────────────────────────
        // CollisionFlags.Below is set by CC.Move in the SAME FRAME the floor is
        // hit. Snapping here prevents the large negative velocity from persisting
        // into the next frame's gravity accumulation, eliminating the
        // depenetration bounce entirely.
        if ((_lastMoveFlags & CollisionFlags.Below) != 0 && _velocity.y < 0f)
            _velocity.y = -2f;

        // Ceiling hit: zero upward velocity immediately so the character doesn't
        // stick to ceilings for 1-2 frames while gravity catches up.
        if ((_lastMoveFlags & CollisionFlags.Above) != 0 && _velocity.y > 0f)
            _velocity.y = 0f;
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
            // Lerp the offset toward 0 every frame. No coroutine, no allocation.
            // Naturally composes with the crouch camera offset below.
            _landingBobOffset = Mathf.Lerp(_landingBobOffset, 0f, landingBobRecovery * Time.deltaTime);

            float targetY = _isCrouching
                ? _camDefaultLocalPos.y + crouchCameraOffset
                : _camDefaultLocalPos.y;

            // Add the bob on top of the crouch target so both effects can be
            // active simultaneously (e.g. landing while crouched still feels right).
            Vector3 target = new Vector3(
                _camDefaultLocalPos.x,
                Mathf.Lerp(cameraHolder.localPosition.y, targetY + _landingBobOffset, crouchLerp * Time.deltaTime),
                _camDefaultLocalPos.z);

            cameraHolder.localPosition = target;
        }
    }

    public bool IsSprinting() => _isSprinting;
    public bool IsCrouching() => _isCrouching;
    public bool IsGrounded()  => _isGrounded;
    public bool IsJumping()   => !_isGrounded && _velocity.y > 0f;
    public bool IsFalling()   => !_isGrounded && _velocity.y < -1f;
    public bool IsAirborne()  => !_isGrounded;

    /// <summary>
    /// True on the exact frame CC.Move() reported a floor contact (CollisionFlags.Below).
    ///
    /// MORE ACCURATE THAN IsAirborne() for landing animation detection:
    ///   IsAirborne() uses _isGrounded which includes Physics.CheckSphere with
    ///   groundRadius = 0.3 m — that sphere fires up to 0.3 m BEFORE the capsule
    ///   visually touches the floor, causing the Land trigger to fire early.
    ///
    ///   HasGroundContact() uses the CC's own collision solver (CollisionFlags.Below),
    ///   which is set on the exact frame the capsule bottom contacts the floor.
    ///   CollectorAnimator uses this for owner landing detection so the Land
    ///   trigger fires on the visually correct frame.
    ///
    ///   IsAirborne() (CheckSphere) is kept for jump / coyote / movement logic
    ///   where the generous detection radius is intentional — it prevents the
    ///   player from being stranded when they walk off a ledge's edge by a hair.
    /// </summary>
    public bool HasGroundContact() => (_lastMoveFlags & CollisionFlags.Below) != 0;

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
        _lastMoveFlags = CollisionFlags.None; // clear stale collision state from before warp

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

        // Clear landing bob so a warp/respawn never plays a spurious dip.
        _landingBobOffset     = 0f;
        _wasGroundedLastFrame = false;

        _cc.enabled = true;
        Debug.Log($"[PlayerController] Warped to {position}, rot {rotation.eulerAngles}");
    }
}