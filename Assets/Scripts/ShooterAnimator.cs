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
//   • Shooting : Per-weapon fire triggers synced via shootFireSequence NV to all clients.
//                  Rifle   (auto)  → IsFiring bool (held for burst duration)
//                  Shotgun (semi)  → FireShotgun trigger
//                  Sniper  (semi)  → FireSniper  trigger
//                  Bazooka (semi)  → FireBazooka trigger
//                Each weapon gets its own UpperBody fire state so timing, hold frames,
//                and exit conditions can be tuned independently in the Animator.
//
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
//   Bool    IsFiring        — true while Rifle auto-fire mouse is held
//   Trigger Jump            — fires once per jump (jumpSequence change)
//   Trigger FireShotgun     — fires once per shotgun shot (semi-auto)
//   Trigger FireSniper      — fires once per sniper shot  (semi-auto)
//   Trigger FireBazooka     — fires once per bazooka shot (semi-auto)
//   Trigger Reload          — fires when reload starts (rising edge of IsReloading)
//   Bool    IsScoped        — true while Sniper is in ADS (aim-down-sights)

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

    private static readonly int H_Speed        = Animator.StringToHash("Speed");
    private static readonly int H_CrouchX      = Animator.StringToHash("CrouchMoveX");
    private static readonly int H_CrouchY      = Animator.StringToHash("CrouchMoveY");
    private static readonly int H_WeaponType   = Animator.StringToHash("WeaponType");
    private static readonly int H_IsCrouching  = Animator.StringToHash("IsCrouching");
    private static readonly int H_IsDead       = Animator.StringToHash("IsDead");
    private static readonly int H_IsReloading  = Animator.StringToHash("IsReloading");
    private static readonly int H_IsFiring     = Animator.StringToHash("IsFiring");
    private static readonly int H_Jump         = Animator.StringToHash("Jump");
    private static readonly int H_Reload       = Animator.StringToHash("Reload");

    // Per-weapon fire triggers — one per semi-auto weapon.
    // Rifle uses IsFiring (bool). Each other weapon gets its own trigger so
    // the Animator can have a dedicated fire state per weapon with independent
    // hold frames, exit time, and transition settings.
    private static readonly int H_FireShotgun  = Animator.StringToHash("FireShotgun");
    private static readonly int H_FireSniper   = Animator.StringToHash("FireSniper");
    private static readonly int H_FireBazooka  = Animator.StringToHash("FireBazooka");
    private static readonly int H_IsScoped     = Animator.StringToHash("IsScoped");

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

        // ── 4. Fire animation ─────────────────────────────────────────────
        // Weapon index mapping:  0=Rifle(auto)  1=Shotgun  2=Sniper  3=Bazooka
        //
        // Rifle  — IsFiring bool. True while the mouse is held, so the animator
        //          stays inside UB_Fire for the full burst without per-bullet stutter.
        //
        // Shotgun / Sniper / Bazooka — dedicated per-weapon triggers.
        //   Each trigger maps to its own UpperBody fire state in the Animator,
        //   letting you tune hold frames, exit time, and blending per weapon
        //   without the states sharing any transition conditions.
        bool isAuto = playerStats.equippedWeaponIndex.Value == 0; // Rifle only

        if (isAuto)
        {
            _anim.SetBool(H_IsFiring, playerStats.isAutoFiring.Value);

            // Keep _lastFireSequence in sync so we don't fire a stale trigger
            // when the player switches to a semi-auto weapon.
            _lastFireSequence = playerStats.shootFireSequence.Value;
        }
        else
        {
            // Semi-auto: ensure IsFiring bool is cleared, then fire the
            // weapon-specific trigger on each shootFireSequence increment.
            _anim.SetBool(H_IsFiring, false);

            int fireSeq = playerStats.shootFireSequence.Value;
            if (fireSeq != _lastFireSequence)
            {
                _lastFireSequence = fireSeq;
                FireByWeaponIndex(playerStats.equippedWeaponIndex.Value);
            }
        }

        // ── 4b. Scope / ADS (Sniper only) ────────────────────────────────────
        // isScopedNV is true only while the owner holds the Sniper scoped in.
        // ShooterController clears it to false on every weapon switch, so this bool
        // is always false on non-Sniper weapon states — the Animator Controller
        // does not need an extra WeaponType guard on the transition conditions.
        _anim.SetBool(H_IsScoped, playerStats.isScopedNV.Value);

        // ── 5. Reload animation ───────────────────────────────────────────
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
        int currentSeq = playerStats.jumpSequence.Value;
        if (currentSeq != _lastJumpSequence)
        {
            _lastJumpSequence = currentSeq;
            _anim.ResetTrigger(H_Jump);
            _anim.SetTrigger(H_Jump);
        }

        // ── 7. Crouch bool + 2D blend tree ───────────────────────────────
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
            _smoothedLocalX = 0f;
            _smoothedLocalZ = 0f;
            _anim.SetFloat(H_CrouchX, 0f);
            _anim.SetFloat(H_CrouchY, 0f);
        }

        // ── 8. Standing locomotion speed ──────────────────────────────────
        float targetSpeed;

        if (isCrouching)
        {
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
            bool isMoving    = playerStats.isMovingNV.Value;
            bool isSprinting = playerStats.isSprinting.Value;
            targetSpeed = isMoving ? (isSprinting ? 2f : 1f) : 0f;
        }

        _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, targetSpeed, speedSmoothFactor * Time.deltaTime);
        _anim.SetFloat(H_Speed, _smoothedSpeed);
    }

    // Required so the Animator does not apply root-motion displacement.
    private void OnAnimatorMove() { /* intentionally empty */ }

    // ── Fire dispatch ─────────────────────────────────────────────────────
    //
    // Fires the trigger that matches the currently equipped semi-auto weapon.
    // Each trigger corresponds to its own UpperBody fire state in the Animator,
    // so clips, hold frames, and exit conditions are fully independent.
    //
    //   weaponIndex:  1 = Shotgun   2 = Sniper   3 = Bazooka
    //
    // ResetTrigger before SetTrigger is the NGO-safe pattern: it clears any
    // un-consumed trigger from a previous frame before adding the new one,
    // preventing double-triggers if two shots arrive in the same tick.
    private void FireByWeaponIndex(int weaponIndex)
    {
        switch (weaponIndex)
        {
            case 1:
                _anim.ResetTrigger(H_FireShotgun);
                _anim.SetTrigger(H_FireShotgun);
                break;
            case 2:
                _anim.ResetTrigger(H_FireSniper);
                _anim.SetTrigger(H_FireSniper);
                break;
            case 3:
                _anim.ResetTrigger(H_FireBazooka);
                _anim.SetTrigger(H_FireBazooka);
                break;
            default:
                // Index 0 (Rifle) is handled by the IsFiring bool path.
                // Any unknown index is safely ignored.
                break;
        }
    }

    // ── Death / Respawn ───────────────────────────────────────────────────

    private void OnDeadChanged(bool prev, bool next)
    {
        if (next && !_wasDead)
        {
            ApplyDeathState();
        }
        else if (!next && _wasDead)
        {
            _wasDead = false;
            _anim.SetBool(H_IsDead, false);
        }
    }

    private void ApplyDeathState()
    {
        _anim.SetBool(H_IsDead,      true);
        _anim.SetBool(H_IsReloading, false);
        _anim.SetBool(H_IsFiring,    false);
        _anim.SetBool(H_IsScoped,    false);   // exit ADS pose on death

        // Clear all pending fire triggers so death doesn't replay a shot.
        _anim.ResetTrigger(H_FireShotgun);
        _anim.ResetTrigger(H_FireSniper);
        _anim.ResetTrigger(H_FireBazooka);

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

        ResetEMA();

        if (_anim != null)
        {
            _anim.ResetTrigger(H_Jump);
            _anim.ResetTrigger(H_Reload);
            _anim.ResetTrigger(H_FireShotgun);
            _anim.ResetTrigger(H_FireSniper);
            _anim.ResetTrigger(H_FireBazooka);
            _anim.SetBool(H_IsFiring, false);
        }
    }
}