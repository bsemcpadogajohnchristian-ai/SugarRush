// NetworkGameManager.cs — Sugar Rush  (UPDATED: Kill feed now includes team IDs)
//
// ── WHAT CHANGED FROM THE ORIGINAL ───────────────────────────────────────────
//   OnPlayerKilled now resolves killerTeam / victimTeam and passes them into
//   BroadcastKillRpc so KillFeedUI can apply the correct team background sprite
//   to each player's name panel.
//
//   BroadcastKillRpc signature updated:
//     added  TeamID killerTeam, TeamID victimTeam
//
//   KillFeedUI.AddEntry signature updated to match (see KillFeedUI.cs).
//
//   All other match logic (timer, scoring, respawn, etc.) is unchanged.

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

    private void EndGame(TeamID winner) { AnnounceWinnerRpc(winner); StartCoroutine(LoadResultAfterDelay()); }

    private IEnumerator DrawRoutine()          { AnnouncDrawRpc(); yield return new WaitForSeconds(3f); LoadResultRpc(); }
    private IEnumerator LoadResultAfterDelay() { yield return new WaitForSeconds(3f); LoadResultRpc(); }

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

    // ── KILL FEED ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Server-side entry point called by PlayerStats.DieServer().
    /// Resolves display labels + team IDs, then broadcasts to all clients.
    /// </summary>
    public void OnPlayerKilled(ulong killerId, string weaponName, PlayerStats victim)
    {
        if (!IsServer) return;

        // ── Victim info ───────────────────────────────────────────────────────
        string victimLabel    = victim != null ? victim.GetDisplayName() : "Unknown";
        ulong  victimClientId = victim != null ? victim.OwnerClientId   : 0;
        TeamID victimTeam     = victim != null ? victim.team.Value       : TeamID.TeamA;

        // ── Killer info ───────────────────────────────────────────────────────
        string killerLabel;
        TeamID killerTeam;

        if (killerId == 0)
        {
            killerLabel = "World";
            killerTeam  = TeamID.TeamA; // arbitrary — World has no team
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

    private PlayerStats FindPlayerByClientId(ulong clientId)
    {
        foreach (PlayerStats ps in _players)
            if (ps != null && ps.OwnerClientId == clientId) return ps;
        return null;
    }

    /// <summary>
    /// Runs on every client + host. Passes team IDs so KillFeedUI can apply
    /// the correct background sprite to each player's name panel.
    /// </summary>
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

    // ── RPCs ──────────────────────────────────────────────────────────────────

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