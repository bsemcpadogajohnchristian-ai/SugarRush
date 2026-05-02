// ResultScreenManager.cs — Sugar Rush
//
// ── REMATCH + SCENE-LOAD FIX ──────────────────────────────────────────────────
//
//  BUG 1 — "Play Again / Main Menu didn't load the scene"
//    Root cause: NetworkManager.Shutdown() is asynchronous. Calling
//    SceneManager.LoadScene immediately after Shutdown() fires before NGO has
//    finished tearing down, so the load either fails silently or the new scene
//    inherits broken NGO state.
//    Fix: ShutdownThenLoad() coroutine polls IsListening each frame and only
//    loads the scene once NGO is fully stopped.
//
//  BUG 2 — "Play Again" is now "Rematch" (host-style, waits for players)
//    The old Play Again called Shutdown → LoadScene just like Main Menu, so
//    both players ended up on the lobby screen with no connection between them.
//    Fix: Rematch does NOT shut down NGO. Instead:
//      • The server calls LobbyManager.PrepareRematch() (resets _gameStarted so
//        OnSceneLoaded fires again), then reloads GameScene via NGO's own
//        SceneManager — all clients follow automatically.
//      • Non-server clients disable their Rematch button and show "Waiting for
//        host…" — the server drives the transition, clients just wait.
//    This reuses ALL existing LobbyManager logic: OnSceneLoaded → SpawnAllPlayers
//    → StartMatch. No duplicate code.
//
//  CURSOR FIX (carried over from previous version)
//    Cursor is unlocked in Start() so buttons are clickable.

using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

