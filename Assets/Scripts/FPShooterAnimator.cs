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

    
    private static readonly int H_WeaponType   = Animator.StringToHash("WeaponType");
    private static readonly int H_Speed        = Animator.StringToHash("Speed");
    private static readonly int H_Fire         = Animator.StringToHash("Fire");
    private static readonly int H_IsFiring     = Animator.StringToHash("IsFiring");
    private static readonly int H_Reload       = Animator.StringToHash("Reload");
    private static readonly int H_WeaponSwitch = Animator.StringToHash("WeaponSwitch");
    private static readonly int H_Jump         = Animator.StringToHash("Jump");
    private static readonly int H_IsScoped     = Animator.StringToHash("IsScoped");
    private static readonly int H_IsReloading  = Animator.StringToHash("IsReloading");

    
    private Animator   _anim;
    private WeaponBase _trackedWeapon;
    private float      _smoothedSpeed;
    private int        _lastJumpSequence;
    private int        _lastWeaponIndex = -1; 

    
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

    private void UpdateAutoFire()
    {
        // Automatic weapons: hold IsFiring bool true while mouse is held.
        // This keeps the FP arms animator inside the Fire loop state for the
        // full burst duration instead of triggering per-bullet (which stutters).
        // Semi-auto weapons: clear the bool so the Fire trigger drives the clip.
        WeaponBase cur = shooterController?.GetCurrentWeapon();
        bool isAuto    = cur != null && cur.isAutomatic;
        _anim.SetBool(H_IsFiring, isAuto && Input.GetMouseButton(0));
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
        
        
    }

    private void OnFired()
    {
        // Only use the Fire trigger for semi-auto weapons.
        // Automatic weapons are handled by the IsFiring bool in UpdateAutoFire()
        // so the animator stays inside the Fire loop state for the full burst.
        WeaponBase cur = shooterController?.GetCurrentWeapon();
        if (cur != null && cur.isAutomatic) return;

        _anim.ResetTrigger(H_Fire);
        _anim.SetTrigger(H_Fire);
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

    
    private static int GetWeaponType(WeaponBase w)
    {
        if (w is RifleWeapon)   return 0;
        if (w is ShotgunWeapon) return 1;
        if (w is SniperWeapon)  return 2;
        if (w is BazookaWeapon) return 3;
        return 0; 
    }

    
    public void ResetState()
    {
        _smoothedSpeed    = 0f;
        _lastJumpSequence = playerStats != null ? playerStats.jumpSequence.Value : 0;
        _lastWeaponIndex  = -1;

        _anim.ResetTrigger(H_Fire);
        _anim.ResetTrigger(H_Reload);
        _anim.ResetTrigger(H_WeaponSwitch);
        _anim.ResetTrigger(H_Jump);
        _anim.SetFloat(H_Speed,       0f);
        _anim.SetBool(H_IsScoped,     false);
        _anim.SetBool(H_IsReloading,  false);
        _anim.SetBool(H_IsFiring,     false);

        // Re-track the current weapon so events are wired correctly after a reset.
        if (shooterController != null)
            TrackWeapon(shooterController.GetCurrentWeapon(),
                        shooterController.CurrentWeaponIndex);
    }
}
