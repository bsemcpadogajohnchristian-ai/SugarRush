// FPShooterAnimator.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// ── PAUSE GUARD ADDED ────────────────────────────────────────────────────────
//   BUG: Clicking the mouse while the pause menu was open played the FP fire
//   animation even though ShooterController correctly blocked the actual shot.
//   ROOT CAUSE: UpdateAutoFire() reads Input.GetMouseButton(0) directly and
//   was never guarded by PauseMenuUI.IsPaused.
//   FIX: PauseMenuUI.IsPaused early-out in Update() forces IsFiring = false
//   and Speed = 0 before any input read occurs.
//
// ── SMOKE GRENADE ADDED ───────────────────────────────────────────────────────
//   • Added H_ThrowSmoke animator hash (trigger "ThrowSmoke").
//   • OnEnable subscribes to ShooterController.onSmokeGrenadeFired.
//   • OnDisable unsubscribes to prevent stale listener references.
//   • OnSmokeGrenadeFired() fires the ThrowSmoke trigger on the FP arms animator
//     so the first-person throw animation plays for the local owner.
//   • ResetState() clears the new trigger.
//
// ── ANIMATOR SETUP REQUIRED ───────────────────────────────────────────────────
//   In your FP Shooter Arms Animator Controller, add a Trigger parameter called
//   "ThrowSmoke" and wire it to your throw animation state.
//
// Drives the FIRST-PERSON Shooter arms Animator on the LOCAL OWNER only.
// Attach to fpShooterArms — the first-person arms root (child of CameraHolder).
// The GameObject must start INACTIVE in the prefab; PlayerSetup activates it.

using UnityEngine;
using UnityEngine.Events;

[DefaultExecutionOrder(50)]
[RequireComponent(typeof(Animator))]
public class FPShooterAnimator : MonoBehaviour
{
    [Header("References (auto-found in Awake if left empty)")]
    public ShooterController shooterController;
    public PlayerStats       playerStats;

    [Header("Input settings")]
    [Tooltip("Raw Input.GetAxis dead zone (0–1). Axes below this are treated as no-input.")]
    public float inputDeadZone    = 0.15f;

    [Tooltip("EMA smoothing factor for the Speed float. Higher = snappier.")]
    public float speedSmoothFactor = 12f;

    // ── Animator parameter hashes ─────────────────────────────────────────

    private static readonly int H_WeaponType    = Animator.StringToHash("WeaponType");
    private static readonly int H_Speed         = Animator.StringToHash("Speed");
    private static readonly int H_IsFiring      = Animator.StringToHash("IsFiring");
    private static readonly int H_IsScoped      = Animator.StringToHash("IsScoped");
    private static readonly int H_IsReloading   = Animator.StringToHash("IsReloading");
    private static readonly int H_WeaponSwitch  = Animator.StringToHash("WeaponSwitch");
    private static readonly int H_Jump          = Animator.StringToHash("Jump");
    private static readonly int H_Reload        = Animator.StringToHash("Reload");
    private static readonly int H_FireShotgun   = Animator.StringToHash("FireShotgun");
    private static readonly int H_FireSniper    = Animator.StringToHash("FireSniper");
    private static readonly int H_FireBazooka   = Animator.StringToHash("FireBazooka");
    private static readonly int H_ThrowSmoke    = Animator.StringToHash("ThrowSmoke"); // ← NEW

    // ── Runtime state ─────────────────────────────────────────────────────

    private Animator   _anim;
    private WeaponBase _trackedWeapon;
    private float      _smoothedSpeed;
    private int        _lastJumpSequence;
    private int        _lastWeaponIndex = -1;

    // ── Lifecycle ─────────────────────────────────────────────────────────

    private void Awake()
    {
        _anim = GetComponent<Animator>();

        if (shooterController == null)
            shooterController = GetComponentInParent<ShooterController>(includeInactive: true);
        if (playerStats == null)
            playerStats = GetComponentInParent<PlayerStats>(includeInactive: true);

        if (shooterController == null)
            Debug.LogError("[FPShooterAnimator] ShooterController not found in parent hierarchy. " +
                           "Assign it in the Inspector or check the prefab hierarchy.", this);
        if (playerStats == null)
            Debug.LogError("[FPShooterAnimator] PlayerStats not found in parent hierarchy. " +
                           "Assign it in the Inspector or check the prefab hierarchy.", this);
    }

