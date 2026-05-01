// ShooterAnimator.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// ── SMOKE GRENADE ADDED ───────────────────────────────────────────────────────
//   • Added H_ThrowSmoke animator hash (trigger "ThrowSmoke").
//   • Tracks PlayerStats.smokeThrowSequence (int NV, Owner-write).
//     ShooterController increments it on every smoke throw.
//     When the value changes on ANY client, this script fires the ThrowSmoke
//     trigger so the 3P throw animation plays for every observer.
//   • _lastSmokeSequence initialised in Start() and OnEnable().
//   • ApplyDeathState() resets the trigger so death doesn't replay a throw.
//   • OnRespawn() resets _lastSmokeSequence and the trigger.
//
// ── ANIMATOR SETUP REQUIRED ───────────────────────────────────────────────────
//   Add a Trigger parameter called "ThrowSmoke" to your 3P Shooter body
//   Animator Controller and wire it to a throw animation state.
//
// Drives the 3rd-person Shooter body Animator on ALL clients (owner + non-owner).
// Attach to bodyShooter — the child GameObject that holds the Shooter mesh and Animator.

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
    public float speedSmoothFactor  = 12f;
    public float crouchSmoothFactor = 7f;

    [Header("Input dead zone (owner only)")]
    public float inputDeadZone = 0.15f;

    // ── Animator parameter hashes ─────────────────────────────────────────

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
    private static readonly int H_FireShotgun  = Animator.StringToHash("FireShotgun");
    private static readonly int H_FireSniper   = Animator.StringToHash("FireSniper");
    private static readonly int H_FireBazooka  = Animator.StringToHash("FireBazooka");
    private static readonly int H_IsScoped     = Animator.StringToHash("IsScoped");
    private static readonly int H_ThrowSmoke   = Animator.StringToHash("ThrowSmoke"); // ← NEW

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
    private int _lastSmokeSequence;      // ← NEW
    private int _lastWeaponIndex = -1;

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

        _lastJumpSequence  = playerStats.jumpSequence.Value;
        _lastFireSequence  = playerStats.shootFireSequence.Value;
        _lastSmokeSequence = playerStats.smokeThrowSequence.Value; // ← NEW

        _lastWeaponIndex = playerStats.equippedWeaponIndex.Value;
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

        ResetEMA();

        if (playerStats == null) return;

        _lastJumpSequence  = playerStats.jumpSequence.Value;
        _lastFireSequence  = playerStats.shootFireSequence.Value;
        _lastSmokeSequence = playerStats.smokeThrowSequence.Value; // ← NEW
        _lastWeaponIndex   = playerStats.equippedWeaponIndex.Value;
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

        // ── 1. Teleport detection ─────────────────────────────────────────
        if (Vector3.Distance(_root.position, _prevPos) > TELEPORT_THRESHOLD)
            ResetEMA();
        _prevPos = _root.position;

        // ── 2. Dead ───────────────────────────────────────────────────────
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
        int wi = playerStats.equippedWeaponIndex.Value;
        if (wi != _lastWeaponIndex)
        {
            _lastWeaponIndex = wi;
            _anim.SetInteger(H_WeaponType, wi);
        }

        // ── 4. Fire animation ─────────────────────────────────────────────
        bool isAuto = playerStats.equippedWeaponIndex.Value == 0;

        if (isAuto)
        {
            _anim.SetBool(H_IsFiring, playerStats.isAutoFiring.Value);
            _lastFireSequence = playerStats.shootFireSequence.Value;
        }
        else
        {
            _anim.SetBool(H_IsFiring, false);
            int fireSeq = playerStats.shootFireSequence.Value;
            if (fireSeq != _lastFireSequence)
            {
                _lastFireSequence = fireSeq;
                FireByWeaponIndex(playerStats.equippedWeaponIndex.Value);
            }
        }

        // ── 4b. Scope / ADS ───────────────────────────────────────────────
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

        // ── 6b. NEW: Smoke throw trigger ──────────────────────────────────
        // smokeThrowSequence is incremented by ShooterController (Owner-write).
        // All clients see the change and play the 3P throw animation.
        int smokeSeq = playerStats.smokeThrowSequence.Value;
        if (smokeSeq != _lastSmokeSequence)
        {
            _lastSmokeSequence = smokeSeq;
            _anim.ResetTrigger(H_ThrowSmoke);
            _anim.SetTrigger(H_ThrowSmoke);
        }
        // ─────────────────────────────────────────────────────────────────

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

    private void OnAnimatorMove() { /* intentionally empty */ }

    // ── Fire dispatch ─────────────────────────────────────────────────────

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
        }
    }

    // ── Death / Respawn ───────────────────────────────────────────────────

    private void OnDeadChanged(bool prev, bool next)
    {
        if (next && !_wasDead)  ApplyDeathState();
        else if (!next && _wasDead) { _wasDead = false; _anim.SetBool(H_IsDead, false); }
    }

    private void ApplyDeathState()
    {
        _anim.SetBool(H_IsDead,      true);
        _anim.SetBool(H_IsReloading, false);
        _anim.SetBool(H_IsFiring,    false);
        _anim.SetBool(H_IsScoped,    false);

        _anim.ResetTrigger(H_FireShotgun);
        _anim.ResetTrigger(H_FireSniper);
        _anim.ResetTrigger(H_FireBazooka);
        _anim.ResetTrigger(H_ThrowSmoke);   // ← NEW

        _smoothedSpeed = 0f;
        _wasReloading  = false;
        _wasDead       = true;
    }

    private void OnRespawn()
    {
        if (playerStats != null)
        {
            _lastJumpSequence  = playerStats.jumpSequence.Value;
            _lastFireSequence  = playerStats.shootFireSequence.Value;
            _lastSmokeSequence = playerStats.smokeThrowSequence.Value; // ← NEW
        }

        ResetEMA();

        if (_anim != null)
        {
            _anim.ResetTrigger(H_Jump);
            _anim.ResetTrigger(H_Reload);
            _anim.ResetTrigger(H_FireShotgun);
            _anim.ResetTrigger(H_FireSniper);
            _anim.ResetTrigger(H_FireBazooka);
            _anim.ResetTrigger(H_ThrowSmoke);   // ← NEW
            _anim.SetBool(H_IsFiring, false);
        }
    }
}
