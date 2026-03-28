// PlayerController.cs
// Sugar Rush
// Unity 6.3 LTS + Netcode for GameObjects v2.1+
//
// Handles local player movement, look, jump, crouch.
// Owner-only. Non-owners are disabled immediately.
//
// INSPECTOR SETUP:
//   groundCheck  — empty child GameObject placed at the player's feet
//   groundMask   — LayerMask set to your "Ground" layer
//   cameraHolder — the child Transform that holds the Camera

using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : NetworkBehaviour
{
    [Header("Movement speeds")]
    public float walkSpeed   = 5f;
    public float sprintSpeed = 9f;
    public float crouchSpeed = 2.5f;

    [Header("Jump & gravity")]
    public float jumpHeight = 1.5f;
    public float gravity    = -19.62f;

    [Header("Crouch")]
    public float standHeight  = 2f;
    public float crouchHeight = 1f;
    public float crouchLerp   = 8f;

    [Header("Ground detection")]
    public Transform groundCheck;
    public float     groundRadius = 0.3f;
    public LayerMask groundMask;

    [Header("Look")]
    public Transform cameraHolder;
    public float     mouseSensitivity = 2f;

    // Used by CollectorController to apply candy penalty / superspeed on top
    [HideInInspector] public float speedMultiplier = 1f;

    private CharacterController _cc;
    private PlayerStats         _stats;

    private Vector3 _velocity;
    private float   _xRot;
    private bool    _isGrounded;
    private bool    _isCrouching;
    private bool    _isSprinting;

    private void Awake()
    {
        _cc    = GetComponent<CharacterController>();
        _stats = GetComponent<PlayerStats>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            // Disable camera and audio on non-owner clones
            if (cameraHolder != null)
            {
                Camera cam = cameraHolder.GetComponentInChildren<Camera>();
                if (cam != null) cam.gameObject.SetActive(false);
                AudioListener al = cameraHolder.GetComponentInChildren<AudioListener>();
                if (al != null) al.enabled = false;
            }
            enabled = false;
            return;
        }

        Cursor.lockState = CursorLockMode.Locked;
        _cc.height = standHeight;
    }

    private void Update()
    {
        if (!IsOwner || _stats.IsDead()) return;

        Look();
        Move();
        Crouch();
    }

    private void Look()
    {
        float mx = Input.GetAxis("Mouse X") * mouseSensitivity;
        float my = Input.GetAxis("Mouse Y") * mouseSensitivity;

        _xRot = Mathf.Clamp(_xRot - my, -90f, 90f);
        cameraHolder.localRotation = Quaternion.Euler(_xRot, 0f, 0f);
        transform.Rotate(Vector3.up * mx);
    }

    private void Move()
    {
        // Ground check
        Vector3 checkPos = groundCheck != null
            ? groundCheck.position
            : transform.position + Vector3.down * (_cc.height * 0.5f);

        _isGrounded = Physics.CheckSphere(checkPos, groundRadius, groundMask, QueryTriggerInteraction.Ignore);

        if (_isGrounded && _velocity.y < 0f)
            _velocity.y = -2f;

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        _isSprinting = Input.GetKey(KeyCode.LeftShift) && !_isCrouching && v > 0f;

        float speed = _isCrouching ? crouchSpeed
                    : _isSprinting ? sprintSpeed
                    : walkSpeed;

        speed *= _stats.speedMultiplier * speedMultiplier;

        _cc.Move((transform.right * h + transform.forward * v) * speed * Time.deltaTime);

        // Jump
        if (Input.GetButtonDown("Jump") && _isGrounded && !_isCrouching)
            _velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);

        _velocity.y += gravity * Time.deltaTime;
        _cc.Move(_velocity * Time.deltaTime);
    }

    private void Crouch()
    {
        bool wantCrouch = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C);

        if (wantCrouch && !_isCrouching)
        {
            _isCrouching = true;
        }
        else if (!wantCrouch && _isCrouching)
        {
            // Raycast upward from head to check for ceiling
            Vector3 head   = transform.position + Vector3.up * _cc.height;
            float   needed = standHeight - crouchHeight;
            if (!Physics.Raycast(head, Vector3.up, needed))
                _isCrouching = false;
        }

        float targetH = _isCrouching ? crouchHeight : standHeight;
        _cc.height = Mathf.Lerp(_cc.height, targetH, crouchLerp * Time.deltaTime);
    }

    public bool IsSprinting() => _isSprinting;
    public bool IsCrouching() => _isCrouching;
    public bool IsGrounded()  => _isGrounded;

    // ── Spawn warp ────────────────────────────────────────────────────────────
    //
    // WHY THIS EXISTS:
    //   NetworkTransform is Owner Authoritative. The server cannot set a player's
    //   position and have it stick — the owning client will immediately override
    //   it with their own local position on the next NetworkTransform tick.
    //
    //   The correct pattern is:
    //     1. Server spawns player (position doesn't matter yet)
    //     2. Server sends this RPC to the OWNER ONLY (SendTo.Owner)
    //     3. Owning client disables CharacterController, sets transform.position,
    //        re-enables CharacterController
    //     4. Owner's NetworkTransform now broadcasts the correct position to ALL
    //        other clients — because the owner IS the authority
    //
    //   This guarantees every client sees every player at the correct spawn
    //   position regardless of timing or network conditions.

    [Rpc(SendTo.Owner)]
    public void WarpToSpawnRpc(Vector3 position)
    {
        // Disable CharacterController — it fights against direct transform moves
        _cc.enabled = false;
        transform.position = position;
        _velocity = Vector3.zero;   // clear any accumulated gravity
        _cc.enabled = true;

        Debug.Log($"[PlayerController] Warped to spawn position {position}");
    }
}