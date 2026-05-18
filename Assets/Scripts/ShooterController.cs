// ShooterController.cs — Sugar Rush
//
// ── AMMO FIX ──────────────────────────────────────────────────────────────────
//   BUG: EquipWeapon() called _current?.RefillAmmo() on the weapon being
//   swapped away. This silently reset the old weapon's magazine to full every
//   time the player switched weapons, contributing to the infinite-ammo feel.
//   FIX: That line has been removed. Weapons now retain their actual ammo count
//   across swaps. Reserve ammo is only restored by an AmmoPickup.
//
// ── PAUSE GUARD / SMOKE GRENADE (unchanged from previous version) ─────────────

using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

public class ShooterController : NetworkBehaviour
{
    [Header("Weapons — FP set (WeaponBase scripts, children of fpShooterArms)")]
    public List<WeaponBase> availableWeapons = new();

    [Header("Weapons — 3P set (pure-visual meshes on Body_Shooter hand bone)")]
    public List<GameObject> thirdPersonWeapons  = new();
    public List<Transform>  thirdPersonMuzzles  = new();
    public List<GameObject> thirdPersonMuzzleFX = new();

    [Header("Audio")]
    public AudioSource sharedWeaponAudio;

    [Header("Camera")]
    public Camera playerCamera;
    public float  defaultFOV = 70f;
    public float  scopedFOV  = 30f;

    [Header("Scope — FP Arms Hiding")]
    [Tooltip("Drag fpShooterArms here. All Renderers inside are hidden while scoped " +
             "so the weapon model doesn't overlap the scope overlay. " +
             "Scripts on the object keep running — only visuals are toggled.")]
    public GameObject fpShooterArmsRoot;

    [Header("Smoke Grenade")]
    [Tooltip("Drag your SmokeGrenade prefab here.")]
    public GameObject smokeGrenadePrefab;

    [Tooltip("How fast the grenade travels forward (m/s).")]
    public float smokeThrowForce    = 14f;

    [Tooltip("Upward component added to throw velocity for the arc.")]
    public float smokeThrowArc      = 5.5f;

    [Tooltip("Maximum charges before the cooldown starts.")]
    public int   smokeMaxCharges    = 2;

    [Tooltip("Seconds to recharge all charges after they are spent.")]
    public float smokeGrenadeCooldown = 25f;

    public UnityEvent        onSmokeGrenadeFired    = new();
    public UnityEvent<float> onSmokeGrenadeCooldown = new();
    public UnityEvent<int>   onSmokeChargesChanged  = new();

    public UnityEvent<int>  onWeaponEquipped = new();
    public UnityEvent<bool> onScopeChanged   = new();

    private WeaponBase  _current;
    private int         _currentIndex;
    private bool        _isScoped;
    private bool        _prevScoped;
    private bool        _inventoryOpen;
    private bool        _inSwapZone;
    private PlayerStats _stats;

    private int   _smokeCharges;
    private float _smokeTimer;

    private float _smokeHudTick;
    private const float SMOKE_HUD_RATE = 0.05f; // 20 Hz

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

        _smokeCharges = smokeMaxCharges;

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

