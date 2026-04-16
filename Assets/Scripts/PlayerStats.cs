// PlayerStats.cs
// Sugar Rush
// Unity 6.3 LTS + Netcode for GameObjects v2.1+
//
// Holds all synced player data: role, team, health, alive/dead, crouching, sprinting.
// Damage and respawn are server-authoritative.
//
// ── ADDED FOR SHOOTER ANIMATION ───────────────────────────────────────────
//   shootFireSequence  — ever-increasing counter, Owner-write.
//                        ShooterController increments it on every bullet fired.
//                        ShooterAnimator and FPShooterAnimator detect changes
//                        to fire the "Fire" trigger on ALL clients in sync
//                        with the muzzle-flash RPC. Same pattern as jumpSequence.
//
//   equippedWeaponIndex — syncs the active weapon slot (0–3) to all clients.
//                         ShooterController writes it in EquipWeapon().
//                         ShooterAnimator reads it to update the WeaponType
//                         animator int so 3rd-person bodies hold the right grip.
//
//   isReloadingNV — (NEW) syncs reload state to all clients.
//                   ShooterController sets it true when the active weapon's
//                   onReloadStart fires, and false on onReloadEnd or on
//                   weapon switch (CancelReload fires onReloadEnd).
//                   ShooterAnimator polls it every frame to drive the Reload
//                   trigger + IsReloading bool on the UpperBody animator layer
//                   so both owner and non-owner see the 3rd-person reload
//                   animation. FPShooterAnimator reads onReloadStart/End directly
//                   (local events) — this NV is only needed for 3P.

using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.Events;

public class PlayerStats : NetworkBehaviour
{
    // ── Synced variables ──────────────────────────────────────────────────────

