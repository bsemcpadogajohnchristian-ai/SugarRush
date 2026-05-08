// LobbyRoomUI.cs — Sugar Rush
//
// ── PURPOSE ───────────────────────────────────────────────────────────────────
//   Displays the lobby waiting room in LobbyScene.
//   Shows four player slots (TeamA Shooter, TeamA Collector, TeamB Shooter,
//   TeamB Collector), the room IP so friends can connect, and a Start Game
//   button that is only interactive for the host.
//
//   Replaces the original LobbyUI.cs. The old LobbyUI.cs can be kept for
//   backward compatibility — this script is separate.
//
// ── SETUP ─────────────────────────────────────────────────────────────────────
//   1. In LobbyScene, create a Canvas and build the lobby room UI hierarchy
//      (see TUTORIAL.md for the full hierarchy list).
//   2. Attach this script to a "LobbyRoomManager" GameObject in the scene.
//   3. Assign every Inspector reference below.

using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class LobbyRoomUI : MonoBehaviour
{
    public static LobbyRoomUI Instance { get; private set; }

    // ── Slot labels — Team A ──────────────────────────────────────────────────
    [Header("Team A — Slot 0: Shooter")]
    public TextMeshProUGUI lblTeamAShooterName;
    public TextMeshProUGUI lblTeamAShooterStatus;

    [Header("Team A — Slot 1: Collector")]
    public TextMeshProUGUI lblTeamACollectorName;
    public TextMeshProUGUI lblTeamACollectorStatus;

    // ── Slot labels — Team B ──────────────────────────────────────────────────
    [Header("Team B — Slot 2: Shooter")]
    public TextMeshProUGUI lblTeamBShooterName;
    public TextMeshProUGUI lblTeamBShooterStatus;

    [Header("Team B — Slot 3: Collector")]
    public TextMeshProUGUI lblTeamBCollectorName;
    public TextMeshProUGUI lblTeamBCollectorStatus;

    // ── Room info ─────────────────────────────────────────────────────────────
    [Header("Room Info")]
    public TextMeshProUGUI lblRoomIP;
    public TextMeshProUGUI lblPlayerCount;
    public Button          btnCopyIP;

    // ── Buttons ───────────────────────────────────────────────────────────────
    [Header("Control Buttons")]
    [Tooltip("Only the host sees this button active.")]
    public Button          btnStartGame;
    public Button          btnLeaveRoom;

    [Header("Status")]
    public TextMeshProUGUI lblStatus;

    // ── Navigation ────────────────────────────────────────────────────────────
    [Header("Scene Names")]
    public string startMenuSceneName = "StartMenuScene";

    // ── Internal ──────────────────────────────────────────────────────────────

    private string _localIP;
    private bool   _isHost;

    // Slot to role label mapping (matches LobbyManager.TeamForSlot / RoleForSlot)
    private static readonly string[] SlotRole = { "SHOOTER", "COLLECTOR", "SHOOTER", "COLLECTOR" };
    private static readonly string[] SlotTeam = { "TEAM A", "TEAM A", "TEAM B", "TEAM B" };

    // Colors
    private static readonly Color ColConnected = new Color(0.2f, 0.9f, 0.4f);
    private static readonly Color ColEmpty     = new Color(0.5f, 0.5f, 0.5f);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        _isHost  = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        _localIP = GetLocalIPAddress();

        // Room IP label
        if (lblRoomIP != null)
            lblRoomIP.text = _isHost
                ? $"Your IP:  {_localIP}  (share with friends)"
                : "Connected to host";

        // Copy-IP button (only meaningful for host)
        if (btnCopyIP != null)
        {
            btnCopyIP.gameObject.SetActive(_isHost);
            btnCopyIP.onClick.AddListener(OnCopyIP);
        }

        // Start Game button — visible only to host
        if (btnStartGame != null)
        {
            btnStartGame.gameObject.SetActive(_isHost);
            btnStartGame.interactable = false;   // enabled once ≥ 1 player
            btnStartGame.onClick.AddListener(OnStartGame);
        }

        if (btnLeaveRoom != null) btnLeaveRoom.onClick.AddListener(OnLeaveRoom);

        // Subscribe to lobby state broadcasts
        StartCoroutine(SubscribeWhenReady());
    }

    private void OnDestroy()
    {
        if (LobbyNetworkBridge.Instance != null)
            LobbyNetworkBridge.Instance.onLobbyStateUpdated.RemoveListener(OnLobbyStateUpdated);
    }

    // ── Wait for LobbyNetworkBridge to spawn, then register name ─────────────

    private IEnumerator SubscribeWhenReady()
    {
        // LobbyNetworkBridge is a NetworkBehaviour — wait until it is spawned
        // before calling RPCs.
        float timeout = 5f;
        while (LobbyNetworkBridge.Instance == null && timeout > 0f)
        {
            timeout -= Time.deltaTime;
            yield return null;
        }

        if (LobbyNetworkBridge.Instance == null)
        {
            Debug.LogError("[LobbyRoomUI] LobbyNetworkBridge not found after 5 s. " +
                           "Ensure it is on the same GameObject as LobbyManager in LobbyScene.");
            yield break;
        }

        LobbyNetworkBridge.Instance.onLobbyStateUpdated.AddListener(OnLobbyStateUpdated);

        // Register this player's name with the server
        string name = PlayerNameHolder.Instance?.LocalPlayerName ?? "Player";
        LobbyNetworkBridge.Instance.RegisterNameServerRpc(name);

        // Initial blank display while we wait for server response
        ClearAllSlots();
        SetStatus(_isHost ? "Waiting for players..." : "Connected — waiting for host to start...");
    }

    // ── Lobby state callback ──────────────────────────────────────────────────

    private void OnLobbyStateUpdated(List<LobbyPlayerInfo> players)
    {
        ClearAllSlots();

        foreach (LobbyPlayerInfo p in players)
            SetSlot(p.slotIndex, p.playerName, connected: true);

        int count = players.Count;

        if (lblPlayerCount != null)
            lblPlayerCount.text = $"Players:  {count} / 4";

        // Update Start Game button
        if (btnStartGame != null && _isHost)
            btnStartGame.interactable = count >= 1;

        // Status message
        if (_isHost)
            SetStatus(count >= 1
                ? $"{count}/4 players — press Start Game when ready!"
                : "Waiting for players to join...");
        else
            SetStatus($"{count}/4 players connected — waiting for host...");
    }

    // ── Slot display helpers ──────────────────────────────────────────────────

    private void ClearAllSlots()
    {
        for (int i = 0; i < 4; i++) SetSlot(i, "Waiting...", connected: false);
    }

    private void SetSlot(int slot, string name, bool connected)
    {
        Color    col    = connected ? ColConnected : ColEmpty;
        string   status = connected ? "● CONNECTED" : "○ EMPTY";

        switch (slot)
        {
            case 0:
                SetLabel(lblTeamAShooterName,   name,   col);
                SetLabel(lblTeamAShooterStatus, status, col);
                break;
            case 1:
                SetLabel(lblTeamACollectorName,   name,   col);
                SetLabel(lblTeamACollectorStatus, status, col);
                break;
            case 2:
                SetLabel(lblTeamBShooterName,   name,   col);
                SetLabel(lblTeamBShooterStatus, status, col);
                break;
            case 3:
                SetLabel(lblTeamBCollectorName,   name,   col);
                SetLabel(lblTeamBCollectorStatus, status, col);
                break;
        }
    }

    private static void SetLabel(TextMeshProUGUI lbl, string text, Color col)
    {
        if (lbl == null) return;
        lbl.text  = text;
        lbl.color = col;
    }

    private void SetStatus(string msg)
    {
        if (lblStatus != null) lblStatus.text = msg;
    }

    // ── Button callbacks ──────────────────────────────────────────────────────

    private void OnStartGame()
    {
        if (btnStartGame != null) btnStartGame.interactable = false;
        SetStatus("Starting game...");
        LobbyNetworkBridge.Instance?.RequestStartGameServerRpc();
    }

    private void OnCopyIP()
    {
        GUIUtility.systemCopyBuffer = _localIP;
        if (lblRoomIP != null) lblRoomIP.text = $"Your IP:  {_localIP}  ✔ Copied!";
        Invoke(nameof(ResetIPLabel), 2f);
    }

    private void ResetIPLabel()
    {
        if (lblRoomIP != null) lblRoomIP.text = $"Your IP:  {_localIP}  (share with friends)";
    }

    private void OnLeaveRoom()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.Shutdown();

        UnityEngine.SceneManagement.SceneManager.LoadScene(startMenuSceneName);
    }

    // ── Legacy support (called by LobbyManager.RefreshLobbyUIRpc) ────────────
    //    Keeps LobbyManager compiling without changes to its RefreshLobbyUIRpc.

    public void SetPlayerCount(int count)
    {
        if (lblPlayerCount != null) lblPlayerCount.text = $"Players:  {count} / 4";
    }

    public void SetStatus_Legacy(string msg) => SetStatus(msg);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetLocalIPAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
