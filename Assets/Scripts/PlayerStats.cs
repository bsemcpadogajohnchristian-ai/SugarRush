// PlayerStats.cs
// Sugar Rush
// Unity 6.3 LTS + Netcode for GameObjects v2.1+
//
// Holds all synced player data: role, team, health, alive/dead.
// Damage and respawn are server-authoritative.

using Unity.Netcode;
using Unity.Netcode.Components;
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

    // ── Inspector settings ────────────────────────────────────────────────────

    [Header("Max HP per role")]
    public float shooterMaxHP   = 100f;
    public float collectorMaxHP = 150f;

    [Header("Base speed multiplier per role")]
    public float shooterSpeed   = 1.0f;
    public float collectorSpeed = 1.3f;

    // ── Derived (set by ApplyRoleStats) ───────────────────────────────────────

    [HideInInspector] public float maxHP;
    [HideInInspector] public float speedMultiplier;

    // ── Events ────────────────────────────────────────────────────────────────

    public UnityEvent<float, float> onHealthChanged = new();
    public UnityEvent               onDeath         = new();
    public UnityEvent               onRespawn       = new();

    // ── Internal ─────────────────────────────────────────────────────────────

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
            maxHP          = shooterMaxHP;
            speedMultiplier = shooterSpeed;
        }
        else
        {
            maxHP          = collectorMaxHP;
            speedMultiplier = collectorSpeed;
        }

        if (maxHP > 0f)
            onHealthChanged?.Invoke(currentHP.Value, maxHP);
    }

    // ── Damage / death ────────────────────────────────────────────────────────

    public void TakeDamage(float damage)
    {
        if (!IsServer || isDead.Value) return;
        currentHP.Value = Mathf.Max(currentHP.Value - damage, 0f);
        if (currentHP.Value <= 0f) DieServer();
    }

    private void DieServer()
    {
        isDead.Value = true;
        NetworkGameManager.Instance?.OnPlayerDied(this);
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

        // Owner-authoritative NetworkTransform means the server CANNOT set the
        // player's position and have it stick — the owning client will override it.
        // We must send a SendTo.Owner RPC so the owning client moves their own
        // CharacterController. Their NetworkTransform then broadcasts the correct
        // position to all other clients automatically.
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null)
            pc.WarpToSpawnRpc(pos, rot);
        else
            Debug.LogWarning($"[PlayerStats] WarpToSpawnRpc failed — no PlayerController on {gameObject.name}");

        RespawnServer();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public bool IsDead() => isDead.Value;
}
