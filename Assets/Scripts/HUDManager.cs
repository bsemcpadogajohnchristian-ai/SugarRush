// HUDManager.cs — Sugar Rush (UPDATED: Smoke Grenade HUD)
//
// ── WHAT CHANGED ──────────────────────────────────────────────────────────
//   New Inspector fields (Collector section):
//     smokeGrenadePanel   — root GameObject containing smoke UI elements
//     smokeGrenadeFill    — Image (Filled type) for cooldown ring / bar
//     smokeGrenadeTimerText — TMP label showing remaining seconds or "Ready!"
//     smokeChargesText    — TMP label showing "x2" / "x1" / "x0"
//     smokeOverlayPanel   — full-screen Image shown when the player is inside smoke
//
//   ResetAndInitialize() wires CollectorController's new smoke events.
//   Cleanup() properly unsubscribes them.
//   New methods:
//     UpdateSmokeCooldown(float)  — drives smokeGrenadeFill + smokeGrenadeTimerText
//     UpdateSmokeCharges(int)     — drives smokeChargesText
//     SetSmokeOverlay(bool)       — shows/hides the screen-space smoke overlay
//                                   called by SmokeCloud via targeted RPC

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

    // ── NEW: Smoke Grenade UI ─────────────────────────────────────────────────
    [Header("Smoke Grenade (Collector)")]
    [Tooltip("Root GameObject that groups the smoke grenade HUD elements. " +
             "Enable/disable this alongside abilityPanel.")]
    public GameObject      smokeGrenadePanel;

    [Tooltip("Image component with Image Type = Filled. Fill Amount drives the cooldown.")]
    public Image           smokeGrenadeFill;

    [Tooltip("TMP label showing remaining cooldown seconds, or 'Ready!' when available.")]
    public TextMeshProUGUI smokeGrenadeTimerText;

    [Tooltip("TMP label showing current charge count, e.g. 'x2'.")]
    public TextMeshProUGUI smokeChargesText;

    [Header("Smoke Screen Overlay")]
    [Tooltip("Full-screen Image (or CanvasGroup) shown when the local player is " +
             "standing inside a smoke cloud. Set its alpha to about 0.55 in the " +
             "Inspector so it tints the screen without fully obscuring vision. " +
             "The Image color should be a dark grey/green smoke tint.")]
    public GameObject smokeOverlayPanel;
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Notifications")]
    public TextMeshProUGUI notificationText;

    [Header("Weapon Swap Zone")]
    public GameObject swapZonePrompt;

    [Header("Inventory")]
    public GameObject  inventoryPanel;
    public InventoryUI inventoryUI;

    // ── Private runtime ───────────────────────────────────────────────────────

    private PlayerStats         _player;
    private WeaponBase          _trackedWeapon;
    private CollectorController _trackedCollector;
    private float               _superSpeedMax = 30f;
    private float               _decoyMax      = 20f;
    private float               _smokeMax      = 25f;   // ← NEW: matches smokeGrenadeCooldown

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        if (notificationText) notificationText.text = "";
        reloadText?.gameObject.SetActive(false);
        SetInventoryVisible(false);

        // Smoke overlay starts hidden.
        if (smokeOverlayPanel != null) smokeOverlayPanel.SetActive(false);  // ← NEW
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    // ── Initialize ────────────────────────────────────────────────────────────

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
        smokeGrenadePanel?.SetActive(!isShooter);   // ← NEW

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
                _smokeMax         = cc.smokeGrenadeCooldown;   // ← NEW

                cc.onCandyCountChanged.AddListener(UpdateCandyCount);
                cc.onSuperSpeedCooldown.AddListener(UpdateSuperSpeedCD);
                cc.onDecoyCooldown.AddListener(UpdateDecoyCD);

                // ── NEW: wire smoke events ────────────────────────────────────
                cc.onSmokeGrenadeCooldown.AddListener(UpdateSmokeCooldown);
                cc.onSmokeChargesChanged.AddListener(UpdateSmokeCharges);
                // ─────────────────────────────────────────────────────────────

                UpdateCandyCount(0);
                UpdateSmokeCooldown(0f);                       // ← NEW (show "Ready!")
                UpdateSmokeCharges(cc.smokeMaxCharges);        // ← NEW (show full charges)
            }
        }
    }

    // ── Inventory ─────────────────────────────────────────────────────────────

    public void SetInventoryVisible(bool show)
    {
        if (inventoryPanel != null) inventoryPanel.SetActive(show);
    }

    // ── GameManager wiring ────────────────────────────────────────────────────

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

    // ── Weapon wiring ─────────────────────────────────────────────────────────

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
        smokeGrenadePanel?.SetActive(false);   // ← NEW
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

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

            // ── NEW: unsubscribe smoke events ─────────────────────────────────
            _trackedCollector.onSmokeGrenadeCooldown.RemoveListener(UpdateSmokeCooldown);
            _trackedCollector.onSmokeChargesChanged.RemoveListener(UpdateSmokeCharges);
            // ─────────────────────────────────────────────────────────────────

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

    // ── Score / Timer updates ─────────────────────────────────────────────────

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

    // ── NEW: Smoke grenade UI updates ─────────────────────────────────────────

    /// <summary>Called by CollectorController.onSmokeGrenadeCooldown.</summary>
    private void UpdateSmokeCooldown(float remaining)
    {
        if (smokeGrenadeFill)
            smokeGrenadeFill.fillAmount = _smokeMax > 0f ? remaining / _smokeMax : 0f;

        if (smokeGrenadeTimerText)
            smokeGrenadeTimerText.text = remaining > 0f ? $"{remaining:F1}s" : "Ready!";
    }

    /// <summary>Called by CollectorController.onSmokeChargesChanged.</summary>
    private void UpdateSmokeCharges(int charges)
    {
        if (smokeChargesText)
            smokeChargesText.text = $"x{charges}";
    }

    /// <summary>
    /// Called by SmokeCloud (via targeted RPC) when the local player enters or
    /// exits a smoke cloud. Activates the full-screen smoke overlay panel.
    /// </summary>
    public void SetSmokeOverlay(bool isInside)
    {
        if (smokeOverlayPanel != null)
            smokeOverlayPanel.SetActive(isInside);
    }

    // ─────────────────────────────────────────────────────────────────────────

    // ── Reload text ───────────────────────────────────────────────────────────

    private void ShowReloadText()
    {
        if (ammoText)   ammoText.gameObject.SetActive(false);
        if (reloadText) reloadText.gameObject.SetActive(true);
    }

    private void HideReloadText()
    {
        if (reloadText) reloadText.gameObject.SetActive(false);
        if (ammoText)   ammoText.gameObject.SetActive(true);
    }

    // ── Match result ──────────────────────────────────────────────────────────

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

    // ── Weapon changed ────────────────────────────────────────────────────────

    public void NotifyWeaponChanged(int index)
    {
        inventoryUI?.SetSelected(index);

        if (_player == null) return;

        ShooterController sc = _player.GetComponent<ShooterController>();
        WeaponBase w = sc?.GetCurrentWeapon();
        if (w == null || w == _trackedWeapon) return;

        if (_trackedWeapon != null)
        {
            if (_trackedWeapon.IsReloading())
                HideReloadText();

            _trackedWeapon.onAmmoChanged.RemoveListener(UpdateAmmo);
            _trackedWeapon.onReloadStart.RemoveListener(ShowReloadText);
            _trackedWeapon.onReloadEnd.RemoveListener(HideReloadText);
        }
        WireWeapon(w);
    }
}