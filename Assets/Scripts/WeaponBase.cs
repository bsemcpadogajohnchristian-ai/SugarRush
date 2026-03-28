// WeaponBase.cs
// Sugar Rush
// Unity 6.3 LTS + Netcode for GameObjects v2.1+
//
// Base class for all weapons.
// - Owner fires locally (instant feedback), sends damage RPC to server.
// - Muzzle flash plays locally for owner; broadcast to other clients via NotOwner RPC.
// - Impact FX plays locally for owner; broadcast to other clients via NotOwner RPC.
// - World impacts (walls/floors) are also broadcast so all clients see them.

using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

public abstract class WeaponBase : NetworkBehaviour
{
    [Header("Weapon info")]
    public string weaponName  = "Weapon";
    public bool   isAutomatic = false;

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
    public UnityEvent<int, int> onAmmoChanged  = new();
    public UnityEvent           onFired        = new();
    public UnityEvent           onReloadStart  = new();
    public UnityEvent           onReloadEnd    = new();

    protected int      _currentAmmo;
    protected int      _totalAmmo;
    protected bool     _isReloading;
    protected float    _nextFireTime;

    // ── Reload coroutine reference ────────────────────────────────────────────
    //
    // CRITICAL: we must store this so CancelReload() can stop it precisely.
    //
    // Without the reference, switching weapons calls SetActive(false) on the
    // old weapon, which Unity silently kills ALL coroutines on that object.
    // _isReloading stays true forever — the gun refuses to fire when switched
    // back to, until the player manually reloads to reset the flag.
    //
    // Protected so ShotgunWeapon can clear it at the end of its own routine.
    protected Coroutine _reloadCoroutine;

    protected ShooterController _shooter;

    protected virtual void Awake()
    {
        _currentAmmo = magazineSize;
        _totalAmmo   = maxAmmo;
        _shooter     = GetComponentInParent<ShooterController>();
    }

    // ── Fire entry point (called by ShooterController on owner only) ──────────

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

        // Owner: play fire sound + muzzle flash immediately
        if (fireSound != null) audioSource?.PlayOneShot(fireSound);
        PlayMuzzleFlashLocal();

        // Tell other clients to play muzzle flash on their copy of this weapon
        _shooter?.BroadcastMuzzleFlashRpc(_shooter.CurrentWeaponIndex);

        Fire(cam);

        // Infinite ammo — auto-reload whenever magazine runs dry.
        if (_currentAmmo == 0)
            StartReload();
    }

    protected abstract void Fire(Camera cam);

    // ── Reload ────────────────────────────────────────────────────────────────

    public virtual void StartReload()
    {
        if (_isReloading || _currentAmmo == magazineSize) return;
        _reloadCoroutine = StartCoroutine(ReloadRoutine());
    }

    protected virtual IEnumerator ReloadRoutine()
    {
        _isReloading = true;
        onReloadStart?.Invoke();
        if (reloadSound != null) audioSource?.PlayOneShot(reloadSound);

        yield return new WaitForSeconds(reloadTime);

        // Infinite ammo — always refill magazine fully, never consume reserve.
        _currentAmmo     = magazineSize;
        _isReloading     = false;
        _reloadCoroutine = null; // routine finished naturally — clear reference

        onReloadEnd?.Invoke();
        onAmmoChanged?.Invoke(_currentAmmo, _totalAmmo);
    }

    // ── Reload cancellation ───────────────────────────────────────────────────
    //
    // Called by ShooterController.EquipWeapon() BEFORE SetActive(false).
    //
    // WHY THIS MUST HAPPEN BEFORE SetActive:
    //   SetActive(false) kills all coroutines on the GameObject immediately and
    //   silently — _isReloading is left true with no coroutine to ever clear it.
    //   When the player switches back, TryFire() sees _isReloading == true and
    //   refuses to fire permanently. CancelReload() resets all state cleanly and
    //   fires onReloadEnd so HUD listeners (HideReloadText) are notified.
    //
    // Virtual so ShotgunWeapon can reset its own _cancelReload flag.
    public virtual void CancelReload()
    {
        if (!_isReloading) return;

        if (_reloadCoroutine != null)
        {
            StopCoroutine(_reloadCoroutine);
            _reloadCoroutine = null;
        }

        _isReloading = false;
        onReloadEnd?.Invoke(); // drives HideReloadText on HUD — must come last
    }

    // ── Ammo refill on weapon swap ────────────────────────────────────────────
    //
    // Called by ShooterController.EquipWeapon() after CancelReload() and before
    // SetActive(false). Restores the departing weapon to a full magazine so that
    // when the player returns to it later it is always ready to fire.
    //
    // Does NOT fire onAmmoChanged — the weapon is being hidden so there is no HUD
    // to update. The new weapon's ammo is pushed to HUD via NotifyWeaponChanged.
    public void RefillAmmo()
    {
        _currentAmmo = magazineSize;
    }

    // ── FX helpers ────────────────────────────────────────────────────────────

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
        {
            // Pooled path — zero allocation, zero GC
            FXPool.Instance.Spawn(bulletImpactFX, point, Quaternion.LookRotation(normal));
        }
        else
        {
            // Fallback if FXPool is not in the scene
            GameObject fx = Instantiate(bulletImpactFX, point, Quaternion.LookRotation(normal));
            ParticleSystem ps = fx.GetComponent<ParticleSystem>();
            Destroy(fx, ps != null ? ps.main.duration + ps.main.startLifetime.constantMax : 1.5f);
        }
    }

    // ── Hit reporting ─────────────────────────────────────────────────────────

    /// <summary>Call when raycast hits a NetworkObject (player or decoy).</summary>
    protected void ReportHit(ulong targetId, Vector3 point, Vector3 normal)
        => ReportHit(targetId, point, normal, damage);

    /// <summary>Overload for custom damage (e.g. shotgun pellet falloff).</summary>
    protected void ReportHit(ulong targetId, Vector3 point, Vector3 normal, float dmg)
    {
        // Owner handles own impact FX immediately
        SpawnImpactFX(point, normal);
        // Send damage to server; send impact FX to other clients
        _shooter?.RegisterHitServerRpc(targetId, dmg);
        _shooter?.BroadcastImpactRpc(_shooter.CurrentWeaponIndex, point, normal);
    }

    /// <summary>Call when raycast hits world geometry (wall, floor, etc.).</summary>
    protected void ReportWorldHit(Vector3 point, Vector3 normal)
    {
        SpawnImpactFX(point, normal);
        _shooter?.BroadcastImpactRpc(_shooter.CurrentWeaponIndex, point, normal);
    }

    // ── Getters ───────────────────────────────────────────────────────────────

    public int  GetCurrentAmmo() => _currentAmmo;
    public int  GetTotalAmmo()   => _totalAmmo;
    public bool IsReloading()    => _isReloading;
}