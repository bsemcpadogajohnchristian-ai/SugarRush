// PauseMenuUI.cs — Sugar Rush
//
// ── BUG FIXES ─────────────────────────────────────────────────────────────────
//
//   FIX 1 — MAIN MENU BUTTON LOADS WRONG SCENE
//     Root cause: the field was named 'lobbySceneName' and defaulted to
//     "LobbyScene". OnMainMenuClicked() was shutting NGO down and then loading
//     LobbyScene — but with no running host/client the LobbyRoomUI in that
//     scene could never connect, so everything appeared broken.
//     Fix: field renamed to 'startMenuSceneName', default "StartMenuScene".
//     ShutdownThenLoad() now loads the start-menu scene, which is the correct
//     destination after in-game disconnect.
//
//   FIX 2 — "PRESS START AGAIN AND IT WON'T START"
//     This was a cascade of Fix 1: because the user landed in LobbyScene
//     with NGO shut down, LobbyRoomUI.RequestStartGameServerRpc() had no
//     server to send to, so nothing happened. Fixing Fix 1 routes the user
//     to StartMenuScene where they can host/join a fresh session normally.
//     Additionally, IsPaused is now forced to false in OnDestroy so the
//     static flag can never get stuck true across a scene reload.
//
//   FIX 3 — MOUSE LOOK STILL WORKS WHILE PAUSE MENU IS OPEN
//     Two stacked causes:
//
//     a) Execution-order race: PlayerController uses [DefaultExecutionOrder(-50)],
//        which means its Update() runs BEFORE PauseMenuUI's Update() (default
//        order 0). On the exact frame Escape is pressed, PlayerController.Look()
//        fires first (IsPaused is still false), then PauseMenuUI processes the
//        key and sets IsPaused = true — one frame too late.
//        Fix: [DefaultExecutionOrder(-100)] on PauseMenuUI ensures it processes
//        Escape and sets IsPaused BEFORE PlayerController.Update() reads it.
//
//     b) Accumulated delta on resume: while the cursor is unlocked during the
//        pause menu, the user moves the mouse to click buttons. Unity's
//        Input.GetAxis("Mouse X/Y") records those deltas. The moment IsPaused
//        is cleared, PlayerController.Look() drains that accumulated delta and
//        the camera snaps to a new angle.
//        Fix: Input.ResetInputAxes() is called in both Open() and Close() to
//        flush any pending mouse delta at the transition boundary.
//
// ── EVERYTHING ELSE IS UNCHANGED ─────────────────────────────────────────────
//   Three-button main panel, Settings sub-panel, volume/sensitivity sliders,
//   ShutdownThenLoad coroutine — all logic is identical to the original.
//
// ── MULTIPLAYER NOTES ─────────────────────────────────────────────────────────
//   Time.timeScale is NOT set to 0 — NGO's server-side simulation must keep
//   running. PauseMenuUI.IsPaused is a static bool that PlayerController reads
//   to block Look/Move/Crouch while the overlay is visible.
//
// ── SETUP ─────────────────────────────────────────────────────────────────────
//   See PAUSE_MENU_TUTORIAL.md for the full step-by-step Unity setup guide.
//   Inspector field 'lobbySceneName' has been renamed 'startMenuSceneName' —
//   re-assign it in the Inspector if you had already saved a reference.

using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

// FIX 3a — run BEFORE PlayerController [DefaultExecutionOrder(-50)] so that
// IsPaused is already set when PlayerController.Update() reads it.
[DefaultExecutionOrder(-100)]
public class PauseMenuUI : MonoBehaviour
{
    public static PauseMenuUI Instance { get; private set; }

    /// <summary>
    /// True while the pause menu is visible.
    /// PlayerController.Update() reads this to block movement and look input.
    /// </summary>
    public static bool IsPaused { get; private set; }

    // ── Panel roots ───────────────────────────────────────────────────────────