    private void OnEnable()
    {
        if (playerStats != null)
        {
            _lastJumpSequence = playerStats.jumpSequence.Value;
            playerStats.onRespawn.AddListener(OnRespawn);
        }

        if (shooterController != null)
        {
            shooterController.onWeaponEquipped.AddListener(OnWeaponEquipped);
            shooterController.onScopeChanged.AddListener(OnScopeChanged);
            shooterController.onSmokeGrenadeFired.AddListener(OnSmokeGrenadeFired); // ← NEW

            WeaponBase currentWeapon = shooterController.GetCurrentWeapon();
            if (currentWeapon != null)
                TrackWeapon(currentWeapon, shooterController.CurrentWeaponIndex);
        }

        _smoothedSpeed = 0f;

        _lastWeaponIndex = shooterController != null
            ? shooterController.CurrentWeaponIndex
            : -1;
    }

    private void OnDisable()
    {
        if (shooterController != null)
        {
            shooterController.onWeaponEquipped.RemoveListener(OnWeaponEquipped);
            shooterController.onScopeChanged.RemoveListener(OnScopeChanged);
            shooterController.onSmokeGrenadeFired.RemoveListener(OnSmokeGrenadeFired); // ← NEW
        }

        if (playerStats != null)
            playerStats.onRespawn.RemoveListener(OnRespawn);

        UntrackWeapon();
    }

    // ── Per-frame update ──────────────────────────────────────────────────

    private void Update()
    {
        if (_anim == null || playerStats == null || shooterController == null)
        {
            Debug.LogWarning("[FPShooterAnimator] Missing reference — verify prefab hierarchy " +
                             "and that fpShooterArms starts INACTIVE.", this);
            enabled = false;
            return;
        }

        if (playerStats.IsDead())
        {
            _anim.SetBool(H_IsFiring,    false);
            _anim.SetBool(H_IsScoped,    false);
            _anim.SetBool(H_IsReloading, false);
            _anim.SetFloat(H_Speed,      0f);
            _smoothedSpeed = 0f;
            return;
        }

        // ── PAUSE GUARD ───────────────────────────────────────────────────────
        // ShooterController blocks actual firing while paused, but this animator
        // still ran UpdateAutoFire() which reads Input.GetMouseButton(0) directly
        // and set IsFiring = true — playing the fire animation on the FP arms
        // even though no shot was fired.
        // Fix: when paused, force IsFiring off and bail out before any input read.
        if (PauseMenuUI.IsPaused)
        {
            _anim.SetBool(H_IsFiring, false);
            _smoothedSpeed = 0f;
            _anim.SetFloat(H_Speed, 0f);
            return;
        }

        UpdateSpeed();
        UpdateScope();
        UpdateJump();
        UpdateAutoFire();
    }

    private void UpdateAutoFire()
    {
        WeaponBase cur = shooterController?.GetCurrentWeapon();

        bool isAuto  = cur != null && cur.isAutomatic;
        bool canFire = isAuto
                       && cur.GetCurrentAmmo() > 0
                       && !cur.IsReloading();

        _anim.SetBool(H_IsFiring, canFire && Input.GetMouseButton(0));
    }

