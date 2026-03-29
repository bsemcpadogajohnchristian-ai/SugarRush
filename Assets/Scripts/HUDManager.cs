// HUDManager.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+

using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class HUDManager : MonoBehaviour
{
    public static HUDManager Instance { get; private set; }

    [Header("Scores & timer")]
    public TextMeshProUGUI teamAScoreText;
    public TextMeshProUGUI teamBScoreText;
    public TextMeshProUGUI timerText;

    [Header("Health")]
    public Slider          healthBar;
    public TextMeshProUGUI healthText;

    [Header("Ammo (Shooter)")]
    public GameObject      ammoPanel;
    public TextMeshProUGUI ammoText;
    public TextMeshProUGUI reloadText;

    [Header("Candy (Collector)")]
    public GameObject      candyPanel;
    public TextMeshProUGUI candyCountText;

    [Header("Abilities (Collector)")]
    public GameObject      abilityPanel;
    public Image           superSpeedFill;
    public Image           decoyFill;
    public TextMeshProUGUI superSpeedTimerText;
    public TextMeshProUGUI decoyTimerText;

    [Header("Notifications")]
    public TextMeshProUGUI notificationText;

    [Header("Weapon Swap Zone")]
    [Tooltip("Drag the SwapZonePrompt Text object from HUDCanvas here.")]
    public GameObject swapZonePrompt;

    [Header("Inventory")]
    [Tooltip("Drag the InventoryPanel root GameObject from HUDCanvas here.")]
    public GameObject  inventoryPanel;   // the root panel to show/hide
    public InventoryUI inventoryUI;      // the InventoryUI component on that panel

    // Cached references for cleanup
    private PlayerStats         _player;
    private WeaponBase          _trackedWeapon;
    private CollectorController _trackedCollector;
    private float               _superSpeedMax = 30f;
    private float               _decoyMax      = 20f;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        if (notificationText) notificationText.text = "";
        reloadText?.gameObject.SetActive(false);

        // Always start with the inventory panel hidden.
        // It will be shown only when the player presses B inside a SwapZone.
        SetInventoryVisible(false);
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    // ── Single entry point ────────────────────────────────────────────────────

    public void ResetAndInitialize(PlayerStats ps)
    {
        Cleanup();

        _player = ps;
        ps.onHealthChanged.AddListener(UpdateHealth);
        UpdateHealth(ps.currentHP.Value, ps.maxHP > 0f ? ps.maxHP : ps.shooterMaxHP);

        WireGameManager();

        bool isShooter = ps.role.Value == PlayerRole.Shooter;
        ammoPanel?.SetActive(isShooter);
        candyPanel?.SetActive(!isShooter);
        abilityPanel?.SetActive(!isShooter);

        if (isShooter)
        {
            ShooterController sc = ps.GetComponent<ShooterController>();
            WeaponBase w = sc?.GetCurrentWeapon();
            if (w != null) WireWeapon(w);

            if (inventoryUI != null && sc != null) inventoryUI.Initialize(sc);
        }
        else
        {
            CollectorController cc = ps.GetComponent<CollectorController>();
            if (cc != null)
            {
                _trackedCollector = cc;
                _superSpeedMax    = cc.superSpeedCooldown;
                _decoyMax         = cc.decoyCooldown;
                cc.onCandyCountChanged.AddListener(UpdateCandyCount);
                cc.onSuperSpeedCooldown.AddListener(UpdateSuperSpeedCD);
                cc.onDecoyCooldown.AddListener(UpdateDecoyCD);
                UpdateCandyCount(0);
            }
        }
    }

    // ── Inventory visibility ──────────────────────────────────────────────────

    /// <summary>
    /// Shows or hides the inventory panel on HUDCanvas.
    /// Called by ShooterController — never directly by InventoryUI or WeaponSwapZone.
    /// </summary>
    public void SetInventoryVisible(bool show)
    {
        if (inventoryPanel != null) inventoryPanel.SetActive(show);
    }

    // ── Game manager wiring ───────────────────────────────────────────────────

    private void WireGameManager()
    {
        NetworkGameManager ngm = NetworkGameManager.Instance;
        if (ngm == null)
        {
            StartCoroutine(RetryWireGameManager());
            return;
        }

        ngm.onScoreUpdated.RemoveListener(UpdateScore);
        ngm.onTimerUpdated.RemoveListener(UpdateTimer);
        ngm.onMatchOver.RemoveListener(ShowWinner);
        ngm.onMatchDraw.RemoveListener(ShowDraw);
        ngm.scoreTeamA.OnValueChanged    -= OnScoreAChanged;
        ngm.scoreTeamB.OnValueChanged    -= OnScoreBChanged;
        ngm.timeRemaining.OnValueChanged -= OnTimeChanged;

        ngm.onScoreUpdated.AddListener(UpdateScore);
        ngm.onTimerUpdated.AddListener(UpdateTimer);
        ngm.onMatchOver.AddListener(ShowWinner);
        ngm.onMatchDraw.AddListener(ShowDraw);
        _ngmWired = true;

        ngm.scoreTeamA.OnValueChanged    += OnScoreAChanged;
        ngm.scoreTeamB.OnValueChanged    += OnScoreBChanged;
        ngm.timeRemaining.OnValueChanged += OnTimeChanged;

        UpdateScore(ngm.scoreTeamA.Value, ngm.scoreTeamB.Value);
        UpdateTimer(ngm.timeRemaining.Value);

        if (ngm.timeRemaining.Value <= 0f || ngm.matchState.Value == MatchState.WaitingForPlayers)
            StartCoroutine(PollInitialValues(ngm));
    }

    private IEnumerator PollInitialValues(NetworkGameManager ngm)
    {
        float waited = 0f;
        while (waited < 10f)
        {
            yield return new WaitForSeconds(0.1f);
            waited += 0.1f;
            if (ngm == null) yield break;
            if (ngm.matchState.Value == MatchState.InProgress && ngm.timeRemaining.Value > 0f)
            {
                UpdateScore(ngm.scoreTeamA.Value, ngm.scoreTeamB.Value);
                UpdateTimer(ngm.timeRemaining.Value);
                yield break;
            }
        }
    }

    private bool _ngmWired;

    private IEnumerator RetryWireGameManager()
    {
        float waited = 0f;
        while (NetworkGameManager.Instance == null && waited < 5f)
        {
            yield return null;
            waited += Time.deltaTime;
        }
        if (NetworkGameManager.Instance != null && !_ngmWired)
            WireGameManager();
    }

    private void OnScoreAChanged(int prev, int next) => UpdateScore(next, NetworkGameManager.Instance?.scoreTeamB.Value ?? 0);
    private void OnScoreBChanged(int prev, int next) => UpdateScore(NetworkGameManager.Instance?.scoreTeamA.Value ?? 0, next);
    private void OnTimeChanged(float prev, float next) => UpdateTimer(next);

    public void ResetInitialization() => Cleanup();

    // ── Weapon wiring helpers ─────────────────────────────────────────────────

    private void WireWeapon(WeaponBase w)
    {
        _trackedWeapon = w;
        w.onAmmoChanged.AddListener(UpdateAmmo);
        w.onReloadStart.AddListener(ShowReloadText);
        w.onReloadEnd.AddListener(HideReloadText);
        UpdateAmmo(w.GetCurrentAmmo(), w.GetTotalAmmo());
    }

    public void RefreshShooterAmmo(ShooterController sc)
    {
        if (sc == null) return;
        WeaponBase w = sc.GetCurrentWeapon();
        if (w == null) return;

        if (_trackedWeapon != null)
        {
            _trackedWeapon.onAmmoChanged.RemoveListener(UpdateAmmo);
            _trackedWeapon.onReloadStart.RemoveListener(ShowReloadText);
            _trackedWeapon.onReloadEnd.RemoveListener(HideReloadText);
        }

        WireWeapon(w);
        ammoPanel?.SetActive(true);
        candyPanel?.SetActive(false);
        abilityPanel?.SetActive(false);
    }

    private void Cleanup()
    {
        StopAllCoroutines();
        _ngmWired = false;

        if (_player != null)
            _player.onHealthChanged.RemoveListener(UpdateHealth);

        if (_trackedWeapon != null)
        {
            _trackedWeapon.onAmmoChanged.RemoveListener(UpdateAmmo);
            _trackedWeapon.onReloadStart.RemoveListener(ShowReloadText);
            _trackedWeapon.onReloadEnd.RemoveListener(HideReloadText);
            _trackedWeapon = null;
        }

        if (_trackedCollector != null)
        {
            _trackedCollector.onCandyCountChanged.RemoveListener(UpdateCandyCount);
            _trackedCollector.onSuperSpeedCooldown.RemoveListener(UpdateSuperSpeedCD);
            _trackedCollector.onDecoyCooldown.RemoveListener(UpdateDecoyCD);
            _trackedCollector = null;
        }

        NetworkGameManager ngm = NetworkGameManager.Instance;
        if (ngm != null)
        {
            ngm.onScoreUpdated.RemoveListener(UpdateScore);
            ngm.onTimerUpdated.RemoveListener(UpdateTimer);
            ngm.onMatchOver.RemoveListener(ShowWinner);
            ngm.onMatchDraw.RemoveListener(ShowDraw);
            ngm.scoreTeamA.OnValueChanged    -= OnScoreAChanged;
            ngm.scoreTeamB.OnValueChanged    -= OnScoreBChanged;
            ngm.timeRemaining.OnValueChanged -= OnTimeChanged;
        }
    }

    // ── UI update methods ─────────────────────────────────────────────────────

    private void UpdateScore(int a, int b)
    {
        if (teamAScoreText) teamAScoreText.text = $"Team A: {a}";
        if (teamBScoreText) teamBScoreText.text = $"Team B: {b}";
    }

    private void UpdateTimer(float secs)
    {
        if (!timerText) return;
        int m = Mathf.FloorToInt(secs / 60f);
        int s = Mathf.FloorToInt(secs % 60f);
        timerText.text  = $"{m:00}:{s:00}";
        timerText.color = secs <= 30f ? Color.Lerp(Color.red, Color.white, secs / 30f) : Color.white;
    }

    private void UpdateHealth(float cur, float max)
    {
        if (max <= 0f) return;
        if (healthBar)  healthBar.value = cur / max;
        if (healthText) healthText.text = $"{Mathf.CeilToInt(cur)} / {Mathf.CeilToInt(max)}";
    }

    private void UpdateAmmo(int cur, int total)
    {
        if (ammoText) ammoText.text = $"{cur} / ∞";
    }

    private void UpdateCandyCount(int count)
    {
        if (candyCountText) candyCountText.text = $"Carrying: {count}";
    }

    private void UpdateSuperSpeedCD(float r)
    {
        if (superSpeedFill)      superSpeedFill.fillAmount = _superSpeedMax > 0f ? r / _superSpeedMax : 0f;
        if (superSpeedTimerText) superSpeedTimerText.text  = r > 0f ? $"{r:F1}s" : "Ready!";
    }

    private void UpdateDecoyCD(float r)
    {
        if (decoyFill)      decoyFill.fillAmount = _decoyMax > 0f ? r / _decoyMax : 0f;
        if (decoyTimerText) decoyTimerText.text  = r > 0f ? $"{r:F1}s" : "Ready!";
    }

    private void ShowReloadText()
    {
        if (ammoText)   ammoText.gameObject.SetActive(false); // hide ammo count while reloading
        if (reloadText) reloadText.gameObject.SetActive(true);
    }

    private void HideReloadText()
    {
        if (reloadText) reloadText.gameObject.SetActive(false);
        if (ammoText)   ammoText.gameObject.SetActive(true);  // restore ammo count when done
    }

    private void ShowWinner(TeamID w) =>
        ShowNotification(w == TeamID.TeamA ? "TEAM A WINS! 🍬" : "TEAM B WINS! 🍬", 5f);

    private void ShowDraw() => ShowNotification("DRAW!", 5f);

    public void ShowNotification(string msg, float dur = 2f)
    {
        if (!notificationText) return;
        notificationText.text = msg;
        CancelInvoke(nameof(ClearNote));
        Invoke(nameof(ClearNote), dur);
    }

    private void ClearNote() { if (notificationText) notificationText.text = ""; }

    public void ShowSwapZonePrompt(bool show)
    {
        if (swapZonePrompt != null) swapZonePrompt.SetActive(show);
    }

    /// <summary>
    /// Called by ShooterController.EquipWeapon every time a weapon is equipped —
    /// whether from clicking a card, pressing 1–4, or the initial equip on spawn.
    ///
    /// Does two things:
    ///   1. Highlights the correct card in the inventory UI.
    ///   2. Rewires ammo-change / reload events to the newly active weapon so the
    ///      ammo counter always reflects the gun currently in hand.
    ///
    /// Safe to call before the HUD is initialized (_player null-check handles that).
    /// </summary>
    public void NotifyWeaponChanged(int index)
    {
        // 1 — highlight the selected inventory card
        inventoryUI?.SetSelected(index);

        // 2 — rewire ammo display to the new weapon
        //     Skip if _player isn't set yet (called during prefab OnNetworkSpawn
        //     before ResetAndInitialize has run — RefreshShooterAmmo will handle it).
        if (_player == null) return;

        ShooterController sc = _player.GetComponent<ShooterController>();
        WeaponBase w = sc?.GetCurrentWeapon();
        if (w == null || w == _trackedWeapon) return; // same weapon — no rewire needed

        if (_trackedWeapon != null)
        {
            // Safety net: ShooterController.EquipWeapon calls CancelReload() before
            // reaching here, which fires onReloadEnd → HideReloadText() automatically.
            // This explicit check covers any edge-case call path that bypasses that flow.
            if (_trackedWeapon.IsReloading())
                HideReloadText();

            _trackedWeapon.onAmmoChanged.RemoveListener(UpdateAmmo);
            _trackedWeapon.onReloadStart.RemoveListener(ShowReloadText);
            _trackedWeapon.onReloadEnd.RemoveListener(HideReloadText);
        }
        WireWeapon(w);
    }
}
