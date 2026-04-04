// CollectorAnimator.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Drives the Collector body Animator on ALL clients (owner + non-owner).
//
// ── CHANGES IN THIS VERSION ───────────────────────────────────────────────
//
//   FIX (BUG G): Land animation fires before feet visually touch ground.
//
//     ROOT CAUSE:
//       The owner's landing signal was derived from isAirborne, which relies
//       on PlayerController.IsAirborne() = !_isGrounded. _isGrounded uses
//       Physics.CheckSphere with groundRadius = 0.3 m. The sphere fires when
//       the floor surface is within 0.3 m of the groundCheck pivot — meaning
//       _isGrounded becomes true (IsAirborne() returns false) while the
//       character's visual feet are still 0.3 m above the ground.
//       CollectorAnimator read this as "landed" → SetTrigger(Land) fired
//       ~0.3 m too early → Land animation played while the character was
//       visually still in the air.
//
//     FIX: Use PlayerController.HasGroundContact() for owner landing signal.
//       HasGroundContact() exposes (CollisionFlags.Below != 0) from the CC's
//       own physics solver. It fires on the exact frame the capsule bottom
//       contacts the floor — no early-sphere offset — matching what the player
//       sees on screen. _jumpForceAirborneTimer still overrides HasGroundContact
//       during the 0.12 s launch window so jump→air stays correct.
//
//   FIX (BUG H): Walk animation stops playing when walking on stairs.
//
//     ROOT CAUSE A (primary — PlayerController):
//       PlayerController called CC.Move() TWICE per frame: once for horizontal
//       movement, once for vertical (gravity). _lastMoveFlags only captured
//       the second (gravity-only) call's CollisionFlags. When the CC
//       auto-stepped up a stair tread on the FIRST horizontal move,
//       CollisionFlags.Below was set on that call — but discarded. The second
//       call after the step could miss Below for 1-2 frames (character briefly
//       0-1 mm above the new tread). Result: _isGrounded flickered false
//       per tread → IsAirborne() = true → step 10 set Speed = 0 → walk stops.
//       FIXED IN PlayerController.cs: single combined CC.Move per frame.
//
//     ROOT CAUSE B (secondary — CollectorAnimator):
//       step 10 had no tolerance for owner-side single-frame ground misses.
//       Even with the single-Move fix, micro-geometry edges or unusual stair
//       meshes can cause a 1-frame _isGrounded drop. Without a buffer, every
//       such frame killed the walk animation.
//
//     FIX B: _ownerLocomotionAirborneBuffer (0.05 s).
//       When the owner IS grounded (rawAirborne = false), the buffer is held
//       at OWNER_LOCOMOTION_AIRBORNE_BUFFER (0.05 s). As soon as the owner
//       becomes airborne, the buffer drains. Speed = 0 only when rawAirborne
//       has been true for longer than the buffer — so 1-2 frame stair-edge
//       misses (< 0.05 s) never reach step 10.
//       On a REAL JUMP: jumpJustFired immediately zeroes the buffer so the
//       walk-to-airborne transition happens on the same frame as the Jump
//       trigger — no delay.
//       Applied ONLY to the Speed blend tree (step 10). Land detection
//       (step 7) still uses HasGroundContact() / isAirborne directly so
//       the Land trigger fires on the exact physics frame.
//
//   FIX: IsAirborne Bool was false for 2-3 frames after the Jump trigger fired.
//
//     ROOT CAUSE (owner):
//       PlayerController._isGrounded is computed at the TOP of Move() using
//       CheckSphere against the PREVIOUS frame's position. On the jump frame the
//       character hasn't physically risen yet. With groundRadius = 0.3 m and
//       jumpVelocity ≈ 8.66 m/s, the character needs 2-3 frames (≈50 ms) to clear
//       the sphere. During those frames:
//         _isGrounded = true → IsAirborne() = false → SetBool(IsAirborne, false)
//       But jumpSequence.Value has already been incremented, so step 8 fires
//       SetTrigger(Jump). If ANY animator transition uses !IsAirborne as an exit
//       condition from jump states, it immediately aborts Jump_Start and snaps back
//       to Locomotion.
//
//     ROOT CAUSE (non-owner):
//       rawAirborne is derived from Mathf.Abs(_smoothedYVel) > airborneYThreshold.
//       With yVelSmoothFactor = 20, _smoothedYVel ramps up over 1-2 frames. When
//       jumpSequence.Value arrives from the network (which can arrive BEFORE any
//       Y-position delta is visible), rawAirborne is still false — same race as owner.
//
//     FIX: _jumpForceAirborneTimer (new field)
//       Detects the jump via (jumpSequence.Value != _lastJumpSequence) in step 6,
//       before _lastJumpSequence is updated in step 8. Forces rawAirborne = true for
//       JUMP_FORCE_AIRBORNE_TIME (0.12 s). Both SetBool(IsAirborne, true) and
//       SetTrigger(Jump) now fire on the SAME frame, eliminating the race.
//       0.12 s is safely shorter than minimum air time (~0.69 s with these settings).
//
//   FIX: _airborneElapsed not reset in teleport-detection block.
//     After a respawn-while-airborne, if the teleport block runs before OnRespawn()
//     is called, a stale _airborneElapsed could exceed MIN_AIRBORNE_FOR_LAND and
//     fire a spurious Land trigger on the first post-respawn frame.
//     _airborneElapsed and _jumpForceAirborneTimer are now both reset there.
//
//   FIX: Jump_Loop → Jump_Land "teleporting" position snap:
//     ROOT CAUSE 1 (primary):
//       If Apply Root Motion is enabled on the Animator and the Jump_Land clip
//       has any vertical/forward root delta (a landing impact squat baked into
//       the root), Unity moves the character mesh by that delta every frame.
//       CharacterController holds the character at its physics position. On the
//       next CC update the mesh snaps back — a visible position "teleport".
//       FIX: OnAnimatorMove() discards all root motion; CC owns all movement.
//
//     ROOT CAUSE 2 (secondary):
//       The airborne hold-buffer was applied to the OWNER as well as non-owners:
//         isAirborne = rawAirborne || _airborneBuffer > 0f
//       On the landing frame the buffer kept IsAirborne=true for 2-3 extra
//       frames after Land trigger was already queued. During those frames
//       Jump_Loop continued running while Jump_Land's blend tried to start —
//       the two states fought and produced a visible pop/snap.
//       FIX: owner now uses rawAirborne directly (exact physics frame).
//            Only non-owners use the buffer (to bridge 30 Hz NT dead frames).
//
//     ROOT CAUSE 3 (minor):
//       Jump_Loop → Jump_Land transition blend was 0.06s (~3 frames at 60 Hz).
//       Too short to smooth a large pose difference between the loop and land.
//       FIX: increase blend duration to 0.15s in the Animator.
//            (See 3-STATE JUMP ANIMATOR SETUP below.)
//
//     ROOT CAUSE 4 (minor):
//       No minimum airborne duration guard on the Land trigger, so stair steps
//       and 1 cm ledges (which briefly set !isGrounded) played a spurious
//       land-crouch animation on every step down.
//       FIX: MIN_AIRBORNE_FOR_LAND = 0.06s guard added.
//
//   FIX: Stuck / delayed landing animation with high gravity (e.g. -25):
//     OLD: Landing was detected from the buffer-delayed `isAirborne` bool.
//          With airborneHoldTime = 0.15s the Land trigger fired 150ms AFTER
//          the player physically touched the ground. During those 150ms, the
//          animator was still in Jump_Loop even though the character was
//          visibly on the floor — it looked "stuck" in the air pose.
//     FIX: For the LOCAL OWNER the landing signal now uses HasGroundContact()
//          (CC collision only), so the Land trigger fires on the EXACT frame
//          feet touch the ground. Non-owners still use the buffered isAirborne
//          because their rawAirborne is an estimate from NT position-delta.
//
//   FIX: airborneHoldTime tuned DOWN from 0.15 s → 0.05 s:
//     With gravity = -25 the NT Y-velocity signal updates quickly enough
//     that 0.05 s (3 frames at 60 Hz) is sufficient to bridge dead frames
//     for non-owners. The smaller buffer means the airborne-to-grounded
//     transition is much snappier for everyone.
//
// ── BUG HISTORY (previous versions) ──────────────────────────────────────
//
//   BUG 1  — Sprint always shows walk animation (runThreshold heuristic)
//   BUG 2  — Jump animation stutters (isAirborne toggling off between NT ticks)
//   BUG 3  — Walk flash at jump start / crouch entry
//   BUG 4  — Non-owner walk detection flickers at 30 Hz NT tick rate
//   BUG 5  — Crouch blend tree shows stale direction after un-crouching
//   BUG 6  — Run animation flashes 1 frame on spawn / respawn (teleport)
//   BUG 7  — Death state missing when joining mid-game while player is dead
//   BUG 8  — PickUp animation / counter stale after respawn
//   BUG 9  — Superspeed keeps FastRun playing while standing still
//   BUG 10 — Crouch-walk forward causes visible character shaking
//   BUG 11 — Jump trigger silently disappears on non-owners (CRITICAL)
//   BUG 12 — Airborne apex dead zone causes owner flicker
//   BUG 13 — Ghost jump trigger fires immediately on respawn
//   BUG A  — Jump animation replays on every landing
//   BUG B  — Superspeed transition infinite loop while standing still
//   BUG C  — Superspeed entry shows visible walk→run blend
//   BUG D  — 3-State Jump: Land trigger stale on respawn / re-enable
//   BUG E  — IsAirborne Bool false for 2-3 frames after Jump trigger fires  ← fixed prev
//   BUG F  — _airborneElapsed carries stale value through teleport            ← fixed prev
//   BUG G  — Land animation fires before visual feet touch ground             ← FIXED NOW
//   BUG H  — Walk animation stops playing on stairs                           ← FIXED NOW
//   (All bugs above are fixed.)
//
// ── ANIMATOR PARAMETERS (exact names, case-sensitive) ─────────────────────
//   Float   "Speed"        — 0=Idle  1=Walk  2=FastRun  (1D Blend Tree)
//   Float   "CrouchMoveX" — local X velocity when crouching  (-1 to 1)
//   Float   "CrouchMoveY" — local Z velocity when crouching  (-1 to 1)
//   Bool    "IsCrouching"  — transitions to CrouchMovement state
//   Bool    "IsSuperspeed" — transitions to FastRun state
//   Bool    "IsAirborne"   — drives Jump_Loop looping
//   Bool    "IsPickingUp"  — transitions to PickUp state
//   Bool    "IsDead"       — locks into Death state
//   Trigger "Die"          — fires once on death event
//   Trigger "Jump"         — fires once per real jump (owner + non-owners)
//   Trigger "Land"         — fires on the EXACT CC-contact frame (owner)
//                            or within 0.05s buffer (non-owners)
//
// ── 3-STATE JUMP ANIMATOR SETUP ───────────────────────────────────────────
//   States:   Jump_Start (once) → Jump_Loop (loops) → Jump_Land (once)
//   AnyState → Jump_Start:  Trigger "Jump",  duration 0.05s
//   Jump_Start → Jump_Loop: HasExitTime=true, ExitTime=0.75, duration 0.08s
//   Jump_Loop → Jump_Land:  Trigger "Land",  HasExitTime=false, duration 0.15s
//   Jump_Land → Locomotion: HasExitTime=true, ExitTime=0.85, duration 0.12s
//
//   IMPORTANT: Do NOT add !IsAirborne as an exit condition on Jump_Start or
//   Jump_Loop. The Land trigger alone drives the exit from the jump loop.
//   IsAirborne is used to HOLD the loop and is set true before the Jump
//   trigger fires (see _jumpForceAirborneTimer fix above).
// ─────────────────────────────────────────────────────────────────────────

