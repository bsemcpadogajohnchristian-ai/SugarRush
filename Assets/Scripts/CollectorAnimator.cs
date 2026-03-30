// CollectorAnimator.cs
// Sugar Rush — Unity 6.3 LTS + NGO v2.1+
//
// Drives the Collector's Animator on ALL clients (owner + non-owner).
//
// ── ROOT BUG FIX: position-delta velocity, not CC.velocity ───────────────────
//   PlayerController.Update() is disabled on non-owner clients — they never
//   call CC.Move(), so CC.velocity is always ZERO on them.  The Animator
//   would be stuck on Idle for every other player on screen.
//   Position delta works on every client: the owner moves via CC, non-owners
//   move via NetworkTransform — both change transform.position every frame.
//
// ── ANIMATOR PARAMETERS (exact names, case-sensitive) ─────────────────────────
//   Float   "Speed"        — 0=Idle  1=Walk  2=MedRun     (1D blend tree)
//   Float   "CrouchMoveX" — local X velocity when crouching  (-1 to 1)
//   Float   "CrouchMoveY" — local Z velocity when crouching  (-1 to 1)
//   Bool    "IsCrouching"  — transitions to/from Crouch sub-state
//   Bool    "IsSuperspeed" — E-skill active  → FastRun state
//   Bool    "IsPickingUp"  — candy pickup happened → PickUp state
//   Bool    "IsDead"       — locks into Death state
//   Trigger "Die"          — fires once on death
// ─────────────────────────────────────────────────────────────────────────────

using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(Animator))]
public class CollectorAnimator : NetworkBehaviour
{
    [Header("References (auto-found on Awake if left empty)")]
    public PlayerController    playerController;
    public CollectorController collectorController;
    public PlayerStats         playerStats;

    [Header("Standing speed thresholds (m/s)")]
    [Tooltip("Speed below this = Idle.  0.5 prevents idle-jitter from micro-movements.")]
    public float walkThreshold = 0.5f;

    [Tooltip("Speed at or above this = Medium Run (non-owner fallback). " +
             "Set between walk speed and sprint speed.  Default 7 works for " +
             "all candy-carry penalties.")]
    public float runThreshold  = 7f;

    [Header("Crouch blend normalisation")]
    [Tooltip("Max expected crouch speed in m/s used to normalise CrouchMoveX/Y " +
             "to the -1..1 range the 2D blend tree expects. " +
             "crouchSpeed(2.5) x collectorMult(1.3) ≈ 3.25 — keep slightly above.")]
    public float crouchMaxSpeed = 3.5f;

    [Header("Pick-up")]
    [Tooltip("Seconds the PickUpItem clip plays after a candy pickup is detected.")]
    public float pickupDuration = 0.6f;

    // ── Animator parameter hashes ─────────────────────────────────────────────
    private static readonly int H_Speed        = Animator.StringToHash("Speed");
    private static readonly int H_CrouchX      = Animator.StringToHash("CrouchMoveX");
    private static readonly int H_CrouchY      = Animator.StringToHash("CrouchMoveY");
    private static readonly int H_IsCrouching  = Animator.StringToHash("IsCrouching");
    private static readonly int H_IsSuperspeed = Animator.StringToHash("IsSuperspeed");
    private static readonly int H_IsPickingUp  = Animator.StringToHash("IsPickingUp");
    private static readonly int H_IsDead       = Animator.StringToHash("IsDead");
    private static readonly int H_Die          = Animator.StringToHash("Die");

    private Animator  _anim;
    private Transform _root;
    private Vector3   _prevPos;
    private float     _pickupTimer;
    private int       _lastCarriedCount;
    private bool      _wasDead;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        _anim = GetComponent<Animator>();
        _root = transform.root;   // Body_Collector is a child — root = Player root

