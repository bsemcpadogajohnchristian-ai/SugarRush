using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class InventoryUI : MonoBehaviour
{
    [Header("Container")]
    [Tooltip("The RectTransform where weapon cards are spawned. " +
             "Assign the SlotRow child of InventoryPanel here.")]
    public RectTransform slotContainer;

    [Header("Card dimensions")]
    public float cardWidth   = 110f;
    public float cardHeight  = 130f;
    public float cardSpacing =  12f;

    [Header("Colors")]
    public Color colorNormal   = new Color(0.12f, 0.14f, 0.22f, 0.95f);
    public Color colorSelected = new Color(0.15f, 0.38f, 0.80f, 1.00f);
    public Color colorBadgeBg  = new Color(0.06f, 0.06f, 0.12f, 1.00f);
    public Color colorText     = new Color(0.88f, 0.92f, 1.00f, 1.00f);
    public Color colorSep      = new Color(1.00f, 1.00f, 1.00f, 0.10f);

    private ShooterController    _shooter;
    private readonly List<Image> _cardBGs = new();

    
    public void Initialize(ShooterController shooter)
    {
        _shooter = shooter;
        EnsureLayout();
        Rebuild();
    }

    
    public void SetSelected(int index)
    {
        for (int i = 0; i < _cardBGs.Count; i++)
            _cardBGs[i].color = (i == index) ? colorSelected : colorNormal;
    }

    
    private void EnsureLayout()
    {
        
        
        var hlg = slotContainer.GetComponent<HorizontalLayoutGroup>();
        if (hlg == null) hlg = slotContainer.gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing                = cardSpacing;
        hlg.childAlignment         = TextAnchor.MiddleCenter;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = false;
        hlg.padding                = new RectOffset(8, 8, 0, 0);
    }

    private void Rebuild()
    {
        foreach (Transform child in slotContainer) Destroy(child.gameObject);
        _cardBGs.Clear();

        for (int i = 0; i < _shooter.availableWeapons.Count; i++)
            BuildCard(i, _shooter.availableWeapons[i]);

        SetSelected(_shooter.CurrentWeaponIndex);
    }

    private void BuildCard(int index, WeaponBase weapon)
    {
        
        var rootRt = NewRect($"Card_{index}", slotContainer, cardWidth, cardHeight);
        var bg     = rootRt.gameObject.AddComponent<Image>();
        bg.color   = colorNormal;
        _cardBGs.Add(bg);

        
        var le = rootRt.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth  = cardWidth;
        le.preferredHeight = cardHeight;

        
        var badgeRt = NewRect("Badge", rootRt, 26f, 26f);
        badgeRt.anchorMin        = new Vector2(0, 1);
        badgeRt.anchorMax        = new Vector2(0, 1);
        badgeRt.pivot            = new Vector2(0, 1);
        badgeRt.anchoredPosition = new Vector2(8, -8);
        badgeRt.gameObject.AddComponent<Image>().color = colorBadgeBg;
        AddTMP(badgeRt, (index + 1).ToString(), 13, bold: true, fill: true);

        
        var sepRt    = NewRect("Separator", rootRt, 0, 1);
        sepRt.anchorMin  = new Vector2(0.10f, 0.36f);
        sepRt.anchorMax  = new Vector2(0.90f, 0.36f);
        sepRt.sizeDelta  = new Vector2(0, 1);
        sepRt.gameObject.AddComponent<Image>().color = colorSep;

        
        var nameRt = NewRect("WeaponName", rootRt, 0, 0);
        nameRt.anchorMin = new Vector2(0,    0.04f);
        nameRt.anchorMax = new Vector2(1,    0.36f);
        nameRt.offsetMin = new Vector2(6,    0);
        nameRt.offsetMax = new Vector2(-6,   0);
        var nameTMP  = nameRt.gameObject.AddComponent<TextMeshProUGUI>();
        nameTMP.text             = weapon.weaponName.ToUpper();
        nameTMP.fontSize         = 11;
        nameTMP.fontStyle        = FontStyles.Bold;
        nameTMP.alignment        = TextAlignmentOptions.Center;
        nameTMP.color            = colorText;
        nameTMP.enableWordWrapping = false;
        nameTMP.overflowMode     = TextOverflowModes.Ellipsis;

        
        var btn  = rootRt.gameObject.AddComponent<Button>();
        var cols = btn.colors;
        cols.normalColor      = Color.white;          
        cols.highlightedColor = new Color(1.3f, 1.3f, 1.3f, 1f);
        cols.pressedColor     = new Color(0.8f, 0.8f, 0.8f, 1f);
        btn.colors            = cols;
        btn.targetGraphic     = bg;

        int captured = index;
        btn.onClick.AddListener(() =>
        {
            _shooter.EquipWeapon(captured);     
            _shooter.CloseInventory();          
        });
    }

    
    private static RectTransform NewRect(string name, RectTransform parent, float w, float h)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        if (w > 0 || h > 0) rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    private TextMeshProUGUI AddTMP(RectTransform parent, string text, float size,
        bool bold = false, bool fill = false)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        if (fill)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }
        var tmp            = go.AddComponent<TextMeshProUGUI>();
        tmp.text           = text;
        tmp.fontSize       = size;
        tmp.fontStyle      = bold ? FontStyles.Bold : FontStyles.Normal;
        tmp.alignment      = TextAlignmentOptions.Center;
        tmp.color          = colorText;
        return tmp;
    }
}