using UnityEngine;

[RequireComponent(typeof(Animator))]
public class CollectorAnimator : MonoBehaviour
{
    [Header("References (auto-found in Awake if left empty)")]
    public PlayerController    playerController;
    public CollectorController collectorController;
    public PlayerStats         playerStats;

    [Header("Standing speed thresholds")]
    [Tooltip("Horizontal speed (m/s) below which the character is considered idle.\n" +
             "0.5 prevents idle-jitter from micro-inputs or NT position noise.")]
    public float walkThreshold = 0.5f;

    [Header("Crouch blend normalisation")]
    [Tooltip("Peak crouch speed (m/s) used to normalise CrouchMoveX/Y to -1..1.\n" +
             "crouchSpeed(2.5) × collectorMult(1.3) ≈ 3.25 — keep slightly above that.")]
    public float crouchMaxSpeed = 3.0f;

    [Header("Airborne detection")]
    [Tooltip("Smoothed Y velocity (m/s) above which a non-owner is considered airborne.")]
    public float airborneYThreshold = 0.6f;

    [Tooltip("Seconds to hold IsAirborne = true after the signal drops (non-owners only).\n" +
             "Bridges NT dead frames (30 Hz) so the jump clip doesn't stutter.\n" +
             "TUNED DOWN to 0.05 s for fast-gravity games (≤ -20). Raise to 0.15 s\n" +
             "for slower games. The owner always uses the exact ground-check frame.")]
    public float airborneHoldTime = 0.05f;   // was 0.15 — reduced for -25 gravity