        for (int i = 0; i < thirdPersonWeapons.Count; i++)
        {
            if (thirdPersonWeapons[i] != null)
                thirdPersonWeapons[i].SetActive(i == index);
        }
    }

    private void Update()
    {
        if (!IsOwner || _stats.IsDead()) return;

        // ── Block all input while the pause overlay is open ───────────────────
        if (PauseMenuUI.IsPaused) return;

        HandleFire();
        HandleScope();
        HandleReload();
        HandleInventory();
        HandleSmokeGrenade();
        TickSmokeCooldown();
    }

    // ── Smoke grenade ─────────────────────────────────────────────────────────

    private void HandleSmokeGrenade()
    {
        if (_stats.role.Value != PlayerRole.Shooter) return;
        if (!Input.GetKeyDown(KeyCode.Alpha4)) return;
        if (_smokeCharges <= 0 || _smokeTimer > 0f) return;

        if (playerCamera == null)
        {
            Debug.LogError("[ShooterController] HandleSmokeGrenade: playerCamera is null. " +
                           "Assign it in the Inspector.");
            return;
        }

        Vector3 flatForward = playerCamera.transform.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.001f) flatForward = transform.forward;
        flatForward.Normalize();

        Vector3 spawnPos = transform.position
                         + Vector3.up  * 1.5f
                         + flatForward * 1.0f;

        Vector3 velocity = playerCamera.transform.forward * smokeThrowForce
                         + Vector3.up                     * smokeThrowArc;

        ThrowSmokeGrenadeServerRpc(spawnPos, velocity);

        _smokeCharges--;
        onSmokeGrenadeFired?.Invoke();
        onSmokeChargesChanged?.Invoke(_smokeCharges);

        if (_stats != null) _stats.smokeThrowSequence.Value++;

        if (_smokeCharges <= 0)
            _smokeTimer = smokeGrenadeCooldown;
    }

    [Rpc(SendTo.Server)]
    private void ThrowSmokeGrenadeServerRpc(Vector3 spawnPos, Vector3 velocity)
    {
        if (smokeGrenadePrefab == null)
        {
            Debug.LogError("[ShooterController] smokeGrenadePrefab is not assigned!");
            return;
        }

        Quaternion rot = velocity.sqrMagnitude > 0.01f
            ? Quaternion.LookRotation(velocity.normalized)
            : Quaternion.identity;

        GameObject    obj = Instantiate(smokeGrenadePrefab, spawnPos, rot);
        NetworkObject no  = obj.GetComponent<NetworkObject>();

        if (no == null)
        {
            Debug.LogError("[ShooterController] smokeGrenadePrefab is missing a NetworkObject!");
            Destroy(obj);
            return;
        }

        no.Spawn(true);
        obj.GetComponent<SmokeGrenade>()?.Initialize(velocity, _stats.team.Value, NetworkObjectId);
    }

    private void TickSmokeCooldown()
    {
        if (_smokeTimer <= 0f) return;

        _smokeTimer -= Time.deltaTime;

        if (_smokeTimer <= 0f)
        {
            _smokeTimer   = 0f;
            _smokeCharges = smokeMaxCharges;
            onSmokeGrenadeCooldown?.Invoke(0f);
            onSmokeChargesChanged?.Invoke(_smokeCharges);
            _smokeHudTick = 0f;
            return;
        }

        _smokeHudTick -= Time.deltaTime;
        if (_smokeHudTick <= 0f)
        {
            _smokeHudTick = SMOKE_HUD_RATE;
            onSmokeGrenadeCooldown?.Invoke(_smokeTimer);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

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
            SetFPArmsVisible(!_isScoped);
        }
    }

    private void SetFPArmsVisible(bool visible)
    {
        if (fpShooterArmsRoot == null) return;
        foreach (Renderer r in fpShooterArmsRoot.GetComponentsInChildren<Renderer>(includeInactive: true))
            r.enabled = visible;
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
        // ── FIX: removed _current?.RefillAmmo() here.
        // The old code reset the magazine to full on every weapon swap,
        // making ammo effectively infinite across swaps. Weapons now
        // retain their actual ammo state when you switch away from them.

        _currentIndex = index;
        _current      = availableWeapons[index];

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

        if (_isScoped) { _isScoped = false; _prevScoped = false; playerCamera.fieldOfView = defaultFOV; SetFPArmsVisible(true); }

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

    [Rpc(SendTo.NotOwner)]
    public void BroadcastMuzzleFlashRpc(int weaponIndex)
    {
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

        if (weaponIndex >= 0 && weaponIndex < availableWeapons.Count)
        {
            WeaponBase w = availableWeapons[weaponIndex];
            if (w?.fireSound != null)
            {
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
            baz.explosionMask, baz.bulletImpactFX, _stats.team.Value, OwnerClientId);
    }

    public WeaponBase GetCurrentWeapon() => _current;
    public bool IsScoped() => _isScoped;

    // ── Ammo pickup (called by AmmoPickup on the server) ──────────────────────

    /// <summary>
    /// Called server-side by AmmoPickup when the teammate Collector grabs a pack.
    /// Sends an RPC to this Shooter's owner so they refill all weapons locally.
    /// </summary>
    public void RefillAllAmmo()
    {
        if (!IsServer) return;
        RefillAllAmmoRpc(RpcTarget.Single(OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void RefillAllAmmoRpc(RpcParams rpcParams = default)
    {
        // Runs only on the Shooter's own client.
        // Each weapon fires onAmmoChanged so the HUD updates immediately.
        foreach (WeaponBase weapon in availableWeapons)
            weapon?.RefillAllAmmo();

        Debug.Log("[ShooterController] All weapons refilled by teammate Collector!");
    }
}