        if (playerController    == null) playerController    = GetComponentInParent<PlayerController>();
        if (collectorController == null) collectorController = GetComponentInParent<CollectorController>();
        if (playerStats         == null) playerStats         = GetComponentInParent<PlayerStats>();
    }

    private void Start() => _prevPos = _root != null ? _root.position : transform.position;

    public override void OnNetworkSpawn()
    {
        if (playerStats != null)
            playerStats.isDead.OnValueChanged += OnDeadChanged;

        if (collectorController != null)
            _lastCarriedCount = collectorController.GetCarriedCount();

        _prevPos = _root != null ? _root.position : transform.position;
    }

    public override void OnNetworkDespawn()
    {
        if (playerStats != null)
            playerStats.isDead.OnValueChanged -= OnDeadChanged;
    }

    // ── Update ────────────────────────────────────────────────────────────────

    private void Update()
    {
        if (_anim == null || playerStats == null || _root == null) return;

        // ── Step 1 — Position-delta velocity (works owner + non-owner) ────────
        Vector3 worldVel = (_root.position - _prevPos) / Time.deltaTime;
        worldVel.y  = 0f;
        float hSpeed = worldVel.magnitude;
        _prevPos    = _root.position;

        // ── Step 2 — Dead ─────────────────────────────────────────────────────
        if (playerStats.IsDead())
        {
            _anim.SetBool(H_IsDead, true);
            return;   // don't update any other parameters while dead
        }
        _anim.SetBool(H_IsDead, false);

        // ── Step 3 — PickUp via carriedCount NetworkVariable ──────────────────
        if (collectorController != null)
        {
            int now = collectorController.GetCarriedCount();
            if (now > _lastCarriedCount) _pickupTimer = pickupDuration;
            _lastCarriedCount = now;
        }
        if (_pickupTimer > 0f) _pickupTimer -= Time.deltaTime;
        _anim.SetBool(H_IsPickingUp, _pickupTimer > 0f);

        // ── Step 4 — Superspeed skill → FastRun state ─────────────────────────
        bool isSuperspeed = collectorController != null && collectorController.IsSuperspeedActive();
        _anim.SetBool(H_IsSuperspeed, isSuperspeed);

        // ── Step 5 — Crouch + 2D blend tree ──────────────────────────────────
        bool isCrouching = playerController != null && playerController.IsCrouching();
        _anim.SetBool(H_IsCrouching, isCrouching);

        if (isCrouching)
        {
            // Convert world velocity into the player's LOCAL forward/right axes.
            // X = strafe (right = +1, left = -1)
            // Y = forward/back (forward = +1, back = -1)
            Vector3 local = _root.InverseTransformDirection(worldVel);
            float cx = Mathf.Clamp(local.x / crouchMaxSpeed, -1f, 1f);
            float cy = Mathf.Clamp(local.z / crouchMaxSpeed, -1f, 1f);

            _anim.SetFloat(H_CrouchX, cx, 0.1f, Time.deltaTime);
            _anim.SetFloat(H_CrouchY, cy, 0.1f, Time.deltaTime);
        }

        // ── Step 6 — Standing Speed parameter ────────────────────────────────
        //
        // Owner:     use IsSprinting() — 100% accurate because the owner runs
        //            the full PlayerController.Update() loop.
        // Non-owner: infer from measured hSpeed — PlayerController is disabled
        //            on non-owners so IsSprinting() always returns false there.
        float speedParam;
        if (hSpeed < walkThreshold)
        {
            speedParam = 0f;
        }
        else if (IsOwner)
        {
            speedParam = (playerController != null && playerController.IsSprinting()) ? 2f : 1f;
        }
        else
        {
            speedParam = hSpeed >= runThreshold ? 2f : 1f;
        }

        _anim.SetFloat(H_Speed, speedParam, 0.1f, Time.deltaTime);
    }

    // ── Death NetworkVariable callback (fires on ALL clients) ─────────────────

    private void OnDeadChanged(bool prev, bool next)
    {
        if (next && !_wasDead)
        {
            _anim.SetTrigger(H_Die);
            _anim.SetBool(H_IsDead, true);
            _wasDead = true;
        }
        else if (!next)
        {
            _anim.ResetTrigger(H_Die);
            _anim.SetBool(H_IsDead, false);
            _wasDead = false;
        }
    }
}