    [Header("Velocity smoothing")]
    [Tooltip("EMA factor for horizontal speed used by the 1D blend tree.\n" +
             "Higher = faster response; lower = smoother for non-owners.\n" +
             "10-15 is a good range for 30 Hz NT with 60 Hz render.")]
    public float hSpeedSmoothFactor = 12f;

    [Tooltip("EMA factor for Y velocity used by airborne detection.\n" +
             "20 rises quickly to catch jumps yet still bridges NT dead frames.")]
    public float yVelSmoothFactor = 20f;

    [Tooltip("EMA factor for CrouchMoveX/Y on non-owners.\n" +
             "Lower than hSpeedSmoothFactor — crouch blend needs extra stability.\n" +
             "6-8 is a good range. Too high = shaking; too low = laggy direction.")]
    public float crouchSmoothFactor = 7f;

    [Header("Pick-up")]
    [Tooltip("Duration (seconds) the PickUpItem clip plays after a successful pickup.")]
    public float pickupDuration = 0.6f;

    // ── Animator parameter hashes ─────────────────────────────────────────
    private static readonly int H_Speed        = Animator.StringToHash("Speed");
    private static readonly int H_CrouchX      = Animator.StringToHash("CrouchMoveX");
    private static readonly int H_CrouchY      = Animator.StringToHash("CrouchMoveY");
    private static readonly int H_IsCrouching  = Animator.StringToHash("IsCrouching");
    private static readonly int H_IsSuperspeed = Animator.StringToHash("IsSuperspeed");
    private static readonly int H_IsAirborne   = Animator.StringToHash("IsAirborne");
    private static readonly int H_IsPickingUp  = Animator.StringToHash("IsPickingUp");
    private static readonly int H_IsDead       = Animator.StringToHash("IsDead");
    private static readonly int H_Die          = Animator.StringToHash("Die");
    private static readonly int H_JumpTrigger  = Animator.StringToHash("Jump");
    private static readonly int H_LandTrigger  = Animator.StringToHash("Land");