    [Header("Panels")]
    [Tooltip("Root overlay panel (the dark semi-transparent background Image). " +
             "Parent of both mainButtonPanel and settingsPanel. " +
             "This is the object you toggle to show/hide the whole menu.")]
    public GameObject overlayRoot;

    [Tooltip("The child panel that holds the three main buttons " +
             "(Settings / Main Menu / Leave). Shown when the overlay opens.")]
    public GameObject mainButtonPanel;

    [Tooltip("The child panel with the sliders. Hidden by default; " +
             "shown when the player clicks Settings.")]
    public GameObject settingsPanel;

    // ── Main buttons ──────────────────────────────────────────────────────────

    [Header("Main Buttons")]
    public Button btnSettings;
    public Button btnMainMenu;
    public Button btnLeave;

    // ── Settings sub-panel ────────────────────────────────────────────────────

    [Header("Settings Sub-Panel")]
    [Tooltip("Optional — assign your AudioMixer to control the MasterVolume exposed parameter.")]
    public AudioMixer audioMixer;

    [Tooltip("Volume slider (0–1 range). Drives AudioListener.volume and the MasterVolume mixer.")]
    public Slider sliderVolume;

    [Tooltip("Sensitivity slider. Recommended range 0.5–10. " +
             "Applied live to the local PlayerController.mouseSensitivity.")]
    public Slider sliderSensitivity;

    [Tooltip("Optional TMP label that shows the current sensitivity value next to the slider.")]
    public TextMeshProUGUI lblSensValue;

    [Tooltip("Back button inside the settings sub-panel — returns to the main button panel.")]
    public Button btnSettingsBack;

    // ── Navigation ────────────────────────────────────────────────────────────

    [Header("Scene Names")]
    // FIX 1 — renamed from 'lobbySceneName' ("LobbyScene") to 'startMenuSceneName'
    // ("StartMenuScene"). Re-assign in the Inspector if you had a previous value saved.
    [Tooltip("The start-menu / title scene to load when Main Menu is pressed. " +
             "Must match the exact scene name in Build Settings (e.g. 'StartMenuScene').")]
    public string startMenuSceneName = "StartMenuScene";

    // ── Internal ──────────────────────────────────────────────────────────────

    private PlayerController _playerController; // cached on first sensitivity change

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        // Wire main buttons
        if (btnSettings  != null) btnSettings.onClick.AddListener(OnSettingsClicked);
        if (btnMainMenu  != null) btnMainMenu.onClick.AddListener(OnMainMenuClicked);
        if (btnLeave     != null) btnLeave.onClick.AddListener(OnLeaveClicked);

        // Wire settings back button
        if (btnSettingsBack != null) btnSettingsBack.onClick.AddListener(OnSettingsBack);

        // Wire sliders
        if (sliderVolume      != null) sliderVolume.onValueChanged.AddListener(OnVolumeChanged);
        if (sliderSensitivity != null) sliderSensitivity.onValueChanged.AddListener(OnSensitivityChanged);

        // Restore saved values — fires onValueChanged to apply them immediately
        LoadSettings();

        // Always start hidden
        ForceClose();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;

