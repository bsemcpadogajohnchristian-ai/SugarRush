// HUDManager.cs — Sugar Rush
//
// ── SNIPER SCOPE OVERLAY ADDED ────────────────────────────────────────────────
//   • New Inspector field: scopeOverlayPanel (GameObject).
//     Assign the full-screen scope PNG Image GameObject here.
//   • New public method: SetScopeOverlay(bool) — shows/hides the scope panel
//     and also hides ammoPanel + smokeGrenadePanel for a clean scoped view.
//   • ResetAndInitialize() now subscribes sc.onScopeChanged → OnScopeChanged
//     when the local player is a Shooter.
//   • Cleanup() now unsubscribes onScopeChanged and clears the overlay.
//   • SetScopeOverlay(false) is called in Start() so the overlay always starts hidden.
//
// ── ALL 3 SKILL SLOTS UNIFIED ─────────────────────────────────────────────────
//   Magnet, Super Speed, and Decoy now all share the SAME abilityPanel.
//   The old separate magnetPanel field has been removed — SkillSlot_Magnet
//   is simply a child of AbilityPanel, exactly like SkillSlot_SuperSpeed and
//   SkillSlot_Decoy. No extra show/hide logic is needed.
//
//   WHAT CHANGED vs the previous version:
//     • Removed [Header("Magnet (Collector)")] section and magnetPanel field.
//     • magnetFill, magnetTimerText, magnetActiveIndicator moved into the
//       unified [Header("Abilities (Collector)")] section.
//     • Start() no longer calls magnetPanel.SetActive(false) separately.
//     • ResetAndInitialize() no longer calls magnetPanel.SetActive(!isShooter).
//     • Cleanup() no longer references magnetPanel.
//     • All three skill bars (Magnet, SuperSpeed, Decoy) use identical visual
//       rules driven by their Refresh*Bar() methods (unchanged):
//
//   SHARED VISUAL RULES (all three bars):
//     ACTIVE / READY   → SkillReadyColor  (pink), fillAmount = 1.0, timer hidden
//     ON COOLDOWN      → SkillCooldownColor (grey), fillAmount fills 0→1,
//                        timer shows integer seconds remaining
//
// ── Inspector re-wiring required ─────────────────────────────────────────────
//   In the Inspector, drag the three Image/TMP components that were previously
//   assigned to the old "Magnet" header into the new "Abilities (Collector)"
//   header fields: magnetFill, magnetTimerText, magnetActiveIndicator.
//   Unassign anything that was in the old magnetPanel slot — it no longer exists.

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

    // ── HEART HP DISPLAY ──────────────────────────────────────────────────────
    [Header("Health — Heart Display")]
    [Tooltip("The FILL heart Image. Must have:\n" +
             "  • Image Type  = Filled\n" +
             "  • Fill Method = Vertical\n" +
             "  • Fill Origin = Bottom\n" +
             "Its fillAmount (0–1) is driven by current HP / max HP.")]
    public Image heartFillImage;

    [Tooltip("The OUTLINE heart Image. Always visible — never changed by code.\n" +
             "Stack it on top of heartFillImage in the hierarchy so the outline\n" +
             "is always drawn over the fill.")]
    public Image heartOutlineImage;

    [Tooltip("TMP label that shows current HP, e.g. \"85\".\n" +
             "Updated every time the player's HP changes.")]
    public TextMeshProUGUI healthText;

    [Header("Health — Heart Sprites")]
    public Sprite shooterHeartFill;
    public Sprite shooterHeartOutline;
    public Sprite collectorHeartFill;
    public Sprite collectorHeartOutline;
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Ammo (Shooter)")]
    public GameObject      ammoPanel;
    public TextMeshProUGUI ammoText;
    public TextMeshProUGUI reloadText;

    [Tooltip("Image that displays the current weapon's icon. " +
             "Place it inside AmmoPanel, to the left of the ammo text.")]
    public Image           weaponIconImage;

    [Header("Candy (Collector)")]
    public GameObject      candyPanel;
    public TextMeshProUGUI candyCountText;

    // ── ALL THREE SKILL SLOTS live inside this one panel ─────────────────────
    [Header("Abilities (Collector) — all three skill slots share this panel")]
    [Tooltip("Root panel that contains SkillSlot_SuperSpeed, SkillSlot_Decoy, " +
             "AND SkillSlot_Magnet as children. Shown for Collector, hidden for Shooter.")]
    public GameObject      abilityPanel;

    // Super Speed
    [Tooltip("Bar_Fill Image inside SkillSlot_SuperSpeed.")]
    public Image           superSpeedFill;
    [Tooltip("Skill_CD TMP label inside SkillSlot_SuperSpeed.")]
    public TextMeshProUGUI superSpeedTimerText;

    // Decoy
    [Tooltip("Bar_Fill Image inside SkillSlot_Decoy.")]
    public Image           decoyFill;
    [Tooltip("Skill_CD TMP label inside SkillSlot_Decoy.")]
    public TextMeshProUGUI decoyTimerText;

    // Magnet — now just another slot inside abilityPanel, not a separate panel
    [Tooltip("Bar_Fill Image inside SkillSlot_Magnet (child of AbilityPanel).")]
    public Image           magnetFill;
    [Tooltip("Skill_CD TMP label inside SkillSlot_Magnet.")]
    public TextMeshProUGUI magnetTimerText;
    [Tooltip("Optional: extra glow/indicator object shown while magnet is active. " +
             "Leave empty if not used.")]
    public GameObject      magnetActiveIndicator;

    [Header("Smoke Grenade (Shooter)")]
    [Tooltip("Root GameObject that groups the smoke grenade HUD elements. " +
             "Shown when the local player is a Shooter.")]
    public GameObject      smokeGrenadePanel;

    [Tooltip("Image component with Image Type = Filled. Fill Amount drives the cooldown.")]
    public Image           smokeGrenadeFill;

    [Tooltip("TMP label showing remaining cooldown as an integer. Hidden when ready.")]
    public TextMeshProUGUI smokeGrenadeTimerText;

    [Tooltip("TMP label showing current charge count, e.g. 'x2'.")]
    public TextMeshProUGUI smokeChargesText;

    [Header("Smoke Screen Overlay")]
    [Tooltip("Full-screen Image shown when the local player is inside a smoke cloud.")]
    public GameObject smokeOverlayPanel;

    [Header("Scope Overlay (Shooter — Sniper)")]
    [Tooltip("Full-screen panel shown when the local Sniper player is scoped in. " +
             "Create a Canvas Image using the scope_overlay.png sprite, stretched to fill screen, " +
             "then drag that Image's parent GameObject here.")]
    public GameObject scopeOverlayPanel;

    [Tooltip("Seconds to wait after scoping before the overlay appears. " +
             "Match this to your FOV zoom-in duration so the overlay fades in after the zoom. " +
             "Set to 0 for instant. Default 0.15 s works well with the built-in SmoothFOV.")]
    [Range(0f, 1f)]
    public float scopeOverlayDelay = 0.15f;

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
    private float               _magnetMax     = 25f;
    private Coroutine           _scopeOverlayCoroutine; // tracks the show-overlay delay

    // Skill state cache
    private float _superSpeedCD;
    private bool  _superSpeedIsActive;
    private float _decoyCD;
    private int   _decoyChargesLocal;
    private float _magnetCD;
    private bool  _magnetIsActive;

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

        if (smokeOverlayPanel     != null) smokeOverlayPanel.SetActive(false);
        if (scopeOverlayPanel     != null) scopeOverlayPanel.SetActive(false);
        if (smokeGrenadePanel     != null) smokeGrenadePanel.SetActive(false);
        if (magnetActiveIndicator != null) magnetActiveIndicator.SetActive(false);
        // NOTE: abilityPanel visibility is set in ResetAndInitialize — not here.
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    // ── Initialize ────────────────────────────────────────────────────────────

    public void ResetAndInitialize(PlayerStats ps)
    {
        Cleanup();

        _player = ps;
        ps.onHealthChanged.AddListener(UpdateHealth);
        UpdateHealth(ps.currentHP.Value, ps.maxHP > 0f ? ps.maxHP : ps.shooterMaxHP);

        ApplyHeartSprites(ps.role.Value);
        ps.role.OnValueChanged += (_, next) => ApplyHeartSprites(next);

        WireGameManager();

        bool isShooter = ps.role.Value == PlayerRole.Shooter;

        // All three skill slots live inside abilityPanel — one toggle does all.
        ammoPanel?.SetActive(isShooter);
        candyPanel?.SetActive(!isShooter);
        abilityPanel?.SetActive(!isShooter);
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
                sc.onScopeChanged.AddListener(OnScopeChanged);   // ← NEW

                UpdateSmokeCooldown(0f);
                UpdateSmokeCharges(sc.smokeMaxCharges);
                SetScopeOverlay(false);                           // ← NEW: ensure hidden on init
            }
        }
        else
        {
            CollectorController cc = ps.GetComponent<CollectorController>();
            if (cc != null)
            {
                _trackedCollector  = cc;
                _superSpeedMax     = cc.superSpeedCooldown;
                _decoyMax          = cc.decoyCooldown;
                _magnetMax         = cc.magnetCooldown;

                // Reset cached state
                _superSpeedCD      = 0f;
                _superSpeedIsActive = false;
                _decoyCD           = 0f;
                _decoyChargesLocal  = cc.decoyMaxCharges;
                _magnetCD          = 0f;
                _magnetIsActive    = false;

                cc.onCandyCountChanged.AddListener(UpdateCandyCount);
                cc.onSuperSpeedCooldown.AddListener(UpdateSuperSpeedCD);
                cc.onSuperSpeedActiveChanged.AddListener(UpdateSuperSpeedActive);
                cc.onDecoyCooldown.AddListener(UpdateDecoyCD);
                cc.onDecoyChargesChanged.AddListener(UpdateDecoyCharges);
                cc.onMagnetCooldown.AddListener(UpdateMagnetCD);
                cc.onMagnetActiveChanged.AddListener(UpdateMagnetActive);

                UpdateCandyCount(0);

                // Draw all three bars in their initial "ready" state
                RefreshSuperSpeedBar();
                RefreshDecoyBar();
                RefreshMagnetBar();
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

        if (weaponIconImage != null)
        {
            weaponIconImage.sprite  = w.weaponIcon;
            weaponIconImage.enabled = w.weaponIcon != null;
        }
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
        smokeGrenadePanel?.SetActive(true);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void Cleanup()
    {
        StopAllCoroutines();
        _ngmWired = false;

        // Cancel any pending scope overlay delay.
        if (_scopeOverlayCoroutine != null)
        {
            StopCoroutine(_scopeOverlayCoroutine);
            _scopeOverlayCoroutine = null;
        }

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
            _trackedCollector.onSuperSpeedActiveChanged.RemoveListener(UpdateSuperSpeedActive);
            _trackedCollector.onDecoyCooldown.RemoveListener(UpdateDecoyCD);
            _trackedCollector.onDecoyChargesChanged.RemoveListener(UpdateDecoyCharges);
            _trackedCollector.onMagnetCooldown.RemoveListener(UpdateMagnetCD);
            _trackedCollector.onMagnetActiveChanged.RemoveListener(UpdateMagnetActive);
            if (magnetActiveIndicator != null) magnetActiveIndicator.SetActive(false);
            _trackedCollector = null;
        }

        if (_trackedShooterForSmoke != null)
        {
            _trackedShooterForSmoke.onSmokeGrenadeCooldown.RemoveListener(UpdateSmokeCooldown);
            _trackedShooterForSmoke.onSmokeChargesChanged.RemoveListener(UpdateSmokeCharges);
            _trackedShooterForSmoke.onScopeChanged.RemoveListener(OnScopeChanged); // ← NEW
            _trackedShooterForSmoke = null;
        }

        SetScopeOverlay(false); // ← NEW: always clear on cleanup (respawn, role change)

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
        if (teamAScoreText) teamAScoreText.text = a.ToString();
        if (teamBScoreText) teamBScoreText.text = b.ToString();
    }

    private void UpdateTimer(float secs)
    {
        if (!timerText) return;
        int m = Mathf.FloorToInt(secs / 60f);
        int s = Mathf.FloorToInt(secs % 60f);
        timerText.text  = $"{m:00}:{s:00}";
        timerText.color = secs <= 30f ? Color.Lerp(Color.red, Color.white, secs / 30f) : Color.white;
    }

    // ── HEART SPRITE SWAP ─────────────────────────────────────────────────────

    private void ApplyHeartSprites(PlayerRole role)
    {
        bool isShooter = role == PlayerRole.Shooter;
        if (heartFillImage    != null) heartFillImage.sprite    = isShooter ? shooterHeartFill    : collectorHeartFill;
        if (heartOutlineImage != null) heartOutlineImage.sprite = isShooter ? shooterHeartOutline : collectorHeartOutline;
    }

    // ── HEART HP UPDATE ───────────────────────────────────────────────────────

    private void UpdateHealth(float cur, float max)
    {
        if (max <= 0f) return;
        float fraction = Mathf.Clamp01(cur / max);
        if (heartFillImage != null) heartFillImage.fillAmount = fraction;
        if (healthText     != null) healthText.text = $"{Mathf.CeilToInt(cur)}";
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateAmmo(int cur, int total)
    {
        if (ammoText) ammoText.text = $"{cur}";
    }

    private void UpdateCandyCount(int count)
    {
        if (candyCountText) candyCountText.text = $"Candy: {count}";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SKILL BAR LOGIC — identical visual rules for all three skills:
    //
    //  ACTIVE / READY  →  SkillReadyColor  (pink),  fillAmount = 1,  timer hidden
    //  ON COOLDOWN     →  SkillCooldownColor (grey), fillAmount 0→1, timer shows secs
    // ══════════════════════════════════════════════════════════════════════════

    // ── SUPER SPEED ───────────────────────────────────────────────────────────

    private void UpdateSuperSpeedCD(float remaining)
    {
        _superSpeedCD = remaining;
        RefreshSuperSpeedBar();
    }

    private void UpdateSuperSpeedActive(bool isActive)
    {
        _superSpeedIsActive = isActive;
        RefreshSuperSpeedBar();
    }

    private void RefreshSuperSpeedBar()
    {
        // While ACTIVE: bar is pink and full.
        // While COOLDOWN: bar is grey and fills 0→1.
        // When READY: bar is pink and full.
        bool onCooldown = _superSpeedCD > 0.05f && !_superSpeedIsActive;

        if (superSpeedFill != null)
        {
            if (_superSpeedIsActive || !onCooldown)
                superSpeedFill.fillAmount = 1f;
            else
                superSpeedFill.fillAmount = _superSpeedMax > 0f
                    ? 1f - (_superSpeedCD / _superSpeedMax)
                    : 0f;
        }

        if (superSpeedTimerText != null)
        {
            superSpeedTimerText.gameObject.SetActive(onCooldown);
            superSpeedTimerText.text = onCooldown ? Mathf.CeilToInt(_superSpeedCD).ToString() : "";
        }
    }

    // ── DECOY ─────────────────────────────────────────────────────────────────

    private void UpdateDecoyCD(float remaining)
    {
        _decoyCD = remaining;
        RefreshDecoyBar();
    }

    private void UpdateDecoyCharges(int charges)
    {
        _decoyChargesLocal = charges;
        RefreshDecoyBar();
    }

    private void RefreshDecoyBar()
    {
        // HAS CHARGES: bar is pink and full.
        // COOLDOWN:    bar is grey and fills 0→1.
        bool onCooldown = _decoyCD > 0.05f;
        bool hasCharges = _decoyChargesLocal > 0;

        if (decoyFill != null)
        {
            if (hasCharges || !onCooldown)
                decoyFill.fillAmount = 1f;
            else
                decoyFill.fillAmount = _decoyMax > 0f
                    ? 1f - (_decoyCD / _decoyMax)
                    : 0f;
        }

        if (decoyTimerText != null)
        {
            decoyTimerText.gameObject.SetActive(onCooldown);
            decoyTimerText.text = onCooldown ? Mathf.CeilToInt(_decoyCD).ToString() : "";
        }
    }

    // ── MAGNET ────────────────────────────────────────────────────────────────

    private void UpdateMagnetCD(float remaining)
    {
        _magnetCD = remaining;
        RefreshMagnetBar();
    }

    private void UpdateMagnetActive(bool isActive)
    {
        _magnetIsActive = isActive;
        if (magnetActiveIndicator != null)
            magnetActiveIndicator.SetActive(isActive);
        RefreshMagnetBar();
    }

    private void RefreshMagnetBar()
    {
        // While ACTIVE: bar is pink and full (magnet is pulling — ability running).
        // While COOLDOWN: bar is grey and fills 0→1.
        // When READY: bar is pink and full.
        bool onCooldown = _magnetCD > 0.05f && !_magnetIsActive;

        if (magnetFill != null)
        {
            if (_magnetIsActive || !onCooldown)
                magnetFill.fillAmount = 1f;
            else
                magnetFill.fillAmount = _magnetMax > 0f
                    ? 1f - (_magnetCD / _magnetMax)
                    : 0f;
        }

        if (magnetTimerText != null)
        {
            magnetTimerText.gameObject.SetActive(onCooldown);
            magnetTimerText.text = onCooldown ? Mathf.CeilToInt(_magnetCD).ToString() : "";
        }
    }

    // ── SMOKE GRENADE BAR (Shooter) ───────────────────────────────────────────

    private void UpdateSmokeCooldown(float remaining)
    {
        bool onCooldown = remaining > 0.05f;

        if (smokeGrenadeFill != null)
        {
            smokeGrenadeFill.fillAmount = _smokeMax > 0f
                ? onCooldown ? 1f - (remaining / _smokeMax) : 1f
                : 1f;
        }

        if (smokeGrenadeTimerText != null)
        {
            smokeGrenadeTimerText.gameObject.SetActive(onCooldown);
            smokeGrenadeTimerText.text = onCooldown ? Mathf.CeilToInt(remaining).ToString() : "";
        }
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

    // ── SCOPE OVERLAY (Sniper) ────────────────────────────────────────────────

    /// <summary>
    /// Subscribed to ShooterController.onScopeChanged.
    /// When scoping IN:  waits scopeOverlayDelay seconds before showing the overlay,
    ///                   so it appears after the FOV zoom animation finishes.
    /// When scoping OUT: cancels any pending delay and hides the overlay immediately.
    /// </summary>
    private void OnScopeChanged(bool scoped)
    {
        // Always cancel an in-flight delay first.
        if (_scopeOverlayCoroutine != null)
        {
            StopCoroutine(_scopeOverlayCoroutine);
            _scopeOverlayCoroutine = null;
        }

        if (scoped && scopeOverlayDelay > 0f)
            _scopeOverlayCoroutine = StartCoroutine(ShowScopeOverlayDelayed());
        else
            SetScopeOverlay(scoped);
    }

    private IEnumerator ShowScopeOverlayDelayed()
    {
        yield return new WaitForSeconds(scopeOverlayDelay);
        SetScopeOverlay(true);
        _scopeOverlayCoroutine = null;
    }

    /// <summary>
    /// Shows or hides the full-screen scope PNG overlay.
    /// Also hides/restores the ammo and smoke-grenade HUD panels so the
    /// scoped view is clean — only the scope graphic is visible.
    /// </summary>
    public void SetScopeOverlay(bool isScoped)
    {
        if (scopeOverlayPanel != null)
            scopeOverlayPanel.SetActive(isScoped);

        // Hide other shooter HUD elements while scoped for a clean view
        if (ammoPanel         != null) ammoPanel.SetActive(!isScoped);
        if (smokeGrenadePanel != null) smokeGrenadePanel.SetActive(!isScoped);
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