    public NetworkVariable<PlayerRole> role = new(PlayerRole.Shooter,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<TeamID> team = new(TeamID.TeamA,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<float> currentHP = new(100f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<bool> isDead = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<bool> isCrouching = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<bool> isSprinting = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // jumpSequence: ever-increasing int so NGO always replicates it.
    // A one-frame bool would be missed between 30 Hz NGO ticks at 60 fps.
    public NetworkVariable<int> jumpSequence = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ── SHOOTER ANIMATION NVs ─────────────────────────────────────────────────

    // shootFireSequence: same pattern as jumpSequence.
    // Incremented by ShooterController on every fired bullet (owner-only write).
    // ShooterAnimator detects changes on ALL clients to fire the "Fire" trigger
    // on the upper-body layer so remote players see the shooting animation.
    public NetworkVariable<int> shootFireSequence = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // equippedWeaponIndex: current weapon slot (0=Rifle, 1=Shotgun, 2=Sniper, 3=Bazooka).
    // Written by ShooterController.EquipWeapon() (owner-only).
    // Read by ShooterAnimator to set the WeaponType animator int on all clients
    // so 3rd-person bodies swap grip animations when the owner changes weapon.
    public NetworkVariable<int> equippedWeaponIndex = new(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // isReloadingNV: true while the owner's active weapon is reloading.
    // Written by ShooterController via OnCurrentWeaponReloadStart / OnCurrentWeaponReloadEnd.
    // Also cleared to false in EquipWeapon() (weapon switch cancels the old reload).
    // Read by ShooterAnimator every frame on ALL clients:
    //   • rising edge  → fires H_Reload trigger (starts reload clip on UpperBody layer)
    //   • falling edge → clears H_IsReloading bool (exits Reload state)
    // FPShooterAnimator does NOT read this — it subscribes to onReloadStart/End
    // directly since it only ever runs on the local owner.
    public NetworkVariable<bool> isReloadingNV = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ── LOCOMOTION ANIMATION NVs ──────────────────────────────────────────────
    //
    // These replace per-frame position-delta velocity on non-owner clients.
    //
    // PROBLEM THEY SOLVE:
    //   ShooterAnimator (and CollectorAnimator) previously estimated movement
    //   speed for remote players by dividing the NetworkTransform position delta
    //   by Time.deltaTime. NT updates arrive at ~30 Hz; Update() runs at 60 Hz.
    //   On NT-update frames the position jumps all-at-once → velocity spikes to
    //   ~10× the real speed. On the two dead frames between NT updates the delta
    //   is zero. This "spike + zero + zero + spike …" pattern causes:
    //     • _smoothedHSpeed to oscillate around the walk-threshold hysteresis
    //       band → _nonOwnerWalking toggles → Speed NV flips 0↔1 → blend-tree
    //       flickers between Idle and Walk every couple of frames ("back and
    //       forth" locomotion).
    //     • CrouchMoveX/Y driven from the same delta velocity oscillates between
    //       the spike value and zero → crouch blend snaps direction in/out.
    //
    // FIX:
    //   The owner writes isMovingNV and localMoveDir directly from raw input,
    //   which is frame-accurate and never oscillates. Non-owner clients read
    //   these NVs instead of computing velocity from position — eliminating all
    //   threshold oscillation. Because these are Owner-write NVs they are only
    //   dirtied when the value actually changes (not every frame), so bandwidth
    //   impact is minimal.

    // isMovingNV: true while the owner has movement input AND is grounded.
    // Written by PlayerController.Move() only when the state changes (guard).
    // Read by ShooterAnimator on ALL clients (non-owner path) to drive the
    // locomotion blend-tree Speed parameter without position-delta instability.
    public NetworkVariable<bool> isMovingNV = new(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // localMoveDir: raw (h, v) input vector from the owner, in local space.
    // Written by PlayerController.Move() with a 0.05 magnitude change guard to
    // avoid dirtying the NV on every single input frame.
    // Read by ShooterAnimator (non-owner path) for CrouchMoveX/Y in the 2D
    // crouch blend tree instead of the oscillating position-delta estimate.
    // X = strafe (left/right), Y = forward/backward. Range –1..1.
    public NetworkVariable<Vector2> localMoveDir = new(Vector2.zero,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ── Inspector settings ────────────────────────────────────────────────────

    [Header("Max HP per role")]
    public float shooterMaxHP   = 100f;
    public float collectorMaxHP = 150f;

    [Header("Base speed multiplier per role")]
    public float shooterSpeed   = 1.0f;
    public float collectorSpeed = 1.3f;

    // ── Derived (set by ApplyRoleStats) ───────────────────────────────────────

    [HideInInspector] public float maxHP;
    [HideInInspector] public float speedMultiplier;

    // ── Events ────────────────────────────────────────────────────────────────

    public UnityEvent<float, float> onHealthChanged = new();
    public UnityEvent               onDeath         = new();
    public UnityEvent               onRespawn       = new();

    // ── Internal ─────────────────────────────────────────────────────────────

    private CharacterController _cc;

    public override void OnNetworkSpawn()
    {
        _cc = GetComponent<CharacterController>();

        currentHP.OnValueChanged += (_, next) =>
        {
            if (maxHP > 0f) onHealthChanged?.Invoke(next, maxHP);
        };

        isDead.OnValueChanged += (_, next) =>
        {
            if (next) onDeath?.Invoke();
            else      onRespawn?.Invoke();
        };

        role.OnValueChanged += (_, _) => ApplyRoleStats();
        ApplyRoleStats();
    }

    // ── Role stats ────────────────────────────────────────────────────────────

    public void ApplyRoleStats()
    {
        if (role.Value == PlayerRole.Shooter)
        {
            maxHP           = shooterMaxHP;
            speedMultiplier = shooterSpeed;
        }
        else
        {
            maxHP           = collectorMaxHP;
            speedMultiplier = collectorSpeed;
        }

        if (maxHP > 0f)
            onHealthChanged?.Invoke(currentHP.Value, maxHP);
    }

    // ── Damage / death ────────────────────────────────────────────────────────

    public void TakeDamage(float damage)
    {
        if (!IsServer || isDead.Value) return;
        currentHP.Value = Mathf.Max(currentHP.Value - damage, 0f);
        if (currentHP.Value <= 0f) DieServer();
    }

    private void DieServer()
    {
        isDead.Value = true;
        NetworkGameManager.Instance?.OnPlayerDied(this);
    }

    // ── Respawn ───────────────────────────────────────────────────────────────

    public void RespawnServer()
    {
        if (!IsServer) return;
        isDead.Value    = false;
        currentHP.Value = maxHP;
    }

    public void RespawnAtPosition(Vector3 pos, Quaternion rot)
    {
        if (!IsServer) return;
        PlayerController pc = GetComponent<PlayerController>();
        if (pc != null)
            pc.WarpToSpawnRpc(pos, rot);
        else
            Debug.LogWarning($"[PlayerStats] WarpToSpawnRpc failed — no PlayerController on {gameObject.name}");

        RespawnServer();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public bool IsDead() => isDead.Value;
}