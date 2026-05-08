using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LobbyManager : NetworkBehaviour
{
    public static LobbyManager Instance { get; private set; }

    
    private const int PLAYERS_NEEDED = 4;

    [Header("Setup")]
    public GameObject playerPrefab;
    public string     gameSceneName = "GameScene";

    
    private static readonly TeamID[]     TeamForSlot = { TeamID.TeamA, TeamID.TeamA, TeamID.TeamB, TeamID.TeamB };
    private static readonly PlayerRole[] RoleForSlot = { PlayerRole.Shooter, PlayerRole.Collector, PlayerRole.Shooter, PlayerRole.Collector };

    private readonly Dictionary<ulong, int> _clientSlot = new();
    private readonly List<ulong>            _clients    = new();
    private int  _nextSlot    = 0;
    private bool _gameStarted = false;

    
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public override void OnNetworkSpawn()
    {
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
        Debug.Log($"[Lobby] Server ready. Waiting for {PLAYERS_NEEDED} player(s).");
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer) return;
        NetworkManager.Singleton.OnClientConnectedCallback  -= OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        if (NetworkManager.Singleton?.SceneManager != null)
            NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= OnSceneLoaded;
    }

    
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
        Debug.Log($"[Lobby] Connected: {id}   Players: {_clients.Count}/{PLAYERS_NEEDED}");
        RefreshLobbyUIRpc(_clients.Count);

        if (_clients.Count >= PLAYERS_NEEDED && !_gameStarted)
        {
            _gameStarted = true;
            Debug.Log($"[Lobby] All players ready — loading {gameSceneName}.");
            NetworkManager.Singleton.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
        }
    }

    private void OnClientDisconnected(ulong id)
    {
        if (_gameStarted) return;
        _clients.Remove(id);
        RefreshLobbyUIRpc(_clients.Count);
    }

    
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

    
    /// <summary>
    /// Called by ResultScreenManager before reloading GameScene for a rematch.
    /// Resets _gameStarted so OnSceneLoaded will run SpawnAllPlayers again.
    /// _clients and _clientSlot are kept intact — all players are still connected
    /// in the same NGO session with the same slot assignments.
    /// </summary>
    public void PrepareRematch()
    {
        if (!IsServer) return;
        _gameStarted = false;
        _nextSlot    = 0;

        // Re-assign slots from the existing connected client list so slot
        // indices are consistent and no slot exceeds the valid range (0-3).
        _clientSlot.Clear();
        for (int i = 0; i < _clients.Count; i++)
            _clientSlot[_clients[i]] = i;

        Debug.Log("[Lobby] PrepareRematch — state reset, ready for OnSceneLoaded.");
    }

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

            // ── FIX v2: set NetworkVariables BEFORE SpawnAsPlayerObject ──────
            // NGO includes the current NV values in the spawn message sent to
            // clients. Setting them after the Spawn call means clients receive
            // the spawn with default values (Shooter / TeamA) and a separate
            // NV-correction message 1 frame later. AllyIndicator and other
            // scripts that read role/team in their first frame of life then see
            // stale defaults, causing icons to silently hide and never recover
            // until the next polling window. Setting before Spawn ensures every
            // client sees the correct role and team from the very first frame.
            PlayerStats ps = obj.GetComponent<PlayerStats>();
            if (ps != null)
            {
                ps.role.Value      = role;
                ps.team.Value      = team;
                ps.currentHP.Value = role == PlayerRole.Shooter
                    ? ps.shooterMaxHP : ps.collectorMaxHP;
            }

            no.SpawnAsPlayerObject(clientId, true);

            
            PlayerController pc = obj.GetComponent<PlayerController>();
            if (pc != null)
                pc.WarpToSpawnRpc(pos, spawnRot);
            else
                Debug.LogWarning($"[Lobby] PlayerController not found on prefab — client {clientId} will spawn at origin.");

            NetworkGameManager.Instance?.RegisterPlayer(ps);
            Debug.Log($"[Lobby] Spawned client={clientId}  {role}/{team}  warpTarget={pos}");
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
        string    parentName = team == TeamID.TeamA ? "Spawns_TeamA" : "Spawns_TeamB";
        GameObject parent    = GameObject.Find(parentName);

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
        int idx      = _usedSpawnPositions.Count;
        Vector3 pos  = team == TeamID.TeamA
            ? new Vector3(-3f + idx * 3f, 1f,  20f)
            : new Vector3(-3f + idx * 3f, 1f, -20f);
        _usedSpawnPositions.Add(pos);

        
        Quaternion rot = team == TeamID.TeamA ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
        return (pos, rot);
    }

        [Rpc(SendTo.ClientsAndHost)]
    private void RefreshLobbyUIRpc(int count)
    {
        LobbyUI.Instance?.SetPlayerCount(count);
        LobbyUI.Instance?.SetStatus(count < PLAYERS_NEEDED
            ? $"Waiting for players... ({count}/{PLAYERS_NEEDED})"
            : "All players ready! Loading game...");
    }
}