// PlayerStats.cs — Sugar Rush
// Unity 6.3 LTS + Netcode for GameObjects v2.1+
//
// ── KILL FEED CHANGES ─────────────────────────────────────────────────────────
//   • TakeDamageFrom(float, ulong, string) — new overload that carries killer
//     clientId and weapon name into DieServer, which forwards them to
//     NetworkGameManager.OnPlayerKilled for kill-feed broadcasting.
//   • DieServer now accepts (ulong killerId, string weaponName).
//     The old zero-arg DieServer is gone; TakeDamage(float) calls the new one
//     with killerId=0 / weaponName="Unknown" as a safe fallback.
//   • GetDisplayName() — returns "[TeamA] Shooter" style label for kill feed.

using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

public class PlayerStats : NetworkBehaviour
{
    // ── Synced variables ──────────────────────────────────────────────────────

    public NetworkVariable<PlayerRole> role = new(PlayerRole.Shooter,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<TeamID> team = new(TeamID.TeamA,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<float> currentHP = new(100f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<bool> isDead = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<bool> isCrouching = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<bool> isSprinting = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<int> jumpSequence = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ── Shooter animation NVs ─────────────────────────────────────────────────

    public NetworkVariable<int> shootFireSequence = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<int> equippedWeaponIndex = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<bool> isReloadingNV = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<bool> isAutoFiring = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<bool> isScopedNV = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ── Locomotion animation NVs ──────────────────────────────────────────────

    public NetworkVariable<bool> isMovingNV = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<Vector2> localMoveDir = new(Vector2.zero,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<bool> isGroundedNV = new(true,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ── Inspector settings ────────────────────────────────────────────────────

    [Header("Max HP per role")]
    public float shooterMaxHP   = 100f;
    public float collectorMaxHP = 150f;

    [Header("Base speed multiplier per role")]
    public float shooterSpeed   = 1.0f;
    public float collectorSpeed = 1.3f;

    // ── Derived ───────────────────────────────────────────────────────────────

    [HideInInspector] public float maxHP;
    [HideInInspector] public float speedMultiplier;

    // ── Events ────────────────────────────────────────────────────────────────

    public UnityEvent<float, float> onHealthChanged = new();
    public UnityEvent               onDeath         = new();
    public UnityEvent               onRespawn       = new();

    // ── Unity / NGO lifecycle ─────────────────────────────────────────────────

    private CharacterController _cc;

    public override void OnNetworkSpawn()
    {
        _cc = GetComponent<CharacterController>();

        currentHP.OnValueChanged += (_, next) =>
        {
            if (maxHP > 0f) onHealthChanged?.Invoke(next, maxHP);
        };

        isDead.OnValueChanged += (_, next) =>
        {
            if (next) onDeath?.Invoke();
            else      onRespawn?.Invoke();
        };

        role.OnValueChanged += (_, _) => ApplyRoleStats();
        ApplyRoleStats();
    }

    // ── Role stats ────────────────────────────────────────────────────────────

    public void ApplyRoleStats()
    {
        if (role.Value == PlayerRole.Shooter)
        {
            maxHP           = shooterMaxHP;
            speedMultiplier = shooterSpeed;
        }
        else
        {
            maxHP           = collectorMaxHP;
            speedMultiplier = collectorSpeed;
        }

        if (maxHP > 0f)
            onHealthChanged?.Invoke(currentHP.Value, maxHP);
    }

    // ── Damage / death ────────────────────────────────────────────────────────

    /// <summary>
    /// Generic damage — no kill attribution.
    /// Kill feed will show "Unknown" as the killer (e.g. fall damage sources).
    /// </summary>
    public void TakeDamage(float damage)
        => TakeDamageFrom(damage, 0, "Unknown");

    /// <summary>
    /// Damage with full kill attribution — always prefer this from weapons.
    /// </summary>
    /// <param name="damage">Raw damage amount (server-side).</param>
    /// <param name="killerId">OwnerClientId of the shooter.</param>
    /// <param name="weaponName">Weapon display name, e.g. "Rifle".</param>
    public void TakeDamageFrom(float damage, ulong killerId, string weaponName)
    {
        if (!IsServer || isDead.Value) return;
        currentHP.Value = Mathf.Max(currentHP.Value - damage, 0f);
        if (currentHP.Value <= 0f) DieServer(killerId, weaponName);
    }

    // Called server-side only.
    private void DieServer(ulong killerId, string weaponName)
    {
        isDead.Value = true;
        NetworkGameManager.Instance?.OnPlayerDied(this);
        NetworkGameManager.Instance?.OnPlayerKilled(killerId, weaponName, this);
    }

    // ── Respawn ───────────────────────────────────────────────────────────────

    public void RespawnServer()
    {
        if (!IsServer) return;
        isDead.Value    = false;
        currentHP.Value = maxHP;
    }

    public void RespawnAtPosition(Vector3 pos, Quaternion rot)
    {
        if (!IsServer) return;
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null)
            pc.WarpToSpawnRpc(pos, rot);
        else
            Debug.LogWarning($"[PlayerStats] WarpToSpawnRpc failed — no PlayerController on {gameObject.name}");

        RespawnServer();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public bool IsDead() => isDead.Value;

    /// <summary>
    /// Human-readable display label used in kill feed entries.
    /// Returns e.g. "[TeamA] Shooter". Extend once you have real player names.
    /// </summary>
    public string GetDisplayName()
    {
        string t = team.Value == TeamID.TeamA ? "TeamA" : "TeamB";
        string r = role.Value == PlayerRole.Shooter ? "Shooter" : "Collector";
        return $"[{t}] {r}";
    }
}