    // ── State ──────────────────────────────────────────────────────────────
    private Animator  _anim;
    private Transform _root;

    private Vector3 _prevPos;
    private float   _smoothedHSpeed;
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
    private bool    _justLanded;
    private bool    _wasSuperspeed;

    // Tracks consecutive seconds in the air for the current jump.
    // Used by MIN_AIRBORNE_FOR_LAND to suppress spurious Land triggers
    // when brushing a step, slope, or 1-cm ledge.
    private float _airborneElapsed;

    // ── FIX (BUG E): Jump-force airborne timer ────────────────────────────
    //
    // PROBLEM:
    //   PlayerController._isGrounded is computed at the TOP of Move() using
    //   CheckSphere. On the jump frame the character hasn't physically left the
    //   ground yet — with groundRadius = 0.3 m it takes 2-3 frames to clear.
    //   So IsAirborne() returns false for those frames even though the jump has
    //   already fired. SetBool(IsAirborne, false) racing against SetTrigger(Jump)
    //   in the same animator frame lets any !IsAirborne exit condition abort
    //   Jump_Start prematurely, sending the animator back to Locomotion.
    //   Same lag exists for non-owners: _smoothedYVel takes 1-2 frames to ramp
    //   above airborneYThreshold after jumpSequence.Value arrives from the network.
    //
    // FIX:
    //   _jumpForceAirborneTimer starts at JUMP_FORCE_AIRBORNE_TIME the moment
    //   jumpSequence.Value != _lastJumpSequence is detected in step 6 (before
    //   _lastJumpSequence is updated in step 8). rawAirborne is forced = true for
    //   the duration of the timer, guaranteeing SetBool(IsAirborne, true) fires
    //   on the SAME frame as SetTrigger(Jump) for both owners and non-owners.
    //   0.12 s is well below minimum air time (~0.69 s with gravity = -25).
    private float _jumpForceAirborneTimer;
    private const float JUMP_FORCE_AIRBORNE_TIME = 0.12f;

    private const float TELEPORT_THRESHOLD    = 3f;

    // Minimum time airborne before a Land trigger is allowed to fire.
    // Prevents stair-step micro-hops from playing the landing animation.
    // ~4 frames at 60 Hz. Raise to 0.10s for slower-gravity games.
    private const float MIN_AIRBORNE_FOR_LAND = 0.06f;

    // ── FIX: Tracks previous frame's isAirborne so we detect the exact
    // landing edge (true→false). Shared between owner and non-owner now
    // that isAirborne itself is correctly split per-client (see step 6).
    private bool _wasLandingSignal;

