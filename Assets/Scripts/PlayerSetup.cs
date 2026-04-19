using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(PlayerStats))]
public class PlayerSetup : NetworkBehaviour
{
    [Header("Role components (assign in Player prefab Inspector)")]
    public ShooterController   shooterController;
    public CollectorController collectorController;

    [Header("Camera & audio")]
    public Camera        playerCamera;
    public AudioListener audioListener;

    [Header("Animator (optional — on root)")]
    public Animator animator;

    [Header("Role Models")]
    [Tooltip("The Body child that holds the Shooter mesh and ShooterAnimator.")]
    public GameObject bodyShooter;
    [Tooltip("The Body child that holds the Collector mesh and CollectorAnimator.")]
    public GameObject bodyCollector;

    [Header("First-Person Arms — Collector")]
    [Tooltip("fpArms root (child of CameraHolder). MUST start INACTIVE.\n" +
             "When assigned, the Collector owner's bodyCollector renderer is\n" +
             "hidden (they see FP arms instead).")]
    public GameObject fpArms;

    [Header("First-Person Arms — Shooter")]
    [Tooltip("fpShooterArms root (child of CameraHolder). MUST start INACTIVE.\n" +
             "When assigned, the Shooter owner's bodyShooter renderer is hidden.\n" +
             "Leave unassigned while testing TPP — body stays visible.")]
    public GameObject fpShooterArms;

    [Tooltip("Secondary camera — Depth Only, Depth=1, Culling Mask=Arms only.\n" +
             "Only activated when matching FP arms object is assigned.\n" +
             "IMPORTANT: If this field is left NULL but an ArmsCamera exists in\n" +
             "the prefab and is active, it will paint over MainCamera.")]
    public Camera armsCamera;

    private PlayerStats _stats;

    private void Awake()
    {
        _stats = GetComponent<PlayerStats>();

        
        if (playerCamera  != null) playerCamera.gameObject.SetActive(false);
        if (audioListener != null) audioListener.enabled = false;
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            if (playerCamera  != null) playerCamera.gameObject.SetActive(true);
            if (audioListener != null) audioListener.enabled = true;
            Cursor.lockState = CursorLockMode.Locked;
        }

        _stats.role.OnValueChanged += OnRoleChanged;
        _stats.team.OnValueChanged += OnTeamChanged;

        ApplyRole(_stats.role.Value);

        
        if (IsOwner)
            StartCoroutine(InitHUDWhenReady());
    }

    private System.Collections.IEnumerator InitHUDWhenReady()
    {
        yield return null; 
        HUDManager hud = HUDManager.Instance;
        if (hud != null)
            hud.ResetAndInitialize(_stats);
        else
            Debug.LogWarning("[PlayerSetup] HUDManager.Instance is null after one frame.");
    }

    public override void OnNetworkDespawn()
    {
        _stats.role.OnValueChanged -= OnRoleChanged;
        _stats.team.OnValueChanged -= OnTeamChanged;
    }

    private void OnRoleChanged(PlayerRole prev, PlayerRole next)
    {
        ApplyRole(next);
        if (IsOwner)
        {
            HUDManager hud = HUDManager.Instance;
            if (hud != null) hud.ResetAndInitialize(_stats);
        }
    }

    private void OnTeamChanged(TeamID prev, TeamID next)
    {
        if (animator != null) animator.SetInteger("TeamID", (int)next);
    }

    private void ApplyRole(PlayerRole role)
    {
        bool shooter = role == PlayerRole.Shooter;

        
        if (shooterController   != null) shooterController.enabled   = shooter;
        if (collectorController != null) collectorController.enabled = !shooter;

        
        if (bodyShooter   != null) bodyShooter.SetActive(shooter);
        if (bodyCollector != null) bodyCollector.SetActive(!shooter);

        
        // FP shooter arms are disabled for now.
        // To re-enable: change this line to:
        //   bool shooterFPReady = IsOwner && shooter && fpShooterArms != null;
        bool shooterFPReady   = false;
        bool collectorFPReady = IsOwner && !shooter && fpArms != null;

        if (fpShooterArms != null) fpShooterArms.SetActive(shooterFPReady);
        if (fpArms        != null) fpArms.SetActive(collectorFPReady);

        
        bool needArmsCamera = shooterFPReady || collectorFPReady;
        if (armsCamera != null) armsCamera.gameObject.SetActive(needArmsCamera);

        
        if (bodyShooter != null)
        {
            if (IsOwner && shooterFPReady)
            {
                // FP arms active: hide the 3rd-person body so it doesn't clip the camera.
                foreach (Renderer r in bodyShooter.GetComponentsInChildren<Renderer>())
                    r.enabled = false;
            }
            else
            {
                // Owner in 3rd-person (shooterFPReady = false) OR remote client:
                // always show the body so ShooterAnimator has something to drive.
                // Without this explicit enable, if the prefab renderers ever default
                // to disabled the local owner's body becomes invisible.
                foreach (Renderer r in bodyShooter.GetComponentsInChildren<Renderer>())
                    r.enabled = true;
            }
        }

        
        if (bodyCollector != null)
        {
            if (IsOwner && collectorFPReady)
            {
                
                foreach (Renderer r in bodyCollector.GetComponentsInChildren<Renderer>())
                    r.enabled = false;
            }
            else if (!IsOwner)
            {
                
                foreach (Renderer r in bodyCollector.GetComponentsInChildren<Renderer>())
                    r.enabled = true;
            }
            
        }

        
        if (IsOwner && playerCamera != null)
        {
            int armsLayer = LayerMask.NameToLayer("Arms");
            if (armsLayer >= 0)
                playerCamera.cullingMask &= ~(1 << armsLayer);
        }

        
        if (animator != null && animator.isInitialized)
        {
            animator.SetInteger("RoleID", (int)role);
            if (_stats == null || !_stats.IsDead())
            {
                animator.ResetTrigger("Die");
                animator.SetBool("IsDead", false);
            }
        }
    }
}