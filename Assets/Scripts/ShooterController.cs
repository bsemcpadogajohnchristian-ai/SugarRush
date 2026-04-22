using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

public class ShooterController : NetworkBehaviour
{
    [Header("Weapons")]
    public List<WeaponBase> availableWeapons = new();

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

    private void ApplyWeaponVisibility(int index)
    {
        for (int i = 0; i < availableWeapons.Count; i++)
        {
            if (availableWeapons[i] != null)
                availableWeapons[i].gameObject.SetActive(i == index);
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

    // ── FIX: Bug 1 — Rifle auto-fire animation persisting when ammo runs out ──────
    //
    // BEFORE: isAutoFiring was set to Input.GetMouseButton(0) alone.
    //   When the magazine empties mid-burst, the mouse is still held,
    //   so isAutoFiring stayed true → IsFiring bool stayed true →
    //   ShooterAnimator and FPShooterAnimator kept the fire animation
    //   playing even though no bullets were leaving the barrel.
    //
    // FIX: isAutoFiring is now only true when ALL THREE conditions hold:
    //   1. Mouse button is held.
    //   2. Current ammo > 0  (clip is not empty).
    //   3. Weapon is not reloading.
    //
    // This guarantees the fire animation stops the instant the last round
    // leaves the chamber and the reload begins, matching the visual exactly.
    private void HandleFire()
    {
        if (_inventoryOpen || _current == null) return;

        if (_current.isAutomatic)
        {
            bool holding = Input.GetMouseButton(0);
            if (holding) _current.TryFire(playerCamera);

            // Only drive the fire animation while the weapon is genuinely
            // shooting — has ammo AND is not mid-reload.
            bool actuallyFiring = holding
                                  && _current.GetCurrentAmmo() > 0
                                  && !_current.IsReloading();

            if (_stats != null && _stats.isAutoFiring.Value != actuallyFiring)
                _stats.isAutoFiring.Value = actuallyFiring;
        }
        else
        {
            // Semi-auto: always clear the auto-fire bool.
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
            _current.onFired.RemoveListener(OnCurrentWeaponFired);

        _current?.CancelReload();
        _current?.RefillAmmo();
        _current?.gameObject.SetActive(false);

        if (_current != null)
        {
            _current.onReloadStart.RemoveListener(OnCurrentWeaponReloadStart);
            _current.onReloadEnd.RemoveListener(OnCurrentWeaponReloadEnd);
        }

        _currentIndex = index;
        _current      = availableWeapons[index];
        _current.gameObject.SetActive(true);

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
    public void RegisterHitServerRpc(ulong targetId, float dmg)
    {
        if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(targetId, out var obj)) return;
        TeamID myTeam = _stats.team.Value;

        PlayerStats ps = obj.GetComponent<PlayerStats>();
        if (ps != null && ps.team.Value != myTeam)
            ps.TakeDamage(dmg);

        obj.GetComponent<DecoyAI>()?.TakeHitRpc(myTeam);
    }

    [Rpc(SendTo.Server)]
    public void RegisterShotgunHitsServerRpc(ulong[] ids, float[] damages)
    {
        TeamID myTeam = _stats.team.Value;
        int count = Mathf.Min(ids.Length, damages.Length);
        for (int i = 0; i < count; i++)
        {
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(ids[i], out var obj)) continue;
            PlayerStats ps = obj.GetComponent<PlayerStats>();
            if (ps != null && ps.team.Value != myTeam)
                ps.TakeDamage(damages[i]);
            obj.GetComponent<DecoyAI>()?.TakeHitRpc(myTeam);
        }
    }

    [Rpc(SendTo.NotOwner)]
    public void BroadcastMuzzleFlashRpc(int weaponIndex)
    {
        if (weaponIndex < 0 || weaponIndex >= availableWeapons.Count) return;
        WeaponBase w = availableWeapons[weaponIndex];
        w?.PlayMuzzleFlashLocal();
        if (w?.fireSound != null) w.audioSource?.PlayOneShot(w.fireSound);
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
            baz.explosionMask, baz.bulletImpactFX, _stats.team.Value);
    }

    public WeaponBase GetCurrentWeapon() => _current;
    public bool IsScoped() => _isScoped;
}