        // FIX 2 — ensure the static flag is never left true if this object is
        // destroyed mid-pause (e.g. scene unloads while the menu is open).
        // Without this, PlayerController would stay blocked in the next scene.
        IsPaused = false;
    }

    // ── Escape key (runs at order -100, before PlayerController at -50) ───────

    private void Update()
    {
        if (!Input.GetKeyDown(KeyCode.Escape)) return;

        // If the settings sub-panel is open, Escape closes just that first
        if (IsPaused && settingsPanel != null && settingsPanel.activeSelf)
        {
            OnSettingsBack();
            return;
        }

        Toggle();
    }

    // ── Open / Close ──────────────────────────────────────────────────────────

    private void Toggle()
    {
        if (IsPaused) Close();
        else          Open();
    }

    /// <summary>Opens the pause overlay and unlocks the cursor.</summary>
    public void Open()
    {
        IsPaused = true;

        if (overlayRoot     != null) overlayRoot.SetActive(true);
        if (mainButtonPanel != null) mainButtonPanel.SetActive(true);
        if (settingsPanel   != null) settingsPanel.SetActive(false);

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;

        // FIX 3b — flush any mouse delta that accumulated this frame so the
        // camera doesn't jump when the menu closes.
        Input.ResetInputAxes();
    }

    /// <summary>Closes the pause overlay and re-locks the cursor.</summary>
    public void Close()
    {
        IsPaused = false;

        if (overlayRoot != null) overlayRoot.SetActive(false);

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;

        PlayerPrefs.Save();

        // FIX 3b — flush mouse delta accumulated while the cursor was free
        // (user moved mouse to click buttons). Without this, PlayerController
        // drains the delta on the first Look() frame and the camera snaps.
        Input.ResetInputAxes();
    }

    /// <summary>Closes without saving PlayerPrefs — used in Start() initialization.</summary>
    private void ForceClose()
    {
        IsPaused = false;
        if (overlayRoot != null) overlayRoot.SetActive(false);
    }

    // ── Button callbacks ──────────────────────────────────────────────────────

    private void OnSettingsClicked()
    {
        if (mainButtonPanel != null) mainButtonPanel.SetActive(false);
        if (settingsPanel   != null) settingsPanel.SetActive(true);
    }

    private void OnSettingsBack()
    {
        if (settingsPanel   != null) settingsPanel.SetActive(false);
        if (mainButtonPanel != null) mainButtonPanel.SetActive(true);
        PlayerPrefs.Save();
    }

    private void OnMainMenuClicked()
    {
        // Disable interactivity so the player can't click twice
        if (btnMainMenu != null) btnMainMenu.interactable = false;
        if (btnLeave    != null) btnLeave.interactable    = false;

        IsPaused = false;  // clear before scene load so the next scene starts unpaused

        // FIX 1 — was: ShutdownThenLoad(lobbySceneName) → loaded "LobbyScene"
        // with NGO shut down, which left the user stuck with no running session.
        // Now loads 'startMenuSceneName' ("StartMenuScene") so the user lands
        // on the title screen and can host/join a fresh session normally.
        StartCoroutine(ShutdownThenLoad(startMenuSceneName));
    }

    private void OnLeaveClicked()
    {
        IsPaused = false;

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        float vol  = PlayerPrefs.GetFloat("SR_MasterVol", 1f);
        float sens = PlayerPrefs.GetFloat("SR_MouseSens", 3f);

        // Setting .value fires onValueChanged which applies the value immediately
        if (sliderVolume      != null) sliderVolume.value      = vol;
        if (sliderSensitivity != null) sliderSensitivity.value = sens;
    }

    private void OnVolumeChanged(float v)
    {
        AudioListener.volume = v;
        SetMixerVolume("MasterVolume", v);
        PlayerPrefs.SetFloat("SR_MasterVol", v);
    }

    private void OnSensitivityChanged(float v)
    {
        PlayerPrefs.SetFloat("SR_MouseSens", v);

        if (lblSensValue != null)
            lblSensValue.text = v.ToString("F1");

        ApplySensitivity(v);
    }

    private void ApplySensitivity(float v)
    {
        // Cache the owner's PlayerController on first call
        if (_playerController == null || !_playerController.IsOwner)
        {
            _playerController = null;
            foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            {
                if (pc.IsOwner) { _playerController = pc; break; }
            }
        }

        if (_playerController != null)
            _playerController.mouseSensitivity = v;
    }

    private void SetMixerVolume(string exposedParam, float linearValue)
    {
        if (audioMixer == null) return;
        float db = Mathf.Log10(Mathf.Max(linearValue, 0.0001f)) * 20f;
        audioMixer.SetFloat(exposedParam, db);
    }

    // ── Scene load helper ─────────────────────────────────────────────────────

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
}