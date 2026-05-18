// WeaponBase.cs — Sugar Rush
//
// ── AMMO FIX ──────────────────────────────────────────────────────────────────
//   BUG: Ammo was effectively infinite.
//
//   ROOT CAUSE 1 — ReloadRoutine():
//     _currentAmmo was simply set to magazineSize without ever subtracting
//     from _totalAmmo. You could reload indefinitely with no reserve cost.
//   FIX: Reload now calculates how many rounds are needed, caps it against
//     what's left in _totalAmmo, deducts from _totalAmmo, and adds to
//     _currentAmmo. Partial reloads are supported (if reserves are low).
//
//   ROOT CAUSE 2 — StartReload():
//     No guard for _totalAmmo == 0. The player could trigger the reload
//     animation and sound even when completely dry.
//   FIX: Added `|| _totalAmmo <= 0` guard.
//
//   NOTE: ShotgunWeapon overrides ReloadRoutine() and is fixed separately.
//   NOTE: EquipWeapon() in ShooterController called RefillAmmo() on weapon
//         swap — that is fixed in ShooterController.cs.
//
// ── KILL FEED / AMMO SPAWNER (unchanged from previous version) ────────────────

using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

public abstract class WeaponBase : NetworkBehaviour
{
    [Header("Weapon info")]
    public string weaponName  = "Weapon";
    public bool   isAutomatic = false;

    [Tooltip("Icon sprite shown in the HUD ammo panel next to the ammo counter. " +
             "Assign the weapon's icon in each weapon prefab/child Inspector.")]
    public Sprite weaponIcon;

    [Header("Stats")]
    public float damage       = 25f;
    public float fireRate     = 0.2f;
    public float range        = 100f;
    public int   magazineSize = 30;
    public int   maxAmmo      = 90;
    public float reloadTime   = 2f;

    [Header("References")]
    public Transform   muzzle;
    public GameObject  muzzleFlashFX;
    public GameObject  bulletImpactFX;
    public AudioSource audioSource;
    public AudioClip   fireSound;
    public AudioClip   reloadSound;
    public AudioClip   emptySound;

    [Header("Events")]
    public UnityEvent<int, int> onAmmoChanged = new();
    public UnityEvent           onFired       = new();
    public UnityEvent           onReloadStart = new();
    public UnityEvent           onReloadEnd   = new();

    protected int       _currentAmmo;
    protected int       _totalAmmo;
    protected bool      _isReloading;
    protected float     _nextFireTime;
    protected Coroutine _reloadCoroutine;
    protected ShooterController _shooter;

    protected virtual void Awake()
    {
        _currentAmmo = magazineSize;
        _totalAmmo   = maxAmmo;
        _shooter     = GetComponentInParent<ShooterController>();
    }

    public virtual void TryFire(Camera cam)
    {
        if (_isReloading || Time.time < _nextFireTime) return;

        if (_currentAmmo <= 0)
        {
            if (emptySound != null) audioSource?.PlayOneShot(emptySound);
            return;
        }

        _nextFireTime = Time.time + fireRate;
        _currentAmmo--;
        onAmmoChanged?.Invoke(_currentAmmo, _totalAmmo);
        onFired?.Invoke();

        if (fireSound != null) audioSource?.PlayOneShot(fireSound);
        PlayMuzzleFlashLocal();

        _shooter?.BroadcastMuzzleFlashRpc(_shooter.CurrentWeaponIndex);

        Fire(cam);

        if (_currentAmmo == 0) StartReload();
    }

    protected abstract void Fire(Camera cam);

    public virtual void StartReload()
    {
        // ── FIX: also block reload when reserve is empty ──────────────────────
        if (_isReloading || _currentAmmo == magazineSize || _totalAmmo <= 0) return;
        _reloadCoroutine = StartCoroutine(ReloadRoutine());
    }

