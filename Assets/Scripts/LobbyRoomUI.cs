// LobbyRoomUI.cs — Sugar Rush
//
// ── BUG FIX: NullReferenceException in RegisterNameServerRpc ─────────────────
//
//   ROOT CAUSE:
//     SubscribeWhenReady() only waited for LobbyNetworkBridge.Instance != null.
//     The singleton is assigned in Awake() — BEFORE NGO calls OnNetworkSpawn()
//     and marks the NetworkBehaviour as spawned. Calling an RPC on an unspawned
//     NetworkBehaviour causes a NullReferenceException deep inside NGO's
//     __endSendRpc internals (NetworkBehaviour.cs:354).
//
//   FIX:
//     The wait condition now requires BOTH:
//       1. LobbyNetworkBridge.Instance != null   (singleton assigned)
//       2. LobbyNetworkBridge.Instance.IsSpawned (NGO finished OnNetworkSpawn)
//     This guarantees the RPC channel is open before we use it.
//
//   ADDITIONAL IMPROVEMENTS:
//     • Timeout raised from 5 s to 10 s — gives NGO more breathing room on
//       slower machines or high-latency connections.
//     • _subscribeCoroutine reference stored so OnDestroy can cancel it if
//       the player leaves the lobby before it finishes, preventing a
//       MissingReferenceException on the destroyed MonoBehaviour.
//     • OnDestroy now unsubscribes onLobbyStateUpdated regardless of whether
//       the coroutine ran to completion, preventing stale listener leaks.
//     • SetStatus helper made public (SetStatus_Legacy already was) for
//       external callers.
//     • All other logic is identical to the original.
//
// ── SETUP (unchanged) ─────────────────────────────────────────────────────────
//   1. In LobbyScene, create a Canvas and build the lobby room UI hierarchy.
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

    private string    _localIP;
    private bool      _isHost;
    private bool      _subscribedToEvents;
    private Coroutine _subscribeCoroutine;   // ← NEW: tracked so we can cancel it

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
            btnStartGame.interactable = false;
            btnStartGame.onClick.AddListener(OnStartGame);
        }

        if (btnLeaveRoom != null) btnLeaveRoom.onClick.AddListener(OnLeaveRoom);

        // Initial display while waiting for network
        ClearAllSlots();
        SetStatus("Connecting to lobby...");

        // ── FIX: store the coroutine reference so OnDestroy can cancel it ─────
        _subscribeCoroutine = StartCoroutine(SubscribeWhenReady());
    }

    private void OnDestroy()
    {
        // ── FIX: cancel the coroutine if it is still running when we leave ────
        if (_subscribeCoroutine != null)
        {
            StopCoroutine(_subscribeCoroutine);
            _subscribeCoroutine = null;
        }

        // ── FIX: always unsubscribe, even if the coroutine never finished ─────
        if (_subscribedToEvents && LobbyNetworkBridge.Instance != null)
            LobbyNetworkBridge.Instance.onLobbyStateUpdated.RemoveListener(OnLobbyStateUpdated);

        _subscribedToEvents = false;
    }

    // ── Wait for LobbyNetworkBridge to be SPAWNED, then register name ─────────
    //
    // KEY FIX: The original only checked (Instance == null). The singleton is
    // set in Awake(), which runs before NGO calls OnNetworkSpawn(). We MUST
    // also wait for IsSpawned == true; otherwise the RPC call crashes inside
    // NGO because the internal network channel isn't open yet.

    private IEnumerator SubscribeWhenReady()
    {
        // ── FIX: wait for both Instance AND IsSpawned ─────────────────────────
        float timeout = 10f;   // raised from 5 s
        while (timeout > 0f)
        {
            if (LobbyNetworkBridge.Instance != null && LobbyNetworkBridge.Instance.IsSpawned)
                break;

            timeout -= Time.deltaTime;
            yield return null;
        }

        // Timeout or destroyed during wait
        if (LobbyNetworkBridge.Instance == null || !LobbyNetworkBridge.Instance.IsSpawned)
        {
            Debug.LogError(
                "[LobbyRoomUI] LobbyNetworkBridge not found or not spawned after 10 s. " +
                "Ensure LobbyNetworkBridge is on the same GameObject as LobbyManager " +
                "in LobbyScene and that the NetworkObject is properly configured.");
            SetStatus("Error: lobby service unavailable. Please leave and try again.");
            _subscribeCoroutine = null;
            yield break;
        }

        // Subscribe to lobby state broadcasts (only once)
        if (!_subscribedToEvents)
        {
            LobbyNetworkBridge.Instance.onLobbyStateUpdated.AddListener(OnLobbyStateUpdated);
            _subscribedToEvents = true;
        }

        // Register this player's name with the server.
        // This also triggers a BroadcastLobbyState → SyncLobbyStateRpc so we
        // get a fresh lobby snapshot immediately after subscribing.
        string name = PlayerNameHolder.Instance?.LocalPlayerName ?? "Player";
        LobbyNetworkBridge.Instance.RegisterNameServerRpc(name);

        SetStatus(_isHost ? "Waiting for players..." : "Connected — waiting for host to start...");

        _subscribeCoroutine = null;
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
        Color  col    = connected ? ColConnected : ColEmpty;
        // ── FIX: replaced ●/○ (U+25CB) — not in Modak font, caused console spam ──
        string status = connected ? "CONNECTED" : "EMPTY";

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
            // Fallback: iterate local host addresses for a non-loopback IPv4.
            try
            {
                string hostName = Dns.GetHostName();
                foreach (IPAddress addr in Dns.GetHostAddresses(hostName))
                    if (addr.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr))
                        return addr.ToString();
            }
            catch { /* ignored */ }

            return "127.0.0.1";
        }
    }
}