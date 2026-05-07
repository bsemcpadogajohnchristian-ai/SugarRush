// ResultScreenManager.cs — Sugar Rush
//
// ── STRIPPED VERSION ──────────────────────────────────────────────────────────
//   Removed from this version:
//     • bannerLeftBar / bannerRightBar   (banner accent bars)
//     • subLabel                         (banner sub-label)
//     • teamAAccentBar / teamBAccentBar  (team panel accent bars)
//     • teamAHeaderLabel / teamBHeaderLabel (team panel header labels)
//     • Entire MVP strip section         (all six mvp* labels + PopulateMVP())
//
//   Everything else (rematch / main-menu logic, score banner, team stats,
//   win/lose graphics) is unchanged.
//
// ── REMATCH + SCENE-LOAD FIX (unchanged) ─────────────────────────────────────
//   ShutdownThenLoad() polls IsListening before loading so NGO tears down
//   cleanly before the scene transition.
//   Rematch reuses LobbyManager.PrepareRematch() — no NGO shutdown needed.

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
    public TextMeshProUGUI scoreLabel;
    // bannerLeftBar, bannerRightBar, subLabel — REMOVED

    // ── Team A Stats ──────────────────────────────────────────────────────────
    [Header("Team A Stats Panel")]
    public Image           teamAPanelBg;       // NOT tinted — your sprite shows as-is
    // teamAAccentBar, teamAHeaderLabel — REMOVED

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
    // teamBAccentBar, teamBHeaderLabel — REMOVED

    public TextMeshProUGUI teamBShooterName;
    public TextMeshProUGUI teamBShooterKills;
    public TextMeshProUGUI teamBShooterDamage;
    public TextMeshProUGUI teamBShooterDeaths;

    public TextMeshProUGUI teamBCollectorName;
    public TextMeshProUGUI teamBCollectorCandies;
    public TextMeshProUGUI teamBCollectorDeaths;

    // MVP Strip — REMOVED entirely

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
        // PopulateMVP() — REMOVED
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

        // subLabel — REMOVED (no longer written to)

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

        // bannerLeftBar / bannerRightBar — REMOVED (no longer tinted)

        if (victoryGraphic != null) victoryGraphic.SetActive(!isDraw && aWins);
        if (defeatGraphic  != null) defeatGraphic.SetActive(!isDraw && !aWins);
        if (drawGraphic    != null) drawGraphic.SetActive(isDraw);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TEAM STATS
    // ─────────────────────────────────────────────────────────────────────────

    private void PopulateTeam(TeamID team)
    {
        bool isA = team == TeamID.TeamA;

        // teamAAccentBar, teamAHeaderLabel, teamBAccentBar, teamBHeaderLabel — REMOVED

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

    // PopulateMVP() — REMOVED entirely

    // ─────────────────────────────────────────────────────────────────────────
    //  BUTTON CALLBACKS
    // ─────────────────────────────────────────────────────────────────────────

    private void OnRematch()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
        {
            SceneManager.LoadScene(lobbySceneName);
            return;
        }

        if (!NetworkManager.Singleton.IsServer) return;

        if (rematchButton      != null) rematchButton.interactable      = false;
        if (rematchStatusLabel != null) rematchStatusLabel.text         = "Loading…";

        LobbyManager.Instance?.PrepareRematch();

        NetworkManager.Singleton.SceneManager.LoadScene(
            gameSceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
    }

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