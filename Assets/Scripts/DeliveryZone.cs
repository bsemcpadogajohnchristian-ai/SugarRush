// DeliveryZone.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Place one DeliveryZone in each team's base. Set ownerTeam in the Inspector.
// Attach a Collider (any shape) — IsTrigger does NOT need to be checked.
// We use Physics.OverlapBox on the server every 0.1s.
//
// WHY NOT OnTriggerEnter:
//   Unity requires at least one Rigidbody on the colliding pair.
//   The player uses CharacterController (not Rigidbody), so the trigger
//   never fires. Physics.OverlapBox on the server is authoritative and
//   requires no Rigidbody.
//
// WHY NOT NetworkBehaviour:
//   DeliveryZone is a static scene object. If its NetworkObject is not
//   registered and spawned by NGO, IsServer is always false and every
//   delivery attempt is silently dropped.
//
// DELIVERY GUARD:
//   We do NOT use a HashSet to block re-delivery. Instead we rely on the
//   fact that DeliverCandiesServer sets carriedCount to 0 immediately.
//   The next tick sees GetCarriedCount() == 0 and skips — no double scoring.
//   When the Collector picks up new candy and returns, carriedCount > 0 again
//   and delivery fires correctly. Simple, stateless, always correct.

using Unity.Netcode;
using UnityEngine;

public class DeliveryZone : MonoBehaviour
{
    [Header("Which team scores by entering this zone")]
    public TeamID ownerTeam;

    [Header("Detection")]
    public LayerMask playerLayer;
    public float     checkInterval = 0.1f;

    private Collider _col;
    private float    _timer;

    private void Awake() => _col = GetComponent<Collider>();

    private void Update()
    {
        if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsServer) return;

        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _timer = checkInterval;

        CheckDelivery();
    }

    private void CheckDelivery()
    {
        if (_col == null) return;

        Collider[] hits = Physics.OverlapBox(
            _col.bounds.center,
            _col.bounds.extents,
            transform.rotation,
            playerLayer);

        foreach (Collider hit in hits)
        {
            PlayerStats ps = hit.GetComponent<PlayerStats>();
            if (ps == null)                              continue;
            if (ps.role.Value  != PlayerRole.Collector) continue;
            if (ps.team.Value  != ownerTeam)            continue;
            if (ps.IsDead())                            continue;

            CollectorController col = hit.GetComponent<CollectorController>();

            // GetCarriedCount() == 0 after a delivery, so this naturally
            // prevents double-scoring without any ID tracking needed.
            if (col == null || col.GetCarriedCount() <= 0) continue;

            int count = col.GetCarriedCount();
            col.DeliverCandiesServer(ownerTeam);
            Debug.Log($"[DeliveryZone] {ownerTeam} +{count}. Client={ps.OwnerClientId}");
        }
    }

    private void OnDrawGizmos()
    {
        if (_col == null) _col = GetComponent<Collider>();
        if (_col == null) return;

        Gizmos.color = ownerTeam == TeamID.TeamA
            ? new Color(0.2f, 0.4f, 1f, 0.35f)
            : new Color(1f, 0.2f, 0.2f, 0.35f);
        Gizmos.matrix = Matrix4x4.TRS(
            _col.bounds.center, transform.rotation, Vector3.one);
        Gizmos.DrawCube(Vector3.zero, _col.bounds.size);
    }
}
