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
