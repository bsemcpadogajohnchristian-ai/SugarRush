using UnityEngine;

[DefaultExecutionOrder(50)]   
[RequireComponent(typeof(Animator))]
public class FPArmsAnimator : MonoBehaviour
{
    [Header("References (auto-found in Awake if left empty)")]
    public CollectorController collectorController;
    public PlayerStats         playerStats;

    [Header("Input settings")]
    [Tooltip("Raw Input.GetAxis dead zone (0–1). " +
             "Axes below this are treated as no-input to prevent idle jitter. " +
             "Default 0.15 matches Unity's default input dead zone.")]
    public float inputDeadZone = 0.15f;

    [Tooltip("EMA smoothing factor for the Speed float. " +
             "Higher = snappier transitions. 12 is a good default.")]
    public float speedSmoothFactor = 12f;

    
    private static readonly int H_Speed         = Animator.StringToHash("Speed");
    private static readonly int H_Pickup        = Animator.StringToHash("Pickup");
    private static readonly int H_DeployDecoy   = Animator.StringToHash("DeployDecoy");
    private static readonly int H_ActivateSpeed = Animator.StringToHash("ActivateSpeed");
    private static readonly int H_Jump          = Animator.StringToHash("Jump");

    
    private Animator _anim;
    private float    _smoothedSpeed;
    private bool     _superSpeedWas;
    private int      _lastCarriedCount;
    private int      _lastJumpSequence;

    
    private void Awake()
    {
        _anim = GetComponent<Animator>();

        
        if (collectorController == null) collectorController = GetComponentInParent<CollectorController>();
        if (playerStats         == null) playerStats         = GetComponentInParent<PlayerStats>();
    }

    private void OnEnable()
    {
        
        if (playerStats != null)
            _lastJumpSequence = playerStats.jumpSequence.Value;

        if (collectorController != null)
        {
            
            
            collectorController.onDecoyFired.AddListener(OnDecoyFired);
            _lastCarriedCount = collectorController.GetCarriedCount();
        }

        _smoothedSpeed = 0f;
        _superSpeedWas = false;
    }

    private void OnDisable()
    {
        if (collectorController != null)
            collectorController.onDecoyFired.RemoveListener(OnDecoyFired);
    }

    
    private void Update()
    {
        
        
        if (_anim == null || playerStats == null || collectorController == null)
        {
            Debug.LogWarning("[FPArmsAnimator] Missing reference — check Inspector or " +
                             "prefab hierarchy. Ensure fpArms starts INACTIVE in the prefab.", this);
            enabled = false;
            return;
        }

        
        float targetSpeed;

        if (playerStats.isCrouching.Value)
        {
            
            
            targetSpeed = 0f;
        }
        else
        {
            float h        = Input.GetAxis("Horizontal");
            float v        = Input.GetAxis("Vertical");
            bool  hasInput = Mathf.Abs(h) > inputDeadZone || Mathf.Abs(v) > inputDeadZone;
            targetSpeed    = hasInput ? 1f : 0f;
        }

        _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, targetSpeed, speedSmoothFactor * Time.deltaTime);
        _anim.SetFloat(H_Speed, _smoothedSpeed);

        
        int nowCount = collectorController.GetCarriedCount();
        if (nowCount > _lastCarriedCount)
        {
            _anim.ResetTrigger(H_Pickup);
            _anim.SetTrigger(H_Pickup);
        }
        _lastCarriedCount = nowCount;

        
        bool isSuperspeed = collectorController.superSpeedActive.Value;
        if (isSuperspeed && !_superSpeedWas)
        {
            _anim.ResetTrigger(H_ActivateSpeed);
            _anim.SetTrigger(H_ActivateSpeed);
        }
        _superSpeedWas = isSuperspeed;

        
        int currentSeq = playerStats.jumpSequence.Value;
        if (currentSeq != _lastJumpSequence)
        {
            _lastJumpSequence = currentSeq;
            _anim.ResetTrigger(H_Jump);
            _anim.SetTrigger(H_Jump);
        }
    }

    
    private void OnDecoyFired()
    {
        _anim.ResetTrigger(H_DeployDecoy);
        _anim.SetTrigger(H_DeployDecoy);
    }

    
    public void ResetState()
    {
        _smoothedSpeed    = 0f;
        _superSpeedWas    = false;
        _lastJumpSequence = playerStats != null ? playerStats.jumpSequence.Value : 0;
        _lastCarriedCount = collectorController != null
            ? collectorController.GetCarriedCount() : 0;

        _anim.ResetTrigger(H_Pickup);
        _anim.ResetTrigger(H_DeployDecoy);
        _anim.ResetTrigger(H_ActivateSpeed);
        _anim.ResetTrigger(H_Jump);
        _anim.SetFloat(H_Speed, 0f);
    }
}
