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
    private ShooterController _occupant; 

    private void Awake()
    {
        _col = GetComponent<Collider>();

        
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
            if (!ps.IsOwner)                           continue; 
            if (ps.team.Value  != ownerTeam)           continue; 
            if (ps.role.Value  != PlayerRole.Shooter)  continue; 
            if (ps.IsDead())                           continue;

            ShooterController sc = hit.GetComponent<ShooterController>();
            if (sc != null) { found = sc; break; }
        }

        
        if (found == _occupant) return;

        _occupant?.SetInSwapZone(false); 
        _occupant = found;
        _occupant?.SetInSwapZone(true);  
    }

    private void OnDrawGizmos()
    {
        if (_col == null) _col = GetComponent<Collider>();
        if (_col == null) return;

        
        Gizmos.color = ownerTeam == TeamID.TeamA
            ? new Color(0.2f, 0.5f, 1f, 0.25f)
            : new Color(1f, 0.3f, 0.2f, 0.25f);

        Gizmos.matrix = Matrix4x4.TRS(_col.bounds.center, transform.rotation, Vector3.one);
        Gizmos.DrawCube(Vector3.zero, _col.bounds.size);

        
        Gizmos.color = ownerTeam == TeamID.TeamA
            ? new Color(0.2f, 0.5f, 1f, 0.8f)
            : new Color(1f, 0.3f, 0.2f, 0.8f);
        Gizmos.DrawWireCube(Vector3.zero, _col.bounds.size);
    }
}
