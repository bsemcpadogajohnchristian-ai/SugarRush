// Rocket.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
// Server-spawned projectile. Only the server moves it and triggers explosion.

using Unity.Netcode;
using UnityEngine;

public class Rocket : NetworkBehaviour
{
    private float      _speed;
    private float      _splashRadius;
    private float      _splashDamage;
    private float      _directDamage;
    private LayerMask  _mask;
    private GameObject _impactFX;
    private TeamID     _firingTeam;
    private bool       _exploded;
    private Rigidbody  _rb;

    private void Awake() => _rb = GetComponent<Rigidbody>();

    public void Initialize(float speed, float splashRadius, float splashDamage,
        float directDamage, LayerMask mask, GameObject fx, TeamID team)
    {
        _speed        = speed;
        _splashRadius = splashRadius;
        _splashDamage = splashDamage;
        _directDamage = directDamage;
        _mask         = mask;
        _impactFX     = fx;
        _firingTeam   = team;

        if (_rb != null) _rb.linearVelocity = transform.forward * _speed;
        Invoke(nameof(TimeoutExplode), 10f);
    }

    private void OnCollisionEnter(Collision col)
    {
        if (!IsServer || _exploded) return;
        Explode(col.contacts[0].point);
    }

    private void TimeoutExplode()
    {
        if (!IsServer) return;
        Explode(transform.position);
    }

    private void Explode(Vector3 point)
    {
        _exploded = true;
        ShowExplosionRpc(point);

        Collider[] cols = Physics.OverlapSphere(point, _splashRadius, _mask);
        foreach (Collider c in cols)
        {
            PlayerStats ps = c.GetComponentInParent<PlayerStats>();
            if (ps == null || ps.team.Value == _firingTeam) continue;
            float falloff = 1f - Mathf.Clamp01(Vector3.Distance(point, c.transform.position) / _splashRadius);
            ps.TakeDamage(_splashDamage * falloff);
        }

        GetComponent<NetworkObject>()?.Despawn(true);
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void ShowExplosionRpc(Vector3 point)
    {
        if (_impactFX == null) return;

        if (FXPool.Instance != null)
        {
            FXPool.Instance.Spawn(_impactFX, point, Quaternion.identity);
        }
        else
        {
            GameObject fx = Instantiate(_impactFX, point, Quaternion.identity);
            ParticleSystem ps = fx.GetComponent<ParticleSystem>();
            Destroy(fx, ps != null ? ps.main.duration + ps.main.startLifetime.constantMax : 2f);
        }
    }
}
