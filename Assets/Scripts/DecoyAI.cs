using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class DecoyAI : NetworkBehaviour
{
    [Header("Movement")]
    public float lifetime = 5f;
    public float gravity  = -19.62f;

    [Header("Durability")]
    public int maxHits = 10;

    // Synced so ALL clients know the team and can show the right model colour.
    // ownerTeam is kept for server-side friendly-fire checks (TakeHitRpc).
    public NetworkVariable<TeamID> syncedTeam = new(TeamID.TeamA,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    [HideInInspector] public TeamID ownerTeam; // server-only helper, set from syncedTeam on spawn

    [Header("Visuals")]
    [Tooltip("Root of the collector body mesh child. Assign in the Decoy prefab.")]
    public GameObject collectorBody;

    // Animator lives on the collector body child (same controller as the real collector).
    private Animator _animator;

    // ── Animator hashes ─────────────────────────────────────────────────────
    private static readonly int H_Speed  = Animator.StringToHash("Speed");
    private static readonly int H_TeamID = Animator.StringToHash("TeamID");

    private CharacterController _cc;
    private NetworkTransform    _nt;

    private Vector3 _dir;
    private float   _speed;
    private float   _yVelocity;
    private int     _hits;
    private bool    _ready;

    private void Awake()
    {
        _cc = GetComponent<CharacterController>();
        _nt = GetComponent<NetworkTransform>();

        // Grab the Animator from the collector body child if assigned.
        if (collectorBody != null)
            _animator = collectorBody.GetComponentInChildren<Animator>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
        {
            // Clients let NetworkTransform drive position.
            if (_nt != null) _nt.enabled = false;
        }

        // Mirror team into the plain field so server-side TakeHitRpc can read it
        // without going through the NetworkVariable every frame.
        ownerTeam = syncedTeam.Value;

        // Apply visuals immediately and whenever the value changes.
        syncedTeam.OnValueChanged += (_, next) => ApplyTeamVisuals(next);
        ApplyTeamVisuals(syncedTeam.Value);

        if (IsServer && _dir != Vector3.zero)
            Activate();
    }

    // ── Called by CollectorController.SpawnDecoyRpc AFTER Spawn() ───────────
    public void InitializeMovement(Vector3 direction, float speed)
    {
        _dir   = direction.normalized;
        _speed = Mathf.Max(speed, 0f);

        if (IsServer)
        {
            Activate();
            InitStateClientRpc(transform.position, _dir, _speed);
        }
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void InitStateClientRpc(Vector3 spawnPos, Vector3 dir, float speed)
    {
        if (IsServer) return;

        _dir   = dir.normalized;
        _speed = speed;

        _cc.enabled = false;
        transform.position = spawnPos;
        _cc.enabled = true;

        _yVelocity = 0f;
        _ready     = true;

        // Start run animation on clients too.
        SetRunning(true);
    }

    private void Activate()
    {
        if (_ready) return;
        _ready     = true;
        _yVelocity = 0f;
        SetRunning(true);
        StartCoroutine(LifetimeRoutine());
    }

    private void Update()
    {
        if (!_ready) return;

        // Always force run — Speed 2f = run in the collector blend tree (0=idle, 1=walk, 2=run).
        if (_animator != null)
            _animator.SetFloat(H_Speed, 2f);

        Vector3 move = _dir * _speed * Time.deltaTime;

        if (_cc.isGrounded && _yVelocity < 0f)
            _yVelocity = -2f;

        _yVelocity += gravity * Time.deltaTime;
        move.y      = _yVelocity * Time.deltaTime;

        _cc.Move(move);
    }

    // ── Visuals ──────────────────────────────────────────────────────────────

    private void ApplyTeamVisuals(TeamID team)
    {
        ownerTeam = team; // keep the plain field in sync on all clients

        if (_animator == null) return;
        _animator.SetInteger(H_TeamID, (int)team);
    }

    // Drives the same Speed float your CollectorAnimator uses so the run clip plays.
    private void SetRunning(bool running)
    {
        if (_animator == null) return;
        _animator.SetFloat(H_Speed, running ? 1f : 0f);
    }

    // ── Damage ───────────────────────────────────────────────────────────────

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TakeHitRpc(TeamID attackerTeam)
    {
        if (attackerTeam == ownerTeam) return; // no friendly fire

        _hits++;
        if (_hits >= maxHits) Despawn();
    }

    // ── Lifetime ─────────────────────────────────────────────────────────────

    private IEnumerator LifetimeRoutine()
    {
        yield return new WaitForSeconds(lifetime);
        Despawn();
    }

    private void Despawn()
    {
        if (!IsServer) return;
        StopAllCoroutines();
        GetComponent<NetworkObject>()?.Despawn(true);
    }

    // ── Gizmos ───────────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        if (_cc == null) return;
        float bottomOffset = _cc.center.y - _cc.height * 0.5f + _cc.skinWidth;
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position + Vector3.up * bottomOffset, 0.1f);
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(transform.position, _dir * 2f);
    }
}