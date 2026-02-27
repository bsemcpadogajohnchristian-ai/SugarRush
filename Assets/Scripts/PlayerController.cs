using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class SimpleFPSController : MonoBehaviour
{
    [Header("Movement Speeds")]
    public float walkSpeed = 5f;
    public float sprintSpeed = 9f;
    public float crouchSpeed = 2.5f;

    [Header("Jump & Gravity")]
    public float jumpHeight = 1.6f;
    public float gravity = -20f; 

    [Header("Mouse Look")]
    public float mouseSensitivity = 100f;
    public Transform cameraHolder;
    public bool invertMouseY = false; 

    [Header("Crouch Settings")]
    public float standingHeight = 2f;
    public float crouchHeight = 1f;
    public float crouchTransitionSpeed = 10f;
    public float cameraStandingY = 0.8f; // Eye level
    public float cameraCrouchY = 0.2f;   

    [Header("Ground Check")]
    public Transform groundCheck;
    public float groundDistance = 0.2f;
    public LayerMask groundMask;

    private CharacterController controller;
    private Vector3 velocity;
    private bool isGrounded;
    private bool isCrouching;
    private float xRotation = 0f;

    void Start()
    {
        controller = GetComponent<CharacterController>();
        Cursor.lockState = CursorLockMode.Locked;
        
        // --- THE SINKING FIX ---
        // Forces the controller to be centered on your pivot
        controller.center = new Vector3(0, 0, 0); 
    }

    void Update()
    {
        isGrounded = Physics.CheckSphere(groundCheck.position, groundDistance, groundMask);

        HandleMouseLook();
        HandleCrouch(); 
        Move();
        Jump();
        ApplyGravity();
    }

    void HandleMouseLook()
    {
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity * Time.deltaTime;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity * Time.deltaTime;

        // FIXED INVERSION: Subtracting mouseY is standard (Up looks Up)
        xRotation -= (invertMouseY ? -mouseY : mouseY);
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);

        cameraHolder.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        transform.Rotate(Vector3.up * mouseX);
    }

    void Move()
    {
        float x = Input.GetAxis("Horizontal");
        float z = Input.GetAxis("Vertical");
        Vector3 move = transform.right * x + transform.forward * z;

        float speed = walkSpeed;
        if (Input.GetKey(KeyCode.LeftShift) && !isCrouching) speed = sprintSpeed;
        if (isCrouching) speed = crouchSpeed;

        controller.Move(move * speed * Time.deltaTime);
    }

    void HandleCrouch()
    {
        isCrouching = Input.GetKey(KeyCode.LeftControl);
        float targetHeight = isCrouching ? crouchHeight : standingHeight;
        
        // Smoothly change height
        controller.height = Mathf.Lerp(controller.height, targetHeight, crouchTransitionSpeed * Time.deltaTime);

        // --- THE "GOING UP" FIX ---
        // Since your pivot is in the center, we KEEP the center at 0.
        // This makes the capsule shrink from BOTH top and bottom equally.
        controller.center = Vector3.zero;

        // Move camera to match the eye level
        float targetCamY = isCrouching ? cameraCrouchY : cameraStandingY;
        Vector3 camPos = cameraHolder.localPosition;
        camPos.y = Mathf.Lerp(camPos.y, targetCamY, crouchTransitionSpeed * Time.deltaTime);
        cameraHolder.localPosition = camPos;
    }

    void Jump()
    {
        if (Input.GetButtonDown("Jump") && isGrounded && !isCrouching)
        {
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }
    }

    void ApplyGravity()
    {
        if (isGrounded && velocity.y < 0)
        {
            velocity.y = -2f; 
        }
        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);
    }
}