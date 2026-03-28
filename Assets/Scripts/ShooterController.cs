// ShooterController.cs
// Sugar Rush
// Unity 6.3 LTS + Netcode for GameObjects v2.1+

using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class ShooterController : NetworkBehaviour
{
    [Header("Weapons (assign all weapon child GameObjects)")]
    public List<WeaponBase> availableWeapons = new();

    [Header("Camera")]
    public Camera playerCamera;
    public float  defaultFOV = 70f;
    public float  scopedFOV  = 30f;

    // NOTE: inventoryUI has been REMOVED from this prefab component.
    // The inventory panel now lives on HUDCanvas (a scene object) and is
    // controlled exclusively through HUDManager.Instance.SetInventoryVisible().
    // ShooterController never touches the panel GameObject directly.

    private WeaponBase  _current;
    private int         _currentIndex;
    private bool        _isScoped;
    private bool        _inventoryOpen;
    private bool        _inSwapZone;
    private PlayerStats _stats;

    public int CurrentWeaponIndex => _currentIndex;

    private void Awake() => _stats = GetComponent<PlayerStats>();

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) { enabled = false; return; }
        EquipWeapon(0);

        // Make sure the inventory panel is hidden on spawn.
        // HUDManager may not be ready yet on the very first frame,
        // so we hide it one frame later (same pattern as InitHUDWhenReady).
        HUDManager.Instance?.SetInventoryVisible(false);

        // HUD may have run ResetAndInitialize before this OnNetworkSpawn fired
        // (NGO component order is not guaranteed). Re-wire the ammo panel now
        // that we have a confirmed current weapon.
        if (HUDManager.Instance != null)
            HUDManager.Instance.RefreshShooterAmmo(this);
    }

    private void Update()
    {
        if (!IsOwner || _stats.IsDead()) return;
        HandleFire();
        HandleScope();
        HandleReload();
        HandleInventory();
    }

    // ── Input handling ────────────────────────────────────────────────────────

    private void HandleFire()
    {
        if (_inventoryOpen || _current == null) return;
        bool fire = _current.isAutomatic ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0);
        if (fire) _current.TryFire(playerCamera);
    }

    private void HandleScope()
    {
        if (_current is not SniperWeapon sniper) return;

        if (Input.GetMouseButtonDown(1)) { _isScoped = !_isScoped; StartCoroutine(SmoothFOV(_isScoped ? scopedFOV : defaultFOV)); }
        if (Input.GetMouseButtonUp(1) && _isScoped) { _isScoped = false; StartCoroutine(SmoothFOV(defaultFOV)); }
        sniper.isScoped = _isScoped;
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

    // ── Called by WeaponSwapZone and InventoryUI ──────────────────────────────

    /// <summary>
    /// Called by WeaponSwapZone. Shows/hides the swap prompt and auto-closes
    /// the inventory if the player walks out mid-selection.
    /// </summary>
    public void SetInSwapZone(bool inside)
    {
        _inSwapZone = inside;
        HUDManager.Instance?.ShowSwapZonePrompt(inside);

        if (!inside && _inventoryOpen)
            CloseInventory();
    }

    /// <summary>
    /// Called by InventoryUI when the player selects a weapon.
    /// Resets _inventoryOpen so the B-key toggle stays in sync.
    /// </summary>
    public void CloseInventory()
    {
        _inventoryOpen = false;
        HUDManager.Instance?.SetInventoryVisible(false);
        Cursor.lockState = CursorLockMode.Locked;
    }

    // ── Weapon equip ──────────────────────────────────────────────────────────

    public void EquipWeapon(int index)
    {
        if (index < 0 || index >= availableWeapons.Count) return;

        // ── Cancel any in-progress reload BEFORE SetActive(false) ────────────
        //
        // SetActive(false) kills all coroutines on the weapon silently.
        // Without this call, _isReloading stays true on the old weapon with no
        // coroutine to ever clear it. Switching back → TryFire sees _isReloading
        // == true → gun refuses to fire permanently.
        //
        // CancelReload() stops the coroutine, resets _isReloading, and fires
        // onReloadEnd — which drives HideReloadText() on the HUD automatically.
        _current?.CancelReload();

        // Refill the departing weapon to a full magazine so when the player
        // returns to it later it is always ready to fire at full capacity.
        _current?.RefillAmmo();

        _current?.gameObject.SetActive(false);
        _currentIndex = index;
        _current = availableWeapons[index];
        _current.gameObject.SetActive(true);
        if (_isScoped) { _isScoped = false; playerCamera.fieldOfView = defaultFOV; }

        // Tell the HUD: re-wire ammo events and highlight the correct inventory card.
        // Safe to call before HUDManager is ready — the ?. guard handles null.
        HUDManager.Instance?.NotifyWeaponChanged(index);
    }

    // ── Server RPCs (damage) ──────────────────────────────────────────────────

    [Rpc(SendTo.Server)]
    public void RegisterHitServerRpc(ulong targetId, float dmg)
    {
        if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(targetId, out var obj)) return;
        TeamID myTeam = GetComponent<PlayerStats>()?.team.Value ?? _stats.team.Value;

        PlayerStats ps = obj.GetComponent<PlayerStats>();
        if (ps != null && ps.team.Value != myTeam)
            ps.TakeDamage(dmg);

        DecoyAI decoy = obj.GetComponent<DecoyAI>();
        decoy?.TakeHitRpc(myTeam);
    }

    [Rpc(SendTo.Server)]
    public void RegisterShotgunHitsServerRpc(ulong[] ids, float[] damages)
    {
        TeamID myTeam = GetComponent<PlayerStats>()?.team.Value ?? _stats.team.Value;
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

    // ── NotOwner FX RPCs ──────────────────────────────────────────────────────

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

    // ── Rocket (Bazooka) ──────────────────────────────────────────────────────

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