    // ── FIX (Land delay): Non-owner fast landing detection ─────────────────
    //
    // PROBLEM:
    //   _smoothedYVel (yVelSmoothFactor=20) takes ~7 frames (~116 ms) to decay
    //   from a typical fall velocity (−8 m/s) to below airborneYThreshold (0.6).
    //   Add the 0.05s buffer: total code-side Land trigger delay = ~166–200 ms.
    //   If the Animator also has HasExitTime=true on Jump_Loop→Jump_Land, that
    //   stacks another 1–2 s on top. Together they read as "1–2 s late" in play.
    //
    // FIX — _prevRawVelY + _nonOwnerLandLatch:
    //   Store the raw (unsmoothed) worldVel.y from the previous frame.
    //   When it transitions from significantly negative (falling) to near-zero in
    //   a single frame, that is the characteristic signature of NT reporting the
    //   player has stopped descending — i.e. they just landed. This fires within
    //   one NT tick (~33 ms) of actual touchdown, not after 200 ms of EMA decay.
    //
    //   After fast landing is detected, _nonOwnerLandLatch suppresses rawAirborne
    //   for NON_OWNER_LAND_LATCH_TIME (0.15 s) so _smoothedYVel — which is still
    //   high due to inertia — cannot re-trigger airborne. The latch is cleared
    //   immediately if a new jump is detected (jumpJustFired).
    //
    // FALSE-POSITIVE SAFETY:
    //   The check requires _prevRawVelY < −airborneYThreshold (was significantly
    //   falling). This is never true at the jump apex (worldVel.y was positive
    //   or near-zero while rising) or on flat ground. No false landing fires.
    private float _prevRawVelY;
    private float _nonOwnerLandLatch;
    private const float NON_OWNER_LAND_LATCH_TIME = 0.15f;

    // ── FIX (BUG H): Stair walk animation — owner locomotion airborne buffer ──
    //
    // Even with the single-CC.Move fix in PlayerController, unusual stair meshes
    // or micro-geometry edges can still cause a 1-frame _isGrounded = false.
    // Without any tolerance, that single frame makes rawAirborne = true → step 10
    // sets Speed = 0 → the walk animation pops to idle for one frame.
    //
    // This buffer holds "grounded for locomotion" for OWNER_LOCOMOTION_AIRBORNE_BUFFER
    // seconds (0.05 s, ~3 frames) after the last grounded frame. Applied ONLY to
    // the Speed blend tree parameter in step 10 — NOT to the IsAirborne bool, NOT
    // to the landing detection. Those still use exact physics so the jump and land
    // triggers fire correctly.
    //
    // On a real jump: jumpJustFired zeroes the buffer immediately so the
    // walk→airborne transition happens on the same frame as the Jump trigger.
    private float _ownerLocomotionAirborneBuffer;
    private const float OWNER_LOCOMOTION_AIRBORNE_BUFFER = 0.05f;

    // ── Lifecycle ──────────────────────────────────────────────────────────

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

        _smoothedHSpeed               = 0f;
        _smoothedYVel                 = 0f;
        _airborneBuffer               = 0f;
        _smoothedLocalX               = 0f;
        _smoothedLocalZ               = 0f;
        _wasLandingSignal             = false;
        _justLanded                   = false;
        _wasSuperspeed                = false;
        _airborneElapsed              = 0f;
        _jumpForceAirborneTimer       = 0f;
        _prevRawVelY                  = 0f;
        _nonOwnerLandLatch            = 0f;
        _ownerLocomotionAirborneBuffer = 0f;  // FIX (BUG H): reset stair buffer on enable

        if (playerStats != null)
            _lastJumpSequence = playerStats.jumpSequence.Value;

        if (_anim != null)
        {
            _anim.ResetTrigger(H_LandTrigger);
            _anim.ResetTrigger(H_JumpTrigger);
        }

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

    // ── Per-frame update ───────────────────────────────────────────────────

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

        // ── 1. Teleport / respawn detection ───────────────────────────────────
        float jumpDist = Vector3.Distance(_root.position, _prevPos);
        if (jumpDist > TELEPORT_THRESHOLD)
        {
            _prevPos                       = _root.position;
            _smoothedHSpeed               = 0f;
            _smoothedYVel                 = 0f;
            _airborneBuffer               = 0f;
            _airborneElapsed              = 0f;
            _jumpForceAirborneTimer       = 0f;
            _prevRawVelY                  = 0f;
            _nonOwnerLandLatch            = 0f;
            _ownerLocomotionAirborneBuffer = 0f;  // FIX (BUG H): reset on teleport
        }

        // ── 2. Position-delta velocity ─────────────────────────────────────────
        Vector3 worldVel = (_root.position - _prevPos) / Time.deltaTime;
        _prevPos = _root.position;

