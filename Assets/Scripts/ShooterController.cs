// ShooterController.cs — Sugar Rush
//
// ── FP / 3P WEAPON ARCHITECTURE ─────────────────────────────────────────────
//
//  PROBLEM (double gun, no muzzle flash for observers):
//    availableWeapons holds the FP weapon GameObjects (children of fpShooterArms).
//    For non-owners, fpShooterArms is inactive → those GameObjects are inaccessible
//    → BroadcastMuzzleFlashRpc called PlayMuzzleFlashLocal() on a disabled object
//    → no muzzle flash and no fire audio were ever produced for spectating clients.
//    The FP weapons were also not on the "Arms" layer, so the main camera rendered
//    them alongside the 3P body weapon → two guns visible to the owner.
//
//  FIX — two-weapon-set pattern (standard in production FPS games):
//
//    FP set  (availableWeapons):  WeaponBase scripts + FP meshes + FP audio.
//      • Lives under fpShooterArms (child of CameraHolder).
//      • PlayerSetup.SetLayerRecursively stamps the entire hierarchy with the
//        "Arms" layer → ArmsCamera (Overlay, Culling Mask = Arms only) is the
//        sole renderer; main camera excludes Arms → owner never sees FP arms
//        through the main camera.
//      • Only the owner activates fpShooterArms. All game logic (firing, reload,
//        ammo) runs here. FP muzzle flash plays via PlayMuzzleFlashLocal().
//
//    3P set  (thirdPersonWeapons):  Pure-visual GameObjects on the body rig.
//      • Parented to the hand bone on Body_Shooter (or as children of it).
//      • Switched by ApplyWeaponVisibility() on ALL clients via equippedWeaponIndex.
//      • On the owner: bodyShooter renderers are disabled (PlayerSetup), so the
//        3P weapon is invisible to the owner — only the FP set is seen.
//      • On non-owners: fpShooterArms is inactive; bodyShooter is visible; the
//        active 3P weapon is the one observers see.
//
//    Effects:
//      Owner     — FP muzzle flash on FP weapon muzzle, seen via ArmsCamera.
//                  Impact FX spawned at world position → visible to all cameras.
//      Non-owner — BroadcastMuzzleFlashRpc uses thirdPersonMuzzles[i] to spawn
//                  3P muzzle flash at the correct world position on the body rig.
//                  Audio played via sharedWeaponAudio (always-active AudioSource
//                  on the player root, never under a disabled parent).
//
//  INSPECTOR SETUP (per slot, must match availableWeapons order):
//    thirdPersonWeapons[i]  — the 3P weapon mesh GameObject on Body_Shooter's hand bone
//    thirdPersonMuzzles[i]  — Transform at the 3P weapon's barrel tip
//    thirdPersonMuzzleFX[i] — muzzle flash prefab (can reuse the same as FP or make a smaller one)
//    sharedWeaponAudio      — AudioSource on the Player root (always active)

using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

public class ShooterController : NetworkBehaviour
{
    [Header("Weapons — FP set (WeaponBase scripts, children of fpShooterArms)")]
    public List<WeaponBase> availableWeapons = new();

    // ── 3P weapon visuals ────────────────────────────────────────────────────
    [Header("Weapons — 3P set (pure-visual meshes on Body_Shooter hand bone)")]
    [Tooltip("One entry per availableWeapons slot, same order.\n" +
             "Each GameObject is the 3P mesh that represents that weapon on the body rig.\n" +
             "ApplyWeaponVisibility enables the active one and disables the rest on ALL clients.")]
    public List<GameObject> thirdPersonWeapons = new();

    [Tooltip("One entry per availableWeapons slot, same order.\n" +
             "The Transform at the barrel tip of each 3P weapon mesh.\n" +
             "BroadcastMuzzleFlashRpc spawns the muzzle-flash FX here for non-owners.")]
    public List<Transform>  thirdPersonMuzzles = new();

    [Tooltip("One entry per availableWeapons slot, same order.\n" +
             "The muzzle-flash VFX prefab for each 3P weapon.\n" +
             "Can be the same prefab as the FP weapon's muzzleFlashFX or a smaller version.")]
    public List<GameObject> thirdPersonMuzzleFX = new();

    [Header("Audio")]
    [Tooltip("AudioSource on the Player root (NOT under fpShooterArms).\n" +
             "Must be always-active so non-owner clients hear fire and reload sounds\n" +
             "even though fpShooterArms is disabled on their end.")]
    public AudioSource sharedWeaponAudio;

    [Header("Camera")]
    public Camera playerCamera;
    public float  defaultFOV = 70f;
    public float  scopedFOV  = 30f;

