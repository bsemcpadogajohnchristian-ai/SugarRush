// StartMenuManager.cs — Sugar Rush
//
// ── PURPOSE ───────────────────────────────────────────────────────────────────
//   Controls every panel in StartMenuScene:
//     • Main Menu      (Play Game / How To Play / Settings / Quit)
//     • How To Play    (info screen)
//     • Settings       (volume / sensitivity / quality)
//     • Play Mode      (Host Game / Join Game)
//     • Host Setup     (enter player name → Create Room)
//     • Join Setup     (enter player name + host IP → Connect)
//
// ── SETUP ─────────────────────────────────────────────────────────────────────
//   See the full step-by-step tutorial: TUTORIAL.md
//   Attach this script to a GameObject named "StartMenuManager" in StartMenuScene.
//   Assign every inspector reference listed below in the Unity Inspector.
//
// ── DEPENDENCIES ──────────────────────────────────────────────────────────────
//   • PlayerNameHolder   (in scene, DontDestroyOnLoad)
//   • NetworkManager     (in scene, NGO v2 + UnityTransport)
//   • LobbyScene         must be in File → Build Settings

using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;
using TMPro;

public class StartMenuManager : MonoBehaviour
{
    public static StartMenuManager Instance { get; private set; }

    // ── Panel roots (assign each Panel GameObject) ────────────────────────────
    [Header("Panels — assign the root GameObject of each panel")]
    public GameObject panelMainMenu;
    public GameObject panelHowToPlay;
    public GameObject panelSettings;
    public GameObject panelPlayMode;
    public GameObject panelHostSetup;
    public GameObject panelJoinSetup;

    // ── Main Menu ─────────────────────────────────────────────────────────────
    [Header("Main Menu")]
    public Button btnPlay;
    public Button btnHowToPlay;
    public Button btnSettings;
    public Button btnQuit;

    // ── How To Play ───────────────────────────────────────────────────────────
    [Header("How To Play")]
    public Button btnHowToPlayBack;
    // Add more TMP labels for your rules text if desired.

    // ── Settings ──────────────────────────────────────────────────────────────
    [Header("Settings")]
    [Tooltip("Optional — assign your project's AudioMixer to control volume groups.")]
    public AudioMixer audioMixer;

    public Slider  sliderMasterVolume;
    public Slider  sliderMusicVolume;
    public Slider  sliderSFXVolume;
    public Slider  sliderMouseSensitivity;
    public TMP_Dropdown dropdownQuality;
    public Button  btnSettingsBack;
    public TextMeshProUGUI lblMouseSensValue;   // optional: shows numeric value

    // ── Play Mode ─────────────────────────────────────────────────────────────
    [Header("Play Mode")]
    public Button btnHostGame;
    public Button btnJoinGame;
    public Button btnPlayModeBack;

    // ── Host Setup ────────────────────────────────────────────────────────────
    [Header("Host Setup")]
    public TMP_InputField  inputHostName;
    public Button          btnCreateRoom;
    public Button          btnHostSetupBack;
    public TextMeshProUGUI lblHostStatus;

    // ── Join Setup ────────────────────────────────────────────────────────────
    [Header("Join Setup")]
    public TMP_InputField  inputJoinName;
    public TMP_InputField  inputJoinIP;
    public Button          btnConnect;
    public Button          btnJoinSetupBack;
    public TextMeshProUGUI lblJoinStatus;

    // ── Navigation ────────────────────────────────────────────────────────────
    [Header("Scene Names")]
    [Tooltip("Must match exactly the scene name in Build Settings.")]
    public string lobbySceneName = "LobbyScene";

    // ── Internal ──────────────────────────────────────────────────────────────

    private bool _connecting;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        // Restore cursor (may be locked from a previous game session)
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;

        // Load previously saved player name
        PlayerNameHolder.Instance?.LoadSavedName();

        string savedName = PlayerNameHolder.Instance?.LocalPlayerName ?? "Player";
        if (inputHostName != null) inputHostName.text = savedName;
        if (inputJoinName != null) inputJoinName.text = savedName;
        if (inputJoinIP   != null) inputJoinIP.text   = "127.0.0.1";

        WireButtons();
        LoadSettings();

