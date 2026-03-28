// WeaponSwapZone.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Place one WeaponSwapZone in each team's base (same setup as DeliveryZone).
// Shooters can only open the weapon inventory (B key) while standing inside.
//
// WHY NOT OnTriggerEnter:
//   Players use CharacterController — no Rigidbody — so trigger events never fire.
//   Physics.OverlapBox every 0.1s is the correct pattern for this project.
//   (Same reason DeliveryZone uses it.)
//
// WHY NOT NetworkBehaviour:
//   This is pure client-side UI gating. The server never needs to know that
//   a player is browsing their loadout. No network traffic is generated.
//
// WHY isTrigger = true (set in Awake):
//   If the Box Collider is solid (isTrigger = false), it becomes a physical
//   surface. The player's ground check (Physics.CheckSphere with groundMask)
//   won't detect it as ground because the zone is on the wrong layer, so
//   _isGrounded stays false — allowing infinite jumping and broken gravity
//   while standing inside the zone. Forcing isTrigger = true in Awake makes
//   the collider invisible to physics while keeping Physics.OverlapBox
//   detection working perfectly.
//
// SETUP (see tutorial):
//   1. Add an empty GameObject inside your team's base.
//   2. Add a Box Collider (any size). isTrigger is forced on by this script.
//   3. Attach this script. Set ownerTeam and playerLayer in the Inspector.
//   4. Repeat for the other team's base.

using Unity.Netcode;
using UnityEngine;

public class WeaponSwapZone : MonoBehaviour
{
    [Header("Which team's base this zone belongs to")]
    public TeamID ownerTeam;

    [Header("Detection")]
    [Tooltip("Set this to the layer your Player GameObjects are on.")]
    public LayerMask playerLayer;
    public float     checkInterval = 0.1f;

    private Collider          _col;
    private float             _timer;
    private ShooterController _occupant; // the local shooter currently inside

    private void Awake()
    {
        _col = GetComponent<Collider>();

        // Force isTrigger so this collider never acts as a physical surface.
        // A solid collider here breaks the player's ground detection (CheckSphere
        // uses groundMask and won't recognise this layer), causing infinite
        // jumping and wrong gravity inside the zone.
        // Physics.OverlapBox detects triggers just fine, so detection is unaffected.
        if (_col != null) _col.isTrigger = true;
    }

    private void Update()
    {
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _timer = checkInterval;
        CheckZone();
    }

    private void CheckZone()
    {
        if (_col == null) return;

        Collider[] hits = Physics.OverlapBox(
            _col.bounds.center,
            _col.bounds.extents,
            transform.rotation,
            playerLayer);

        ShooterController found = null;

        foreach (Collider hit in hits)
        {
            PlayerStats ps = hit.GetComponent<PlayerStats>();
            if (ps == null)                            continue;
            if (!ps.IsOwner)                           continue; // only react to the local player
            if (ps.team.Value  != ownerTeam)           continue; // wrong team
            if (ps.role.Value  != PlayerRole.Shooter)  continue; // collectors can't swap weapons
            if (ps.IsDead())                           continue;

            ShooterController sc = hit.GetComponent<ShooterController>();
            if (sc != null) { found = sc; break; }
        }

        // Only notify on change — don't spam SetInSwapZone every 0.1s
        if (found == _occupant) return;

        _occupant?.SetInSwapZone(false); // tell the previous occupant they left
        _occupant = found;
        _occupant?.SetInSwapZone(true);  // tell the new occupant they entered
    }

    private void OnDrawGizmos()
    {
        if (_col == null) _col = GetComponent<Collider>();
        if (_col == null) return;

        // Blue for Team A, Red for Team B — matches DeliveryZone colour coding
        Gizmos.color = ownerTeam == TeamID.TeamA
            ? new Color(0.2f, 0.5f, 1f, 0.25f)
            : new Color(1f, 0.3f, 0.2f, 0.25f);

        Gizmos.matrix = Matrix4x4.TRS(_col.bounds.center, transform.rotation, Vector3.one);
        Gizmos.DrawCube(Vector3.zero, _col.bounds.size);

        // Solid outline so it's visible even when not selected
        Gizmos.color = ownerTeam == TeamID.TeamA
            ? new Color(0.2f, 0.5f, 1f, 0.8f)
            : new Color(1f, 0.3f, 0.2f, 0.8f);
        Gizmos.DrawWireCube(Vector3.zero, _col.bounds.size);
    }
}