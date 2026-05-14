// BazookaWeapon.cs — Sugar Rush
//
// ── CHANGES ───────────────────────────────────────────────────────────────────
//   • explosionMask tooltip updated — explains which layers to assign and notes
//     that leaving it at Nothing is safe (Rocket.cs falls back to
//     DefaultRaycastLayers) but an explicit mask is recommended.
//   • Awake() now logs a LogWarning when explosionMask == 0 so developers
//     are alerted during Play Mode testing rather than silently getting the
//     fallback behaviour in production.
//   • No gameplay logic changed.

using UnityEngine;

public class BazookaWeapon : WeaponBase
{
    [Header("Bazooka settings")]
    public GameObject rocketPrefab;
    public float      rocketSpeed     = 20f;
    public float      splashRadius    = 5f;
    public float      splashDamage    = 80f;
    public float      directDamage    = 120f;

    [Tooltip("Layers the explosion can detect players and terrain on.\n\n" +
             "RECOMMENDED: assign your Player layer and your Ground/World layer here.\n\n" +
             "If left as Nothing (0) the rocket falls back to DefaultRaycastLayers " +
             "(hits everything), which works but may produce unwanted interactions " +
             "with UI, triggers, or other non-physical layers.")]
    public LayerMask explosionMask;

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

        // Warn the developer if explosionMask was never assigned.
        // Rocket.cs will fall back gracefully, but an explicit mask is better.
        if (explosionMask.value == 0)
            Debug.LogWarning(
                "[BazookaWeapon] explosionMask is not assigned (Nothing). " +
                "The rocket will fall back to Physics.DefaultRaycastLayers. " +
                "Assign your Player + Ground layers in the Inspector for precise control.",
                this);
    }

    protected override void Fire(Camera cam)
    {
        _shooter?.SpawnRocketServerRpc(
            muzzle.position, cam.transform.rotation,
            rocketSpeed, splashRadius, splashDamage, directDamage);
    }
}