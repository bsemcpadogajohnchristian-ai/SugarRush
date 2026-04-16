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
            
            
            if (_nt != null) _nt.enabled = false;
        }

        if (IsServer && _dir != Vector3.zero)
            Activate();
    }

    
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

        
        Vector3 move = _dir * _speed * Time.deltaTime;

        if (_cc.isGrounded && _yVelocity < 0f)
            _yVelocity = -2f;

        _yVelocity += gravity * Time.deltaTime;
        move.y      = _yVelocity * Time.deltaTime;

        _cc.Move(move);
    }

    
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void TakeHitRpc(TeamID attackerTeam)
    {
        
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
