// PlayerStats.cs — Sugar Rush
// Unity 6.3 LTS + Netcode for GameObjects v2.1+
//
// ── RESULT SCREEN CHANGES ─────────────────────────────────────────────────────
//   TakeDamageFrom  — before applying damage, finds the attacker's
//                     PlayerMatchStats and calls AddDamage(damage).
//   DieServer       — calls GetComponent<PlayerMatchStats>()?.AddDeath()
//                     on the victim so the death counter is always accurate.
//   Everything else is identical to the original.
//
// ── SMOKE GRENADE MIGRATION (previous change, unchanged) ─────────────────────
//   smokeThrowSequence NetworkVariable added.

using Unity.Collections;
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

    public NetworkVariable<int> smokeThrowSequence = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<int> equippedWeaponIndex = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<bool> isReloadingNV = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<bool> isAutoFiring = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<bool> isScopedNV = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ── Display name (set by LobbyManager from registered menu name) ─────────

    public NetworkVariable<FixedString64Bytes> playerName = new(
        new FixedString64Bytes("Player"),
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

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
    /// Kill feed will show "World" as the killer (e.g. fall damage, environment).
    /// ulong.MaxValue is the sentinel for "no player killer" — avoids colliding
    /// with the host's real ClientId which is 0 in NGO host mode.
    /// </summary>
    public void TakeDamage(float damage)
        => TakeDamageFrom(damage, ulong.MaxValue, "World");

    /// <summary>
    /// Damage with full kill attribution — always prefer this from weapons.
    /// </summary>
    public void TakeDamageFrom(float damage, ulong killerId, string weaponName)
    {
        if (!IsServer || isDead.Value) return;

        // ── RESULT SCREEN CHANGE: track damage dealt on the attacker ──────────
        if (killerId != ulong.MaxValue)
        {
            PlayerStats attacker = NetworkGameManager.Instance?.FindPlayerByClientId(killerId);
            attacker?.GetComponent<PlayerMatchStats>()?.AddDamage(damage);
        }
        // ─────────────────────────────────────────────────────────────────────

        currentHP.Value = Mathf.Max(currentHP.Value - damage, 0f);
        if (currentHP.Value <= 0f) DieServer(killerId, weaponName);
    }

    private void DieServer(ulong killerId, string weaponName)
    {
        isDead.Value = true;

        // ── RESULT SCREEN CHANGE: increment victim's death counter ────────────
        GetComponent<PlayerMatchStats>()?.AddDeath();
        // ─────────────────────────────────────────────────────────────────────

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

    public string GetDisplayName()
    {
        string name = playerName.Value.ToString();
        // Fall back to role/team label if the name was never set
        if (string.IsNullOrWhiteSpace(name) || name == "Player")
        {
            string t = team.Value == TeamID.TeamA ? "TeamA" : "TeamB";
            string r = role.Value == PlayerRole.Shooter ? "Shooter" : "Collector";
            return $"[{t}] {r}";
        }
        return name;
    }
}