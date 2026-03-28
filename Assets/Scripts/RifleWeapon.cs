// RifleWeapon.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+

using UnityEngine;
using Unity.Netcode;

public class RifleWeapon : WeaponBase
{
    [Header("Rifle settings")]
    public float spreadAngle = 1.5f;

    protected override void Awake()
    {
        base.Awake();
        weaponName   = "Rifle";
        isAutomatic  = true;
        damage       = 22f;
        fireRate     = 0.1f;
        range        = 80f;
        magazineSize = 30;
        maxAmmo      = 90;
        reloadTime   = 2f;

        // FIX: base.Awake() runs _currentAmmo = magazineSize BEFORE we set
        // magazineSize above, so it captures WeaponBase's default (30) instead
        // of our value. Re-initialize here now that our stats are final.
        // (Rifle happens to share the default of 30, but the fix is applied
        // consistently across all weapons for correctness.)
        _currentAmmo = magazineSize;
        _totalAmmo   = maxAmmo;
    }

    protected override void Fire(Camera cam)
    {
        float   sx  = Random.Range(-spreadAngle, spreadAngle);
        float   sy  = Random.Range(-spreadAngle, spreadAngle);
        Vector3 dir = Quaternion.Euler(sx, sy, 0) * cam.transform.forward;

        if (Physics.Raycast(cam.transform.position, dir, out RaycastHit hit, range))
        {
            NetworkObject netObj = hit.collider.GetComponentInParent<NetworkObject>();
            if (netObj != null) ReportHit(netObj.NetworkObjectId, hit.point, hit.normal);
            else                ReportWorldHit(hit.point, hit.normal);
        }
    }
}
