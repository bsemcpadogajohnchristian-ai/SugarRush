// GrenadeHandModel.cs — Sugar Rush
//
// ── BUG FIX ───────────────────────────────────────────────────────────────────
//   BEFORE: OnThrown() called gameObject.SetActive(false) on the same GameObject
//           this script lives on. That pauses Update(), so _hideTimer never counts
//           down and the grenade mesh never comes back.
//
//   AFTER:  We toggle a dedicated meshRoot child (or fall back to all Renderers
//           on this object's children) so the script itself stays active and
//           Update() keeps ticking.
//
// ── PREFAB SETUP ─────────────────────────────────────────────────────────────
//   1. Inside your fpArms rig, find the right-hand bone (e.g. Hand_R).
//   2. Create an empty child named "GrenadeInHand". Attach THIS script to it.
//   3. Add a child named "Mesh" (copy the visual from your SmokeGrenade prefab,
//      strip all physics/network components). Scale to taste (~0.5).
//   4. Drag that "Mesh" child into the meshRoot field in the Inspector.
//      If you leave meshRoot empty the script falls back to toggling all
//      Renderer components found in the children of this GameObject.
//   5. Set hideOnThrowDuration to roughly match your throw animation length.
//
// ── NOTES ────────────────────────────────────────────────────────────────────
//   • This GameObject should ALWAYS remain active — only the mesh inside it
//     is shown or hidden.
//   • CollectorController is found via GetComponentInParent, so it will be
//     found as long as this object is a descendant of the player root.

using UnityEngine;

public class GrenadeHandModel : MonoBehaviour
{
    [Tooltip("The child GameObject that holds the grenade mesh. " +
             "Only this object is shown/hidden — NOT the GrenadeInHand root itself. " +
             "If left empty, all Renderers in children will be toggled instead.")]
    public GameObject meshRoot;

    [Tooltip("Seconds to hide the in-hand grenade after throwing. " +
             "Set this to roughly match your throw animation clip length (0.5–0.8 s).")]
    public float hideOnThrowDuration = 0.6f;

    private CollectorController _collector;
    private float               _hideTimer;
    private int                 _lastKnownCharges;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        _collector = GetComponentInParent<CollectorController>();

        if (_collector == null)
            Debug.LogWarning("[GrenadeHandModel] Could not find CollectorController in parents. " +
                             "Make sure this GameObject is a child of the player root.", this);
    }

    private void OnEnable()
    {
        if (_collector == null) return;

        _collector.onSmokeChargesChanged.AddListener(OnChargesChanged);
        _collector.onSmokeGrenadeFired.AddListener(OnThrown);

        // Sync visual state immediately (in case charges changed while arms were inactive).
        _lastKnownCharges = _collector.smokeMaxCharges;
        SetMeshVisible(true);
    }

    private void OnDisable()
    {
        if (_collector == null) return;

        _collector.onSmokeChargesChanged.RemoveListener(OnChargesChanged);
        _collector.onSmokeGrenadeFired.RemoveListener(OnThrown);

        // Reset timer so we don't accidentally stay hidden next activation.
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

            // Restore visibility only if there are still charges left.
            if (_lastKnownCharges > 0)
                SetMeshVisible(true);
        }
    }

    // ── Event callbacks ───────────────────────────────────────────────────────

    private void OnChargesChanged(int charges)
    {
        _lastKnownCharges = charges;

        if (charges <= 0)
        {
            // Out of charges — hide permanently until cooldown resets.
            _hideTimer = 0f;
            SetMeshVisible(false);
        }
        else if (_hideTimer <= 0f)
        {
            // Charges restored (cooldown finished) — show the grenade again.
            SetMeshVisible(true);
        }
        // If hideTimer > 0 we're mid-throw animation; let Update() restore on finish.
    }

    private void OnThrown()
    {
        // Hide the mesh during the throw arc.
        // Update() will re-show it after hideOnThrowDuration seconds if charges
        // remain, or OnChargesChanged(0) will keep it hidden if they do not.
        // 
        // KEY FIX: we hide the child mesh, NOT this GameObject, so Update()
        // keeps running and the timer works correctly.
        SetMeshVisible(false);
        _hideTimer = hideOnThrowDuration;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows or hides the grenade mesh without touching this GameObject's
    /// active state, so Update() always keeps running.
    /// </summary>
    private void SetMeshVisible(bool visible)
    {
        if (meshRoot != null)
        {
            // Preferred path: toggle the dedicated mesh child.
            meshRoot.SetActive(visible);
        }
        else
        {
            // Fallback: toggle every Renderer in children.
            foreach (Renderer r in GetComponentsInChildren<Renderer>(includeInactive: true))
                r.enabled = visible;
        }
    }
}