    protected virtual IEnumerator ReloadRoutine()
    {
        _isReloading = true;
        onReloadStart?.Invoke();
        if (reloadSound != null) audioSource?.PlayOneShot(reloadSound);
        yield return new WaitForSeconds(reloadTime);

        // ── FIX: deduct from reserve, not a free refill ───────────────────────
        // How many rounds the magazine needs to be full.
        int needed = magazineSize - _currentAmmo;
        // Can't pull more than what's left in reserve.
        int refill = Mathf.Min(needed, _totalAmmo);
        _totalAmmo   -= refill;
        _currentAmmo += refill;
        // ─────────────────────────────────────────────────────────────────────

        _isReloading     = false;
        _reloadCoroutine = null;
        onReloadEnd?.Invoke();
        onAmmoChanged?.Invoke(_currentAmmo, _totalAmmo);
    }

    public virtual void CancelReload()
    {
        if (!_isReloading) return;
        if (_reloadCoroutine != null) { StopCoroutine(_reloadCoroutine); _reloadCoroutine = null; }
        _isReloading = false;
        onReloadEnd?.Invoke();
    }

    // ── Ammo refill helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Refills the current magazine only (used on weapon swap — no reserve cost).
    /// Fires onAmmoChanged so the HUD updates.
    /// </summary>
    public void RefillAmmo()
    {
        _currentAmmo = magazineSize;
        onAmmoChanged?.Invoke(_currentAmmo, _totalAmmo);
    }

    /// <summary>
    /// Refills both the magazine AND the reserve to their maximums.
    /// Called by ShooterController.RefillAllAmmo() when the teammate Collector
    /// picks up an AmmoPickup.
    /// </summary>
    public void RefillAllAmmo()
    {
        _currentAmmo = magazineSize;
        _totalAmmo   = maxAmmo;
        onAmmoChanged?.Invoke(_currentAmmo, _totalAmmo);
    }

    // ── Muzzle flash / impact FX ──────────────────────────────────────────────

    public void PlayMuzzleFlashLocal()
    {
        if (muzzleFlashFX == null) return;
        ParticleSystem ps = muzzleFlashFX.GetComponent<ParticleSystem>();
        if (ps != null) { ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); ps.Play(); }
        else StartCoroutine(FlashRoutine());
    }

    private IEnumerator FlashRoutine()
    {
        muzzleFlashFX.SetActive(true);
        yield return new WaitForSeconds(0.05f);
        muzzleFlashFX.SetActive(false);
    }

    public void SpawnImpactFX(Vector3 point, Vector3 normal)
    {
        if (bulletImpactFX == null) return;
        if (FXPool.Instance != null)
            FXPool.Instance.Spawn(bulletImpactFX, point, Quaternion.LookRotation(normal));
        else
        {
            GameObject fx = Instantiate(bulletImpactFX, point, Quaternion.LookRotation(normal));
            ParticleSystem ps = fx.GetComponent<ParticleSystem>();
            Destroy(fx, ps != null ? ps.main.duration + ps.main.startLifetime.constantMax : 1.5f);
        }
    }

    // ── Hit reporting ─────────────────────────────────────────────────────────

    /// <summary>Uses this weapon's default damage and weaponName.</summary>
    protected void ReportHit(ulong targetId, Vector3 point, Vector3 normal)
        => ReportHit(targetId, point, normal, damage);

    /// <summary>Uses a custom damage value but still attributes weaponName.</summary>
    protected void ReportHit(ulong targetId, Vector3 point, Vector3 normal, float dmg)
    {
        SpawnImpactFX(point, normal);
        _shooter?.RegisterHitServerRpc(targetId, dmg, weaponName);
        _shooter?.BroadcastImpactRpc(_shooter.CurrentWeaponIndex, point, normal);
    }

    protected void ReportWorldHit(Vector3 point, Vector3 normal)
    {
        SpawnImpactFX(point, normal);
        _shooter?.BroadcastImpactRpc(_shooter.CurrentWeaponIndex, point, normal);
    }

    public int  GetCurrentAmmo() => _currentAmmo;
    public int  GetTotalAmmo()   => _totalAmmo;
    public bool IsReloading()    => _isReloading;
}