using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class LobbyUI : MonoBehaviour
{
    public static LobbyUI Instance { get; private set; }

    [Header("Buttons")]
    public Button         hostButton;
    public Button         joinButton;

    [Header("Input")]
    public TMP_InputField ipInputField;

    [Header("Labels")]
    public TextMeshProUGUI playerCountText;
    public TextMeshProUGUI statusText;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        hostButton.onClick.AddListener(OnHostClicked);
        joinButton.onClick.AddListener(OnJoinClicked);
        SetStatus("Press Host or Join to start.");
        SetPlayerCount(0);
    }

    private void OnHostClicked()
    {
        hostButton.interactable = false;
        joinButton.interactable = false;
        SetStatus("Hosting... waiting for players.");
        NetworkManager.Singleton.StartHost();
    }

    private void OnJoinClicked()
    {
        string ip = ipInputField != null ? ipInputField.text.Trim() : "";
        if (string.IsNullOrEmpty(ip)) ip = "127.0.0.1";

        UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport != null)
            transport.ConnectionData.Address = ip;

        hostButton.interactable = false;
        joinButton.interactable = false;
        SetStatus($"Connecting to {ip}...");
        NetworkManager.Singleton.StartClient();
    }

    public void SetPlayerCount(int count)
    {
        if (playerCountText != null)
            playerCountText.text = $"Players: {count} / 4";
    }

    public void SetStatus(string msg)
    {
        if (statusText != null)
            statusText.text = msg;
    }
}
