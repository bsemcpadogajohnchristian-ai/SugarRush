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
