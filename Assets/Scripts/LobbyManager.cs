// LobbyManager.cs — Sugar Rush  (MODIFIED for Start Menu + Lobby Room)
//
// ── CHANGES FROM ORIGINAL ────────────────────────────────────────────────────
//   1. Auto-start removed from OnClientConnected.
//      Game now starts only when TryStartGame() is called (by LobbyNetworkBridge
//      when the host clicks the Start Game button in LobbyRoomUI).
//   2. Three new public accessor methods added so LobbyNetworkBridge can read
//      the connected client list and slot assignments:
//        • GetConnectedClients()        → List<ulong>
//        • GetClientSlot(ulong)         → int
//        • GetConnectedClientCount()    → int
//   3. TryStartGame() public method — loads GameScene for everyone.
//   4. OnClientConnected now also calls LobbyNetworkBridge.BroadcastLobbyState()
//      so the lobby room UI refreshes as players join.
//   5. OnClientDisconnected calls BroadcastLobbyState() for the same reason.
//   6. RefreshLobbyUIRpc now also calls LobbyRoomUI for backward compatibility.
//   7. PLAYERS_NEEDED constant removed (game is now host-controlled).
//
// ── EVERYTHING ELSE IS UNCHANGED ─────────────────────────────────────────────
//   SpawnAllPlayers, PrepareRematch, GetSpawnPosition, OnSceneLoaded — untouched.

