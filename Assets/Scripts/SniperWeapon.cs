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
