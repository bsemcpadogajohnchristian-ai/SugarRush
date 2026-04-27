// KillFeedUI.cs — Sugar Rush  (UPDATED: Kill icon support)
//
// ── WHAT CHANGED FROM THE PREVIOUS VERSION ───────────────────────────────────
//   • Added killIconSprite Sprite field under a new "Kill Icon" header.
//   • SpawnPrefabEntry() now finds a child Image named exactly "KillIcon"
//     and assigns killIconSprite to it. If killIconSprite is null the image
//     is disabled so the layout collapses gracefully.
//   • All other behaviour (team BGs, weapon icon, text colours) is unchanged.
//
// ── UPDATED PREFAB STRUCTURE ──────────────────────────────────────────────────
//
//   KillEntry  (root)
//     RectTransform  — Height 36, Width stretch
//     CanvasGroup    — controls fade-out alpha
//     HorizontalLayoutGroup — spacing 4, padding H:0 V:0, ChildAlignment MiddleCenter
//                             Child Force Expand Width OFF, Height OFF
//
//   ├── KillerPanel  (GameObject)
//   │     Image  [name it exactly "KillerBG"]
//   │     LayoutElement  preferredWidth=160, preferredHeight=36
//   │     HorizontalLayoutGroup  padding H:8 V:4  ChildAlignment MiddleRight
//   │     └── KillerText  (TextMeshProUGUI)
//   │           fontSize 13, Bold, Right-aligned, flexible width
//
//   ├── KillIcon  (GameObject, name exactly "KillIcon")   ← NEW
//   │     Image  — 24×24, Preserve Aspect ON
//   │     LayoutElement  preferredWidth=28, preferredHeight=24
//   │     (assign your skull / crosshair sprite here at runtime via Inspector)
//
//   ├── WeaponIcon  (GameObject, name exactly "WeaponIcon")
//   │     Image  — 32×32, Preserve Aspect ON
//   │     LayoutElement  preferredWidth=36, preferredHeight=32
//
//   │   [Optional fallback if no weapon sprite found]
//   ├── WeaponText  (TextMeshProUGUI, name exactly "WeaponText")
//   │     fontSize 11, Center-aligned — hidden when weapon sprite IS found
//
//   └── VictimPanel  (GameObject)
//         Image  [name it exactly "VictimBG"]
//         LayoutElement  preferredWidth=160, preferredHeight=36
//         HorizontalLayoutGroup  padding H:8 V:4  ChildAlignment MiddleLeft
//         └── VictimText  (TextMeshProUGUI)
//               fontSize 13, Bold, Left-aligned, flexible width
//
// ── KILLFEEDPANEL SETUP ───────────────────────────────────────────────────────
//   1. In HUDCanvas create an empty child "KillFeedPanel".
//   2. Add VerticalLayoutGroup:
//        Child Alignment = Upper Right | Spacing = 4
//        Child Force Expand Width = true, Height = false
//        Reverse Arrangement = true   ← newest entry at bottom
//   3. Add ContentSizeFitter: Vertical Fit = Preferred Size.
//   4. RectTransform: anchor bottom-right, Width 360, Height 0,
//      Pos X -10, Pos Y 10.
//   5. Assign the panel RectTransform to entryContainer.
//   6. Assign your KillEntry prefab to entryPrefab.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class KillFeedUI : MonoBehaviour
{
    public static KillFeedUI Instance { get; private set; }

    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Feed settings")]
    [Tooltip("Maximum number of entries visible at once.")]
    public int   maxEntries    = 5;
    [Tooltip("Seconds each entry stays fully visible before fading.")]
    public float entryDuration = 5f;
    [Tooltip("Seconds the fade-out takes.")]
    public float fadeDuration  = 0.6f;

    [Header("Prefab")]
    [Tooltip("Assign your KillEntry prefab here (see PREFAB STRUCTURE above).")]
    public GameObject entryPrefab;

    [Header("Container")]
    [Tooltip("The RectTransform with VerticalLayoutGroup where entries are spawned.")]
    public RectTransform entryContainer;

    [Header("Team Backgrounds")]
    [Tooltip("Background sprite used for Team A player name panels (KillerBG / VictimBG).")]
    public Sprite teamABackground;
    [Tooltip("Background sprite used for Team B player name panels (KillerBG / VictimBG).")]
    public Sprite teamBBackground;

    [Header("Kill Icon")]
    [Tooltip("A static icon (e.g. skull or crosshair) shown between the killer name and weapon icon.\n" +
             "The KillEntry prefab must have a child Image named exactly \"KillIcon\".\n" +
             "Leave this empty to hide that slot entirely.")]
    public Sprite killIconSprite;

    [Header("Weapon Icons")]
    [Tooltip("Map each weapon name to its icon sprite.\n" +
             "The name must exactly match the weaponName string in the weapon script\n" +
             "(e.g. 'Rifle', 'Shotgun', 'Sniper', 'Bazooka', 'Rocket').")]
    public List<WeaponIconEntry> weaponIcons = new();

    [Header("Text Colors")]
    [Tooltip("Text tint when the local player is the killer.")]
    public Color myKillColor  = new Color(0.45f, 1f,    0.45f, 1f);
    [Tooltip("Text tint when the local player is the victim.")]
    public Color myDeathColor = new Color(1f,    0.35f, 0.35f, 1f);
    [Tooltip("Text tint for all other entries.")]
    public Color defaultColor = new Color(0.9f,  0.9f,  0.9f,  1f);

    // ── Weapon icon entry (shown in Inspector) ────────────────────────────────

    [System.Serializable]
    public struct WeaponIconEntry
    {
        [Tooltip("Must match the weaponName string in the weapon script exactly.")]
        public string weaponName;
        public Sprite icon;
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private Dictionary<string, Sprite> _iconLookup;
    private readonly Queue<GameObject> _activeEntries = new();

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        _iconLookup = new Dictionary<string, Sprite>(System.StringComparer.OrdinalIgnoreCase);
        foreach (WeaponIconEntry e in weaponIcons)
            if (!string.IsNullOrEmpty(e.weaponName) && e.icon != null)
                _iconLookup[e.weaponName] = e.icon;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Add a kill entry. killerTeam / victimTeam drive which background sprite
    /// is applied to each name panel. killerClientId / victimClientId control
    /// the text colour (highlight for local player).
    /// </summary>
    public void AddEntry(string killerLabel, string victimLabel, string weaponLabel,
        ulong killerClientId, ulong victimClientId,
        TeamID killerTeam = TeamID.TeamA, TeamID victimTeam = TeamID.TeamA)
    {
        if (entryContainer == null)
        {
            Debug.LogWarning("[KillFeedUI] entryContainer is not assigned.");
            return;
        }

        ulong localId = Unity.Netcode.NetworkManager.Singleton != null
            ? Unity.Netcode.NetworkManager.Singleton.LocalClientId
            : ulong.MaxValue;

        Color textCol = defaultColor;
        if      (killerClientId == localId) textCol = myKillColor;
        else if (victimClientId == localId) textCol = myDeathColor;

        while (_activeEntries.Count >= maxEntries)
        {
            GameObject old = _activeEntries.Dequeue();
            if (old != null) Destroy(old);
        }

        _iconLookup.TryGetValue(weaponLabel, out Sprite weaponSprite);

        Sprite killerBgSprite = killerTeam == TeamID.TeamA ? teamABackground : teamBBackground;
        Sprite victimBgSprite = victimTeam  == TeamID.TeamA ? teamABackground : teamBBackground;

        GameObject entry = entryPrefab != null
            ? SpawnPrefabEntry(killerLabel, victimLabel, weaponLabel,
                               weaponSprite, killerBgSprite, victimBgSprite, textCol)
            : SpawnFallbackEntry(killerLabel, victimLabel, weaponLabel, textCol);

        _activeEntries.Enqueue(entry);
        StartCoroutine(LifetimeRoutine(entry));
    }

    // ── Entry building ────────────────────────────────────────────────────────

    private GameObject SpawnPrefabEntry(string killer, string victim,
        string weaponLabel, Sprite weaponSprite,
        Sprite killerBg, Sprite victimBg, Color textCol)
    {
        GameObject go = Instantiate(entryPrefab, entryContainer);
        go.SetActive(true);

        // ── Team background images ────────────────────────────────────────────
        Image killerBgImg = FindChildImage(go, "KillerBG");
        if (killerBgImg != null && killerBg != null)
        {
            killerBgImg.sprite = killerBg;
            killerBgImg.type   = Image.Type.Sliced;
        }

        Image victimBgImg = FindChildImage(go, "VictimBG");
        if (victimBgImg != null && victimBg != null)
        {
            victimBgImg.sprite = victimBg;
            victimBgImg.type   = Image.Type.Sliced;
        }

        // ── Kill icon ─────────────────────────────────────────────────────────
        // Sits between KillerPanel and WeaponIcon. Always shown when a sprite
        // is assigned; hidden (and collapsed by layout) when it is not.
        Image killIconImg = FindChildImage(go, "KillIcon");
        if (killIconImg != null)
        {
            if (killIconSprite != null)
            {
                killIconImg.sprite         = killIconSprite;
                killIconImg.preserveAspect = true;
                killIconImg.enabled        = true;
            }
            else
            {
                killIconImg.enabled = false;
            }
        }

        // ── Weapon icon ───────────────────────────────────────────────────────
        Image weaponImage = FindChildImage(go, "WeaponIcon");
        if (weaponImage != null)
        {
            if (weaponSprite != null)
            {
                weaponImage.sprite         = weaponSprite;
                weaponImage.preserveAspect = true;
                weaponImage.enabled        = true;
            }
            else
            {
                weaponImage.enabled = false;
            }
        }

        // ── Weapon text fallback (shown only when no sprite) ──────────────────
        TextMeshProUGUI weaponText = FindChildTMP(go, "WeaponText");
        if (weaponText != null)
        {
            weaponText.gameObject.SetActive(weaponSprite == null);
            if (weaponSprite == null) weaponText.text = $"[{weaponLabel}]";
        }

        // ── Killer / victim text ──────────────────────────────────────────────
        TextMeshProUGUI killerTMP = FindChildTMP(go, "KillerText");
        TextMeshProUGUI victimTMP = FindChildTMP(go, "VictimText");

        if (killerTMP != null) { killerTMP.text = killer; killerTMP.color = textCol; }
        if (victimTMP != null) { victimTMP.text = victim; victimTMP.color = textCol; }

        // ── Fallback: if the prefab uses unnamed TMPs ─────────────────────────
        if (killerTMP == null || victimTMP == null)
        {
            TextMeshProUGUI[] allTMPs = go.GetComponentsInChildren<TextMeshProUGUI>(true);
            var playerTMPs = new List<TextMeshProUGUI>();
            foreach (TextMeshProUGUI t in allTMPs)
                if (t.gameObject.name != "WeaponText") playerTMPs.Add(t);

            if (playerTMPs.Count >= 2)
            {
                playerTMPs[0].text = killer; playerTMPs[0].color = textCol;
                playerTMPs[1].text = victim; playerTMPs[1].color = textCol;
            }
            else if (playerTMPs.Count == 1)
            {
                playerTMPs[0].text  = $"{killer}  [{weaponLabel}]  {victim}";
                playerTMPs[0].color = textCol;
            }
        }

        return go;
    }

    private GameObject SpawnFallbackEntry(string killer, string victim,
        string weapon, Color col)
    {
        var go = new GameObject("KillEntry", typeof(RectTransform));
        go.transform.SetParent(entryContainer, false);

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, 36f);

        var tmp       = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = $"{killer}  <color=#aaaaaa>[{weapon}]</color>  {victim}";
        tmp.fontSize  = 13f;
        tmp.fontStyle = FontStyles.Bold;
        tmp.alignment = TextAlignmentOptions.Right;
        tmp.color     = col;
        tmp.richText  = true;

        go.SetActive(true);
        return go;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Image FindChildImage(GameObject root, string childName)
    {
        foreach (Image img in root.GetComponentsInChildren<Image>(true))
            if (img.gameObject.name == childName) return img;
        return null;
    }

    private static TextMeshProUGUI FindChildTMP(GameObject root, string childName)
    {
        foreach (TextMeshProUGUI t in root.GetComponentsInChildren<TextMeshProUGUI>(true))
            if (t.gameObject.name == childName) return t;
        return null;
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────

    private IEnumerator LifetimeRoutine(GameObject entry)
    {
        yield return new WaitForSeconds(entryDuration);
        if (entry == null) yield break;

        CanvasGroup cg = entry.GetComponent<CanvasGroup>();
        if (cg == null) cg = entry.AddComponent<CanvasGroup>();

        float elapsed = 0f;
        while (elapsed < fadeDuration && entry != null)
        {
            cg.alpha  = Mathf.Lerp(1f, 0f, elapsed / fadeDuration);
            elapsed  += Time.deltaTime;
            yield return null;
        }

        if (entry != null) Destroy(entry);
    }
}