using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LobbyManager : NetworkBehaviour
{
    public static LobbyManager Instance { get; private set; }

    [Header("Setup")]
    public GameObject playerPrefab;
    public string     gameSceneName = "GameScene";

    [Header("Lobby")]
    [Tooltip("Minimum players required before the host can press Start Game.")]
    public int minPlayersToStart = 1;

    // Slot → team / role mapping (slots 0-3)
    private static readonly TeamID[]     TeamForSlot = { TeamID.TeamA, TeamID.TeamA, TeamID.TeamB, TeamID.TeamB };
    private static readonly PlayerRole[] RoleForSlot = { PlayerRole.Shooter, PlayerRole.Collector, PlayerRole.Shooter, PlayerRole.Collector };

    private readonly Dictionary<ulong, int> _clientSlot = new();
    private readonly List<ulong>            _clients    = new();
    private int  _nextSlot    = 0;
    private bool _gameStarted = false;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        // ── FIX: DontDestroyOnLoad removed from Awake. ────────────────────────
        // Calling it here moves the GameObject OUT of the active scene before
        // NGO scans for scene NetworkObjects (via GetActiveScene().GetRootGameObjects()).
        // That caused IsSpawned to stay false forever → LobbyRoomUI 10 s timeout.
        // It is now called in OnNetworkSpawn, after NGO has already spawned us.
    }

    public override void OnNetworkSpawn()
    {
        // ── FIX: persist AFTER NGO has spawned this NetworkObject ─────────────
        DontDestroyOnLoad(gameObject);

        if (!IsServer) return;

        if (NetworkManager.Singleton.NetworkConfig.PlayerPrefab != null)
        {
            Debug.LogError(
                "[Lobby] *** NetworkManager has a Default Player Prefab assigned! ***\n" +
                "Set it to NONE. LobbyManager handles all spawning.");
        }

        NetworkManager.Singleton.OnClientConnectedCallback  += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += OnSceneLoaded;

        AddClient(NetworkManager.Singleton.LocalClientId);
        RefreshLobbyUIRpc(_clients.Count);
        Debug.Log($"[Lobby] Server ready. Min players to start: {minPlayersToStart}");
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer) return;
        NetworkManager.Singleton.OnClientConnectedCallback  -= OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        if (NetworkManager.Singleton?.SceneManager != null)
            NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= OnSceneLoaded;
    }

    // ── Client connection handling ────────────────────────────────────────────

    private void AddClient(ulong id)
    {
        if (_clientSlot.ContainsKey(id)) return;
        _clientSlot[id] = _nextSlot++;
        _clients.Add(id);
        Debug.Log($"[Lobby] Client {id} → slot {_clientSlot[id]}");
    }

    private void OnClientConnected(ulong id)
    {
        AddClient(id);
        Debug.Log($"[Lobby] Connected: {id}   Players: {_clients.Count}");
        RefreshLobbyUIRpc(_clients.Count);

        // ── CHANGED: notify LobbyNetworkBridge so lobby room UI updates ───────
        // (Auto-start logic removed — host manually starts via TryStartGame())
        LobbyNetworkBridge.Instance?.BroadcastLobbyState();
    }

    private void OnClientDisconnected(ulong id)
    {
        if (_gameStarted) return;
        _clients.Remove(id);
        _clientSlot.Remove(id);
        RefreshLobbyUIRpc(_clients.Count);

        // ── CHANGED: update lobby room UI when someone leaves ─────────────────
        LobbyNetworkBridge.Instance?.BroadcastLobbyState();
    }

    // ── Scene loaded callback (fires when GameScene finishes loading) ─────────

    private void OnSceneLoaded(string scene, LoadSceneMode mode,
        List<ulong> done, List<ulong> timedOut)
    {
        if (scene != gameSceneName) return;

        if (timedOut.Count > 0)
            Debug.LogWarning($"[Lobby] {timedOut.Count} client(s) timed out loading scene.");

        Debug.Log("[Lobby] GameScene loaded — spawning players.");

        SpawnAllPlayers();
        StartCoroutine(StartMatchNextFrame());
    }

    private IEnumerator StartMatchNextFrame()
    {
        yield return null;
        NetworkGameManager.Instance?.StartMatch();
        Debug.Log("[Lobby] Match started.");
    }

    // ── Manual game start (called by LobbyNetworkBridge.RequestStartGameServerRpc) ──

    /// <summary>
    /// Loads GameScene for all connected clients. Called by LobbyNetworkBridge
    /// when the host clicks the Start Game button in LobbyRoomUI.
    /// </summary>
    public void TryStartGame()
    {
        if (!IsServer || _gameStarted) return;

        if (_clients.Count < minPlayersToStart)
        {
            Debug.LogWarning($"[Lobby] TryStartGame: need at least {minPlayersToStart} player(s).");
            return;
        }

        _gameStarted = true;
        Debug.Log($"[Lobby] TryStartGame — loading {gameSceneName} for {_clients.Count} player(s).");
        NetworkManager.Singleton.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
    }

    // ── Rematch (unchanged from original) ────────────────────────────────────

    /// <summary>
    /// Called by ResultScreenManager before reloading GameScene for a rematch.
    /// Resets _gameStarted so OnSceneLoaded will run SpawnAllPlayers again.
    /// </summary>
    public void PrepareRematch()
    {
        if (!IsServer) return;
        _gameStarted = false;
        _nextSlot    = 0;

        _clientSlot.Clear();
        for (int i = 0; i < _clients.Count; i++)
            _clientSlot[_clients[i]] = i;

        Debug.Log("[Lobby] PrepareRematch — state reset, ready for OnSceneLoaded.");
    }

    // ── Player spawning (unchanged from original) ─────────────────────────────

    private void SpawnAllPlayers()
    {
        if (playerPrefab == null)
        {
            Debug.LogError("[Lobby] Player Prefab not assigned on LobbyManager!");
            return;
        }

        _usedSpawnPositions.Clear();

        GameObject spawnA = GameObject.Find("Spawns_TeamA");
        GameObject spawnB = GameObject.Find("Spawns_TeamB");
        Transform[] teamASpawns = GetChildTransforms(spawnA);
        Transform[] teamBSpawns = GetChildTransforms(spawnB);
        NetworkGameManager.Instance?.SetSpawnPoints(teamASpawns, teamBSpawns);
        Debug.Log($"[Lobby] SetSpawnPoints — TeamA: {teamASpawns.Length}  TeamB: {teamBSpawns.Length}");

        for (int i = 0; i < Mathf.Min(_clients.Count, 4); i++)
        {
            ulong clientId = _clients[i];
            int   slot     = _clientSlot.TryGetValue(clientId, out int s)
                             ? Mathf.Clamp(s, 0, 3) : i;

            TeamID     team = TeamForSlot[slot];
            PlayerRole role = RoleForSlot[slot];
            var (pos, spawnRot) = GetSpawnPosition(team);

            GameObject    obj = Instantiate(playerPrefab, Vector3.zero, Quaternion.identity);
            NetworkObject no  = obj.GetComponent<NetworkObject>();

            if (no == null)
            {
                Debug.LogError("[Lobby] Player prefab missing NetworkObject component!");
                Destroy(obj);
                continue;
            }

            PlayerStats ps = obj.GetComponent<PlayerStats>();
            if (ps != null)
            {
                ps.role.Value      = role;
                ps.team.Value      = team;
                ps.currentHP.Value = role == PlayerRole.Shooter
                    ? ps.shooterMaxHP : ps.collectorMaxHP;

                // ── Set the registered display name from the lobby ────────────
                string registeredName = LobbyNetworkBridge.Instance?.GetPlayerName(clientId) ?? "Player";
                ps.playerName.Value = new Unity.Collections.FixedString64Bytes(registeredName);
            }

            no.SpawnAsPlayerObject(clientId, true);

            PlayerController pc = obj.GetComponent<PlayerController>();
            if (pc != null)
                pc.WarpToSpawnRpc(pos, spawnRot);
            else
                Debug.LogWarning($"[Lobby] PlayerController not found — client {clientId} spawns at origin.");

            NetworkGameManager.Instance?.RegisterPlayer(ps);
            Debug.Log($"[Lobby] Spawned client={clientId}  {role}/{team}  pos={pos}");
        }
    }

    private readonly List<Vector3> _usedSpawnPositions = new();

    private static Transform[] GetChildTransforms(GameObject parent)
    {
        if (parent == null || parent.transform.childCount == 0)
            return System.Array.Empty<Transform>();
        Transform[] arr = new Transform[parent.transform.childCount];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = parent.transform.GetChild(i);
        return arr;
    }

    private (Vector3 position, Quaternion rotation) GetSpawnPosition(TeamID team)
    {
        string     parentName = team == TeamID.TeamA ? "Spawns_TeamA" : "Spawns_TeamB";
        GameObject parent     = GameObject.Find(parentName);

        if (parent != null && parent.transform.childCount > 0)
        {
            Transform best     = parent.transform.GetChild(0);
            float     bestDist = -1f;

            for (int c = 0; c < parent.transform.childCount; c++)
            {
                Transform pt   = parent.transform.GetChild(c);
                float     minD = float.MaxValue;

                if (_usedSpawnPositions.Count == 0)
                {
                    best = pt;
                    break;
                }

                foreach (Vector3 used in _usedSpawnPositions)
                    minD = Mathf.Min(minD, Vector3.Distance(pt.position, used));

                if (minD > bestDist) { bestDist = minD; best = pt; }
            }

            _usedSpawnPositions.Add(best.position);
            return (best.position, best.rotation);
        }

        Debug.LogWarning($"[Lobby] '{parentName}' not found — using fallback position.");
        int     idx = _usedSpawnPositions.Count;
        Vector3 pos = team == TeamID.TeamA
            ? new Vector3(-3f + idx * 3f, 1f,  20f)
            : new Vector3(-3f + idx * 3f, 1f, -20f);
        _usedSpawnPositions.Add(pos);

        Quaternion rot = team == TeamID.TeamA
            ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
        return (pos, rot);
    }

    // ── RPC — refresh legacy LobbyUI AND new LobbyRoomUI ─────────────────────

    [Rpc(SendTo.ClientsAndHost)]
    private void RefreshLobbyUIRpc(int count)
    {
        // Legacy support (LobbyUI.cs may still be in the scene)
        LobbyUI.Instance?.SetPlayerCount(count);

        // New lobby room UI
        LobbyRoomUI.Instance?.SetPlayerCount(count);
    }

    // ── NEW: Public accessors used by LobbyNetworkBridge ─────────────────────

    /// <summary>Returns a snapshot of all connected client IDs in join order.</summary>
    public List<ulong> GetConnectedClients() => new List<ulong>(_clients);

    /// <summary>Returns the slot index assigned to clientId, or -1 if not found.</summary>
    public int GetClientSlot(ulong clientId)
        => _clientSlot.TryGetValue(clientId, out int s) ? s : -1;

    /// <summary>Returns the number of currently connected clients.</summary>
    public int GetConnectedClientCount() => _clients.Count;
}