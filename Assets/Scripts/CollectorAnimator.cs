using UnityEngine;

[DefaultExecutionOrder(50)]    
[RequireComponent(typeof(Animator))]
public class CollectorAnimator : MonoBehaviour
{
    [Header("References (auto-found in Awake if left empty)")]
    public PlayerController    playerController;
    public CollectorController collectorController;
    public PlayerStats         playerStats;

    [Header("Standing speed thresholds")]
    [Tooltip("Horizontal speed (m/s) below which the character is considered idle.\n" +
             "0.5 prevents idle-jitter from micro-inputs or NT position noise.")]
    public float walkThreshold = 0.5f;

    [Header("Crouch blend normalisation")]
    [Tooltip("Peak crouch speed (m/s) used to normalise CrouchMoveX/Y to -1..1.\n" +
             "crouchSpeed(2.5) x collectorMult(1.3) = 3.25 — keep slightly above that.")]
    public float crouchMaxSpeed = 3.0f;

    [Header("Airborne detection")]
    [Tooltip("Smoothed Y velocity (m/s) above which a non-owner is considered airborne.")]
    public float airborneYThreshold = 0.6f;

    [Tooltip("Seconds to hold IsAirborne = true after the signal drops (non-owners only).\n" +
             "Bridges NT dead frames (30 Hz) so Jump_Start does not stutter.\n" +
             "0.05 s is tuned for fast-gravity games (gravity <= -20).")]
    public float airborneHoldTime = 0.05f;

    [Header("Velocity smoothing")]
    [Tooltip("EMA factor for horizontal speed used by the 1D blend tree.\n" +
             "10-15 is a good range for 30 Hz NT with 60 Hz render.")]
    public float hSpeedSmoothFactor = 12f;

    [Tooltip("EMA factor for Y velocity used by non-owner airborne detection.\n" +
             "20 rises quickly to catch jumps yet still bridges NT dead frames.")]
    public float yVelSmoothFactor = 20f;

    [Tooltip("EMA factor for CrouchMoveX/Y on non-owners.\n" +
             "6-8 is a good range. Too high = shaking; too low = laggy direction.")]
    public float crouchSmoothFactor = 7f;

    [Header("Pick-up")]
    [Tooltip("Duration (seconds) the PickUpItem clip plays after a successful pickup.")]
    public float pickupDuration = 0.6f;

    
    private static readonly int H_Speed        = Animator.StringToHash("Speed");
    private static readonly int H_CrouchX      = Animator.StringToHash("CrouchMoveX");
    private static readonly int H_CrouchY      = Animator.StringToHash("CrouchMoveY");
    private static readonly int H_IsCrouching  = Animator.StringToHash("IsCrouching");
    private static readonly int H_IsSuperspeed = Animator.StringToHash("IsSuperspeed");
    
    
    private static readonly int H_IsGrounded   = Animator.StringToHash("IsGrounded");
    private static readonly int H_IsPickingUp  = Animator.StringToHash("IsPickingUp");
    private static readonly int H_IsDead       = Animator.StringToHash("IsDead");
    private static readonly int H_Die          = Animator.StringToHash("Die");
    private static readonly int H_JumpTrigger  = Animator.StringToHash("Jump");

    
    private Animator  _anim;
    private Transform _root;

    private Vector3 _prevPos;
    private float   _smoothedHSpeed;
    private float   _smoothedYVel;      
    private float   _smoothedLocalX;
    private float   _smoothedLocalZ;
    private float   _airborneBuffer;
    private float   _pickupTimer;
    private int     _lastCarriedCount;
    private bool    _wasDead;
    private bool    _subscribedToDeath;
    private bool    _subscribedToRespawn;
    private int     _lastJumpSequence;
    private bool    _wasSuperspeed;

    
    private bool _wasAirborne;

    
    private float _jumpForceAirborneTimer;
    private const float JUMP_FORCE_AIRBORNE_TIME = 0.15f;

    
    private float _prevRawVelY;         
    private float _nonOwnerLandLatch;
    private const float NON_OWNER_LAND_LATCH_TIME = 0.15f;

