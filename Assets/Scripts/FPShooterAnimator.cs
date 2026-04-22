using UnityEngine;
using UnityEngine.Events;

// FPShooterAnimator.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Drives the FIRST-PERSON Shooter arms Animator on the LOCAL OWNER only.
// Attach to fpShooterArms — the first-person arms root (child of CameraHolder).
// The GameObject must start INACTIVE in the prefab; PlayerSetup activates it.
//
// ── FIRE SYSTEM ───────────────────────────────────────────────────────────
//
//   Rifle   (auto) → IsFiring bool held true while mouse button is down
//                    AND the weapon has ammo AND is not reloading.
//                    FIX: Previously the bool stayed true when holding
//                    through an empty clip, keeping the fire animation
//                    playing during the reload. Now it clears immediately
//                    when the clip empties or reload begins.
//
//   Shotgun (semi) → FireShotgun trigger — one-shot per click.
//   Sniper  (semi) → FireSniper  trigger — one-shot per click.
//   Bazooka (semi) → FireBazooka trigger — one-shot per click.
//
// ── ANIMATOR PARAMETER LIST ───────────────────────────────────────────────
//
//   Float   Speed           — 0=idle 1=walk 2=sprint (locomotion blend tree)
//   Int     WeaponType      — 0=Rifle 1=Shotgun 2=Sniper 3=Bazooka
//   Bool    IsFiring        — true while Rifle mouse is held AND ammo > 0 AND not reloading
//   Bool    IsScoped        — true while Sniper is zoomed
//   Bool    IsReloading     — true during any reload (exit condition)
//   Trigger FireShotgun     — fired once per Shotgun shot
//   Trigger FireSniper      — fired once per Sniper shot
//   Trigger FireBazooka     — fired once per Bazooka shot
//   Trigger Reload          — rising edge of reload start
//   Trigger WeaponSwitch    — fires on weapon equip (after first equip)
//   Trigger Jump            — fires once per jump (jumpSequence change)

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

        if (shooterController == null) shooterController = GetComponentInParent<ShooterController>();
        if (playerStats       == null) playerStats       = GetComponentInParent<PlayerStats>();
    }

    private void OnEnable()
    {
        if (playerStats != null)
            _lastJumpSequence = playerStats.jumpSequence.Value;

        if (shooterController != null)
        {
            shooterController.onWeaponEquipped.AddListener(OnWeaponEquipped);
            shooterController.onScopeChanged.AddListener(OnScopeChanged);

            TrackWeapon(shooterController.GetCurrentWeapon(),
                        shooterController.CurrentWeaponIndex);
        }

        _smoothedSpeed   = 0f;
        _lastWeaponIndex = -1;
    }

    private void OnDisable()
    {
        if (shooterController != null)
        {
            shooterController.onWeaponEquipped.RemoveListener(OnWeaponEquipped);
            shooterController.onScopeChanged.RemoveListener(OnScopeChanged);
        }
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

        UpdateSpeed();
        UpdateScope();
        UpdateJump();
        UpdateAutoFire();
    }

    // ── FIX: Bug 1 — Rifle IsFiring persisting through empty clip ─────────
    //
    // BEFORE: IsFiring was set to (isAuto && Input.GetMouseButton(0)).
    //   When the last round fired, _currentAmmo hit 0, the auto-reload began,
    //   but GetMouseButton(0) was still true → IsFiring stayed true →
    //   the FP fire animation kept looping through the entire reload.
    //
    // FIX: IsFiring is now only true when:
    //   1. The weapon is automatic.
    //   2. Mouse button 0 is held.
    //   3. Current ammo is greater than 0.
    //   4. The weapon is NOT currently reloading.
    //
    // This ensures the FP fire animation stops the exact frame the clip
    // empties, matching ShooterController's isAutoFiring NV fix so both
    // owner FP view and non-owner 3P view snap off at the same time.
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
        // Add scope-in/scope-out one-shot triggers here if needed.
    }

    // Called by the active weapon's onFired event each time a shot fires.
    // Rifle (automatic) is handled by the IsFiring bool in UpdateAutoFire().
    // Semi-auto weapons each dispatch their own trigger so the FP animator
    // can play a dedicated fire state per weapon.
    private void OnFired()
    {
        WeaponBase cur = shooterController?.GetCurrentWeapon();
        if (cur == null) return;

        // Automatic weapons use IsFiring bool — no per-shot trigger needed.
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

    // ── Fire dispatch ─────────────────────────────────────────────────────
    //
    // Routes each semi-auto weapon to its own Animator trigger.
    // ResetTrigger before SetTrigger prevents double-trigger if two callbacks
    // arrive in the same frame.
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
        _lastWeaponIndex  = -1;

        _anim.ResetTrigger(H_FireShotgun);
        _anim.ResetTrigger(H_FireSniper);
        _anim.ResetTrigger(H_FireBazooka);
        _anim.ResetTrigger(H_Reload);
        _anim.ResetTrigger(H_WeaponSwitch);
        _anim.ResetTrigger(H_Jump);
        _anim.SetFloat(H_Speed,       0f);
        _anim.SetBool(H_IsScoped,     false);
        _anim.SetBool(H_IsReloading,  false);
        _anim.SetBool(H_IsFiring,     false);

        if (shooterController != null)
            TrackWeapon(shooterController.GetCurrentWeapon(),
                        shooterController.CurrentWeaponIndex);
    }
}