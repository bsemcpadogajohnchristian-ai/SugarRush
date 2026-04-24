using UnityEngine;
using UnityEngine.Events;

// FPShooterAnimator.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Drives the FIRST-PERSON Shooter arms Animator on the LOCAL OWNER only.
// Attach to fpShooterArms — the first-person arms root (child of CameraHolder).
// The GameObject must start INACTIVE in the prefab; PlayerSetup activates it.
//
// ── BUG FIXES IN THIS FILE ────────────────────────────────────────────────
//
//   BUG 1 — IsFiring persisting through empty clip (previously documented)
//     Fixed in UpdateAutoFire(): IsFiring is only true when ALL THREE conditions
//     hold: mouse button is down AND ammo > 0 AND weapon is not reloading.
//
//   BUG 2 — First weapon switch after arm activation skips WeaponSwitch animation
//     BEFORE: OnEnable set _lastWeaponIndex = -1 AFTER subscribing to
//     onWeaponEquipped. Because OnWeaponEquipped treats any call where
//     _lastWeaponIndex < 0 as the "initial equip", the player's first deliberate
//     weapon switch (e.g., pressing 2 right after spawning) would be silently
//     swallowed — WeaponSwitch trigger never fired, no switch animation played.
//     FIX: _lastWeaponIndex is now set to the CURRENT weapon index at the end
//     of OnEnable so every subsequent player-driven switch fires the trigger.
//
//   BUG 3 — H_IsFiring stays true while the player is dead
//     BEFORE: Update() had no death guard. If the player died while holding the
//     fire button (mouse held, ammo remaining, not reloading), UpdateAutoFire()
//     would keep H_IsFiring = true because it reads from the weapon directly —
//     bypassing ShooterController.HandleFire()'s _stats.IsDead() guard. Result:
//     the FP fire animation kept playing on a dead player in FP view.
//     FIX: Update() now checks _stats.IsDead() first, clears all active bools
//     and returns early when the player is dead.
//
//   BUG 4 — ResetState() was never called on respawn
//     BEFORE: ResetState() was a public method but nothing called it. Stale
//     triggers (H_Jump, H_Reload, H_FireShotgun, H_FireSniper, H_FireBazooka)
//     and bools (H_IsFiring, H_IsScoped, H_IsReloading) could persist through
//     respawn and play on the next life.
//     FIX: OnEnable now subscribes to playerStats.onRespawn and calls
//     ResetState() when the player respawns. OnDisable unsubscribes cleanly.
//
//   BUG 5 — GetComponentInParent misses components when object starts inactive
//     BEFORE: Awake() called GetComponentInParent<ShooterController>() and
//     GetComponentInParent<PlayerStats>() without the includeInactive overload.
//     Because fpShooterArms starts INACTIVE in the prefab, Awake() only fires
//     when PlayerSetup calls SetActive(true). At that moment the PARENT chain
//     is active, but in Unity 6 the non-includeInactive overload has changed
//     traversal semantics and can produce null even for active-parent components
//     in certain prefab instantiation orders.
//     FIX: Use GetComponentInParent<T>(includeInactive: true) so the search
//     always walks the full hierarchy regardless of any transient inactive state.
//     A null-check log is added to catch wiring mistakes during development.
//
//   BUG 6 — TrackWeapon called with null weapon on first OnEnable
//     BEFORE: OnEnable called TrackWeapon(shooterController.GetCurrentWeapon(), ...)
//     immediately. If PlayerSetup.ApplyRole() fires BEFORE ShooterController
//     .OnNetworkSpawn() (which calls EquipWeapon(0)), GetCurrentWeapon() returns
//     null → TrackWeapon(null, 0) → onFired/onReloadStart/onReloadEnd never
//     subscribed → first weapon's fire and reload animations never played.
//     The onWeaponEquipped listener saved us most of the time, but the window
//     existed. FIX: Guard TrackWeapon call; only call it when GetCurrentWeapon()
//     is non-null. The onWeaponEquipped subscription below is always registered
//     and will call TrackWeapon correctly when ShooterController.EquipWeapon(0)
//     fires during its own OnNetworkSpawn().
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

        // ── BUG 5 FIX: includeInactive: true ─────────────────────────────────
        //
        // BEFORE: GetComponentInParent<ShooterController>() — no includeInactive flag.
        //   fpShooterArms starts INACTIVE in the prefab; Awake() only fires when
        //   PlayerSetup calls SetActive(true). At that instant the parent chain IS
        //   active, but in Unity 6 the default (non-includeInactive) overload
        //   changed traversal semantics and can produce null for components on
        //   parents that were inactive at any prior point in the prefab lifecycle.
        //
        // FIX: includeInactive: true forces the search to walk the full hierarchy
        //   regardless of transient inactive state, producing a reliable result in
        //   every Unity 6 prefab instantiation order.
        if (shooterController == null)
            shooterController = GetComponentInParent<ShooterController>(includeInactive: true);
        if (playerStats == null)
            playerStats = GetComponentInParent<PlayerStats>(includeInactive: true);

        // Hard fail — misconfigured prefab. Visible in both Editor and builds.
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

            // BUG 4 FIX: Subscribe to respawn so ResetState() clears stale
            // trigger/bool state from the previous life before the new one begins.
            playerStats.onRespawn.AddListener(OnRespawn);
        }

        if (shooterController != null)
        {
            shooterController.onWeaponEquipped.AddListener(OnWeaponEquipped);
            shooterController.onScopeChanged.AddListener(OnScopeChanged);

            // ── BUG 6 FIX: Null-guard initial TrackWeapon call ────────────────
            //
            // BEFORE: TrackWeapon(shooterController.GetCurrentWeapon(), index) was
            //   called unconditionally. If OnEnable fires before ShooterController
            //   .OnNetworkSpawn() (which calls EquipWeapon(0)), GetCurrentWeapon()
            //   returns null → TrackWeapon(null, 0) silently skips all subscriptions
            //   → onFired/onReloadStart/onReloadEnd never wired for the first weapon
            //   → fire and reload animations never played until the player switched
            //   weapons (which forced a re-wire via OnWeaponEquipped).
            //
            // FIX: Only call TrackWeapon when a weapon is actually available.
            //   The onWeaponEquipped listener (subscribed above) handles the case
            //   where ShooterController.EquipWeapon(0) fires later during its own
            //   OnNetworkSpawn(), so no wiring is lost in the deferred path.
            WeaponBase currentWeapon = shooterController.GetCurrentWeapon();
            if (currentWeapon != null)
                TrackWeapon(currentWeapon, shooterController.CurrentWeaponIndex);
        }

        _smoothedSpeed = 0f;

        // BUG 2 FIX: Set _lastWeaponIndex to the CURRENTLY equipped weapon index
        // rather than -1 so the first player-driven weapon switch correctly fires
        // the WeaponSwitch trigger. Setting it to -1 here caused OnWeaponEquipped
        // to treat the very first switch as an "initial equip" and suppress the
        // trigger — the player saw no switch animation for their first weapon change.
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
        }

        // BUG 4 FIX: Unsubscribe from respawn to prevent callbacks on a
        // disabled or destroyed object.
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

        // BUG 3 FIX: Guard against the dead state before doing anything else.
        // BEFORE: No death check existed. UpdateAutoFire() read weapon.GetCurrentAmmo()
        // and weapon.IsReloading() directly — bypassing ShooterController's IsDead()
        // guard — so H_IsFiring could stay true while the player was dead if they
        // happened to die while the mouse button was held and ammo remained.
        // FIX: Detect death here, clear all active animation bools, and return early
        // so no fire/scope/speed state leaks through from the previous life.
        if (playerStats.IsDead())
        {
            _anim.SetBool(H_IsFiring,    false);
            _anim.SetBool(H_IsScoped,    false);
            _anim.SetBool(H_IsReloading, false);
            _anim.SetFloat(H_Speed,      0f);
            _smoothedSpeed = 0f;
            return;
        }

        UpdateSpeed();
        UpdateScope();
        UpdateJump();
        UpdateAutoFire();
    }

    // ── BUG 1 FIX: Rifle IsFiring persisting through empty clip ──────────
    //
    // BEFORE: IsFiring was set to (isAuto && Input.GetMouseButton(0)).
    //   When the last round fired, _currentAmmo hit 0, the auto-reload began,
    //   but GetMouseButton(0) was still true → IsFiring stayed true →
    //   the FP fire animation kept looping through the entire reload.
    //
    // FIX: IsFiring is now only true when ALL THREE conditions hold:
    //   1. The weapon is automatic.
    //   2. Mouse button 0 is held.
    //   3. Current ammo is greater than 0.
    //   4. The weapon is NOT currently reloading.
    //
    // FPShooterAnimator has [DefaultExecutionOrder(50)] and ShooterController
    // has the default order (0), so ShooterController.HandleFire() — which calls
    // TryFire() and decrements ammo — always runs BEFORE this check.
    // By the time UpdateAutoFire() reads GetCurrentAmmo(), the decrement has
    // already happened, so canFire correctly becomes false on the last-bullet frame.
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
        // BUG 2 FIX NOTE: _lastWeaponIndex is now seeded to the current weapon
        // index in OnEnable (not -1), so isInitialEquip is only true on the very
        // first OnWeaponEquipped call if OnEnable couldn't resolve the index.
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
    // Semi-auto weapons each dispatch their own trigger.
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

    // ── Respawn callback ──────────────────────────────────────────────────

    // BUG 4 FIX: Called when the player respawns. Clears all stale animation
    // state so triggers and bools from the previous life don't bleed through.
    private void OnRespawn() => ResetState();

    // ── Fire dispatch ─────────────────────────────────────────────────────

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

        // BUG 2 FIX NOTE: Seed to current weapon index, not -1, so the first
        // switch after reset correctly triggers the WeaponSwitch animation.
        _lastWeaponIndex = shooterController != null
            ? shooterController.CurrentWeaponIndex
            : -1;

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