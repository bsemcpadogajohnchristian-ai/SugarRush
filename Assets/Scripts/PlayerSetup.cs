using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.Universal;

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
    [Tooltip("fpArms root (child of CameraHolder). MUST start INACTIVE.")]
    public GameObject fpArms;

    [Header("First-Person Arms — Shooter")]
    [Tooltip("fpShooterArms root (child of CameraHolder). MUST start INACTIVE.")]
    public GameObject fpShooterArms;

    [Tooltip("Secondary camera — Render Type = Overlay, Culling Mask = Arms only.")]
    public Camera armsCamera;

    // ── FIX: ARMS LAYER ────────────────────────────────────────────────────────
    //
    // PROBLEM: fpShooterArms and fpArms objects were not assigned to the "Arms"
    //   layer, so the main PlayerCamera (which renders everything) drew the FP
    //   weapon on top of the 3P body weapon → two guns visible to the owner.
    //
    // FIX: SetLayerRecursively() stamps every GameObject under the FP arms root
    //   with the "Arms" layer the moment it is activated for the owner.
    //   The main camera's cullingMask already excludes the Arms layer (set below),
    //   so it never renders FP arms. ArmsCamera has cullingMask = Arms only,
    //   so it's the sole camera that draws them. Non-owners never activate the
    //   FP arms, so the layer stamp is irrelevant for them.
    //
    // IMPORTANT — Inspector setup required:
    //   1. Create a layer called "Arms" in Edit → Project Settings → Tags and Layers.
    //   2. Set ArmsCamera's Culling Mask to ONLY the "Arms" layer.
    //   3. PlayerCamera's Culling Mask is set at runtime below (it removes "Arms").
    //   You do NOT need to pre-assign any GameObjects to the Arms layer; this
    //   script does it at runtime.

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

            EnsureArmsCameraInStack();
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

        // ── Controller enable — owner-only guard ────────────────────────────
        if (shooterController   != null) shooterController.enabled   = IsOwner && shooter;
        if (collectorController != null) collectorController.enabled = IsOwner && !shooter;

        // ── 3P body GameObjects ─────────────────────────────────────────────
        if (bodyShooter   != null) bodyShooter.SetActive(shooter);
        if (bodyCollector != null) bodyCollector.SetActive(!shooter);

        // ── FP arms visibility ──────────────────────────────────────────────
        bool shooterFPReady   = IsOwner && shooter  && fpShooterArms != null;
        bool collectorFPReady = IsOwner && !shooter && fpArms        != null;

        if (fpShooterArms != null) fpShooterArms.SetActive(shooterFPReady);
        if (fpArms        != null) fpArms.SetActive(collectorFPReady);

        // ── FIX: Stamp FP arms to the Arms layer ────────────────────────────
        //
        // Do this every time the arms are activated (idempotent).
        // SetLayerRecursively walks the entire child hierarchy so newly-enabled
        // weapon children (via EquipWeapon) also land on the right layer.
        int armsLayer = LayerMask.NameToLayer("Arms");
        if (shooterFPReady)   SetLayerRecursively(fpShooterArms, armsLayer);
        if (collectorFPReady) SetLayerRecursively(fpArms,        armsLayer);

        // ── ArmsCamera ──────────────────────────────────────────────────────
        bool needArmsCamera = shooterFPReady || collectorFPReady;
        if (armsCamera != null) armsCamera.gameObject.SetActive(needArmsCamera);

        if (IsOwner && needArmsCamera)
            EnsureArmsCameraInStack();

        // ── Hide 3P body from the owner (they see FP arms instead) ─────────
        //
        // Disable renderers rather than deactivating the GameObject so that
        // components on bodyShooter (ShooterAnimator, etc.) keep running and
        // remote clients still see the 3P body via their own cameras.
        if (bodyShooter != null)
        {
            bool hideForOwner = IsOwner && shooterFPReady;
            foreach (Renderer r in bodyShooter.GetComponentsInChildren<Renderer>())
                r.enabled = !hideForOwner;
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

        // ── Owner's main camera must NOT render the Arms layer ──────────────
        //
        // ArmsCamera is the sole renderer of that layer via the URP stack.
        // Non-owners don't have an active playerCamera, so this is harmless.
        if (IsOwner && playerCamera != null)
        {
            if (armsLayer >= 0)
                playerCamera.cullingMask &= ~(1 << armsLayer);
        }

        // ── Root animator ───────────────────────────────────────────────────
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

    // ── SetLayerRecursively ─────────────────────────────────────────────────────
    //
    // Walks the entire transform hierarchy rooted at `go` and assigns `layer`
    // to every GameObject. This ensures that children spawned or activated after
    // this call (e.g. weapon GameObjects enabled by EquipWeapon) also end up on
    // the correct layer, as long as ApplyRole() is called again after any new
    // children are added.
    //
    // Safe to call with layer == -1 (LayerMask.NameToLayer returns -1 when the
    // layer name doesn't exist); the early-out below prevents invalid assignments.
    private static void SetLayerRecursively(GameObject go, int layer)
    {
        if (go == null || layer < 0) return;
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    // ── EnsureArmsCameraInStack ─────────────────────────────────────────────────
    //
    // Adds ArmsCamera to PlayerCamera's URP overlay stack if not already present.
    // In URP an Overlay camera is invisible to the renderer unless it is listed in
    // a Base camera's Stack section — SetActive(true) alone is not sufficient.
    private void EnsureArmsCameraInStack()
    {
        if (playerCamera == null || armsCamera == null) return;

        var cameraData = playerCamera.GetUniversalAdditionalCameraData();
        if (cameraData == null)
        {
            Debug.LogWarning("[PlayerSetup] PlayerCamera has no UniversalAdditionalCameraData. " +
                             "Is this a URP project?", playerCamera);
            return;
        }

        if (!cameraData.cameraStack.Contains(armsCamera))
        {
            cameraData.cameraStack.Add(armsCamera);
            Debug.Log("[PlayerSetup] ArmsCamera added to PlayerCamera URP stack.", this);
        }
    }
}