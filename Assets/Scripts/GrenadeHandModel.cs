// GrenadeHandModel.cs — Sugar Rush
//
// ── PURPOSE ──────────────────────────────────────────────────────────────────
//   Attach this to the GrenadeInHand GameObject inside the fpArms rig
//   (ideally parented to the hand bone).
//
//   It shows/hides the in-hand grenade mesh based on smoke charge state:
//     • Charges > 0  → grenade visible
//     • Charges = 0  → grenade hidden (on cooldown)
//     • On throw     → grenade briefly hidden for the duration of the throw
//                      animation, then shown again if charges remain
//
// ── PREFAB SETUP ─────────────────────────────────────────────────────────────
//   1. Inside your fpArms rig, find the right-hand bone (e.g. Hand_R).
//   2. Create an empty child named "GrenadeInHand".
//   3. Add a child mesh (copy the visual from your SmokeGrenade prefab,
//      strip all physics/network components). Scale to taste (~0.5).
//   4. Attach THIS script to the "GrenadeInHand" GameObject.
//   5. Set hideOnThrowDuration to roughly match your throw animation length.
//
// ── NOTES ────────────────────────────────────────────────────────────────────
//   • fpArms starts INACTIVE in the prefab. OnEnable fires automatically when
//     PlayerSetup.ApplyRole() activates the arms — no extra wiring needed.
//   • CollectorController is found via GetComponentInParent, so it will be
//     found as long as this object is a descendant of the player root.

using UnityEngine;

public class GrenadeHandModel : MonoBehaviour
{
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
        gameObject.SetActive(true);
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
                gameObject.SetActive(true);
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
            gameObject.SetActive(false);
        }
        else if (_hideTimer <= 0f)
        {
            // Charges restored (cooldown finished) — show the grenade again.
            gameObject.SetActive(true);
        }
        // If hideTimer > 0 we're mid-throw animation; let Update() restore on finish.
    }

    private void OnThrown()
    {
        // Hide during throw arc. Update() will re-show it after hideOnThrowDuration
        // if charges remain, or OnChargesChanged(0) will keep it hidden if not.
        gameObject.SetActive(false);
        _hideTimer = hideOnThrowDuration;
    }
}
