// GrenadeHandModel.cs — Sugar Rush
//
// ── SMOKE GRENADE MIGRATION ───────────────────────────────────────────────────
//   Rewired from CollectorController to ShooterController.
//
//   WHAT CHANGED:
//     • _collector field replaced with _shooter (ShooterController).
//     • Awake() searches GetComponentInParent<ShooterController>().
//     • OnEnable() / OnDisable() subscribe to shooter.onSmokeChargesChanged
//       and shooter.onSmokeGrenadeFired instead of collector equivalents.
//     • _lastKnownCharges initialised from shooter.smokeMaxCharges.
//     • All logic (hide on throw, restore on charges, timer countdown) is
//       identical to the original — only the event source changed.
//
// ── PREFAB SETUP ─────────────────────────────────────────────────────────────
//   Move this GameObject from under the Collector FP arms rig to under the
//   SHOOTER FP arms rig (fpShooterArms), specifically onto the right-hand bone:
//     1. Inside fpShooterArms rig, find the right-hand bone (e.g. Hand_R).
//     2. Create an empty child named "GrenadeInHand". Attach THIS script to it.
//     3. Add a child named "Mesh" (copy the visual from your SmokeGrenade prefab,
//        strip all physics/network components). Scale to taste (~0.5).
//     4. Drag that "Mesh" child into the meshRoot field in the Inspector.
//        If you leave meshRoot empty the script falls back to toggling all
//        Renderer components found in the children of this GameObject.
//     5. Set hideOnThrowDuration to roughly match your throw animation length.
//     6. This GameObject should ALWAYS remain active — only the mesh inside
//        it is shown or hidden.
//
// ── NOTES ────────────────────────────────────────────────────────────────────
//   ShooterController is found via GetComponentInParent, so it will be found
//   as long as this object is a descendant of the Shooter player root.

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

    private ShooterController _shooter;   // ← WAS CollectorController
    private float             _hideTimer;
    private int               _lastKnownCharges;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        _shooter = GetComponentInParent<ShooterController>();   // ← CHANGED

        if (_shooter == null)
            Debug.LogWarning("[GrenadeHandModel] Could not find ShooterController in parents. " +
                             "Make sure this GameObject is under the fpShooterArms hierarchy.", this);
    }

    private void OnEnable()
    {
        if (_shooter == null) return;

        _shooter.onSmokeChargesChanged.AddListener(OnChargesChanged);   // ← CHANGED
        _shooter.onSmokeGrenadeFired.AddListener(OnThrown);             // ← CHANGED

        // Sync visual state immediately.
        _lastKnownCharges = _shooter.smokeMaxCharges;
        SetMeshVisible(true);
    }

    private void OnDisable()
    {
        if (_shooter == null) return;

        _shooter.onSmokeChargesChanged.RemoveListener(OnChargesChanged);   // ← CHANGED
        _shooter.onSmokeGrenadeFired.RemoveListener(OnThrown);             // ← CHANGED

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
        // KEY: we hide the child mesh, NOT this GameObject, so Update()
        // keeps running and the timer works correctly.
        SetMeshVisible(false);
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
