// AllyIndicator.cs — Sugar Rush  (v16 — Definitive Professional Fix)
//
// ════════════════════════════════════════════════════════════════════════════
//  ROOT CAUSE — WHY INDICATORS WERE NOT ROTATING
// ════════════════════════════════════════════════════════════════════════════
//
//  FindActiveCamera() is a global scene search.  GameScene has a standalone
//  Camera object at the hierarchy root.  Camera.main returns whichever camera
//  is tagged "MainCamera" — if that is the scene camera rather than the local
//  PlayerCamera, every single AllyIndicator picks up the SAME wrong camera.
//
//  Consequences:
//    • All 4 indicators billboard toward the fixed scene camera → appear not
//      to rotate when the player moves or turns.
//    • Distance measured from the scene camera position → nearby allies read
//      as 54–70 units away and are hidden.
//    • Opacity flickers because _cam swaps between the two cameras.
//
//  FIX (v16):
//    Resolve the camera from PlayerSetup.playerCamera on the local player.
//    PlayerSetup already holds a direct reference to the exact correct camera.
//    No tag, no name string, no global search — O(1) lookup, always right.
//
//  BILLBOARD FIX (v16):
//    Changed from ProjectOnPlane(cam.forward) to the vector from the canvas
//    to the camera eye projected onto XZ.  LookRotation(toCamera) makes the
//    canvas +Z point directly at the camera regardless of pitch or height.
//
// ════════════════════════════════════════════════════════════════════════════
//  FULL VERSION HISTORY
// ════════════════════════════════════════════════════════════════════════════
//  v11  Shared-reference guard.
//  v12  Dirty-flag role poll; LateUpdate-driven visibility.
//  v13  Per-frame _localStats poll (zero blind-spot window).
//  v14  XZ distance metric; local team NV subscription; alpha fixes.
//  v15  Canvas detached from player hierarchy (NGO parent-corruption fix).
//  v16  Camera from PlayerSetup.playerCamera; face-toward-camera billboard;
//       roleIconImage validation fixed for post-detach hierarchy.
//
// ════════════════════════════════════════════════════════════════════════════
//  PREFAB SETUP  (unchanged from v15)
// ════════════════════════════════════════════════════════════════════════════
//  Player (root — PlayerStats + NetworkObject + PlayerSetup)
//  └── AllyIndicatorAnchor    Y ≈ 2.3–2.5
//      ├── AllyIndicator.cs   ← this script
//      └── IndicatorCanvas    (Canvas component)
//          └── Panel
//              └── RoleIcon   (Image component)

using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

