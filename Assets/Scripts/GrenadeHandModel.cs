// GrenadeHandModel.cs — Sugar Rush
//
// ── THROW-ONLY VISIBILITY ─────────────────────────────────────────────────────
//   Previous behaviour: grenade mesh was visible in-hand whenever charges > 0,
//   hidden during the throw arc, then restored.
//
//   New behaviour: grenade mesh is ALWAYS hidden except during the brief
//   throw animation window (hideOnThrowDuration seconds after OnThrown fires).
//   This gives the illusion that the player "pulls out" the grenade, throws it,
//   and it disappears — matching a typical FPS grenade throw feel.
//
//   WHAT CHANGED vs the previous version:
//     • OnEnable()         — mesh starts hidden (was: starts visible when charges > 0).
//     • OnChargesChanged() — no longer shows the mesh when charges are restored;
//                            it only updates _lastKnownCharges for internal tracking.
//     • OnThrown()         — shows the mesh immediately (was: hid it). The existing
//                            hide timer now runs to hide the mesh again after the
//                            throw animation finishes, exactly as before.
//     • Update()           — hide logic is unchanged; when _hideTimer expires the
//                            mesh is hidden (was: shown if charges > 0).
//
//   No prefab changes required. Inspector fields are identical.
//
// ── PREFAB SETUP (unchanged) ─────────────────────────────────────────────────
//   This GameObject sits under fpShooterArms on the right-hand bone (Hand_R).
//   meshRoot  → child "Mesh" with the grenade visual (no physics/network components).
//   hideOnThrowDuration → match your throw animation clip length (~0.5–0.8 s).
//   This GameObject should ALWAYS remain active — only the mesh inside is toggled.

using UnityEngine;

public class GrenadeHandModel : MonoBehaviour
{
    [Tooltip("The child GameObject that holds the grenade mesh. " +
             "Only this object is shown/hidden — NOT the GrenadeInHand root itself. " +
             "If left empty, all Renderers in children will be toggled instead.")]
    public GameObject meshRoot;

    [Tooltip("Seconds the in-hand grenade is VISIBLE after throwing. " +
             "Set this to roughly match your throw animation clip length (0.5–0.8 s).")]
    public float hideOnThrowDuration = 0.6f;

    private ShooterController _shooter;
    private float             _hideTimer;
    private int               _lastKnownCharges;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        _shooter = GetComponentInParent<ShooterController>();

        if (_shooter == null)
            Debug.LogWarning("[GrenadeHandModel] Could not find ShooterController in parents. " +
                             "Make sure this GameObject is under the fpShooterArms hierarchy.", this);
    }

    private void OnEnable()
    {
        if (_shooter == null) return;

        _shooter.onSmokeChargesChanged.AddListener(OnChargesChanged);
        _shooter.onSmokeGrenadeFired.AddListener(OnThrown);

        // ── CHANGED: start hidden — only visible during the throw animation ──
        _lastKnownCharges = _shooter.smokeMaxCharges;
        SetMeshVisible(false);
    }

    private void OnDisable()
    {
        if (_shooter == null) return;

        _shooter.onSmokeChargesChanged.RemoveListener(OnChargesChanged);
        _shooter.onSmokeGrenadeFired.RemoveListener(OnThrown);

        _hideTimer = 0f;
    }

    // ── Per-frame ─────────────────────────────────────────────────────────────

    private void Update()
    {
        if (_hideTimer <= 0f) return;

        _hideTimer -= Time.deltaTime;

        if (_hideTimer <= 0f)
        {
            _hideTimer = 0f;
            // ── CHANGED: always hide after the throw window — never restore ──
            SetMeshVisible(false);
        }
    }

    // ── Event callbacks ───────────────────────────────────────────────────────

    private void OnChargesChanged(int charges)
    {
        // ── CHANGED: only track the count for internal use.
        // Do NOT show the mesh here — it only appears during the throw window.
        _lastKnownCharges = charges;
    }

    private void OnThrown()
    {
        // ── CHANGED: SHOW the mesh when the throw fires so the player sees
        // the grenade leave their hand, then hide it after hideOnThrowDuration.
        if (_lastKnownCharges < 0) return; // safety: no charges at all, skip

        SetMeshVisible(true);
        _hideTimer = hideOnThrowDuration;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetMeshVisible(bool visible)
    {
        if (meshRoot != null)
        {
            meshRoot.SetActive(visible);
        }
        else
        {
            foreach (Renderer r in GetComponentsInChildren<Renderer>(includeInactive: true))
                r.enabled = visible;
        }
    }
}