        ShowPanel(panelMainMenu);
    }

    // ── Button wiring ─────────────────────────────────────────────────────────

    private void WireButtons()
    {
        // Main menu
        if (btnPlay      != null) btnPlay.onClick.AddListener(() => ShowPanel(panelPlayMode));
        if (btnHowToPlay != null) btnHowToPlay.onClick.AddListener(() => ShowPanel(panelHowToPlay));
        if (btnSettings  != null) btnSettings.onClick.AddListener(() => ShowPanel(panelSettings));
        if (btnQuit      != null) btnQuit.onClick.AddListener(OnQuit);

        // How to play
        if (btnHowToPlayBack != null) btnHowToPlayBack.onClick.AddListener(() => ShowPanel(panelMainMenu));

        // Settings
        if (btnSettingsBack       != null) btnSettingsBack.onClick.AddListener(OnSettingsBack);
        if (sliderMasterVolume    != null) sliderMasterVolume.onValueChanged.AddListener(OnMasterVolumeChanged);
        if (sliderMusicVolume     != null) sliderMusicVolume.onValueChanged.AddListener(OnMusicVolumeChanged);
        if (sliderSFXVolume       != null) sliderSFXVolume.onValueChanged.AddListener(OnSFXVolumeChanged);
        if (sliderMouseSensitivity!= null) sliderMouseSensitivity.onValueChanged.AddListener(OnMouseSensChanged);
        if (dropdownQuality       != null) dropdownQuality.onValueChanged.AddListener(OnQualityChanged);

        // Play mode
        if (btnHostGame    != null) btnHostGame.onClick.AddListener(() => ShowPanel(panelHostSetup));
        if (btnJoinGame    != null) btnJoinGame.onClick.AddListener(() => ShowPanel(panelJoinSetup));
        if (btnPlayModeBack!= null) btnPlayModeBack.onClick.AddListener(() => ShowPanel(panelMainMenu));

        // Host setup
        if (btnCreateRoom   != null) btnCreateRoom.onClick.AddListener(OnCreateRoom);
        if (btnHostSetupBack!= null) btnHostSetupBack.onClick.AddListener(() => ShowPanel(panelPlayMode));

        // Join setup
        if (btnConnect      != null) btnConnect.onClick.AddListener(OnJoinRoom);
        if (btnJoinSetupBack!= null) btnJoinSetupBack.onClick.AddListener(OnJoinBack);
    }

    // ── Panel management ──────────────────────────────────────────────────────

    private void ShowPanel(GameObject target)
    {
        if (panelMainMenu  != null) panelMainMenu.SetActive(false);
        if (panelHowToPlay != null) panelHowToPlay.SetActive(false);
        if (panelSettings  != null) panelSettings.SetActive(false);
        if (panelPlayMode  != null) panelPlayMode.SetActive(false);
        if (panelHostSetup != null) panelHostSetup.SetActive(false);
        if (panelJoinSetup != null) panelJoinSetup.SetActive(false);

        if (target != null) target.SetActive(true);
    }

    // ── Quit ──────────────────────────────────────────────────────────────────

    private static void OnQuit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        float master  = PlayerPrefs.GetFloat("SR_MasterVol",  1f);
        float music   = PlayerPrefs.GetFloat("SR_MusicVol",   1f);
        float sfx     = PlayerPrefs.GetFloat("SR_SFXVol",     1f);
        float sens    = PlayerPrefs.GetFloat("SR_MouseSens",  3f);
        int   quality = PlayerPrefs.GetInt  ("SR_Quality",    QualitySettings.GetQualityLevel());

        // Set slider values (this fires onValueChanged → applies the settings)
        if (sliderMasterVolume    != null) sliderMasterVolume.value    = master;
        if (sliderMusicVolume     != null) sliderMusicVolume.value     = music;
        if (sliderSFXVolume       != null) sliderSFXVolume.value       = sfx;
        if (sliderMouseSensitivity!= null) sliderMouseSensitivity.value= sens;
        if (dropdownQuality       != null) dropdownQuality.value       = quality;

        QualitySettings.SetQualityLevel(quality);
    }

    private void OnMasterVolumeChanged(float v)
    {
        AudioListener.volume = v;
        SetMixerVolume("MasterVolume", v);
        PlayerPrefs.SetFloat("SR_MasterVol", v);
    }

    private void OnMusicVolumeChanged(float v)
    {
        SetMixerVolume("MusicVolume", v);
        PlayerPrefs.SetFloat("SR_MusicVol", v);
    }

    private void OnSFXVolumeChanged(float v)
    {
        SetMixerVolume("SFXVolume", v);
        PlayerPrefs.SetFloat("SR_SFXVol", v);
    }

    private void OnMouseSensChanged(float v)
    {
        if (lblMouseSensValue != null) lblMouseSensValue.text = v.ToString("F1");
        PlayerPrefs.SetFloat("SR_MouseSens", v);
    }

    private void OnQualityChanged(int v)
    {
        QualitySettings.SetQualityLevel(v);
        PlayerPrefs.SetInt("SR_Quality", v);
    }

    private void SetMixerVolume(string exposedParam, float linearValue)
    {
        if (audioMixer == null) return;
        float db = Mathf.Log10(Mathf.Max(linearValue, 0.0001f)) * 20f;
        audioMixer.SetFloat(exposedParam, db);
    }

    private void OnSettingsBack()
    {
        PlayerPrefs.Save();
        ShowPanel(panelMainMenu);
    }

    // ── Host ──────────────────────────────────────────────────────────────────

    private void OnCreateRoom()
    {
        if (_connecting) return;

        string name = inputHostName != null ? inputHostName.text.Trim() : "";
        if (string.IsNullOrEmpty(name)) name = "Host";

        PlayerNameHolder.Instance?.SetName(name);

        SetHostStatus("Starting host...", false);
        StartCoroutine(StartHostRoutine());
    }

    private IEnumerator StartHostRoutine()
    {
        _connecting = true;

        NetworkManager.Singleton.StartHost();

        // Give NGO one frame to initialise before loading a scene via its
        // SceneManager — calling LoadScene on the same frame as StartHost()
        // can occasionally throw a "NetworkSceneManager not initialised" error.
        yield return null;

        if (NetworkManager.Singleton.IsHost)
        {
            SetHostStatus("Loading lobby...", false);
            NetworkManager.Singleton.SceneManager.LoadScene(
                lobbySceneName, UnityEngine.SceneManagement.LoadSceneMode.Single);
        }
        else
        {
            SetHostStatus("Failed to start host. Check console.", true);
            _connecting = false;
        }
    }

    private void SetHostStatus(string msg, bool enableBtn)
    {
        if (lblHostStatus  != null) lblHostStatus.text = msg;
        if (btnCreateRoom  != null) btnCreateRoom.interactable = enableBtn;
        if (btnHostSetupBack!=null) btnHostSetupBack.interactable = enableBtn;
    }

    // ── Join ──────────────────────────────────────────────────────────────────

    private void OnJoinRoom()
    {
        if (_connecting) return;

        string name = inputJoinName != null ? inputJoinName.text.Trim() : "";
        string ip   = inputJoinIP   != null ? inputJoinIP.text.Trim()   : "";

        if (string.IsNullOrEmpty(name)) name = "Player";
        if (string.IsNullOrEmpty(ip))   ip   = "127.0.0.1";

        PlayerNameHolder.Instance?.SetName(name);

        // Set connection address
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport != null) transport.ConnectionData.Address = ip;

        SetJoinStatus($"Connecting to {ip}...", false);
        _connecting = true;

        NetworkManager.Singleton.OnClientConnectedCallback  += OnJoinSuccess;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnJoinFailed;
        NetworkManager.Singleton.StartClient();
    }

    private void OnJoinSuccess(ulong clientId)
    {
        // This fires for every connected client event — only care about our own
        if (clientId != NetworkManager.Singleton.LocalClientId) return;

        NetworkManager.Singleton.OnClientConnectedCallback  -= OnJoinSuccess;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnJoinFailed;

        SetJoinStatus("Connected! Entering lobby...", false);
        // NGO scene sync will load LobbyScene for us automatically.
    }

    private void OnJoinFailed(ulong clientId)
    {
        NetworkManager.Singleton.OnClientConnectedCallback  -= OnJoinSuccess;
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnJoinFailed;

        _connecting = false;
        SetJoinStatus("Connection failed. Check IP and try again.", true);
    }

    private void SetJoinStatus(string msg, bool enableInteract)
    {
        if (lblJoinStatus   != null) lblJoinStatus.text = msg;
        if (btnConnect      != null) btnConnect.interactable       = enableInteract;
        if (btnJoinSetupBack!= null) btnJoinSetupBack.interactable = enableInteract;
    }

    private void OnJoinBack()
    {
        if (_connecting) return;
        ShowPanel(panelPlayMode);
    }
}
