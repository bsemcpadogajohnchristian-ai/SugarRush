// ShooterAnimator.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Drives the 3rd-person Shooter body Animator on ALL clients (owner + non-owner).
// Attach to bodyShooter — the child GameObject that holds the Shooter mesh and Animator.
//
// ── DESIGN PRINCIPLES ────────────────────────────────────────────────────
//
//   • Death    : Only H_IsDead (bool). AnyState → Die when true, Die → Locomotion when false.
//                Die/Respawn triggers have been REMOVED — they caused competing transitions.
//
//   • Jumping  : H_Jump (trigger) fires on jumpSequence change. The animator handles
//                Jump Start → Jump Land automatically via exit time transitions.
//                H_IsGrounded and all airborne detection code have been REMOVED —
//                the state machine is the authority on jump state, not the script.
//
//   • Crouch   : H_IsCrouching (bool) + H_CrouchMoveX/Y for the 2D blend tree.
//                Transitions in the animator MUST have Has Exit Time = OFF.
//
//   • Shooting : H_Fire (trigger) synced via shootFireSequence NV to all clients.
//   • Reloading: H_Reload (trigger) + H_IsReloading (bool) via isReloadingNV.
//   • Weapon   : H_WeaponType (int) via equippedWeaponIndex NV.
//   • Speed    : Plain Lerp EMA — never overshoots blend-tree thresholds unlike SmoothDamp.
//
// ── ANIMATOR PARAMETER LIST ───────────────────────────────────────────────
//
//   Float   Speed           — 0=idle, 1=walk, 2=sprint  (Locomotion blend tree)
//   Float   CrouchMoveX     — strafe direction  (Crouch Movement blend tree)
//   Float   CrouchMoveY     — forward/back dir  (Crouch Movement blend tree)
//   Int     WeaponType      — 0=Rifle 1=Shotgun 2=Sniper 3=Bazooka
//   Bool    IsCrouching     — drives Locomotion ↔ Crouch Movement transitions
//   Bool    IsDead          — drives AnyState → Die and Die → Locomotion
//   Bool    IsReloading     — keeps UpperBody in reload state across blend frames
//   Trigger Jump            — fires once per jump (jumpSequence change)
//   Trigger Fire            — fires once per bullet (shootFireSequence change)
//   Trigger Reload          — fires when reload starts (rising edge of IsReloading)

using UnityEngine;