        float rawHSpeed = new Vector3(worldVel.x, 0f, worldVel.z).magnitude;
        _smoothedHSpeed = Mathf.Lerp(_smoothedHSpeed, rawHSpeed, hSpeedSmoothFactor * Time.deltaTime);
        _smoothedYVel   = Mathf.Lerp(_smoothedYVel,   worldVel.y, yVelSmoothFactor   * Time.deltaTime);

        // ── 3. Dead: freeze all other logic ───────────────────────────────────
        if (playerStats.IsDead())
        {
            _anim.SetBool(H_IsDead, true);
            _anim.SetFloat(H_Speed,   0f);
            _anim.SetFloat(H_CrouchX, 0f);
            _anim.SetFloat(H_CrouchY, 0f);
            _anim.SetBool(H_IsAirborne,   false);
            _anim.SetBool(H_IsCrouching,  false);
            _anim.SetBool(H_IsSuperspeed, false);
            _anim.SetBool(H_IsPickingUp,  false);
            _wasSuperspeed = false;
            return;
        }
        _anim.SetBool(H_IsDead, false);

        bool isOwner = playerStats.IsOwner;

        // ── 4. Pickup animation ───────────────────────────────────────────────
        if (collectorController != null)
        {
            int now = collectorController.GetCarriedCount();
            if (now > _lastCarriedCount) _pickupTimer = pickupDuration;
            _lastCarriedCount = now;
        }
        if (_pickupTimer > 0f) _pickupTimer -= Time.deltaTime;
        _anim.SetBool(H_IsPickingUp, _pickupTimer > 0f);

        // ── 5. Superspeed ─────────────────────────────────────────────────────
        bool isSuperspeed = collectorController != null
            && collectorController.superSpeedActive.Value
            && _smoothedHSpeed >= walkThreshold;

        _anim.SetBool(H_IsSuperspeed, isSuperspeed);

        // ── 6. Airborne detection ─────────────────────────────────────────────
        //
        // FIX (BUG E): _jumpForceAirborneTimer bridges the gap where
        // IsAirborne() / _smoothedYVel lag behind the jump sequence increment.
        //
        // FIX (Land delay): non-owner fast landing via _prevRawVelY latch.
        // See field declarations above for full explanation.
        bool jumpJustFired = playerStats.jumpSequence.Value != _lastJumpSequence;

        if (jumpJustFired)
        {
            _jumpForceAirborneTimer       = JUMP_FORCE_AIRBORNE_TIME;
            _nonOwnerLandLatch            = 0f;  // new jump cancels any pending landing latch
            _ownerLocomotionAirborneBuffer = 0f;  // FIX (BUG H): real jump — don't buffer walk→air
        }
        else if (_jumpForceAirborneTimer > 0f)
            _jumpForceAirborneTimer -= Time.deltaTime;

        // Non-owner only: detect the sharp Y-velocity drop that marks landing.
        // Fires on the NT frame the "stopped" position arrives (~33 ms after touchdown)
        // instead of waiting ~200 ms for _smoothedYVel to decay below threshold.
        if (!isOwner)
        {
            bool fastLanding = _prevRawVelY < -airborneYThreshold
                            && worldVel.y  >= -airborneYThreshold * 0.4f;

            if (fastLanding)
                _nonOwnerLandLatch = NON_OWNER_LAND_LATCH_TIME;
            else if (_nonOwnerLandLatch > 0f)
                _nonOwnerLandLatch -= Time.deltaTime;
        }
        _prevRawVelY = worldVel.y;  // store for next frame's fast-landing check

        // Owner: use exact physics ground check, supplemented by force timer.
        // Non-owner: use smoothed Y-velocity heuristic, supplemented by force timer
        //            AND fast-landing latch (_nonOwnerLandLatch suppresses rawAirborne
        //            immediately after the NT position stops moving downward).
        bool rawAirborne;
        if (isOwner && playerController != null)
            rawAirborne = playerController.IsAirborne() || _jumpForceAirborneTimer > 0f;
        else
            rawAirborne = (Mathf.Abs(_smoothedYVel) > airborneYThreshold || _jumpForceAirborneTimer > 0f)
                       && _nonOwnerLandLatch <= 0f;   // latch zeroes rawAirborne after fast landing

        // Track consecutive air-time so landing detection can reject micro-hops.
        if (rawAirborne)
            _airborneElapsed += Time.deltaTime;
        // (reset happens inside the landing block below, after the check fires)

