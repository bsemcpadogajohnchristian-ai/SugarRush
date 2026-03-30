// LobbyManager.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Two-phase spawn system to fix NGO NetworkTransform race condition.
// Phase 1: spawn all players at origin. Phase 2: teleport to correct positions.
//
// IMPORTANT — NetworkManager Inspector:
//   Default Player Prefab → NONE   (LobbyManager spawns players manually)
//   Network Prefabs List  → keep Player, Candy, Rocket, Decoy registered

using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LobbyManager : NetworkBehaviour
{
    public static LobbyManager Instance { get; private set; }

    // Change to 1 for solo testing, 4 for real multiplayer
    private const int PLAYERS_NEEDED = 2;

    [Header("Setup")]
    public GameObject playerPrefab;
    public string     gameSceneName = "GameScene";

    // Slot 0 = TeamA Shooter  |  Slot 1 = TeamA Collector
    // Slot 2 = TeamB Shooter  |  Slot 3 = TeamB Collector
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
        DontDestroyOnLoad(gameObject);
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        // Warn if NetworkManager will auto-spawn players (breaks our system)
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

    // ── Client tracking ───────────────────────────────────────────────────────

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

    // ── Scene loaded ──────────────────────────────────────────────────────────

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
        yield return null;  // let all NetworkObjects finish spawning
        NetworkGameManager.Instance?.StartMatch();
        Debug.Log("[Lobby] Match started.");
    }

    // ── Player spawning ───────────────────────────────────────────────────────
    //
    // WHY WE SPAWN AT ORIGIN THEN WARP:
    //
    //   NetworkTransform is Owner Authoritative on the Player prefab.
    //   This means the SERVER has no positional authority over player objects.
    //   Any position the server sets gets immediately overwritten by the owning
    //   client's NetworkTransform on its next tick.
    //
    //   The only reliable way to place a player is:
    //     1. Spawn the object (position irrelevant — client will own it)
    //     2. Send a SendTo.Owner RPC to the owning client with the target position
    //     3. The owning client moves their OWN CharacterController to that position
    //     4. Their NetworkTransform now broadcasts the correct position to everyone
    //
    //   WarpToSpawnRpc lives on PlayerController and uses [Rpc(SendTo.Owner)].

    private void SpawnAllPlayers()
    {
        if (playerPrefab == null)
        {
            Debug.LogError("[Lobby] Player Prefab not assigned on LobbyManager!");
            return;
        }

        _usedSpawnPositions.Clear();

        // Register spawn point transforms with NetworkGameManager so respawn works.
        // SetSpawnPoints was never called before — respawns always fell back to origin.
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

            // Spawn at origin — position doesn't matter here because the
            // owning client will override it via WarpToSpawnRpc below
            GameObject    obj = Instantiate(playerPrefab, Vector3.zero, Quaternion.identity);
            NetworkObject no  = obj.GetComponent<NetworkObject>();

            if (no == null)
            {
                Debug.LogError("[Lobby] Player prefab missing NetworkObject component!");
                Destroy(obj);
                continue;
            }

            no.SpawnAsPlayerObject(clientId, true);

            // Write NetworkVariables after spawn
            PlayerStats ps = obj.GetComponent<PlayerStats>();
            if (ps != null)
            {
                ps.role.Value      = role;
                ps.team.Value      = team;
                ps.currentHP.Value = role == PlayerRole.Shooter
                    ? ps.shooterMaxHP : ps.collectorMaxHP;
            }

            // Send the correct spawn position directly to the owning client.
            // WarpToSpawnRpc moves the CharacterController on the owner,
            // then their owner-authoritative NetworkTransform broadcasts the
            // correct position to all other clients automatically.
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
            return (best.position, best.rotation);   // rotation comes from the spawn point Transform
        }

        Debug.LogWarning($"[Lobby] '{parentName}' not found — using fallback position.");
        int idx      = _usedSpawnPositions.Count;
        Vector3 pos  = team == TeamID.TeamA
            ? new Vector3(-3f + idx * 3f, 1f,  20f)
            : new Vector3(-3f + idx * 3f, 1f, -20f);
        _usedSpawnPositions.Add(pos);

        // Fallback: Team A faces inward (toward negative Z), Team B faces inward (toward positive Z)
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