[DisallowMultipleComponent]
[DefaultExecutionOrder(10000)]
public class AllyIndicator : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("UI References (leave empty — auto-found at runtime)")]
    public Canvas indicatorCanvas;
    public Image  roleIconImage;

    [Header("Role Sprites")]
    public Sprite shooterIcon;
    public Sprite collectorIcon;

    [Header("Canvas Size  (world size = canvasSize × 0.01)")]
    [Tooltip("Canvas pixel size. At scale 0.01, 120 px = 1.2 world units.")]
    public float canvasSize = 120f;

    [Header("Visibility / Distance  (player-to-player XZ)")]
    [Tooltip("Allies beyond this distance are hidden.")]
    public float maxDistance       = 120f;
    [Tooltip("Fade begins at this distance. Full opacity at or below this value.")]
    public float fadeStartDistance =  80f;

    // ── Private state ─────────────────────────────────────────────────────────

    private PlayerStats  _stats;
    private Transform    _anchor;           // AllyIndicatorAnchor (saved before canvas detach)

    private CanvasGroup  _group;
    private Material     _xrayMaterial;

    // v16: camera resolved from local PlayerSetup — not a global search
    private Camera       _playerCamera;

    private PlayerStats  _localStats;
    private PlayerStats  _subscribedLocalStats;
    private float        _localStatsRevalTtl;
    private const float  REVALIDATE_INTERVAL = 1f;

    private string       _lastHideReason = "";
    private PlayerRole   _lastKnownRole  = (PlayerRole)(-1);

    // ── Awake ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        _stats = GetComponentInParent<PlayerStats>();

        // ── 1. Find canvas from our own children only ─────────────────────────
        if (indicatorCanvas != null && !indicatorCanvas.transform.IsChildOf(transform))
        {
            Debug.LogWarning("[AllyIndicator] indicatorCanvas belongs to a different " +
                             "player instance — re-finding locally.", this);
            indicatorCanvas = null;
        }

        if (indicatorCanvas == null)
            indicatorCanvas = GetComponentInChildren<Canvas>(includeInactive: true);

        if (indicatorCanvas == null)
        {
            Debug.LogError("[AllyIndicator] No Canvas found in children. " +
                           "Add a Canvas child to AllyIndicatorAnchor.", this);
            return;
        }

        // ── 2. Configure canvas ───────────────────────────────────────────────
        indicatorCanvas.renderMode           = RenderMode.WorldSpace;
        indicatorCanvas.transform.localScale = Vector3.one * 0.01f;
        indicatorCanvas.sortingOrder         = 100;

        var canvasRt = indicatorCanvas.GetComponent<RectTransform>();
        if (canvasRt != null)
        {
            canvasRt.sizeDelta        = new Vector2(canvasSize, canvasSize);
            canvasRt.anchoredPosition = Vector2.zero;
        }

        // ── 3. Find roleIconImage from canvas children (BEFORE detach) ────────
        //
        //    v16 fix: check IsChildOf(indicatorCanvas.transform), not IsChildOf(transform).
        //    After SetParent(null) below, the canvas is no longer in our hierarchy
        //    so checking against 'transform' would always return false and
        //    unnecessarily null-out the Inspector assignment.
        if (roleIconImage != null &&
            !roleIconImage.transform.IsChildOf(indicatorCanvas.transform))
        {
            Debug.LogWarning("[AllyIndicator] roleIconImage is not inside indicatorCanvas " +
                             "— re-finding.", this);
            roleIconImage = null;
        }

        if (roleIconImage == null)
        {
            foreach (Image img in indicatorCanvas.GetComponentsInChildren<Image>(true))
            {
                string n = img.name.ToLower();
                if (n.Contains("role") || n.Contains("icon"))
                {
                    roleIconImage = img;
                    break;
                }
            }
            if (roleIconImage == null)
            {
                Image[] imgs = indicatorCanvas.GetComponentsInChildren<Image>(true);
                if (imgs.Length > 0) roleIconImage = imgs[imgs.Length - 1];
            }
        }

        if (roleIconImage == null)
            Debug.LogWarning("[AllyIndicator] roleIconImage not found. " +
                             "Name an Image child 'RoleIcon' inside IndicatorCanvas.", this);

        // Stretch-fill + guarantee full vertex-colour alpha.
        if (roleIconImage != null)
        {
            var iconRt          = roleIconImage.rectTransform;
            iconRt.localScale   = Vector3.one;
            iconRt.anchorMin    = Vector2.zero;
            iconRt.anchorMax    = Vector2.one;
            iconRt.sizeDelta    = Vector2.zero;
            iconRt.anchoredPosition = Vector2.zero;

            Color c = roleIconImage.color;
            c.a = 1f;
            roleIconImage.color = c;
        }

        StripPanelBackground();

        // CanvasGroup — initialise alpha to 1 (prefab may have a stale value).
        _group = indicatorCanvas.GetComponent<CanvasGroup>()
              ?? indicatorCanvas.gameObject.AddComponent<CanvasGroup>();
        _group.alpha = 1f;

        // ── 4. Detach canvas from player hierarchy ────────────────────────────
        //
        //    NGO's NetworkTransform writes the player root rotation after some
        //    LateUpdate orders.  When the canvas is a child, Unity recomputes
        //    its world rotation as  newParentRot × localRot,  overwriting the
        //    billboard rotation we just applied.  Reparenting to scene root
        //    removes the parent entirely so nothing can corrupt our rotation.
        //    OnDestroy() explicitly destroys this orphaned GameObject.
        _anchor = transform;
        indicatorCanvas.transform.SetParent(null, worldPositionStays: true);
    }

    // ── Start ─────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (_stats == null)
        {
            Debug.LogError("[AllyIndicator] PlayerStats not found in parent. " +
                           "AllyIndicatorAnchor must be a direct child of the Player root.", this);
            enabled = false;
            return;
        }

        BuildXRayMaterial();

        _stats.role.OnValueChanged += OnRoleChanged;
        _stats.team.OnValueChanged += OnTeamChanged;

        if (indicatorCanvas != null) indicatorCanvas.enabled = false;

        _localStats         = FindLocalPlayerStats();
        _localStatsRevalTtl = REVALIDATE_INTERVAL;

        if (_localStats != null)
        {
            SubscribeLocalStats(_localStats);
            _playerCamera = ResolvePlayerCamera(_localStats);
        }

        RefreshIcon();
    }

    // ── OnDestroy ─────────────────────────────────────────────────────────────

    private void OnDestroy()
    {
        if (_stats != null)
        {
            _stats.role.OnValueChanged -= OnRoleChanged;
            _stats.team.OnValueChanged -= OnTeamChanged;
        }

        UnsubscribeLocalStats();

        if (indicatorCanvas != null)
        {
            Destroy(indicatorCanvas.gameObject);
            indicatorCanvas = null;
        }

        if (_xrayMaterial != null)
        {
            Destroy(_xrayMaterial);
            _xrayMaterial = null;
        }
    }

    // ── LateUpdate ────────────────────────────────────────────────────────────

    private void LateUpdate()
    {
        if (indicatorCanvas == null) return;

        // ── 1. Resolve local player + camera ──────────────────────────────────

        if (_localStats == null)
        {
            _localStats = FindLocalPlayerStats();
            if (_localStats != null)
            {
                _localStatsRevalTtl = REVALIDATE_INTERVAL;
                SubscribeLocalStats(_localStats);
                _playerCamera   = ResolvePlayerCamera(_localStats);
                _lastHideReason = "";
            }
        }
        else
        {
            _localStatsRevalTtl -= Time.deltaTime;
            if (_localStatsRevalTtl <= 0f)
            {
                PlayerStats fresh = FindLocalPlayerStats();
                if (fresh != _localStats)
                {
                    UnsubscribeLocalStats();
                    _localStats = fresh;
                    if (_localStats != null)
                    {
                        SubscribeLocalStats(_localStats);
                        _playerCamera = ResolvePlayerCamera(_localStats);
                    }
                    _lastHideReason = "";
                }
                _localStatsRevalTtl = REVALIDATE_INTERVAL;
            }

            // Revalidate camera every frame — SetActive can toggle it anytime.
            if (_playerCamera == null || !_playerCamera.isActiveAndEnabled)
                _playerCamera = ResolvePlayerCamera(_localStats);
        }

        if (_playerCamera == null) return;

        // ── 2. Follow anchor position ─────────────────────────────────────────
        if (_anchor != null)
            indicatorCanvas.transform.position = _anchor.position;

        // ── 3. Billboard — face toward camera (yaw-only, XZ plane) ───────────
        //
        //    Compute the vector FROM the canvas TO the camera projected onto XZ.
        //    LookRotation(toCamera) makes the canvas +Z point directly at the
        //    camera eye — correct regardless of camera pitch or height delta.
        //    Because the canvas has no parent, this rotation is never overwritten.
        Vector3 canvasPos = indicatorCanvas.transform.position;
        Vector3 camPos    = _playerCamera.transform.position;
        Vector3 toCamera  = new Vector3(camPos.x - canvasPos.x,
                                         0f,
                                         camPos.z - canvasPos.z);

        if (toCamera.sqrMagnitude > 0.001f)
            indicatorCanvas.transform.rotation = Quaternion.LookRotation(toCamera);

        // Keep worldCamera in sync for correct UI depth sorting.
        if (indicatorCanvas.worldCamera != _playerCamera)
            indicatorCanvas.worldCamera = _playerCamera;

        // ── 4. Role dirty-flag ────────────────────────────────────────────────
        if (_stats != null && _stats.role.Value != _lastKnownRole)
            RefreshIcon();

        // ── 5. Visibility + fade ──────────────────────────────────────────────
        UpdateVisibility();
    }

    // ── Visibility ────────────────────────────────────────────────────────────

    private void UpdateVisibility()
    {
        NetworkObject no = _stats.GetComponent<NetworkObject>();

        if (no == null || !no.IsSpawned)              { LogHide("not spawned");  SetVisible(false); return; }
        if (no.IsOwner)                                { LogHide("IsOwner");      SetVisible(false); return; }
        if (_stats.IsDead())                           { LogHide("IsDead");       SetVisible(false); return; }
        if (_localStats == null)                       { LogHide("local null");   SetVisible(false); return; }
        if (_localStats.team.Value != _stats.team.Value){ LogHide("diff team");  SetVisible(false); return; }

        float dist = HorizontalDistance(_localStats.transform.position,
                                        _stats.transform.position);

        if (dist > maxDistance) { LogHide($"range {dist:F1}m"); SetVisible(false); return; }

        LogHide("");
        SetVisible(true);

        if (_group != null)
        {
            _group.alpha = dist <= fadeStartDistance
                ? 1f
                : Mathf.InverseLerp(maxDistance, fadeStartDistance, dist);
        }
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private void SetVisible(bool show)
    {
        if (indicatorCanvas != null && indicatorCanvas.enabled != show)
            indicatorCanvas.enabled = show;
    }

    private void LogHide(string reason)
    {
        if (reason == _lastHideReason) return;
        _lastHideReason = reason;
        Debug.Log(string.IsNullOrEmpty(reason)
            ? $"[AllyIndicator] ({gameObject.name}) VISIBLE"
            : $"[AllyIndicator] ({gameObject.name}) hidden — {reason}", this);
    }

    // ── Local-stats subscription ──────────────────────────────────────────────

    private void SubscribeLocalStats(PlayerStats ps)
    {
        if (ps == null || ps == _subscribedLocalStats) return;
        UnsubscribeLocalStats();
        _subscribedLocalStats = ps;
        ps.team.OnValueChanged += OnLocalTeamChanged;
    }

    private void UnsubscribeLocalStats()
    {
        if (_subscribedLocalStats == null) return;
        _subscribedLocalStats.team.OnValueChanged -= OnLocalTeamChanged;
        _subscribedLocalStats = null;
    }

    private void OnLocalTeamChanged(TeamID prev, TeamID next)
    {
        _lastHideReason = "";
        UpdateVisibility();
    }

    // ── NGO callbacks ─────────────────────────────────────────────────────────

    private void OnRoleChanged(PlayerRole prev, PlayerRole next) => RefreshIcon();

    private void OnTeamChanged(TeamID prev, TeamID next)
    {
        _lastHideReason = "";
        RefreshIcon();
    }

    // ── Icon ──────────────────────────────────────────────────────────────────

    private void RefreshIcon()
    {
        if (_stats == null || roleIconImage == null) return;
        PlayerRole role = _stats.role.Value;
        _lastKnownRole  = role;
        Sprite next     = role == PlayerRole.Shooter ? shooterIcon : collectorIcon;
        if (roleIconImage.sprite != next) roleIconImage.sprite = next;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void StripPanelBackground()
    {
        if (indicatorCanvas == null) return;
        foreach (Image img in indicatorCanvas.GetComponentsInChildren<Image>(true))
        {
            if (img == roleIconImage) continue;
            if (img.sprite == null) img.color = Color.clear;
        }
    }

    private void BuildXRayMaterial()
    {
        if (indicatorCanvas == null) return;
        Shader s = Shader.Find("UI/Default");
        if (s == null) { Debug.LogWarning("[AllyIndicator] UI/Default shader not found.", this); return; }
        _xrayMaterial = new Material(s);
        _xrayMaterial.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
        foreach (Image img in indicatorCanvas.GetComponentsInChildren<Image>(true))
            img.material = _xrayMaterial;
    }

    // ── Camera resolution (v16) ───────────────────────────────────────────────
    //
    //  Always get the camera from PlayerSetup.playerCamera on the local player.
    //  This is the only camera that is:
    //    a) Guaranteed to be the correct first-person player camera.
    //    b) Active exclusively for the local owner (not shared with any other
    //       object in the scene).
    //    c) Reachable with a direct component reference — no tag or name lookup.
    //
    //  Fallback to Camera.main only if PlayerSetup isn't initialised yet,
    //  with an explicit exclusion of arm/overlay cameras.

    private static Camera ResolvePlayerCamera(PlayerStats localStats)
    {
        if (localStats == null) return null;

        PlayerSetup setup = localStats.GetComponent<PlayerSetup>();
        if (setup != null &&
            setup.playerCamera != null &&
            setup.playerCamera.isActiveAndEnabled)
            return setup.playerCamera;

        Camera main = Camera.main;
        if (main != null && main.isActiveAndEnabled &&
            !main.name.ToLower().Contains("arm"))
            return main;

        return null;
    }

    // ── Local player finder (three-tier, fastest to slowest) ─────────────────

    private static PlayerStats FindLocalPlayerStats()
    {
        // Tier 1: O(1) NGO direct reference.
        NetworkObject obj = NetworkManager.Singleton?.LocalClient?.PlayerObject;
        if (obj != null)
        {
            var ps = obj.GetComponent<PlayerStats>();
            if (ps != null) return ps;
        }

        // Tier 2: NGO SpawnedObjects dictionary — no GC alloc.
        var spawned = NetworkManager.Singleton?.SpawnManager?.SpawnedObjects;
        if (spawned != null)
        {
            foreach (var kvp in spawned)
            {
                var no = kvp.Value;
                if (no == null || !no.IsOwner) continue;
                var ps = no.GetComponent<PlayerStats>();
                if (ps != null) return ps;
            }
        }

        // Tier 3: scene search — last resort.
        foreach (var ps in FindObjectsByType<PlayerStats>(FindObjectsSortMode.None))
        {
            var no = ps.GetComponent<NetworkObject>();
            if (no != null && no.IsOwner) return ps;
        }

        return null;
    }
}