        bool isAirborne;
        if (isOwner && playerController != null)
        {
            // Owner: exact physics frame — no buffer contamination.
            // IsAirborne and the landing edge are perfectly in sync.
            // The force-airborne timer is already factored into rawAirborne above.
            isAirborne = rawAirborne;
        }
        else
        {
            // Non-owner: buffer bridges 30 Hz NT gaps so Jump_Loop doesn't
            // stutter when position updates arrive late.
            if (rawAirborne)
                _airborneBuffer = airborneHoldTime;
            else if (_airborneBuffer > 0f)
                _airborneBuffer -= Time.deltaTime;

            isAirborne = rawAirborne || _airborneBuffer > 0f;
        }

        _anim.SetBool(H_IsAirborne, isAirborne);

        // ── 7. Landing detection ───────────────────────────────────────────────
        //
        // FIX (BUG G): Land animation fired before visual feet touched ground.
        //
        // OLD APPROACH (owner): landingSignal = isAirborne (= rawAirborne = !IsGrounded())
        //   IsGrounded() includes Physics.CheckSphere with groundRadius = 0.3 m.
        //   The sphere fires when the floor is within 0.3 m of groundCheck — so
        //   IsAirborne() returns false (and landingSignal flips) while the feet
        //   are still up to 0.3 m above the surface. Land trigger fired too early.
        //
        // NEW APPROACH (owner): landingSignal uses HasGroundContact().
        //   HasGroundContact() = (CollisionFlags.Below != 0) from the CC's own solver.
        //   It fires on the exact frame the capsule bottom contacts the floor —
        //   no early-sphere artefact — matching the player's visual perception.
        //   _jumpForceAirborneTimer keeps the signal airborne during the launch
        //   window so jumping doesn't immediately fire a spurious Land trigger.
        //
        // Non-owners still use the buffered isAirborne (NT position-delta based).
        bool landingSignal;
        if (isOwner && playerController != null)
        {
            // FIX (BUG G): CC collision contact = exact frame, no early-sphere offset.
            landingSignal = !playerController.HasGroundContact() || _jumpForceAirborneTimer > 0f;
        }
        else
        {
            // Non-owner: use buffered isAirborne as before.
            landingSignal = isAirborne;
        }

        // MIN_AIRBORNE_FOR_LAND guard: stair steps and 1-cm ledges briefly set
        // !isGrounded without a real jump, which would play a spurious land-crouch
        // animation on every step. The guard suppresses those false triggers.
        _justLanded = false;
        if (_wasLandingSignal && !landingSignal)
        {
            if (_airborneElapsed >= MIN_AIRBORNE_FOR_LAND)
            {
                _anim.ResetTrigger(H_JumpTrigger);
                _anim.SetTrigger(H_LandTrigger);
                _justLanded = true;
            }
            _airborneElapsed = 0f;   // reset after check whether or not we fired
        }
        _wasLandingSignal = landingSignal;

        // ── 8. Jump trigger ───────────────────────────────────────────────────
        int currentSeq = playerStats.jumpSequence.Value;
        if (currentSeq != _lastJumpSequence && !_justLanded)
        {
            _anim.ResetTrigger(H_LandTrigger);
            _anim.ResetTrigger(H_JumpTrigger);
            _anim.SetTrigger(H_JumpTrigger);
            _lastJumpSequence = currentSeq;
        }

        // ── 9. Crouch ─────────────────────────────────────────────────────────
        bool isCrouching = playerStats.isCrouching.Value;
        _anim.SetBool(H_IsCrouching, isCrouching);

        if (isCrouching)
        {
            float targetX, targetZ;

            if (isOwner)
            {
                targetX = Input.GetAxis("Horizontal");
                targetZ = Input.GetAxis("Vertical");
            }
            else
            {
                Vector3 local = _root.InverseTransformDirection(worldVel);
                float rawLocalX = Mathf.Clamp(local.x / crouchMaxSpeed, -1f, 1f);
                float rawLocalZ = Mathf.Clamp(local.z / crouchMaxSpeed, -1f, 1f);
                _smoothedLocalX = Mathf.Lerp(_smoothedLocalX, rawLocalX, crouchSmoothFactor * Time.deltaTime);
                _smoothedLocalZ = Mathf.Lerp(_smoothedLocalZ, rawLocalZ, crouchSmoothFactor * Time.deltaTime);
                targetX = _smoothedLocalX;
                targetZ = _smoothedLocalZ;
            }

            _anim.SetFloat(H_CrouchX, targetX);
            _anim.SetFloat(H_CrouchY, targetZ);
        }
        else
        {
            _smoothedLocalX = 0f;
            _smoothedLocalZ = 0f;
            _anim.SetFloat(H_CrouchX, 0f);
            _anim.SetFloat(H_CrouchY, 0f);
        }