    private void UpdateSpeed()
    {
        float h        = Input.GetAxis("Horizontal");
        float v        = Input.GetAxis("Vertical");
        bool  hasInput = Mathf.Abs(h) > inputDeadZone || Mathf.Abs(v) > inputDeadZone;

        float targetSpeed;
        if (!hasInput)
            targetSpeed = 0f;
        else if (playerStats.isCrouching.Value)
            targetSpeed = 0.5f;
        else
            targetSpeed = playerStats.isSprinting.Value ? 2f : 1f;

        _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, targetSpeed, speedSmoothFactor * Time.deltaTime);
        _anim.SetFloat(H_Speed, _smoothedSpeed);
    }

    private void UpdateScope()
    {
        _anim.SetBool(H_IsScoped, shooterController.IsScoped());
    }

    private void UpdateJump()
    {
        int currentSeq = playerStats.jumpSequence.Value;
        if (currentSeq == _lastJumpSequence) return;
        _lastJumpSequence = currentSeq;
        _anim.ResetTrigger(H_Jump);
        _anim.SetTrigger(H_Jump);
    }

    // ── Weapon tracking ───────────────────────────────────────────────────

    private void TrackWeapon(WeaponBase weapon, int index)
    {
        UntrackWeapon();
        _trackedWeapon = weapon;
        if (_trackedWeapon == null) return;

        _trackedWeapon.onFired.AddListener(OnFired);
        _trackedWeapon.onReloadStart.AddListener(OnReloadStart);
        _trackedWeapon.onReloadEnd.AddListener(OnReloadEnd);

        _anim.SetInteger(H_WeaponType, GetWeaponType(_trackedWeapon));
        _anim.SetBool(H_IsReloading, _trackedWeapon.IsReloading());
    }

    private void UntrackWeapon()
    {
        if (_trackedWeapon == null) return;
        _trackedWeapon.onFired.RemoveListener(OnFired);
        _trackedWeapon.onReloadStart.RemoveListener(OnReloadStart);
        _trackedWeapon.onReloadEnd.RemoveListener(OnReloadEnd);
        _trackedWeapon = null;
    }

    // ── Event callbacks ───────────────────────────────────────────────────

    private void OnWeaponEquipped(int index)
    {
        bool isInitialEquip = _lastWeaponIndex < 0;
        _lastWeaponIndex = index;

        if (!isInitialEquip)
        {
            _anim.ResetTrigger(H_WeaponSwitch);
            _anim.SetTrigger(H_WeaponSwitch);
        }

        TrackWeapon(shooterController.GetCurrentWeapon(), index);
    }

    private void OnScopeChanged(bool scoped)
    {
        // Polled each frame in UpdateScope().
    }

    private void OnFired()
    {
        WeaponBase cur = shooterController?.GetCurrentWeapon();
        if (cur == null) return;
        if (cur.isAutomatic) return;
        FireByWeaponType(cur);
    }

    private void OnReloadStart()
    {
        _anim.SetBool(H_IsReloading, true);
        _anim.ResetTrigger(H_Reload);
        _anim.SetTrigger(H_Reload);
    }

    private void OnReloadEnd()
    {
        _anim.SetBool(H_IsReloading, false);
    }

    // ── NEW: Smoke grenade throw ──────────────────────────────────────────────

    /// <summary>
    /// Called by ShooterController.onSmokeGrenadeFired on the local owner only.
    /// Fires the FP "ThrowSmoke" trigger so the first-person throw animation plays.
    /// </summary>
    private void OnSmokeGrenadeFired()
    {
        _anim.ResetTrigger(H_ThrowSmoke);
        _anim.SetTrigger(H_ThrowSmoke);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void OnRespawn() => ResetState();

    private void FireByWeaponType(WeaponBase weapon)
    {
        if (weapon is ShotgunWeapon)
        {
            _anim.ResetTrigger(H_FireShotgun);
            _anim.SetTrigger(H_FireShotgun);
        }
        else if (weapon is SniperWeapon)
        {
            _anim.ResetTrigger(H_FireSniper);
            _anim.SetTrigger(H_FireSniper);
        }
        else if (weapon is BazookaWeapon)
        {
            _anim.ResetTrigger(H_FireBazooka);
            _anim.SetTrigger(H_FireBazooka);
        }
    }

    private static int GetWeaponType(WeaponBase w)
    {
        if (w is RifleWeapon)   return 0;
        if (w is ShotgunWeapon) return 1;
        if (w is SniperWeapon)  return 2;
        if (w is BazookaWeapon) return 3;
        return 0;
    }

    // ── Public reset ──────────────────────────────────────────────────────

    public void ResetState()
    {
        _smoothedSpeed    = 0f;
        _lastJumpSequence = playerStats != null ? playerStats.jumpSequence.Value : 0;

        _lastWeaponIndex = shooterController != null
            ? shooterController.CurrentWeaponIndex
            : -1;

        _anim.ResetTrigger(H_FireShotgun);
        _anim.ResetTrigger(H_FireSniper);
        _anim.ResetTrigger(H_FireBazooka);
        _anim.ResetTrigger(H_Reload);
        _anim.ResetTrigger(H_WeaponSwitch);
        _anim.ResetTrigger(H_Jump);
        _anim.ResetTrigger(H_ThrowSmoke);   // ← NEW
        _anim.SetFloat(H_Speed,       0f);
        _anim.SetBool(H_IsScoped,     false);
        _anim.SetBool(H_IsReloading,  false);
        _anim.SetBool(H_IsFiring,     false);

        if (shooterController != null)
            TrackWeapon(shooterController.GetCurrentWeapon(),
                        shooterController.CurrentWeaponIndex);
    }
}