    private const float TELEPORT_THRESHOLD = 3f;

    
    private float _ownerLocomotionAirborneBuffer;
    private float _nonOwnerLocomotionAirborneBuffer;
    private const float LOCOMOTION_AIRBORNE_BUFFER = 0.05f;  

    
    private void Awake()
    {
        _anim = GetComponent<Animator>();
        _root = transform.root;

        if (playerController    == null) playerController    = GetComponentInParent<PlayerController>();
        if (collectorController == null) collectorController = GetComponentInParent<CollectorController>();
        if (playerStats         == null) playerStats         = GetComponentInParent<PlayerStats>();
    }

    private void Start()
    {
        TrySubscribeToDeath();
        TrySubscribeToRespawn();

        if (collectorController != null)
            _lastCarriedCount = collectorController.GetCarriedCount();

        if (_root != null)
            _prevPos = _root.position;

        if (playerStats != null && playerStats.IsDead())
            ApplyDeathState();

        if (playerStats != null)
            _lastJumpSequence = playerStats.jumpSequence.Value;
    }

    private void OnEnable()
    {
        TrySubscribeToDeath();
        TrySubscribeToRespawn();

        if (_root != null) _prevPos = _root.position;
        else if (transform.root != null) { _root = transform.root; _prevPos = _root.position; }

        ResetRuntimeState();

        if (playerStats != null)
            _lastJumpSequence = playerStats.jumpSequence.Value;

        if (_anim != null)
            _anim.ResetTrigger(H_JumpTrigger);

        if (playerStats != null && playerStats.IsDead() && _anim != null)
            ApplyDeathState();
    }

    private void OnDestroy()
    {
        if (playerStats == null) return;
        if (_subscribedToDeath)   playerStats.isDead.OnValueChanged -= OnDeadChanged;
        if (_subscribedToRespawn) playerStats.onRespawn.RemoveListener(OnRespawn);
    }

    private void TrySubscribeToDeath()
    {
        if (_subscribedToDeath || playerStats == null) return;
        playerStats.isDead.OnValueChanged += OnDeadChanged;
        _subscribedToDeath = true;
    }

