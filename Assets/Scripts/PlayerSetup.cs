// PlayerSetup.cs
// Sugar Rush
// Unity 6.3 LTS + Netcode for GameObjects v2.1+
//
// Runs on every spawned player. Enables/disables role components
// and wires the HUD once the server-assigned role syncs to this client.

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

    [Header("Animator (optional)")]
    public Animator animator;

    [Header("Role Models")]
    [Tooltip("The Body child GameObject that holds the Shooter mesh.")]
    public GameObject bodyShooter;
    [Tooltip("The Body child GameObject that holds the Collector mesh.")]
    public GameObject bodyCollector;

    [Header("First-Person Arms (Collector only)")]
    [Tooltip("The first-person arm/hand mesh. Must be a child of CameraHolder. " +
             "Only visible to the local owner. Assign the root GameObject of the FP arms mesh.")]
    public GameObject fpArms;

    [Tooltip("The secondary camera that renders ONLY the Arms layer on top of the scene. " +
             "Must be a child of CameraHolder. Set Clear Flags=Depth Only, Depth=1, " +
             "Culling Mask=Arms only. Leave empty if not using the layer-camera system.")]
    public Camera armsCamera;

    private PlayerStats _stats;

    private void Awake()
    {
        _stats = GetComponent<PlayerStats>();

        // Disable camera immediately so non-owner players don't see a flash
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

        // FP arms are ONLY for the local owner — hide them on all other clients.
        // Each client has their own prefab instance; setting active false locally
        // does not affect what other clients render on their own instances.
        if (fpArms != null)
            fpArms.SetActive(IsOwner);

        // The arms camera only needs to run on the local owner.
        if (armsCamera != null)
            armsCamera.gameObject.SetActive(IsOwner);

        _stats.role.OnValueChanged += OnRoleChanged;
        _stats.team.OnValueChanged += OnTeamChanged;

        // Always apply the role to enable the correct components immediately.
        ApplyRole(_stats.role.Value);

        // CRITICAL NGO GOTCHA: NetworkVariable.OnValueChanged does NOT fire on a client
        // if the replicated value equals the variable's constructor default.
        // PlayerRole.Shooter == 0 == default(PlayerRole), so a client assigned as Shooter
        // will NEVER receive OnRoleChanged, and their HUD will never initialize.
        //
        // Fix: always call ResetAndInitialize on the owner directly here.
        // OnRoleChanged calls it again if the server later changes the role (e.g. Collector),
        // but for the Shooter case this is the only place it fires.
        if (IsOwner)
            StartCoroutine(InitHUDWhenReady());
    }

    // Defer one frame so ShooterController.OnNetworkSpawn and NGM.Instance have
    // had a chance to run before we wire the HUD.
    private System.Collections.IEnumerator InitHUDWhenReady()
    {
        yield return null; // wait one frame
        HUDManager hud = HUDManager.Instance;
        if (hud != null)
            hud.ResetAndInitialize(_stats);
        else
            Debug.LogWarning("[PlayerSetup] HUDManager.Instance is null after one frame — is HUDCanvas in GameScene?");
    }

    public override void OnNetworkDespawn()
    {
        _stats.role.OnValueChanged -= OnRoleChanged;
        _stats.team.OnValueChanged -= OnTeamChanged;
    }

    private void OnRoleChanged(PlayerRole prev, PlayerRole next)
    {
        ApplyRole(next);

        // Re-initialize HUD if the role changes after initial spawn.
        // Note: for the initial assignment, InitHUDWhenReady() handles it,
        // because NGO won't fire OnValueChanged if the value equals the default (0 = Shooter).
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

        // Swap visible model based on role.
        if (bodyShooter   != null) bodyShooter.SetActive(shooter);
        if (bodyCollector != null) bodyCollector.SetActive(!shooter);

        // ── FP arms / body renderer visibility ────────────────────────────────
        //
        // LOCAL OWNER sees:   FP arms (hands) — NOT their own 3rd-person body
        // OTHER clients see:  The 3rd-person body — NOT the FP arms
        //
        // We achieve this by toggling the body's Renderer components only on
        // the owner's local instance. Other clients have their own prefab copies
        // where the Renderer is still enabled — they see the body normally.
        if (IsOwner && !shooter && bodyCollector != null)
        {
            // Hide every renderer on the 3rd-person collector body from the owner's view
            foreach (Renderer r in bodyCollector.GetComponentsInChildren<Renderer>())
                r.enabled = false;
        }
        else if (!IsOwner && bodyCollector != null)
        {
            // Non-owner always sees the 3rd-person body
            foreach (Renderer r in bodyCollector.GetComponentsInChildren<Renderer>())
                r.enabled = true;
        }

        // ── Main camera culling mask: exclude "Arms" layer ────────────────────
        // This stops the FP arms from appearing through walls via the main camera.
        // The armsCamera (depth-only, higher depth) renders them on top correctly.
        if (IsOwner && playerCamera != null)
        {
            int armsLayer = LayerMask.NameToLayer("Arms");
            if (armsLayer >= 0)
                playerCamera.cullingMask &= ~(1 << armsLayer);  // exclude Arms layer
        }

        if (animator != null && animator.isInitialized)
        {
            animator.SetInteger("RoleID", (int)role);
            animator.ResetTrigger("Die");
            animator.SetBool("IsDead", false);
        }
    }
}