    public UnityEvent<int>  onWeaponEquipped = new();
    public UnityEvent<bool> onScopeChanged   = new();

    private WeaponBase  _current;
    private int         _currentIndex;
    private bool        _isScoped;
    private bool        _prevScoped;
    private bool        _inventoryOpen;
    private bool        _inSwapZone;
    private PlayerStats _stats;

    public int CurrentWeaponIndex => _currentIndex;

    private void Awake() => _stats = GetComponent<PlayerStats>();

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;

            // Drive both FP and 3P weapon visibility from the NV on non-owners.
            ApplyWeaponVisibility(_stats.equippedWeaponIndex.Value);
            _stats.equippedWeaponIndex.OnValueChanged += OnWeaponIndexChanged;
            return;
        }

        EquipWeapon(0);
        HUDManager.Instance?.SetInventoryVisible(false);
        if (HUDManager.Instance != null)
            HUDManager.Instance.RefreshShooterAmmo(this);
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner)
        {
            if (_stats != null)
                _stats.equippedWeaponIndex.OnValueChanged -= OnWeaponIndexChanged;
            return;
        }

        if (_current != null)
        {
            _current.onFired.RemoveListener(OnCurrentWeaponFired);
            _current.onReloadStart.RemoveListener(OnCurrentWeaponReloadStart);
            _current.onReloadEnd.RemoveListener(OnCurrentWeaponReloadEnd);
        }

        if (_stats != null)
        {
            _stats.isReloadingNV.Value = false;
            _stats.isAutoFiring.Value  = false;
        }
    }

    private void OnWeaponIndexChanged(int prev, int next) => ApplyWeaponVisibility(next);

    // ── ApplyWeaponVisibility ────────────────────────────────────────────────
    //
    // Switches both the FP set (availableWeapons) and the 3P set (thirdPersonWeapons)
    // so the correct mesh is shown in each context:
    //
    //   Owner         FP weapon[i] active  |  3P weapon[i] active (body hidden anyway)
    //   Non-owner     FP weapons N/A        |  3P weapon[i] active (body visible)
    //
    // Called on owner from EquipWeapon(), on non-owners from the NV callback.
    private void ApplyWeaponVisibility(int index)
    {
        // FP weapons — only meaningful when fpShooterArms is active (owner side).
        for (int i = 0; i < availableWeapons.Count; i++)
        {
            if (availableWeapons[i] != null)
                availableWeapons[i].gameObject.SetActive(i == index);
        }

        // 3P weapons — meaningful for ALL clients (non-owner sees body; owner has
        // body hidden but no harm done setting this for correctness).
        for (int i = 0; i < thirdPersonWeapons.Count; i++)
        {
            if (thirdPersonWeapons[i] != null)
                thirdPersonWeapons[i].SetActive(i == index);
        }
    }

    private void Update()
    {
        if (!IsOwner || _stats.IsDead()) return;
        HandleFire();
        HandleScope();
        HandleReload();
        HandleInventory();
        HandleQuickSwap();
    }

    // ── FIX: Rifle auto-fire animation stops when ammo runs out ─────────────
    private void HandleFire()
    {
        if (_inventoryOpen || _current == null) return;

        if (_current.isAutomatic)
        {
            bool holding = Input.GetMouseButton(0);
            if (holding) _current.TryFire(playerCamera);

            bool actuallyFiring = holding
                                  && _current.GetCurrentAmmo() > 0
                                  && !_current.IsReloading();

            if (_stats != null && _stats.isAutoFiring.Value != actuallyFiring)
                _stats.isAutoFiring.Value = actuallyFiring;
        }
        else
        {
            if (_stats != null && _stats.isAutoFiring.Value)
                _stats.isAutoFiring.Value = false;

            if (Input.GetMouseButtonDown(0)) _current.TryFire(playerCamera);
        }
    }

    private void HandleScope()
    {
        if (_current is not SniperWeapon sniper) return;

        if (Input.GetMouseButtonDown(1))
        {
            _isScoped = !_isScoped;
            StartCoroutine(SmoothFOV(_isScoped ? scopedFOV : defaultFOV));
        }
        if (Input.GetMouseButtonUp(1) && _isScoped)
        {
            _isScoped = false;
            StartCoroutine(SmoothFOV(defaultFOV));
        }

        sniper.isScoped = _isScoped;

        if (_isScoped != _prevScoped)
        {
            onScopeChanged?.Invoke(_isScoped);
            _prevScoped = _isScoped;
            if (_stats != null) _stats.isScopedNV.Value = _isScoped;
        }
    }

    private IEnumerator SmoothFOV(float target)
    {
        float start = playerCamera.fieldOfView;
        for (float t = 0f; t < 1f; t += Time.deltaTime * 8f)
        { playerCamera.fieldOfView = Mathf.Lerp(start, target, t); yield return null; }
        playerCamera.fieldOfView = target;
    }

    private void HandleReload()
    {
        if (Input.GetKeyDown(KeyCode.R)) _current?.StartReload();
    }

    private void HandleInventory()
    {
        if (!Input.GetKeyDown(KeyCode.B)) return;
        if (!_inSwapZone) return;

        _inventoryOpen = !_inventoryOpen;
        HUDManager.Instance?.SetInventoryVisible(_inventoryOpen);
        Cursor.lockState = _inventoryOpen ? CursorLockMode.None : CursorLockMode.Locked;
    }

    private void HandleQuickSwap()
    {
        if      (Input.GetKeyDown(KeyCode.Alpha1)) EquipWeapon(0);
        else if (Input.GetKeyDown(KeyCode.Alpha2)) EquipWeapon(1);
        else if (Input.GetKeyDown(KeyCode.Alpha3)) EquipWeapon(2);
        else if (Input.GetKeyDown(KeyCode.Alpha4)) EquipWeapon(3);
    }

    public void SetInSwapZone(bool inside)
    {
        _inSwapZone = inside;
        HUDManager.Instance?.ShowSwapZonePrompt(inside);
        if (!inside && _inventoryOpen) CloseInventory();
    }

    public void CloseInventory()
    {
        _inventoryOpen = false;
        HUDManager.Instance?.SetInventoryVisible(false);
        Cursor.lockState = CursorLockMode.Locked;
    }

    public void EquipWeapon(int index)
    {
        if (index < 0 || index >= availableWeapons.Count) return;

        if (_current != null)
        {
            _current.onFired.RemoveListener(OnCurrentWeaponFired);
            _current.onReloadStart.RemoveListener(OnCurrentWeaponReloadStart);
            _current.onReloadEnd.RemoveListener(OnCurrentWeaponReloadEnd);
        }

        _current?.CancelReload();
        _current?.RefillAmmo();

        _currentIndex = index;
        _current      = availableWeapons[index];

        // ApplyWeaponVisibility handles both FP and 3P weapon sets.
        ApplyWeaponVisibility(_currentIndex);

        _current.onFired.AddListener(OnCurrentWeaponFired);
        _current.onReloadStart.AddListener(OnCurrentWeaponReloadStart);
        _current.onReloadEnd.AddListener(OnCurrentWeaponReloadEnd);

        if (_stats != null)
        {
            _stats.isReloadingNV.Value       = false;
            _stats.isAutoFiring.Value        = false;
            _stats.isScopedNV.Value          = false;
            _stats.equippedWeaponIndex.Value = _currentIndex;
        }

        if (_isScoped) { _isScoped = false; _prevScoped = false; playerCamera.fieldOfView = defaultFOV; }

        onWeaponEquipped?.Invoke(_currentIndex);
        HUDManager.Instance?.NotifyWeaponChanged(index);
    }

    private void OnCurrentWeaponFired()
    {
        if (_stats != null) _stats.shootFireSequence.Value++;
    }

    private void OnCurrentWeaponReloadStart()
    {
        if (_stats != null) _stats.isReloadingNV.Value = true;
    }

    private void OnCurrentWeaponReloadEnd()
    {
        if (_stats != null) _stats.isReloadingNV.Value = false;
    }

    [Rpc(SendTo.Server)]
    public void RegisterHitServerRpc(ulong targetId, float dmg, string weaponName,
        RpcParams rpcParams = default)
    {
        ulong senderClientId = rpcParams.Receive.SenderClientId;

        if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(targetId, out var obj)) return;
        TeamID myTeam = _stats.team.Value;

        PlayerStats ps = obj.GetComponent<PlayerStats>();
        if (ps != null && ps.team.Value != myTeam)
            ps.TakeDamageFrom(dmg, senderClientId, weaponName);

        obj.GetComponent<DecoyAI>()?.TakeHitRpc(myTeam);
    }

    [Rpc(SendTo.Server)]
    public void RegisterShotgunHitsServerRpc(ulong[] ids, float[] damages, string weaponName,
        RpcParams rpcParams = default)
    {
        ulong senderClientId = rpcParams.Receive.SenderClientId;
        TeamID myTeam = _stats.team.Value;
        int count = Mathf.Min(ids.Length, damages.Length);
        for (int i = 0; i < count; i++)
        {
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(ids[i], out var obj)) continue;
            PlayerStats ps = obj.GetComponent<PlayerStats>();
            if (ps != null && ps.team.Value != myTeam)
                ps.TakeDamageFrom(damages[i], senderClientId, weaponName);
            obj.GetComponent<DecoyAI>()?.TakeHitRpc(myTeam);
        }
    }

    // ── BroadcastMuzzleFlashRpc ──────────────────────────────────────────────
    //
    // FIX: Previously called availableWeapons[i].PlayMuzzleFlashLocal() on
    // non-owners. Those weapons are children of disabled fpShooterArms →
    // inactive → PlayMuzzleFlashLocal is a no-op → observers saw nothing.
    //
    // NEW BEHAVIOUR for non-owners:
    //   • Spawn 3P muzzle flash at thirdPersonMuzzles[weaponIndex] using FXPool.
    //   • Play fire audio via sharedWeaponAudio (always-active AudioSource on
    //     the player root — never under a disabled parent GameObject).
    //
    // Owner receives this RPC on the FP side (TryFire → PlayMuzzleFlashLocal)
    // and never enters this path because SendTo.NotOwner excludes them.
    [Rpc(SendTo.NotOwner)]
    public void BroadcastMuzzleFlashRpc(int weaponIndex)
    {
        // ── 3P muzzle flash ─────────────────────────────────────────────────
        if (weaponIndex >= 0 && weaponIndex < thirdPersonMuzzles.Count)
        {
            Transform   muzzle3P = thirdPersonMuzzles[weaponIndex];
            GameObject  fx3P     = weaponIndex < thirdPersonMuzzleFX.Count
                                   ? thirdPersonMuzzleFX[weaponIndex] : null;

            if (muzzle3P != null && fx3P != null)
            {
                if (FXPool.Instance != null)
                    FXPool.Instance.Spawn(fx3P, muzzle3P.position, muzzle3P.rotation);
                else
                {
                    GameObject go = Instantiate(fx3P, muzzle3P.position, muzzle3P.rotation);
                    Destroy(go, 2f);
                }
            }
        }

        // ── Fire audio via shared (always-active) AudioSource ───────────────
        if (weaponIndex >= 0 && weaponIndex < availableWeapons.Count)
        {
            WeaponBase w = availableWeapons[weaponIndex];
            if (w?.fireSound != null)
            {
                // Prefer sharedWeaponAudio (on the player root, always active).
                // Fall back to the FP weapon's own AudioSource only if the root
                // source wasn't assigned in the Inspector.
                AudioSource src = sharedWeaponAudio != null ? sharedWeaponAudio : w.audioSource;
                src?.PlayOneShot(w.fireSound);
            }
        }
    }

    [Rpc(SendTo.NotOwner)]
    public void BroadcastImpactRpc(int weaponIndex, Vector3 point, Vector3 normal)
    {
        if (weaponIndex < 0 || weaponIndex >= availableWeapons.Count) return;
        availableWeapons[weaponIndex]?.SpawnImpactFX(point, normal);
    }

    [Rpc(SendTo.NotOwner)]
    public void BroadcastShotgunImpactsRpc(int weaponIndex, Vector3[] points, Vector3[] normals)
    {
        if (weaponIndex < 0 || weaponIndex >= availableWeapons.Count) return;
        WeaponBase w = availableWeapons[weaponIndex];
        int count = Mathf.Min(points.Length, normals.Length);
        for (int i = 0; i < count; i++) w?.SpawnImpactFX(points[i], normals[i]);
    }

    [Rpc(SendTo.Server)]
    public void SpawnRocketServerRpc(Vector3 pos, Quaternion rot,
        float speed, float splashRadius, float splashDmg, float directDmg)
    {
        BazookaWeapon baz = null;
        foreach (var w in availableWeapons) { baz = w as BazookaWeapon; if (baz != null) break; }
        if (baz?.rocketPrefab == null) { Debug.LogWarning("[Shooter] No BazookaWeapon or rocketPrefab found."); return; }

        GameObject obj = Instantiate(baz.rocketPrefab, pos, rot);
        obj.GetComponent<NetworkObject>()?.Spawn(true);
        obj.GetComponent<Rocket>()?.Initialize(speed, splashRadius, splashDmg, directDmg,
            baz.explosionMask, baz.bulletImpactFX, _stats.team.Value,
            OwnerClientId); // ← kill feed: lets Rocket.Explode attribute the kill
    }

    public WeaponBase GetCurrentWeapon() => _current;
    public bool IsScoped() => _isScoped;
}