[DefaultExecutionOrder(50)]
[RequireComponent(typeof(Animator))]
public class ShooterAnimator : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────

    [Header("References (auto-found in Awake if left empty)")]
    public PlayerController playerController;
    public PlayerStats      playerStats;

    [Header("Speed smoothing")]
    [Tooltip("EMA factor for the Speed float. Higher = snappier walk/run transitions.\n" +
             "12 is a good default. Range: 8–18.")]
    public float speedSmoothFactor = 12f;

    [Tooltip("EMA factor for CrouchMoveX/Y in the 2D blend tree.\n" +
             "6–8 recommended. Too high = jerky; too low = sluggish.")]
    public float crouchSmoothFactor = 7f;

    [Header("Input dead zone (owner only)")]
    [Tooltip("Raw Input.GetAxis magnitude below which the axis is treated as zero.\n" +
             "0.15 matches Unity's default Input Manager dead zone.")]
    public float inputDeadZone = 0.15f;

    // ── Animator parameter hashes ─────────────────────────────────────────
    // Keep these in sync with the parameter names in your Animator Controller.

    private static readonly int H_Speed       = Animator.StringToHash("Speed");
    private static readonly int H_CrouchX     = Animator.StringToHash("CrouchMoveX");
    private static readonly int H_CrouchY     = Animator.StringToHash("CrouchMoveY");
    private static readonly int H_WeaponType  = Animator.StringToHash("WeaponType");
    private static readonly int H_IsCrouching = Animator.StringToHash("IsCrouching");
    private static readonly int H_IsDead      = Animator.StringToHash("IsDead");
    private static readonly int H_IsReloading = Animator.StringToHash("IsReloading");
    private static readonly int H_IsFiring    = Animator.StringToHash("IsFiring");
    private static readonly int H_Jump        = Animator.StringToHash("Jump");
    private static readonly int H_Fire        = Animator.StringToHash("Fire");
    private static readonly int H_Reload      = Animator.StringToHash("Reload");

    // ── Runtime state ─────────────────────────────────────────────────────

    private Animator  _anim;
    private Transform _root;
    private Vector3   _prevPos;

    private float _smoothedSpeed;
    private float _smoothedLocalX;
    private float _smoothedLocalZ;

    private bool _wasReloading;
    private bool _wasDead;

    private bool _subscribedToDead;
    private bool _subscribedToRespawn;

    private int _lastJumpSequence;
    private int _lastFireSequence;
    private int _lastWeaponIndex = -1;   // -1 forces a set on first frame

    // Teleport guard: if the root moves more than this in one frame, reset
    // all EMA smoothing to prevent a velocity spike snapping Speed to 2.
    private const float TELEPORT_THRESHOLD = 3f;

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

        // Snapshot sequences so we don't fire stale triggers on enable.
        _lastJumpSequence = playerStats.jumpSequence.Value;
        _lastFireSequence = playerStats.shootFireSequence.Value;

        // Set weapon type immediately so the body is in the right grip pose.
        _lastWeaponIndex = playerStats.equippedWeaponIndex.Value;
        _anim.SetInteger(H_WeaponType, _lastWeaponIndex);

        // Mirror current reload state.
        _wasReloading = playerStats.isReloadingNV.Value;
        _anim.SetBool(H_IsReloading, _wasReloading);

        // If this component enables mid-game while the player is already dead,
        // jump straight into the death state.
        if (playerStats.IsDead()) ApplyDeathState();
    }

    private void OnEnable()
    {
        TrySubscribeToDead();
        TrySubscribeToRespawn();

        if (_root == null && transform.root != null) _root = transform.root;
        if (_root != null) _prevPos = _root.position;

        ResetEMA();

        if (playerStats == null) return;

        _lastJumpSequence = playerStats.jumpSequence.Value;
        _lastFireSequence = playerStats.shootFireSequence.Value;
        _lastWeaponIndex  = playerStats.equippedWeaponIndex.Value;
        _anim.SetInteger(H_WeaponType, _lastWeaponIndex);

        _wasReloading = playerStats.isReloadingNV.Value;
        _anim.SetBool(H_IsReloading, _wasReloading);

        if (playerStats.IsDead()) ApplyDeathState();
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

    // ── Subscription helpers ──────────────────────────────────────────────

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

    // Resets all EMA smoothing values. Called on teleport and OnEnable
    // so a sudden position change doesn't cause a speed spike.
    private void ResetEMA()
    {
        _smoothedSpeed  = 0f;
        _smoothedLocalX = 0f;
        _smoothedLocalZ = 0f;
        _wasReloading   = false;
    }

    // ── Per-frame update ──────────────────────────────────────────────────

    private void Update()
    {
        if (_anim == null) return;

        // Late-join: playerStats may not be assigned if the prefab spawns before
        // the NV values propagate. Retry each frame until found.
        if (playerStats == null)
        {
            playerStats = GetComponentInParent<PlayerStats>();
            TrySubscribeToDead();
            TrySubscribeToRespawn();
            if (playerStats == null) return;
        }

        // This component lives on bodyShooter. Skip if the player is now a Collector.
        if (playerStats.role.Value != PlayerRole.Shooter) return;

        if (_root == null)
        {
            _root    = transform.root;
            _prevPos = _root.position;
        }

        // ── 1. Teleport detection ─────────────────────────────────────────
        // Prevents a large positional jump (respawn warp) from briefly spiking
        // _smoothedSpeed to sprint speed, which would show a wrong run animation.
        if (Vector3.Distance(_root.position, _prevPos) > TELEPORT_THRESHOLD)
            ResetEMA();
        _prevPos = _root.position;

        // ── 2. Dead — freeze all locomotion parameters ────────────────────
        // H_IsDead alone drives the AnyState → Die transition in the animator.
        // Do NOT set triggers here; bool-based transitions are stable and cannot
        // be consumed/dropped the way triggers can between rapid state changes.
        if (playerStats.IsDead())
        {
            _anim.SetBool(H_IsDead,      true);
            _anim.SetBool(H_IsCrouching, false);
            _anim.SetBool(H_IsReloading, false);
            _anim.SetFloat(H_Speed,      0f);
            _anim.SetFloat(H_CrouchX,   0f);
            _anim.SetFloat(H_CrouchY,   0f);
            _smoothedSpeed  = 0f;
            _wasReloading   = false;
            return;
        }
        _anim.SetBool(H_IsDead, false);

        bool isOwner = playerStats.IsOwner;

        // ── 3. Weapon type ────────────────────────────────────────────────
        // Only write when changed — SetInteger is cheap but avoids an unnecessary
        // animator dirty every frame.
        int wi = playerStats.equippedWeaponIndex.Value;
        if (wi != _lastWeaponIndex)
        {
            _lastWeaponIndex = wi;
            _anim.SetInteger(H_WeaponType, wi);
        }

        // ── 4. Fire animation ─────────────────────────────────────────────────
        // Automatic weapons (Rifle): use the IsFiring bool so the animator stays
        // inside UB_Fire for the entire burst — no per-bullet trigger stutter.
        // Semi-auto weapons: use the Fire trigger once per shot as before.
        //
        // isAuto is determined by the equipped weapon index:
        //   0 = Rifle (automatic)   1 = Shotgun   2 = Sniper   3 = Bazooka
        // If you add more automatic weapons, expand the isAuto check below.
        bool isAuto = playerStats.equippedWeaponIndex.Value == 0;

        if (isAuto)
        {
            // Drive the UB_Fire state with a bool: enter on mouse-hold, exit on release.
            // The UB_Fire animation clip should have Loop Time = ON.
            _anim.SetBool(H_IsFiring, playerStats.isAutoFiring.Value);

            // Keep _lastFireSequence in sync so we don't fire a stale trigger
            // when the player switches to a semi-auto weapon.
            _lastFireSequence = playerStats.shootFireSequence.Value;
        }
        else
        {
            // Semi-auto: ensure the bool is cleared, then use trigger per shot.
            _anim.SetBool(H_IsFiring, false);

            int fireSeq = playerStats.shootFireSequence.Value;
            if (fireSeq != _lastFireSequence)
            {
                _lastFireSequence = fireSeq;
                _anim.ResetTrigger(H_Fire);
                _anim.SetTrigger(H_Fire);
            }
        }

        // ── 5. Reload animation ───────────────────────────────────────────
        // isReloadingNV is written by ShooterController when the active weapon
        // starts/ends reloading. We detect the rising edge to fire the Reload
        // trigger (starts the reload clip on the UpperBody layer) and keep
        // IsReloading bool in sync for blend-tree exit conditions.
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

        // ── 6. Jump trigger ───────────────────────────────────────────────
        // jumpSequence is an ever-increasing int (Owner-write NV). NGO always
        // replicates int changes even at 30 Hz tick rate, unlike a one-frame
        // bool which can be missed between ticks. We fire the Jump trigger once
        // per real jump on all clients; the animator state machine transitions
        // Locomotion → Jump Start → Jump Land → Locomotion automatically via
        // exit-time transitions. No IsGrounded parameter needed.
        int currentSeq = playerStats.jumpSequence.Value;
        if (currentSeq != _lastJumpSequence)
        {
            _lastJumpSequence = currentSeq;
            _anim.ResetTrigger(H_Jump);
            _anim.SetTrigger(H_Jump);
        }

        // ── 7. Crouch bool + 2D blend tree ───────────────────────────────
        // The IsCrouching bool drives the Locomotion ↔ Crouch Movement transition
        // (Has Exit Time must be OFF in the animator for instant response).
        // CrouchMoveX/Y feeds the 2D Simple Directional blend tree inside
        // Crouch Movement. We EMA-smooth the values to avoid jerky snapping.
        bool isCrouching = playerStats.isCrouching.Value;
        _anim.SetBool(H_IsCrouching, isCrouching);

        if (isCrouching)
        {
            float targetX, targetZ;

            if (isOwner)
            {
                // Owner: read raw input directly — no network latency.
                float rawH = Input.GetAxis("Horizontal");
                float rawV = Input.GetAxis("Vertical");
                targetX = Mathf.Abs(rawH) > inputDeadZone ? rawH : 0f;
                targetZ = Mathf.Abs(rawV) > inputDeadZone ? rawV : 0f;
            }
            else
            {
                // Non-owner: read localMoveDir NV (written by PlayerController
                // from raw input). Never oscillates at NT update boundaries.
                Vector2 dir = playerStats.localMoveDir.Value;
                targetX = dir.x;
                targetZ = dir.y;
            }

            _smoothedLocalX = Mathf.Lerp(_smoothedLocalX, targetX, crouchSmoothFactor * Time.deltaTime);
            _smoothedLocalZ = Mathf.Lerp(_smoothedLocalZ, targetZ, crouchSmoothFactor * Time.deltaTime);

            _anim.SetFloat(H_CrouchX, _smoothedLocalX);
            _anim.SetFloat(H_CrouchY, _smoothedLocalZ);
        }
        else
        {
            // Snap to zero immediately when standing — no need to ease out.
            _smoothedLocalX = 0f;
            _smoothedLocalZ = 0f;
            _anim.SetFloat(H_CrouchX, 0f);
            _anim.SetFloat(H_CrouchY, 0f);
        }

        // ── 8. Standing locomotion speed ──────────────────────────────────
        // Speed feeds the 1D blend tree: 0 = Idle, 1 = Walk, 2 = Sprint.
        // We compute a discrete targetSpeed (0/1/2) and smooth it with a plain
        // Mathf.Lerp EMA. Unlike Animator.SetFloat's dampTime (which uses
        // SmoothDamp internally), plain Lerp never overshoots, so the blend
        // tree never briefly crosses a threshold and flickers.
        float targetSpeed;

        if (isCrouching)
        {
            // Crouch Movement state handles its own blend tree; zero Speed here
            // so if a transition back to Locomotion is blending it starts clean.
            targetSpeed = 0f;
        }
        else if (isOwner)
        {
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
            // Non-owner: isMovingNV and isSprinting NV are written by PlayerController
            // from direct input — they never oscillate at NT update boundaries.
            bool isMoving    = playerStats.isMovingNV.Value;
            bool isSprinting = playerStats.isSprinting.Value;
            targetSpeed = isMoving ? (isSprinting ? 2f : 1f) : 0f;
        }

        _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, targetSpeed, speedSmoothFactor * Time.deltaTime);
        _anim.SetFloat(H_Speed, _smoothedSpeed);
    }

    // Required so the Animator does not apply root-motion displacement.
    private void OnAnimatorMove() { /* intentionally empty */ }

    // ── Death / Respawn ───────────────────────────────────────────────────

    // Called by the isDead NV callback on ALL clients.
    private void OnDeadChanged(bool prev, bool next)
    {
        if (next && !_wasDead)
        {
            ApplyDeathState();
        }
        else if (!next && _wasDead)
        {
            // Player has respawned. Clear the IsDead bool; the animator's
            // Die → Locomotion transition (condition: IsDead = false) handles
            // the blend back to normal. No trigger needed.
            _wasDead = false;
            _anim.SetBool(H_IsDead, false);
        }
    }

    // Set IsDead = true. The AnyState → Die transition (condition: IsDead = true)
    // in the animator fires automatically. No Die trigger — a bool is more robust
    // because it cannot be consumed/dropped between state machine evaluations.
    private void ApplyDeathState()
    {
        _anim.SetBool(H_IsDead,      true);
        _anim.SetBool(H_IsReloading, false);
        _anim.SetBool(H_IsFiring,    false);
        _smoothedSpeed = 0f;
        _wasReloading  = false;
        _wasDead       = true;
    }

    // Called by PlayerStats.onRespawn UnityEvent. Snapshots sequences so
    // we don't fire stale jump/fire triggers on the first Update after respawn.
    private void OnRespawn()
    {
        if (playerStats != null)
        {
            _lastJumpSequence = playerStats.jumpSequence.Value;
            _lastFireSequence = playerStats.shootFireSequence.Value;
        }

        ResetEMA();

        if (_anim != null)
        {
            // Clear any un-consumed Jump trigger so a mid-air death doesn't
            // replay the jump animation on respawn.
            _anim.ResetTrigger(H_Jump);
            _anim.ResetTrigger(H_Fire);
            _anim.ResetTrigger(H_Reload);
            _anim.SetBool(H_IsFiring, false);
        }
    }
}