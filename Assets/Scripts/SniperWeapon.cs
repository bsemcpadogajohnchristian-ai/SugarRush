// SniperWeapon.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+

using UnityEngine;
using Unity.Netcode;

public class SniperWeapon : WeaponBase
{
    [Header("Sniper settings")]
    public float unscopedSpread = 4f;
    [HideInInspector] public bool isScoped;

    protected override void Awake()
    {
        base.Awake();
        weaponName   = "Sniper";
        isAutomatic  = false;
        damage       = 90f;
        fireRate     = 1.5f;
        range        = 300f;
        magazineSize = 5;
        maxAmmo      = 20;
        reloadTime   = 3.2f;

        // FIX: base.Awake() runs _currentAmmo = magazineSize BEFORE we set
        // magazineSize above, so it captures WeaponBase's default (30) instead
        // of our value (5). This caused the HUD to display 30 (rifle's count)
        // on the sniper until the first reload reassigned _currentAmmo = magazineSize.
        // Re-initialize here now that our stats are final.
        _currentAmmo = magazineSize;
        _totalAmmo   = maxAmmo;
    }

    protected override void Fire(Camera cam)
    {
        Vector3 dir = cam.transform.forward;
        if (!isScoped)
            dir = Quaternion.Euler(
                Random.Range(-unscopedSpread, unscopedSpread),
                Random.Range(-unscopedSpread, unscopedSpread), 0) * dir;

        if (Physics.Raycast(cam.transform.position, dir, out RaycastHit hit, range))
        {
            NetworkObject netObj = hit.collider.GetComponentInParent<NetworkObject>();
            if (netObj != null) ReportHit(netObj.NetworkObjectId, hit.point, hit.normal);
            else                ReportWorldHit(hit.point, hit.normal);
        }
    }
}