public class ResultScreenManager : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────────
    //  INSPECTOR REFERENCES
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Navigation")]
    [Tooltip("Exact name of your lobby / main-menu scene.")]
    public string lobbySceneName = "LobbyScene";

    [Tooltip("Exact name of your game scene — must match LobbyManager.gameSceneName.")]
    public string gameSceneName  = "GameScene";

    [Header("Background")]
    public Image backgroundImage;
    public Image overlayImage;

    // ── Result Banner ─────────────────────────────────────────────────────────
    [Header("Banner — Result")]
    public RectTransform   bannerPanel;
    public TextMeshProUGUI resultLabel;

    [Tooltip("No longer used — leave empty or remove from prefab.")]
    public TextMeshProUGUI subLabel;

    public TextMeshProUGUI scoreLabel;
    public Image           bannerLeftBar;
    public Image           bannerRightBar;

    // ── Team A Stats ──────────────────────────────────────────────────────────
    [Header("Team A Stats Panel")]
    public Image           teamAPanelBg;       // NOT tinted — your sprite shows as-is
    public Image           teamAAccentBar;     // tinted C_A (blue strip)
    public TextMeshProUGUI teamAHeaderLabel;

    public TextMeshProUGUI teamAShooterName;
    public TextMeshProUGUI teamAShooterKills;
    public TextMeshProUGUI teamAShooterDamage;
    public TextMeshProUGUI teamAShooterDeaths;

    public TextMeshProUGUI teamACollectorName;
    public TextMeshProUGUI teamACollectorCandies;
    public TextMeshProUGUI teamACollectorDeaths;

    // ── Team B Stats ──────────────────────────────────────────────────────────
    [Header("Team B Stats Panel")]
    public Image           teamBPanelBg;       // NOT tinted — your sprite shows as-is
    public Image           teamBAccentBar;     // tinted C_B (red strip)
    public TextMeshProUGUI teamBHeaderLabel;

    public TextMeshProUGUI teamBShooterName;
    public TextMeshProUGUI teamBShooterKills;
    public TextMeshProUGUI teamBShooterDamage;
    public TextMeshProUGUI teamBShooterDeaths;

    public TextMeshProUGUI teamBCollectorName;
    public TextMeshProUGUI teamBCollectorCandies;
    public TextMeshProUGUI teamBCollectorDeaths;

    // ── MVP Strip ─────────────────────────────────────────────────────────────
    [Header("MVP Strip")]
    public TextMeshProUGUI mvpKillerName;
    public TextMeshProUGUI mvpKillerStat;
    public TextMeshProUGUI mvpCandyName;
    public TextMeshProUGUI mvpCandyStat;
    public TextMeshProUGUI mvpDamageName;
    public TextMeshProUGUI mvpDamageStat;

    // ── Buttons ───────────────────────────────────────────────────────────────
    [Header("Buttons")]
    [Tooltip("Rematch button — server reloads the game for everyone. " +
             "Non-server clients see this button disabled while waiting.")]
    public Button          rematchButton;

    [Tooltip("Main Menu button — shuts down NGO cleanly then loads lobby.")]
    public Button          mainMenuButton;

    [Tooltip("Optional TMP label inside the Rematch button (set to 'REMATCH' at runtime).")]
    public TextMeshProUGUI rematchButtonLabel;

    [Tooltip("Optional status text shown to non-host clients while waiting.")]
    public TextMeshProUGUI rematchStatusLabel;

    // ── Optional win/lose graphics ────────────────────────────────────────────
    [Header("Optional: Win / Lose graphic GameObjects")]
    public GameObject victoryGraphic;
    public GameObject defeatGraphic;
    public GameObject drawGraphic;

    // ─────────────────────────────────────────────────────────────────────────
    //  COLOUR PALETTE
    // ─────────────────────────────────────────────────────────────────────────

    static readonly Color C_A       = Hex("00C8FF");
    static readonly Color C_A_Text  = Hex("00C8FF");
    static readonly Color C_B       = Hex("FF3B50");
    static readonly Color C_B_Text  = Hex("FF3B50");
    static readonly Color C_Victory = Hex("FFD000");
    static readonly Color C_Defeat  = Hex("FF3B50");
    static readonly Color C_Draw    = Hex("A0A8B8");
    static readonly Color C_TextSub = Hex("8A94A8");

    // ─────────────────────────────────────────────────────────────────────────
    //  ENTRY POINT
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        // Unlock cursor — PlayerSetup locks it during gameplay.
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;

        // Wire buttons.
        if (rematchButton  != null) rematchButton.onClick.AddListener(OnRematch);
        if (mainMenuButton != null) mainMenuButton.onClick.AddListener(OnMainMenu);

        // Set rematch button label.
        if (rematchButtonLabel != null) rematchButtonLabel.text = "REMATCH";

        // Only the server can start a rematch — disable for everyone else.
        bool isServer = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        if (rematchButton != null)
            rematchButton.interactable = isServer;

        if (!isServer && rematchStatusLabel != null)
            rematchStatusLabel.text = "Waiting for host to start rematch…";

        // Populate UI.
        PopulateBanner();
        PopulateTeam(TeamID.TeamA);
        PopulateTeam(TeamID.TeamB);
        PopulateMVP();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  BANNER
    // ─────────────────────────────────────────────────────────────────────────

    private void PopulateBanner()
    {
        bool isDraw = MatchResultHolder.IsDraw;
        bool aWins  = !isDraw && MatchResultHolder.WinnerTeam == TeamID.TeamA;

        string resultText;
        Color  resultColor;

        if (isDraw)
        {
            resultText  = "DRAW";
            resultColor = C_Draw;
        }
        else
        {
            resultText  = aWins ? "VICTORY" : "DEFEAT";
            resultColor = aWins ? C_Victory : C_Defeat;
        }

        if (resultLabel != null)
        {
            resultLabel.text  = resultText;
            resultLabel.color = resultColor;
        }

        // subLabel intentionally not written to.

        if (scoreLabel != null)
        {
            string aHex   = ColorUtility.ToHtmlStringRGB(C_A_Text);
            string bHex   = ColorUtility.ToHtmlStringRGB(C_B_Text);
            string subHex = ColorUtility.ToHtmlStringRGB(C_TextSub);
            scoreLabel.text =
                $"<color=#{aHex}>TEAM A  {MatchResultHolder.ScoreTeamA}</color>" +
                $"<color=#{subHex}>  ·  </color>" +
                $"<color=#{bHex}>{MatchResultHolder.ScoreTeamB}  TEAM B</color>";
            scoreLabel.richText = true;
        }

        Color accentColor = isDraw ? C_Draw : (aWins ? C_A : C_B);
        if (bannerLeftBar  != null) bannerLeftBar.color  = accentColor;
        if (bannerRightBar != null) bannerRightBar.color = accentColor;

        if (victoryGraphic != null) victoryGraphic.SetActive(!isDraw && aWins);
        if (defeatGraphic  != null) defeatGraphic.SetActive(!isDraw && !aWins);
        if (drawGraphic    != null) drawGraphic.SetActive(isDraw);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TEAM STATS
    // ─────────────────────────────────────────────────────────────────────────

    private void PopulateTeam(TeamID team)
    {
        bool  isA       = team == TeamID.TeamA;
        Color teamColor = isA ? C_A : C_B;

        // Panel backgrounds are NOT tinted — your custom sprites display as-is.
        if (isA)
        {
            if (teamAAccentBar   != null) teamAAccentBar.color  = teamColor;
            if (teamAHeaderLabel != null) teamAHeaderLabel.text = "TEAM  A";
        }
        else
        {
            if (teamBAccentBar   != null) teamBAccentBar.color  = teamColor;
            if (teamBHeaderLabel != null) teamBHeaderLabel.text = "TEAM  B";
        }

        MatchResultEntry shooter   = null;
        MatchResultEntry collector = null;

        foreach (var entry in MatchResultHolder.Results)
        {
            if (entry.team != team) continue;
            if (entry.role == PlayerRole.Shooter   && shooter   == null) shooter   = entry;
            if (entry.role == PlayerRole.Collector && collector == null) collector = entry;
        }

        string sName  = shooter  != null ? CleanName(shooter.displayName)                       : "—";
        string sKills = shooter  != null ? shooter.kills.ToString()                             : "—";
        string sDmg   = shooter  != null ? Mathf.RoundToInt(shooter.damageDealt).ToString("N0") : "—";
        string sDeath = shooter  != null ? shooter.deaths.ToString()                            : "—";

        if (isA)
        {
            SetTMP(teamAShooterName,   sName);
            SetTMP(teamAShooterKills,  sKills);
            SetTMP(teamAShooterDamage, sDmg);
            SetTMP(teamAShooterDeaths, sDeath);
        }
        else
        {
            SetTMP(teamBShooterName,   sName);
            SetTMP(teamBShooterKills,  sKills);
            SetTMP(teamBShooterDamage, sDmg);
            SetTMP(teamBShooterDeaths, sDeath);
        }

        string cName  = collector != null ? CleanName(collector.displayName)      : "—";
        string cCandy = collector != null ? collector.candiesDelivered.ToString() : "—";
        string cDeath = collector != null ? collector.deaths.ToString()           : "—";

        if (isA)
        {
            SetTMP(teamACollectorName,    cName);
            SetTMP(teamACollectorCandies, cCandy);
            SetTMP(teamACollectorDeaths,  cDeath);
        }
        else
        {
            SetTMP(teamBCollectorName,    cName);
            SetTMP(teamBCollectorCandies, cCandy);
            SetTMP(teamBCollectorDeaths,  cDeath);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MVP STRIP
    // ─────────────────────────────────────────────────────────────────────────

    private void PopulateMVP()
    {
        int   bestKills   = 0;
        float bestDmg     = 0f;
        int   bestCandies = 0;
        MatchResultEntry topKiller = null, topCandy = null, topDamage = null;

        foreach (var e in MatchResultHolder.Results)
        {
            if (e.role == PlayerRole.Shooter)
            {
                if (e.kills       > bestKills) { bestKills = e.kills;       topKiller = e; }
                if (e.damageDealt > bestDmg)   { bestDmg   = e.damageDealt; topDamage = e; }
            }
            else
            {
                if (e.candiesDelivered > bestCandies) { bestCandies = e.candiesDelivered; topCandy = e; }
            }
        }

        SetTMP(mvpKillerName, topKiller != null ? CleanName(topKiller.displayName) : "—");
        SetTMP(mvpKillerStat, topKiller != null ? $"Kills: {topKiller.kills}"      : "");

        SetTMP(mvpCandyName,  topCandy  != null ? CleanName(topCandy.displayName)             : "—");
        SetTMP(mvpCandyStat,  topCandy  != null ? $"Candies: {topCandy.candiesDelivered}"     : "");

        SetTMP(mvpDamageName, topDamage != null ? CleanName(topDamage.displayName)            : "—");
        SetTMP(mvpDamageStat, topDamage != null
            ? $"Damage: {Mathf.RoundToInt(topDamage.damageDealt):N0}" : "");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  BUTTON CALLBACKS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rematch — server resets LobbyManager state then reloads GameScene via NGO.
    /// All connected clients follow automatically. LobbyManager.OnSceneLoaded
    /// fires on the server and handles SpawnAllPlayers + StartMatch as normal.
    /// Clients stay connected — no shutdown, no reconnection required.
    /// </summary>
    private void OnRematch()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
        {
            // Fallback: connection was lost — go to lobby normally.
            SceneManager.LoadScene(lobbySceneName);
            return;
        }

        if (!NetworkManager.Singleton.IsServer) return;

        if (rematchButton      != null) rematchButton.interactable      = false;
        if (rematchStatusLabel != null) rematchStatusLabel.text         = "Loading…";

        // Reset LobbyManager so it treats the next OnSceneLoaded as a fresh match.
        LobbyManager.Instance?.PrepareRematch();

        // NGO reloads the scene for ALL clients simultaneously.
        NetworkManager.Singleton.SceneManager.LoadScene(
            gameSceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
    }

    /// <summary>
    /// Main Menu — shuts down NGO cleanly then loads lobby.
    /// The coroutine waits until IsListening = false before loading, which
    /// was the root cause of the "scene didn't load" bug.
    /// </summary>
    private void OnMainMenu()
    {
        if (mainMenuButton != null) mainMenuButton.interactable = false;
        if (rematchButton  != null) rematchButton.interactable  = false;
        StartCoroutine(ShutdownThenLoad(lobbySceneName));
    }

    private static IEnumerator ShutdownThenLoad(string sceneName)
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            NetworkManager.Singleton.Shutdown();

            // Poll each frame until NGO has fully stopped.
            while (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
                yield return null;
        }

        SceneManager.LoadScene(sceneName);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  HELPERS
    // ─────────────────────────────────────────────────────────────────────────

    private static string CleanName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "Unknown";
        return raw.Contains("]") ? raw.Split(']')[1].Trim() : raw;
    }

    private static void SetTMP(TextMeshProUGUI label, string value)
    {
        if (label != null) label.text = value;
    }

    private static Color Hex(string hex)
    {
        ColorUtility.TryParseHtmlString("#" + hex, out Color c);
        return c;
    }
}