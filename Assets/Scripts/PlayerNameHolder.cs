// PlayerNameHolder.cs — Sugar Rush
//
// ── PURPOSE ───────────────────────────────────────────────────────────────────
//   Persists across all scene loads. Stores the local player's chosen display
//   name so it is available when the lobby room or game needs it.
//
// ── SETUP ─────────────────────────────────────────────────────────────────────
//   1. Create an empty GameObject in StartMenuScene named "PlayerNameHolder".
//   2. Attach this script to it.
//   3. No other configuration needed — DontDestroyOnLoad keeps it alive.

using UnityEngine;

public class PlayerNameHolder : MonoBehaviour
{
    public static PlayerNameHolder Instance { get; private set; }

    /// <summary>The display name chosen by this local player on the start menu.</summary>
    public string LocalPlayerName { get; private set; } = "Player";

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    /// <summary>
    /// Saves a new display name. Trims whitespace; falls back to "Player" if blank.
    /// Persists to PlayerPrefs so it is remembered between sessions.
    /// </summary>
    public void SetName(string name)
    {
        LocalPlayerName = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
        PlayerPrefs.SetString("SugarRush_PlayerName", LocalPlayerName);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// Loads the last-used name from PlayerPrefs.
    /// Call this once in Start() of your menu manager.
    /// </summary>
    public void LoadSavedName()
    {
        LocalPlayerName = PlayerPrefs.GetString("SugarRush_PlayerName", "Player");
    }
}
