// LobbyNetworkBridge.cs — Sugar Rush
//
// ── PURPOSE ───────────────────────────────────────────────────────────────────
//   NetworkBehaviour that lives on the SAME GAMEOBJECT as LobbyManager in
//   LobbyScene. Handles all lobby-specific networking that the original
//   LobbyManager didn't have:
//     • Accepts player name registrations from clients (ServerRpc).
//     • Builds and broadcasts the full lobby state to all clients whenever
//       it changes (names, slots, connection count).
//     • Allows the host to manually start the game via a ServerRpc.
//
// ── CHANGES FROM PREVIOUS VERSION ────────────────────────────────────────────
//   BroadcastLobbyState() — added IsSpawned guard. If called before NGO
//     has spawned the NetworkObject (can happen if LobbyManager fires an
//     early callback), the method now silently returns instead of crashing
//     inside SyncLobbyStateRpc.
//
//   RegisterNameServerRpc — now gracefully handles an empty or whitespace-
//     only name the same way as before, but logs a debug line for traceability.
//
//   GetPlayerName() — unchanged, kept as public helper.
//
// ── SETUP (unchanged) ─────────────────────────────────────────────────────────
//   Add this script to the same GameObject as LobbyManager in LobbyScene.
//   No extra Inspector configuration needed.
//   LobbyRoomUI subscribes to onLobbyStateUpdated to refresh the slot display.

using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

// ── Data class sent to LobbyRoomUI ───────────────────────────────────────────
public class LobbyPlayerInfo
{
    public ulong  clientId;
    public string playerName;
    public int    slotIndex;   // 0=TeamA Shooter, 1=TeamA Collector, 2=TeamB Shooter, 3=TeamB Collector
}

public class LobbyNetworkBridge : NetworkBehaviour
{
    public static LobbyNetworkBridge Instance { get; private set; }

    // Fired on ALL clients (including host) when lobby player list changes.
    // LobbyRoomUI subscribes to this to refresh slot display.
    public UnityEvent<List<LobbyPlayerInfo>> onLobbyStateUpdated = new();

    // ── Server-side name registry ─────────────────────────────────────────────
    private readonly Dictionary<ulong, string> _playerNames = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        // DontDestroyOnLoad is already applied by LobbyManager on this GameObject.
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;

        // Clear the name registry so a rematch starts fresh.
        _playerNames.Clear();
    }

    private void OnClientDisconnected(ulong clientId)
    {
        _playerNames.Remove(clientId);
        BroadcastLobbyState();
    }

    // ── Client → Server: register this player's display name ─────────────────

    /// <summary>
    /// Called by LobbyRoomUI.SubscribeWhenReady() on every client (including host)
    /// once LobbyNetworkBridge.IsSpawned is true. The server stores the name and
    /// re-broadcasts the updated lobby state to all clients.
    /// </summary>
    [Rpc(SendTo.Server)]
    public void RegisterNameServerRpc(string playerName, RpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        _playerNames[senderId] = string.IsNullOrWhiteSpace(playerName)
            ? "Player" : playerName.Trim();

        Debug.Log($"[LobbyBridge] Client {senderId} registered as '{_playerNames[senderId]}'");
        BroadcastLobbyState();
    }

    // ── Server: build & broadcast lobby state ─────────────────────────────────

    /// <summary>
    /// Re-builds the player list from LobbyManager's slot table and sends it
    /// to every client. Called whenever a player joins, leaves, or registers
    /// their name.
    /// </summary>
    public void BroadcastLobbyState()
    {
        if (!IsServer) return;

        // ── FIX: guard against being called before NGO has spawned us ─────────
        // This can happen if LobbyManager.OnClientConnected fires before
        // OnNetworkSpawn has run. Attempting an RPC on an unspawned
        // NetworkBehaviour crashes inside NGO's __endSendRpc.
        if (!IsSpawned)
        {
            Debug.LogWarning("[LobbyBridge] BroadcastLobbyState called before IsSpawned — skipping.");
            return;
        }

        LobbyManager lm = GetComponent<LobbyManager>() ?? LobbyManager.Instance;
        if (lm == null)
        {
            Debug.LogError("[LobbyBridge] BroadcastLobbyState: LobbyManager not found.");
            return;
        }

        // Pack all data into a single pipe-delimited string to avoid
        // NGO string[] serialisation issues (mirrors MatchResultsPayload approach).
        // Format per entry: "clientId,displayName,slotIndex"
        var sb    = new System.Text.StringBuilder();
        bool first = true;

        foreach (ulong id in lm.GetConnectedClients())
        {
            string name = _playerNames.TryGetValue(id, out string n) ? n : "Player";
            int    slot = lm.GetClientSlot(id);
            if (slot < 0) continue; // skip if slot not assigned yet

            if (!first) sb.Append('|');
            sb.Append($"{id},{name},{slot}");
            first = false;
        }

        SyncLobbyStateRpc(sb.ToString(), lm.GetConnectedClientCount());
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void SyncLobbyStateRpc(string data, int totalConnected)
    {
        var list = new List<LobbyPlayerInfo>();

        if (!string.IsNullOrEmpty(data))
        {
            foreach (string entry in data.Split('|'))
            {
                string[] parts = entry.Split(',');
                if (parts.Length < 3) continue;
                if (!ulong.TryParse(parts[0], out ulong id))  continue;
                if (!int.TryParse  (parts[2], out int slot))  continue;

                list.Add(new LobbyPlayerInfo
                {
                    clientId   = id,
                    playerName = parts[1],
                    slotIndex  = slot
                });
            }
        }

        onLobbyStateUpdated?.Invoke(list);
    }

    // ── Host: request game start ───────────────────────────────────────────────

    /// <summary>
    /// Called by LobbyRoomUI when the host clicks "Start Game".
    /// Server-side validation ensures only the host can trigger this.
    /// </summary>
    [Rpc(SendTo.Server)]
    public void RequestStartGameServerRpc(RpcParams rpcParams = default)
    {
        ulong sender = rpcParams.Receive.SenderClientId;

        // Only the host (server's own local client) may start the game.
        // NetworkManager.ServerClientId is 0 in host mode.
        if (sender != NetworkManager.ServerClientId)
        {
            Debug.LogWarning($"[LobbyBridge] Non-host client {sender} tried to start game. Ignored.");
            return;
        }

        LobbyManager lm = GetComponent<LobbyManager>() ?? LobbyManager.Instance;
        lm?.TryStartGame();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns the registered display name for the given client ID, or "Player".</summary>
    public string GetPlayerName(ulong clientId)
        => _playerNames.TryGetValue(clientId, out string n) ? n : "Player";
}