        // ── 10. Standing Speed (1D blend tree) ────────────────────────────────
        //
        // FIX (BUG H): Use isAirborneForLocomotion instead of isAirborne for the
        // Speed param. isAirborneForLocomotion has a 0.05 s owner-side buffer that
        // absorbs 1-2 frame ground-check misses on stair treads, preventing the
        // walk animation from popping to idle on every step.
        //
        // The IsAirborne animator bool (step 6) and landing detection (step 7) are
        // NOT affected — they still use exact physics — so jump and land triggers
        // remain frame-perfect. Only the Speed blend tree gets the buffer.
        bool isAirborneForLocomotion;
        if (isOwner && playerController != null)
        {
            // Replenish buffer every frame the owner is grounded.
            // Drain it every frame they are airborne.
            // jumpJustFired already zeroed the buffer above (step 6) so real jumps
            // transition walk→airborne immediately without the 0.05 s delay.
            if (!rawAirborne)
                _ownerLocomotionAirborneBuffer = OWNER_LOCOMOTION_AIRBORNE_BUFFER;
            else if (_ownerLocomotionAirborneBuffer > 0f)
                _ownerLocomotionAirborneBuffer -= Time.deltaTime;

            // Only consider airborne for locomotion once buffer has fully drained.
            isAirborneForLocomotion = rawAirborne && _ownerLocomotionAirborneBuffer <= 0f;
        }
        else
        {
            // Non-owners already have the airborneHoldTime buffer in isAirborne.
            isAirborneForLocomotion = isAirborne;
        }

        if (isAirborneForLocomotion || isCrouching)
        {
            _anim.SetFloat(H_Speed, 0f);
        }
        else
        {
            float speedParam;
            if (_smoothedHSpeed < walkThreshold)
            {
                speedParam = 0f;
            }
            else if (isOwner && playerController != null)
            {
                speedParam = (playerController.IsSprinting() || isSuperspeed) ? 2f : 1f;
            }
            else
            {
                speedParam = (playerStats.isSprinting.Value || isSuperspeed) ? 2f : 1f;
            }

            bool superspeedJustStarted = isSuperspeed && !_wasSuperspeed;
            float dampTime = superspeedJustStarted ? 0f : 0.1f;
            _anim.SetFloat(H_Speed, speedParam, dampTime, Time.deltaTime);
        }

        _wasSuperspeed = isSuperspeed;
    }

    // ── Root-motion suppression ────────────────────────────────────────────
    //
    // THIS IS THE PRIMARY FIX FOR THE LANDING "TELEPORT".
    //
    // Unity calls OnAnimatorMove() every frame when it detects this method,
    // even if the body is empty. An empty body tells Unity "this script owns
    // root motion" — which makes Unity skip its default behaviour of applying
    // the animation's root delta to transform.position / transform.rotation.
    //
    // Without this method:
    //   If Apply Root Motion is checked on the Animator AND the Jump_Land clip
    //   has any downward/forward root delta (e.g. a landing impact squat baked
    //   into the root), Unity moves the character mesh by that delta EVERY FRAME
    //   during the clip. CharacterController simultaneously holds the character
    //   at its physics position. On the NEXT frame the CC wins, the mesh snaps
    //   back — producing the visible position "teleport" at landing.
    //
    // The fix: discard all root motion here. CharacterController in
    // PlayerController.cs owns ALL position and rotation changes.
    //
    // Note: you can also fix this by unchecking Apply Root Motion on the
    // Animator in the Inspector, but this method is the code-enforced safety net
    // that works even if a designer accidentally re-enables it.
    private void OnAnimatorMove() { /* intentionally empty — CC owns all movement */ }

    // ── Death / respawn callbacks ──────────────────────────────────────────

    private void OnDeadChanged(bool prev, bool next)
    {
        if (next && !_wasDead)
        {
            ApplyDeathState();
        }
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

        _smoothedHSpeed               = 0f;
        _smoothedYVel                 = 0f;
        _airborneBuffer               = 0f;
        _smoothedLocalX               = 0f;
        _smoothedLocalZ               = 0f;
        _wasLandingSignal             = false;
        _justLanded                   = false;
        _wasSuperspeed                = false;
        _airborneElapsed              = 0f;
        _jumpForceAirborneTimer       = 0f;
        _prevRawVelY                  = 0f;
        _nonOwnerLandLatch            = 0f;
        _ownerLocomotionAirborneBuffer = 0f;  // FIX (BUG H): reset stair buffer on respawn

        if (_anim != null)
        {
            _anim.ResetTrigger(H_LandTrigger);
            _anim.ResetTrigger(H_JumpTrigger);
        }
    }
}