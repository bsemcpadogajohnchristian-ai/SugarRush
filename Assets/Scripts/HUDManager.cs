// HUDManager.cs — Sugar Rush
//
// ── MAGNET ABILITY ADDED ───────────────────────────────────────────────────────
//   New Collector HUD section for the candy magnet (R key ability).
//
//   WHAT CHANGED:
//     • New [Header("Magnet (Collector)")] Inspector fields:
//         magnetPanel          — root GameObject shown only for Collectors
//         magnetFill           — Image (Type = Filled) driven by cooldown
//         magnetTimerText      — TMP label: "X.Xs" or "Ready!"
//         magnetActiveIndicator — GameObject shown while magnet is running
//     • _magnetMax             — private float caching magnetCooldown value
//     • ResetAndInitialize()   — wires cc.onMagnetCooldown + cc.onMagnetActiveChanged
//                                 in the Collector branch; also calls initial
//                                 UpdateMagnetCD(0f) so the label shows "Ready!".
//     • Cleanup()              — unsubscribes both magnet events, hides indicator.
//     • UpdateMagnetCD()       — fills the cooldown image + updates label.
//     • UpdateMagnetActive()   — shows/hides the active indicator.
//     All other logic is identical to the original.

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

    // ── Magnet (Collector) ────────────────────────────────────────────────────
    [Header("Magnet (Collector)")]
    [Tooltip("Root GameObject that groups all magnet HUD elements. " +
             "Shown only for the Collector role.")]
    public GameObject      magnetPanel;

    [Tooltip("Image component with Image Type = Filled. " +
             "Fill Amount is driven by the cooldown (1 = cooling down, 0 = ready).")]
    public Image           magnetFill;

    [Tooltip("TMP label: shows remaining cooldown seconds or 'Ready!' when available.")]
    public TextMeshProUGUI magnetTimerText;

    [Tooltip("GameObject shown (activated) while the magnet is actively pulling candy. " +
             "Hide it by default — this script shows/hides it at runtime.")]
    public GameObject      magnetActiveIndicator;
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Smoke Grenade (Shooter)")]
    [Tooltip("Root GameObject that groups the smoke grenade HUD elements. " +
             "Shown when the local player is a Shooter.")]
    public GameObject      smokeGrenadePanel;

    [Tooltip("Image component with Image Type = Filled. Fill Amount drives the cooldown.")]
    public Image           smokeGrenadeFill;

    [Tooltip("TMP label showing remaining cooldown seconds, or 'Ready!' when available.")]
    public TextMeshProUGUI smokeGrenadeTimerText;

    [Tooltip("TMP label showing current charge count, e.g. 'x2'.")]
    public TextMeshProUGUI smokeChargesText;

    [Header("Smoke Screen Overlay")]
    [Tooltip("Full-screen Image shown when the local player is inside a smoke cloud.")]
    public GameObject smokeOverlayPanel;

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
    private ShooterController   _trackedShooterForSmoke;
    private float               _superSpeedMax = 30f;
    private float               _decoyMax      = 20f;
    private float               _smokeMax      = 25f;
    private float               _magnetMax     = 25f;   // ← NEW

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

        if (smokeOverlayPanel   != null) smokeOverlayPanel.SetActive(false);
        if (smokeGrenadePanel   != null) smokeGrenadePanel.SetActive(false);
        if (magnetPanel         != null) magnetPanel.SetActive(false);          // ← NEW
        if (magnetActiveIndicator != null) magnetActiveIndicator.SetActive(false); // ← NEW
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
        magnetPanel?.SetActive(!isShooter);        // ← NEW: show magnet panel for Collector
        smokeGrenadePanel?.SetActive(isShooter);

        if (isShooter)
        {
            ShooterController sc = ps.GetComponent<ShooterController>();
            WeaponBase w = sc?.GetCurrentWeapon();
            if (w != null) WireWeapon(w);

            if (inventoryUI != null && sc != null) inventoryUI.Initialize(sc);

            if (sc != null)
            {
                _trackedShooterForSmoke = sc;
                _smokeMax = sc.smokeGrenadeCooldown;

                sc.onSmokeGrenadeCooldown.AddListener(UpdateSmokeCooldown);
                sc.onSmokeChargesChanged.AddListener(UpdateSmokeCharges);

                UpdateSmokeCooldown(0f);
                UpdateSmokeCharges(sc.smokeMaxCharges);
            }
        }
        else
        {
            CollectorController cc = ps.GetComponent<CollectorController>();
            if (cc != null)
            {
                _trackedCollector = cc;
                _superSpeedMax    = cc.superSpeedCooldown;
                _decoyMax         = cc.decoyCooldown;
                _magnetMax        = cc.magnetCooldown;    // ← NEW

                cc.onCandyCountChanged.AddListener(UpdateCandyCount);
                cc.onSuperSpeedCooldown.AddListener(UpdateSuperSpeedCD);
                cc.onDecoyCooldown.AddListener(UpdateDecoyCD);

                // ── NEW: wire magnet events ───────────────────────────────────
                cc.onMagnetCooldown.AddListener(UpdateMagnetCD);
                cc.onMagnetActiveChanged.AddListener(UpdateMagnetActive);

                UpdateCandyCount(0);
                UpdateMagnetCD(0f);          // show "Ready!" immediately
                UpdateMagnetActive(false);   // hide indicator
                // ─────────────────────────────────────────────────────────────
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
        magnetPanel?.SetActive(false);     // ← NEW: hide magnet for shooters
        smokeGrenadePanel?.SetActive(true);
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

            // ── NEW: unsubscribe magnet events ────────────────────────────────
            _trackedCollector.onMagnetCooldown.RemoveListener(UpdateMagnetCD);
            _trackedCollector.onMagnetActiveChanged.RemoveListener(UpdateMagnetActive);
            if (magnetActiveIndicator != null) magnetActiveIndicator.SetActive(false);
            // ─────────────────────────────────────────────────────────────────

            _trackedCollector = null;
        }

        if (_trackedShooterForSmoke != null)
        {
            _trackedShooterForSmoke.onSmokeGrenadeCooldown.RemoveListener(UpdateSmokeCooldown);
            _trackedShooterForSmoke.onSmokeChargesChanged.RemoveListener(UpdateSmokeCharges);
            _trackedShooterForSmoke = null;
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

    // ── Magnet HUD ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by CollectorController.onMagnetCooldown every frame during countdown.
    /// remaining == 0 signals "Ready!".
    /// </summary>
    private void UpdateMagnetCD(float remaining)
    {
        if (magnetFill)
            magnetFill.fillAmount = _magnetMax > 0f ? remaining / _magnetMax : 0f;

        if (magnetTimerText)
            magnetTimerText.text = remaining > 0f ? $"{remaining:F1}s" : "Ready!";
    }

    /// <summary>
    /// Called by CollectorController.onMagnetActiveChanged when the magnet
    /// starts or stops. Shows/hides the active indicator.
    /// </summary>
    private void UpdateMagnetActive(bool isActive)
    {
        if (magnetActiveIndicator != null)
            magnetActiveIndicator.SetActive(isActive);
    }

    // ── Smoke grenade UI ──────────────────────────────────────────────────────

    private void UpdateSmokeCooldown(float remaining)
    {
        if (smokeGrenadeFill)
            smokeGrenadeFill.fillAmount = _smokeMax > 0f ? remaining / _smokeMax : 0f;

        if (smokeGrenadeTimerText)
            smokeGrenadeTimerText.text = remaining > 0f ? $"{remaining:F1}s" : "Ready!";
    }

    private void UpdateSmokeCharges(int charges)
    {
        if (smokeChargesText)
            smokeChargesText.text = $"x{charges}";
    }

    public void SetSmokeOverlay(bool isInside)
    {
        if (smokeOverlayPanel != null)
            smokeOverlayPanel.SetActive(isInside);
    }

    // ─────────────────────────────────────────────────────────────────────────

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