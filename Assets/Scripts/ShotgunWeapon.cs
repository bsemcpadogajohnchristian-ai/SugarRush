// ShotgunWeapon.cs — Sugar Rush
//
// ── KILL FEED CHANGE ─────────────────────────────────────────────────────────
//   RegisterShotgunHitsServerRpc now receives weaponName as well.
//   Pass it through from the Fire() call below.
//   All other logic is identical to the original.

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

        _currentAmmo = magazineSize;
        _totalAmmo   = maxAmmo;
    }

    public override void CancelReload()
    {
        _cancelReload = false;
        base.CancelReload();
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

        // ── KILL FEED CHANGE: pass weaponName so server can attribute the kill ──
        if (_hitIds.Count > 0)
            _shooter?.RegisterShotgunHitsServerRpc(_hitIds.ToArray(), _hitDamages.ToArray(), weaponName);

        if (_hitPoints.Count > 0)
            _shooter?.BroadcastShotgunImpactsRpc(_shooter.CurrentWeaponIndex,
                _hitPoints.ToArray(), _hitNormals.ToArray());
    }

    protected override IEnumerator ReloadRoutine()
    {
        _isReloading  = true;
        _cancelReload = false;
        onReloadStart?.Invoke();

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
        _reloadCoroutine = null;
        onReloadEnd?.Invoke();
    }
}
