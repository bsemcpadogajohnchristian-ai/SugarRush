// NetworkGameManager.cs — Sugar Rush
//
// ── RPC SERIALIZATION FIX ─────────────────────────────────────────────────────
//   BroadcastMatchResultsRpc previously passed string[], int[], float[] as
//   separate parameters. NGO cannot serialize string[] in RPCs (it requires
//   INetworkSerializeByMemcpy or INetworkSerializable).
//
//   FIX: CollectAndBroadcastResults() now packs everything into a
//   MatchResultsPayload struct, serializes it to a single JSON string via
//   JsonUtility, and passes ONLY that string to the RPC.
//   MatchResultHolder.SetFromJson() deserializes it on every client.
//
//   Player names are stored as a pipe-delimited string inside the payload
//   (e.g. "TeamA Shooter|TeamB Collector") to avoid any string[] in the struct.
//
// ── OTHER CHANGES (from the previous version, unchanged here) ─────────────────
//   FindPlayerByClientId() is public.
//   OnPlayerKilled() increments killer's PlayerMatchStats.
//   EndGame() / DrawRoutine() call CollectAndBroadcastResults() first.

using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;

public class NetworkGameManager : NetworkBehaviour
{
    public static NetworkGameManager Instance { get; private set; }

    [Header("Match Settings")]
    public float matchDuration        = 300f;
    public int   candiesNeededToWin   = 50;
    public float respawnDelay         = 5f;

