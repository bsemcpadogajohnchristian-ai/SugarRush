// PlayerMatchStats.cs — Sugar Rush
//
// ── PURPOSE ───────────────────────────────────────────────────────────────────
//   Tracks per-player match statistics that are displayed on the Result Screen.
//   Lives on the Player prefab alongside PlayerStats.
//
//   TRACKED:
//     • kills            (Shooter)   — incremented by NetworkGameManager.OnPlayerKilled
//     • damageDealt      (Shooter)   — incremented by PlayerStats.TakeDamageFrom
//     • deaths           (Both)      — incremented by PlayerStats.DieServer
//     • candiesDelivered (Collector) — incremented by CollectorController.DeliverCandiesServer
//
// ── SETUP ─────────────────────────────────────────────────────────────────────
//   Add this component to your Player prefab alongside PlayerStats.
//   No Inspector configuration required.

using Unity.Netcode;

public class PlayerMatchStats : NetworkBehaviour
{
    // All variables are Server-write so only the authoritative server mutates them.
    // Everyone can read them so the result RPC can pull values on any machine.

    public NetworkVariable<int>   kills            = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<float> damageDealt      = new(0f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<int>   deaths           = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<int>   candiesDelivered = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ── Server-only mutators ─────────────────────────────────────────────────

    public void AddKill()             { if (IsServer) kills.Value++; }
    public void AddDamage(float dmg)  { if (IsServer) damageDealt.Value += dmg; }
    public void AddDeath()            { if (IsServer) deaths.Value++; }
    public void AddCandies(int count) { if (IsServer) candiesDelivered.Value += count; }
}
