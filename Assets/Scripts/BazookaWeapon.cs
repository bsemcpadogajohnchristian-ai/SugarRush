using UnityEngine;

public class BazookaWeapon : WeaponBase
{
    [Header("Bazooka settings")]
    public GameObject rocketPrefab;
    public float      rocketSpeed     = 20f;
    public float      splashRadius    = 5f;
    public float      splashDamage    = 80f;
    public float      directDamage    = 120f;
    public LayerMask  explosionMask;

    protected override void Awake()
    {
        base.Awake();
        weaponName   = "Bazooka";
        isAutomatic  = false;
        damage       = directDamage;
        fireRate     = 2.5f;
        range        = 200f;
        magazineSize = 1;
        maxAmmo      = 4;
        reloadTime   = 4f;

        
        _currentAmmo = magazineSize;
        _totalAmmo   = maxAmmo;
    }

    protected override void Fire(Camera cam)
    {
        _shooter?.SpawnRocketServerRpc(
            muzzle.position, cam.transform.rotation,
            rocketSpeed, splashRadius, splashDamage, directDamage);
    }
}