    public NetworkVariable<int> scoreTeamA = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<int> scoreTeamB = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<float> timeRemaining = new(300f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<MatchState> matchState = new(MatchState.WaitingForPlayers,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public UnityEvent<int, int> onScoreUpdated = new();
    public UnityEvent<float>    onTimerUpdated  = new();
    public UnityEvent<TeamID>   onMatchOver     = new();
    public UnityEvent           onMatchDraw     = new();

    private readonly List<PlayerStats> _players = new();
    private Transform[] _spawnA;
    private Transform[] _spawnB;
    private float _localTimer;
    private float _timerSyncAccum;
    private const float TIMER_SYNC_INTERVAL = 0.1f;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        scoreTeamA.OnValueChanged    += (_, _) => onScoreUpdated?.Invoke(scoreTeamA.Value, scoreTeamB.Value);
        scoreTeamB.OnValueChanged    += (_, _) => onScoreUpdated?.Invoke(scoreTeamA.Value, scoreTeamB.Value);
        timeRemaining.OnValueChanged += (_, v)  => onTimerUpdated?.Invoke(v);
        if (IsServer)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
    }

    public void SetSpawnPoints(Transform[] a, Transform[] b) { _spawnA = a; _spawnB = b; }

    public void RegisterPlayer(PlayerStats ps)
    {
        if (!IsServer || ps == null || _players.Contains(ps)) return;
        _players.Add(ps);
    }

    private void OnClientDisconnected(ulong clientId)
        => _players.RemoveAll(p => p == null || p.OwnerClientId == clientId);

    public void StartMatch()
    {
        if (!IsServer) return;
        scoreTeamA.Value    = 0;
        scoreTeamB.Value    = 0;
        timeRemaining.Value = matchDuration;
        _localTimer         = matchDuration;
        _timerSyncAccum     = 0f;
        matchState.Value    = MatchState.InProgress;
        CandySpawner.Instance?.StartSpawning();
        Debug.Log("[NGM] Match started.");
    }

    private void Update()
    {
        if (!IsServer || matchState.Value != MatchState.InProgress) return;
        _localTimer     -= Time.deltaTime;
        _timerSyncAccum += Time.deltaTime;
        if (_timerSyncAccum >= TIMER_SYNC_INTERVAL)
        {
            _timerSyncAccum     = 0f;
            timeRemaining.Value = Mathf.Max(_localTimer, 0f);
        }
        if (_localTimer <= 0f) { timeRemaining.Value = 0f; EndByTime(); }
    }

    private void EndByTime()
    {
        matchState.Value = MatchState.GameOver;
        CandySpawner.Instance?.StopSpawning();
        if      (scoreTeamA.Value > scoreTeamB.Value) EndGame(TeamID.TeamA);
        else if (scoreTeamB.Value > scoreTeamA.Value) EndGame(TeamID.TeamB);
        else                                           StartCoroutine(DrawRoutine());
    }

    private void EndGame(TeamID winner)
    {
        CollectAndBroadcastResults(isDraw: false, winner: winner);
        AnnounceWinnerRpc(winner);
        StartCoroutine(LoadResultAfterDelay());
    }

    private IEnumerator DrawRoutine()
    {
        CollectAndBroadcastResults(isDraw: true, winner: TeamID.TeamA);
        AnnouncDrawRpc();
        yield return new WaitForSeconds(3f);
        LoadResultRpc();
    }

    private IEnumerator LoadResultAfterDelay() { yield return new WaitForSeconds(3f); LoadResultRpc(); }

    // ── Collect + broadcast (JSON transport) ─────────────────────────────────

    private void CollectAndBroadcastResults(bool isDraw, TeamID winner)
    {
        if (!IsServer) return;

        int count = _players.Count;

        // Names are joined with '|' to avoid string[] in the serializable struct.
        var nameList = new System.Text.StringBuilder();
        var teams    = new int[count];
        var roles    = new int[count];
        var kills    = new int[count];
        var damages  = new float[count];
        var deaths   = new int[count];
        var candies  = new int[count];

        for (int i = 0; i < count; i++)
        {
            PlayerStats      ps  = _players[i];
            PlayerMatchStats pms = ps != null ? ps.GetComponent<PlayerMatchStats>() : null;

            if (i > 0) nameList.Append('|');
            nameList.Append(ps?.GetDisplayName() ?? "Unknown");

            teams[i]   = ps  != null ? (int)ps.team.Value   : 0;
            roles[i]   = ps  != null ? (int)ps.role.Value   : 0;
            kills[i]   = pms != null ? pms.kills.Value           : 0;
            damages[i] = pms != null ? pms.damageDealt.Value      : 0f;
            deaths[i]  = pms != null ? pms.deaths.Value           : 0;
            candies[i] = pms != null ? pms.candiesDelivered.Value : 0;
        }

        var payload = new MatchResultsPayload
        {
            names      = nameList.ToString(),
            teams      = teams,
            roles      = roles,
            kills      = kills,
            damages    = damages,
            deaths     = deaths,
            candies    = candies,
            isDraw     = isDraw,
            winnerTeam = (int)winner,
            scoreA     = scoreTeamA.Value,
            scoreB     = scoreTeamB.Value,
        };

        string json = JsonUtility.ToJson(payload);
        BroadcastMatchResultsRpc(json);
    }

    // ── RPC: single string param — fully supported by NGO ────────────────────

    [Rpc(SendTo.ClientsAndHost)]
    private void BroadcastMatchResultsRpc(string json)
    {
        MatchResultHolder.SetFromJson(json);
    }

    // ─────────────────────────────────────────────────────────────────────────

    public void AddScore(TeamID team, int amount)
    {
        if (!IsServer) return;
        if (team == TeamID.TeamA) scoreTeamA.Value += amount;
        else                      scoreTeamB.Value += amount;
        if      (scoreTeamA.Value >= candiesNeededToWin) { matchState.Value = MatchState.GameOver; EndGame(TeamID.TeamA); }
        else if (scoreTeamB.Value >= candiesNeededToWin) { matchState.Value = MatchState.GameOver; EndGame(TeamID.TeamB); }
    }

    public void OnPlayerDied(PlayerStats ps)
    {
        if (!IsServer) return;
        StartCoroutine(RespawnAfterDelay(ps));
    }

    private IEnumerator RespawnAfterDelay(PlayerStats ps)
    {
        yield return new WaitForSeconds(respawnDelay);
        if (ps == null || ps.NetworkObject == null || !ps.NetworkObject.IsSpawned) yield break;
        if (!ps.isDead.Value) yield break;
        Transform pt = BestSpawnPoint(ps.team.Value);
        if (pt != null) ps.RespawnAtPosition(pt.position, pt.rotation);
        else            ps.RespawnServer();
    }

    private Transform BestSpawnPoint(TeamID team)
    {
        Transform[] pts = team == TeamID.TeamA ? _spawnA : _spawnB;
        if (pts == null || pts.Length == 0) return null;
        if (pts.Length == 1) return pts[0];
        Transform best = pts[0]; float bestDist = -1f;
        foreach (Transform pt in pts)
        {
            if (pt == null) continue;
            float minD = float.MaxValue; bool anyAlive = false;
            foreach (PlayerStats p in _players)
            {
                if (p == null || p.IsDead()) continue;
                anyAlive = true;
                float d = Vector3.Distance(pt.position, p.transform.position);
                if (d < minD) minD = d;
            }
            if (!anyAlive) return pts[0];
            if (minD > bestDist) { bestDist = minD; best = pt; }
        }
        return best;
    }

    // ── Kill feed ─────────────────────────────────────────────────────────────

    public void OnPlayerKilled(ulong killerId, string weaponName, PlayerStats victim)
    {
        if (!IsServer) return;

        // Increment killer's kill counter
        if (killerId != 0)
        {
            PlayerStats killer = FindPlayerByClientId(killerId);
            killer?.GetComponent<PlayerMatchStats>()?.AddKill();
        }

        string victimLabel    = victim != null ? victim.GetDisplayName() : "Unknown";
        ulong  victimClientId = victim != null ? victim.OwnerClientId   : 0;
        TeamID victimTeam     = victim != null ? victim.team.Value       : TeamID.TeamA;

        string killerLabel;
        TeamID killerTeam;

        if (killerId == 0)
        {
            killerLabel = "World";
            killerTeam  = TeamID.TeamA;
        }
        else
        {
            PlayerStats killer = FindPlayerByClientId(killerId);
            killerLabel = killer != null ? killer.GetDisplayName() : $"Player {killerId}";
            killerTeam  = killer != null ? killer.team.Value        : TeamID.TeamA;
        }

        if (string.IsNullOrEmpty(weaponName)) weaponName = "Unknown";

        BroadcastKillRpc(killerLabel, victimLabel, weaponName,
                         killerId, victimClientId,
                         killerTeam, victimTeam);
    }

    /// <summary>
    /// Public so PlayerStats.TakeDamageFrom can look up the attacker.
    /// </summary>
    public PlayerStats FindPlayerByClientId(ulong clientId)
    {
        foreach (PlayerStats ps in _players)
            if (ps != null && ps.OwnerClientId == clientId) return ps;
        return null;
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void BroadcastKillRpc(
        string killerLabel, string victimLabel, string weaponName,
        ulong  killerClientId, ulong victimClientId,
        TeamID killerTeam, TeamID victimTeam)
    {
        KillFeedUI.Instance?.AddEntry(
            killerLabel, victimLabel, weaponName,
            killerClientId, victimClientId,
            killerTeam, victimTeam);
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void AnnounceWinnerRpc(TeamID winner) => onMatchOver?.Invoke(winner);

    [Rpc(SendTo.ClientsAndHost)]
    private void AnnouncDrawRpc() => onMatchDraw?.Invoke();

    [Rpc(SendTo.ClientsAndHost)]
    private void LoadResultRpc()
    {
        if (IsServer)
            NetworkManager.Singleton.SceneManager.LoadScene(
                "ResultScreen", UnityEngine.SceneManagement.LoadSceneMode.Single);
    }

    public bool IsMatchRunning() => matchState.Value == MatchState.InProgress;
}