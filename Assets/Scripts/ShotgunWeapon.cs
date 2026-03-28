// ShotgunWeapon.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
// All pellet hits are batched into one pair of RPCs per shot.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class ShotgunWeapon : WeaponBase
{
    [Header("Shotgun settings")]
    public int   pelletCount    = 8;
    public float pelletSpread   = 8f;
    public float effectiveRange = 20f;

    private bool _cancelReload;

    // Pre-allocated per-shot buffers — avoids allocating 4 new List<> objects
    // every time the trigger is pulled (which causes GC pressure during firefights).
    private readonly List<ulong>   _hitIds     = new();
    private readonly List<float>   _hitDamages = new();
    private readonly List<Vector3> _hitPoints  = new();
    private readonly List<Vector3> _hitNormals = new();

    protected override void Awake()
    {
        base.Awake();
        weaponName   = "Shotgun";
        isAutomatic  = false;
        damage       = 12f;
        fireRate     = 0.8f;
        range        = 40f;
        magazineSize = 6;
        maxAmmo      = 24;
        reloadTime   = 0.6f;

        // FIX: re-initialize after setting our own magazineSize (see WeaponBase.Awake comment)
        _currentAmmo = magazineSize;
        _totalAmmo   = maxAmmo;
    }

    // ── Reload cancellation (weapon-switch path) ──────────────────────────────
    //
    // ShotgunWeapon has its own _cancelReload flag for the fire-during-reload path.
    // We must reset it here too, otherwise a stale true value from a previous
    // switch-while-reloading could silently abort the NEXT reload on this weapon.
    public override void CancelReload()
    {
        _cancelReload = false;   // reset shotgun-specific shell-load-cancel flag
        base.CancelReload();     // stops coroutine, clears _isReloading, fires onReloadEnd
    }

    public override void TryFire(Camera cam)
    {
        if (_isReloading) { _cancelReload = true; return; }
        base.TryFire(cam);
    }

    protected override void Fire(Camera cam)
    {
        _hitIds.Clear();
        _hitDamages.Clear();
        _hitPoints.Clear();
        _hitNormals.Clear();

        for (int i = 0; i < pelletCount; i++)
        {
            Vector3 dir = Quaternion.Euler(
                Random.Range(-pelletSpread, pelletSpread),
                Random.Range(-pelletSpread, pelletSpread), 0) * cam.transform.forward;

            if (!Physics.Raycast(cam.transform.position, dir, out RaycastHit hit, range)) continue;

            // Owner sees all impacts immediately
            SpawnImpactFX(hit.point, hit.normal);
            _hitPoints.Add(hit.point);
            _hitNormals.Add(hit.normal);

            NetworkObject netObj = hit.collider.GetComponentInParent<NetworkObject>();
            if (netObj != null)
            {
                float falloff = hit.distance <= effectiveRange ? 1f : 0.5f;
                _hitIds.Add(netObj.NetworkObjectId);
                _hitDamages.Add(damage * falloff);
            }
        }

        if (_hitIds.Count > 0)
            _shooter?.RegisterShotgunHitsServerRpc(_hitIds.ToArray(), _hitDamages.ToArray());

        if (_hitPoints.Count > 0)
            _shooter?.BroadcastShotgunImpactsRpc(_shooter.CurrentWeaponIndex,
                _hitPoints.ToArray(), _hitNormals.ToArray());
    }

    protected override IEnumerator ReloadRoutine()
    {
        _isReloading  = true;
        _cancelReload = false;
        onReloadStart?.Invoke();

        // Infinite ammo — load one shell at a time until full, never consume reserve.
        while (_currentAmmo < magazineSize)
        {
            if (_cancelReload) break;
            if (reloadSound != null) audioSource?.PlayOneShot(reloadSound);
            yield return new WaitForSeconds(reloadTime);
            if (_cancelReload) break;

            _currentAmmo++;
            onAmmoChanged?.Invoke(_currentAmmo, _totalAmmo);
        }

        _cancelReload    = false;
        _isReloading     = false;
        _reloadCoroutine = null; // routine finished naturally — clear base reference
        onReloadEnd?.Invoke();
    }
}