// DecoyAI.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Client-side prediction using CharacterController on both server and client.
//
// KEY INSIGHT:
//   We disable NetworkTransform on clients (they predict locally).
//   Since NT is disabled, there is nothing fighting the CharacterController.
//   So we can simply run CC.Move() on BOTH server and client with the same
//   formula. CC.isGrounded works correctly on both. No raycasts. No hacks.
//   This is the standard professional approach for deterministic objects.

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

    // Set by CollectorController when the decoy is spawned.
    // Used by ShooterController to prevent teammates from damaging their own decoy.
    [HideInInspector] public TeamID ownerTeam;

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
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
        {
            // Disable NetworkTransform so it doesn't fight our local CC simulation.
            // CC stays ENABLED — it handles gravity and ground detection correctly
            // on clients just as well as on the server.
            if (_nt != null) _nt.enabled = false;
        }

        if (IsServer && _dir != Vector3.zero)
            Activate();
    }

    // Called by CollectorController after NetworkObject.Spawn()
    public void InitializeMovement(Vector3 direction, float speed)
    {
        _dir   = direction.normalized;
        _speed = Mathf.Max(speed, 0f);

        if (IsServer)
        {
            Activate();
            // Send initial state to all clients so they start predicting
            // from the exact same position and parameters.
            InitStateClientRpc(transform.position, _dir, _speed);
        }
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void InitStateClientRpc(Vector3 spawnPos, Vector3 dir, float speed)
    {
        if (IsServer) return; // server already handled above

        _dir   = dir.normalized;
        _speed = speed;

        // Teleport CC to the correct spawn position.
        // Disable CC briefly so we can set position directly — CC fights
        // transform.position writes when enabled.
        _cc.enabled = false;
        transform.position = spawnPos;
        _cc.enabled = true;

        _yVelocity = 0f;
        _ready     = true;
    }

    private void Activate()
    {
        if (_ready) return;
        _ready     = true;
        _yVelocity = 0f;
        StartCoroutine(LifetimeRoutine());
    }

    private void Update()
    {
        if (!_ready) return;

        // Identical movement on server and client — CC handles everything.
        Vector3 move = _dir * _speed * Time.deltaTime;

        if (_cc.isGrounded && _yVelocity < 0f)
            _yVelocity = -2f;

        _yVelocity += gravity * Time.deltaTime;
        move.y      = _yVelocity * Time.deltaTime;

        _cc.Move(move);
    }

    /// <param name="attackerTeam">Team of the shooter. Hits from the same team are ignored.</param>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TakeHitRpc(TeamID attackerTeam)
    {
        // Friendly fire guard — teammates cannot destroy their own decoy.
        if (attackerTeam == ownerTeam) return;

        _hits++;
        if (_hits >= maxHits) Despawn();
    }

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