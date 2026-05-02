// MatchResultHolder.cs — Sugar Rush
//
// ── RPC FIX ───────────────────────────────────────────────────────────────────
//   NGO RPCs cannot serialize string[] directly.
//   The fix: NetworkGameManager packs all result data into a
//   MatchResultsPayload, serializes it to JSON (a single string, which NGO
//   handles fine), and passes that one string to the RPC.
//   The RPC calls MatchResultHolder.SetFromJson() to unpack and store it.
//
// ── PURPOSE ───────────────────────────────────────────────────────────────────
//   Static data container that survives scene loads. Written by
//   NetworkGameManager.BroadcastMatchResultsRpc on every client, then read
//   by ResultScreenManager.Start() after the scene transition.

using System.Collections.Generic;
using UnityEngine;

// ── Serializable payload (JsonUtility-compatible) ─────────────────────────────
// All arrays are primitive types — no string[] — so JsonUtility serializes them
// cleanly. Names are stored as a single pipe-delimited string instead.

[System.Serializable]
public class MatchResultsPayload
{
    public string names;      // pipe-delimited: "TeamA Shooter|TeamB Collector|..."
    public int[]  teams;
    public int[]  roles;
    public int[]  kills;
    public float[] damages;
    public int[]  deaths;
    public int[]  candies;
    public bool   isDraw;
    public int    winnerTeam;
    public int    scoreA;
    public int    scoreB;
}

// ── Result entry (one row in the result screen) ───────────────────────────────

public class MatchResultEntry
{
    public string     displayName;
    public TeamID     team;
    public PlayerRole role;
    public int        kills;
    public float      damageDealt;
    public int        deaths;
    public int        candiesDelivered;
}

// ── Static holder ─────────────────────────────────────────────────────────────

public static class MatchResultHolder
{
    public static readonly List<MatchResultEntry> Results = new();
    public static TeamID WinnerTeam = TeamID.TeamA;
    public static bool   IsDraw     = false;
    public static int    ScoreTeamA = 0;
    public static int    ScoreTeamB = 0;

    /// <summary>
    /// Called on every client by BroadcastMatchResultsRpc.
    /// json is the result of JsonUtility.ToJson(MatchResultsPayload).
    /// </summary>
    public static void SetFromJson(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            Debug.LogWarning("[MatchResultHolder] SetFromJson received empty string.");
            return;
        }

        MatchResultsPayload p = JsonUtility.FromJson<MatchResultsPayload>(json);
        if (p == null)
        {
            Debug.LogWarning("[MatchResultHolder] SetFromJson failed to deserialize payload.");
            return;
        }

        Results.Clear();
        IsDraw     = p.isDraw;
        WinnerTeam = (TeamID)p.winnerTeam;
        ScoreTeamA = p.scoreA;
        ScoreTeamB = p.scoreB;

        string[] nameArr = string.IsNullOrEmpty(p.names)
            ? System.Array.Empty<string>()
            : p.names.Split('|');

        int count = nameArr.Length;
        for (int i = 0; i < count; i++)
        {
            Results.Add(new MatchResultEntry
            {
                displayName      = nameArr[i],
                team             = (TeamID)(p.teams   != null && i < p.teams.Length   ? p.teams[i]   : 0),
                role             = (PlayerRole)(p.roles != null && i < p.roles.Length ? p.roles[i]   : 0),
                kills            = p.kills   != null && i < p.kills.Length   ? p.kills[i]   : 0,
                damageDealt      = p.damages != null && i < p.damages.Length ? p.damages[i] : 0f,
                deaths           = p.deaths  != null && i < p.deaths.Length  ? p.deaths[i]  : 0,
                candiesDelivered = p.candies != null && i < p.candies.Length ? p.candies[i] : 0,
            });
        }
    }
}