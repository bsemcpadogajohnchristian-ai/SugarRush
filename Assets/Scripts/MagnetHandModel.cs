// MagnetHandModel.cs — Sugar Rush
//
// ── PURPOSE ───────────────────────────────────────────────────────────────────
//   Shows the in-hand magnet mesh ONLY while the magnet skill is active
//   (R held → magnetDuration seconds). Hidden at all other times.
//
//   This mirrors GrenadeHandModel's approach but is even simpler — no timer
//   is needed because CollectorController already fires onMagnetActiveChanged
//   with the exact bool we need.
//
// ── PREFAB SETUP ─────────────────────────────────────────────────────────────
//   1. Inside fpArms rig, find the right-hand bone (e.g. Hand_R).
//   2. Create an empty child named "MagnetInHand". Attach THIS script to it.
//   3. Add a child named "Mesh" — drag your magnet model in, strip all
//      physics / network components from it. Scale to taste.
//   4. Drag that "Mesh" child into the meshRoot field in the Inspector.
//      If meshRoot is left empty the script falls back to toggling all
//      Renderer components found in children of this GameObject.
//   5. This GameObject should ALWAYS remain active — only the mesh inside
//      is shown or hidden. That way OnEnable/OnDisable run reliably.
//
// ── HOW IT WORKS ─────────────────────────────────────────────────────────────
//   • OnEnable  → hides mesh immediately (safe default).
//   • onMagnetActiveChanged(true)  → shows mesh.
//   • onMagnetActiveChanged(false) → hides mesh.
//   • No per-frame Update needed — purely event-driven.

using UnityEngine;

public class MagnetHandModel : MonoBehaviour
{
    [Tooltip("The child GameObject that holds the magnet mesh. " +
             "Only this object is shown/hidden — NOT the MagnetInHand root itself. " +
             "If left empty, all Renderers in children will be toggled instead.")]
    public GameObject meshRoot;

    private CollectorController _collector;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        _collector = GetComponentInParent<CollectorController>();

        if (_collector == null)
            Debug.LogWarning("[MagnetHandModel] Could not find CollectorController in parents. " +
                             "Make sure this GameObject is under the fpArms hierarchy.", this);
    }

    private void OnEnable()
    {
        // Always start hidden — only show while the skill is active.
        SetMeshVisible(false);

        if (_collector == null) return;
        _collector.onMagnetActiveChanged.AddListener(OnMagnetActiveChanged);

        // Sync immediately in case the skill was already active when this enabled.
        SetMeshVisible(_collector.IsMagnetActive());
    }

    private void OnDisable()
    {
        if (_collector == null) return;
        _collector.onMagnetActiveChanged.RemoveListener(OnMagnetActiveChanged);

        // Ensure the mesh is hidden when this component is disabled
        // (e.g. player dies, role changes).
        SetMeshVisible(false);
    }

    // ── Event callback ────────────────────────────────────────────────────────

    private void OnMagnetActiveChanged(bool isActive)
    {
        SetMeshVisible(isActive);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

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
