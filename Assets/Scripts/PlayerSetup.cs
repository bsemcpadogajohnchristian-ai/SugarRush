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
        // Both clients and server run this so every player sees the correct model.
        if (bodyShooter   != null) bodyShooter.SetActive(shooter);
        if (bodyCollector != null) bodyCollector.SetActive(!shooter);

        if (animator != null && animator.isInitialized)
        {
            animator.SetInteger("RoleID", (int)role);
            animator.ResetTrigger("Die");
            animator.SetBool("IsDead", false);
        }
    }
}