    private void TrySubscribeToRespawn()
    {
        if (_subscribedToRespawn || playerStats == null) return;
        playerStats.onRespawn.AddListener(OnRespawn);
        _subscribedToRespawn = true;
    }

    
    private void ResetRuntimeState()
    {
        _smoothedHSpeed                   = 0f;
        _smoothedYVel                     = 0f;
        _airborneBuffer                   = 0f;
        _smoothedLocalX                   = 0f;
        _smoothedLocalZ                   = 0f;
        _wasAirborne                      = false;
        _wasSuperspeed                    = false;
        _jumpForceAirborneTimer           = 0f;
        _prevRawVelY                      = 0f;
        _nonOwnerLandLatch                = 0f;
        _ownerLocomotionAirborneBuffer    = 0f;
        _nonOwnerLocomotionAirborneBuffer = 0f;
    }

    
    private void Update()
    {
        if (_anim == null) return;

        if (playerStats == null)
        {
            playerStats = GetComponentInParent<PlayerStats>();
            TrySubscribeToDeath();
            TrySubscribeToRespawn();
            if (playerStats == null) return;
        }

        if (_root == null)
        {
            _root    = transform.root;
            _prevPos = _root.position;
        }

        
        float jumpDist = Vector3.Distance(_root.position, _prevPos);
        if (jumpDist > TELEPORT_THRESHOLD)
        {
            _prevPos = _root.position;
            ResetRuntimeState();
        }

        
        Vector3 worldVel = (_root.position - _prevPos) / Time.deltaTime;
        _prevPos = _root.position;

        float rawHSpeed = new Vector3(worldVel.x, 0f, worldVel.z).magnitude;
        _smoothedHSpeed = Mathf.Lerp(_smoothedHSpeed, rawHSpeed, hSpeedSmoothFactor * Time.deltaTime);

        
        if (playerStats.IsDead())
        {
            _anim.SetBool(H_IsDead,       true);
            _anim.SetFloat(H_Speed,       0f);
            _anim.SetFloat(H_CrouchX,     0f);
            _anim.SetFloat(H_CrouchY,     0f);
            
            
            _anim.SetBool(H_IsGrounded,   true);
            _anim.SetBool(H_IsCrouching,  false);
            _anim.SetBool(H_IsSuperspeed, false);
            _anim.SetBool(H_IsPickingUp,  false);
            _wasSuperspeed = false;
            return;
        }
        _anim.SetBool(H_IsDead, false);

        bool isOwner = playerStats.IsOwner;

        
        if (collectorController != null)
        {
            int now = collectorController.GetCarriedCount();
            if (now > _lastCarriedCount) _pickupTimer = pickupDuration;
            _lastCarriedCount = now;
        }
        if (_pickupTimer > 0f) _pickupTimer -= Time.deltaTime;
        _anim.SetBool(H_IsPickingUp, _pickupTimer > 0f);

        
        bool isSuperspeed = collectorController != null
            && collectorController.superSpeedActive.Value
            && _smoothedHSpeed >= walkThreshold;

        _anim.SetBool(H_IsSuperspeed, isSuperspeed);

        
        bool jumpJustFired = playerStats.jumpSequence.Value != _lastJumpSequence;

        if (jumpJustFired)
        {
            _jumpForceAirborneTimer           = JUMP_FORCE_AIRBORNE_TIME;
            _nonOwnerLandLatch                = 0f;   
            _ownerLocomotionAirborneBuffer    = 0f;
            _nonOwnerLocomotionAirborneBuffer = 0f;
        }
        else if (_jumpForceAirborneTimer > 0f)
        {
            _jumpForceAirborneTimer -= Time.deltaTime;
        }

        bool rawAirborne;
        if (isOwner && playerController != null)
        {
            
            
            rawAirborne = !playerController.HasGroundContact()
                       || _jumpForceAirborneTimer > 0f;

            
        }
        else
        {
            
            
            _smoothedYVel = Mathf.Lerp(_smoothedYVel, worldVel.y, yVelSmoothFactor * Time.deltaTime);

            
            bool fastLanding = _prevRawVelY      < -airborneYThreshold
                            && worldVel.y >= -airborneYThreshold * 0.4f;

            if (fastLanding)
                _nonOwnerLandLatch = NON_OWNER_LAND_LATCH_TIME;
            else if (_nonOwnerLandLatch > 0f)
                _nonOwnerLandLatch -= Time.deltaTime;

            _prevRawVelY = worldVel.y;

            
            rawAirborne = (Mathf.Abs(_smoothedYVel) > airborneYThreshold
                       ||  _jumpForceAirborneTimer  > 0f)
                       && _nonOwnerLandLatch <= 0f;
        }

        bool isAirborne;
        if (isOwner && playerController != null)
        {
            isAirborne = rawAirborne;   
        }
        else
        {
            
            if (rawAirborne)
                _airborneBuffer = airborneHoldTime;
            else if (_airborneBuffer > 0f)
                _airborneBuffer -= Time.deltaTime;

            isAirborne = rawAirborne || _airborneBuffer > 0f;
        }

        
        bool justLanded = _wasAirborne && !isAirborne;
        _wasAirborne = isAirborne;

        _anim.SetBool(H_IsGrounded, !isAirborne);

        
        int currentSeq = playerStats.jumpSequence.Value;
        if (currentSeq != _lastJumpSequence)
        {
            _lastJumpSequence = currentSeq;   

            if (!justLanded)
            {
                _anim.ResetTrigger(H_JumpTrigger);
                _anim.SetTrigger(H_JumpTrigger);
            }
        }

        
        bool isCrouching = playerStats.isCrouching.Value;
        _anim.SetBool(H_IsCrouching, isCrouching);

        if (isCrouching)
        {
            float targetX, targetZ;

            if (isOwner)
            {
                targetX = Input.GetAxis("Horizontal");
                targetZ = Input.GetAxis("Vertical");
            }
            else
            {
                Vector3 local   = _root.InverseTransformDirection(worldVel);
                float rawLocalX = Mathf.Clamp(local.x / crouchMaxSpeed, -1f, 1f);
                float rawLocalZ = Mathf.Clamp(local.z / crouchMaxSpeed, -1f, 1f);
                _smoothedLocalX = Mathf.Lerp(_smoothedLocalX, rawLocalX, crouchSmoothFactor * Time.deltaTime);
                _smoothedLocalZ = Mathf.Lerp(_smoothedLocalZ, rawLocalZ, crouchSmoothFactor * Time.deltaTime);
                targetX = _smoothedLocalX;
                targetZ = _smoothedLocalZ;
            }

            _anim.SetFloat(H_CrouchX, targetX);
            _anim.SetFloat(H_CrouchY, targetZ);
        }
        else
        {
            _smoothedLocalX = 0f;
            _smoothedLocalZ = 0f;
            _anim.SetFloat(H_CrouchX, 0f);
            _anim.SetFloat(H_CrouchY, 0f);
        }

        
        bool isAirborneForLocomotion;
        if (isOwner && playerController != null)
        {
            if (!rawAirborne)
                _ownerLocomotionAirborneBuffer = LOCOMOTION_AIRBORNE_BUFFER;
            else if (_ownerLocomotionAirborneBuffer > 0f)
                _ownerLocomotionAirborneBuffer -= Time.deltaTime;

            isAirborneForLocomotion = rawAirborne && _ownerLocomotionAirborneBuffer <= 0f;
        }
        else
        {
            if (!rawAirborne)
                _nonOwnerLocomotionAirborneBuffer = LOCOMOTION_AIRBORNE_BUFFER;
            else if (_nonOwnerLocomotionAirborneBuffer > 0f)
                _nonOwnerLocomotionAirborneBuffer -= Time.deltaTime;

            isAirborneForLocomotion = isAirborne && _nonOwnerLocomotionAirborneBuffer <= 0f;
        }

        if (isAirborneForLocomotion || isCrouching)
        {
            _anim.SetFloat(H_Speed, 0f);
        }
        else
        {
            float speedParam;
            if (_smoothedHSpeed < walkThreshold)
            {
                speedParam = 0f;
            }
            else if (isOwner && playerController != null)
            {
                speedParam = (playerController.IsSprinting() || isSuperspeed) ? 2f : 1f;
            }
            else
            {
                speedParam = (playerStats.isSprinting.Value || isSuperspeed) ? 2f : 1f;
            }

            bool  superspeedJustStarted = isSuperspeed && !_wasSuperspeed;
            float dampTime              = superspeedJustStarted ? 0f : 0.1f;
            _anim.SetFloat(H_Speed, speedParam, dampTime, Time.deltaTime);
        }

        _wasSuperspeed = isSuperspeed;
    }

    
    private void OnAnimatorMove() {  }

    
    private void OnDeadChanged(bool prev, bool next)
    {
        if (next && !_wasDead)
            ApplyDeathState();
        else if (!next)
        {
            _anim.ResetTrigger(H_Die);
            _anim.SetBool(H_IsDead, false);
            _wasDead = false;
        }
    }

    private void ApplyDeathState()
    {
        _anim.SetTrigger(H_Die);
        _anim.SetBool(H_IsDead, true);
        _wasDead = true;
    }

    private void OnRespawn()
    {
        _pickupTimer      = 0f;
        _lastCarriedCount = collectorController != null
            ? collectorController.GetCarriedCount() : 0;

        if (playerStats != null)
            _lastJumpSequence = playerStats.jumpSequence.Value;

        ResetRuntimeState();

        if (_anim != null)
            _anim.ResetTrigger(H_JumpTrigger);
    }
}
