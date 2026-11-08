using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Live 选择面板里的「音频选择」块：模式下拉（Solo concert / Mix voices）+ 每个槽位的歌手下拉。
///
/// src 分支的场景没有这块 UI，master 那份又依赖多个独立的场景容器，所以整块都用代码建。
/// 选中结果写进 Gallop.Live.Director.VocalCharaIds，AudioPlan 构建时会优先采用
/// （见 LiveSongAudioControl.Create 的 vocalCharaIds 参数）。
/// </summary>
public partial class UmaViewerUI
{
    private const int VocalSlotCount = 7;

    private TMP_Dropdown _vocalModeDropdown;
    private TMP_Dropdown[] _vocalSlotDropdowns = new TMP_Dropdown[VocalSlotCount];
    private readonly List<int> _vocalSingerIds = new List<int>();
    private int _vocalSingerSongId = -1;
    private bool _vocalLabelsBuilt;

    /// <summary>在 Live 选择面板里建/刷新音频选择控件。</summary>
    private void EnsureLiveVocalPanel(LiveEntry entry)
    {
        if (entry == null || LiveSelectInfoText == null)
            return;

        RectTransform root = LiveSelectInfoText.transform.parent as RectTransform;

        if (root == null)
            return;

        if (_vocalSingerSongId != entry.MusicId)
        {
            _vocalSingerSongId = entry.MusicId;
            RefreshVocalSingerList(entry.MusicId);
        }

        if (!_vocalLabelsBuilt)
        {
            _vocalLabelsBuilt = true;

            LiveRenderUI.Text("语音选择：", root, new Vector2(-330f, -78f), new Vector2(150f, 32f), 21);

            _vocalModeDropdown = CreateDropdown(root, new Vector2(-120f, -78f), new Vector2(280f, 34f));

            if (_vocalModeDropdown != null)
            {
                _vocalModeDropdown.ClearOptions();
                _vocalModeDropdown.options.Add(
                    new TMP_Dropdown.OptionData(UiLocalization.T("Solo concert")));
                _vocalModeDropdown.options.Add(
                    new TMP_Dropdown.OptionData(UiLocalization.T("Mix voices")));
                _vocalModeDropdown.SetValueWithoutNotify(0);
                _vocalModeDropdown.RefreshShownValue();
                _vocalModeDropdown.onValueChanged.AddListener(OnVocalModeChanged);
            }

            for (int i = 0; i < VocalSlotCount; i++)
            {
                _vocalSlotDropdowns[i] = CreateDropdown(
                    root,
                    new Vector2(-330f + i * 100f, -118f),
                    new Vector2(94f, 30f));

                if (_vocalSlotDropdowns[i] != null)
                {
                    int slot = i;
                    _vocalSlotDropdowns[i].onValueChanged.AddListener(index => OnVocalSlotChanged(slot, index));
                }
            }
        }

        for (int i = 0; i < VocalSlotCount; i++)
            FillVocalSlotDropdown(i);

        // Solo concert 只允许第 1 个槽位发声，其余下拉置灰。
        int limit = _vocalModeDropdown != null && _vocalModeDropdown.value == 0 ? 1 : VocalSlotCount;

        for (int i = 0; i < VocalSlotCount; i++)
        {
            if (_vocalSlotDropdowns[i] != null)
                _vocalSlotDropdowns[i].interactable = i < limit;
        }
    }

    private void RefreshVocalSingerList(int songId)
    {
        _vocalSingerIds.Clear();

        if (Main == null || Main.AbSounds == null)
            return;

        string prefix = "snd_bgm_live_" + songId + "_chara";
        HashSet<int> seen = new HashSet<int>();

        foreach (UmaDatabaseEntry entry in Main.AbSounds)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Name) || !entry.Name.EndsWith("awb"))
                continue;

            string file = Path.GetFileNameWithoutExtension(entry.Name);

            if (!file.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                continue;

            string[] parts = file.Split('_');

            if (parts.Length < 2)
                continue;

            if (!int.TryParse(parts[parts.Length - 2], out int id) || id <= 0)
                continue;

            if (seen.Add(id))
                _vocalSingerIds.Add(id);
        }

        _vocalSingerIds.Sort();
    }

    /// <summary>把某个槽位当前的选择写进 Director.VocalCharaIds（-1 = 自动）。</summary>
    private void FillVocalSlotDropdown(int slot)
    {
        TMP_Dropdown dd = _vocalSlotDropdowns[slot];

        if (dd == null)
            return;

        int current = CurrentVocalCharaId(slot);

        dd.ClearOptions();
        dd.options.Add(new TMP_Dropdown.OptionData(UiLocalization.T("Random")));

        int selected = 0;

        for (int i = 0; i < _vocalSingerIds.Count; i++)
        {
            int id = _vocalSingerIds[i];
            string label = LocalizeEn.GetCharaName(id, id.ToString());
            dd.options.Add(new TMP_Dropdown.OptionData(label));

            if (id == current)
                selected = i + 1;
        }

        dd.SetValueWithoutNotify(selected);
        dd.RefreshShownValue();
    }

    private int CurrentVocalCharaId(int slot)
    {
        Gallop.Live.Director director = Gallop.Live.Director.instance;

        if (director == null || director.VocalCharaIds == null || slot >= director.VocalCharaIds.Length)
            return -1;

        return director.VocalCharaIds[slot];
    }

    private void OnVocalSlotChanged(int slot, int optionIndex)
    {
        Gallop.Live.Director director = Gallop.Live.Director.instance;

        if (director == null || director.VocalCharaIds == null || slot >= director.VocalCharaIds.Length)
            return;

        // 索引 0 是 Random(-1)，其余对应 _vocalSingerIds[optionIndex - 1]
        int id = optionIndex <= 0 || optionIndex > _vocalSingerIds.Count
            ? -1
            : _vocalSingerIds[optionIndex - 1];

        director.VocalCharaIds[slot] = id;

        Debug.Log($"[LiveAudio] slot {slot} -> singer {id}");
    }

    private void OnVocalModeChanged(int mode)
    {
        // 切换模式后重新约束可编辑的槽位；不需要重建整块面板。
        int limit = mode == 0 ? 1 : VocalSlotCount;

        for (int i = 0; i < VocalSlotCount; i++)
        {
            if (_vocalSlotDropdowns[i] != null)
                _vocalSlotDropdowns[i].interactable = i < limit;
        }
    }

    /// <summary>造一个能用的 TMP_Dropdown（含 Template/Viewport/Content/Item/ScrollRect）。</summary>
    private TMP_Dropdown CreateDropdown(Transform parent, Vector2 position, Vector2 size)
    {
        GameObject go = new GameObject("Dropdown", typeof(RectTransform), typeof(Image), typeof(TMP_Dropdown));
        go.transform.SetParent(parent, false);
        go.layer = parent.gameObject.layer;

        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        Image background = go.GetComponent<Image>();
        background.color = new Color(0.95f, 0.95f, 0.95f, 0.95f);

        // Caption
        GameObject caption = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        caption.transform.SetParent(go.transform, false);
        RectTransform captionRect = (RectTransform)caption.transform;
        captionRect.anchorMin = Vector2.zero;
        captionRect.anchorMax = Vector2.one;
        captionRect.offsetMin = new Vector2(8f, 2f);
        captionRect.offsetMax = new Vector2(-8f, -2f);

        TextMeshProUGUI captionText = caption.GetComponent<TextMeshProUGUI>();
        captionText.font = TMP_Settings.defaultFontAsset;
        captionText.fontSize = 16f;
        captionText.color = new Color(0.12f, 0.12f, 0.12f, 1f);
        captionText.alignment = TextAlignmentOptions.MidlineLeft;
        captionText.raycastTarget = false;
        captionText.enableWordWrapping = false;

        // Template
        GameObject template = new GameObject("Template", typeof(RectTransform), typeof(Image), typeof(ScrollRect));
        template.transform.SetParent(go.transform, false);
        template.SetActive(false);

        RectTransform templateRect = (RectTransform)template.transform;
        templateRect.anchorMin = new Vector2(0f, 0f);
        templateRect.anchorMax = new Vector2(1f, 0f);
        templateRect.pivot = new Vector2(0.5f, 1f);
        templateRect.anchoredPosition = new Vector2(0f, 2f);
        templateRect.sizeDelta = new Vector2(0f, 180f);

        Image templateImage = template.GetComponent<Image>();
        templateImage.color = new Color(0.98f, 0.98f, 0.98f, 1f);

        GameObject viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        viewport.transform.SetParent(template.transform, false);
        RectTransform viewportRect = (RectTransform)viewport.transform;
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = new Vector2(2f, 2f);
        viewportRect.offsetMax = new Vector2(-2f, -2f);

        GameObject content = new GameObject("Content", typeof(RectTransform));
        content.transform.SetParent(viewport.transform, false);
        RectTransform contentRect = (RectTransform)content.transform;
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.sizeDelta = new Vector2(0f, 30f * System.Math.Max(1, _vocalSingerIds.Count + 1));

        GameObject item = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
        item.transform.SetParent(content.transform, false);
        RectTransform itemRect = (RectTransform)item.transform;
        itemRect.anchorMin = new Vector2(0f, 1f);
        itemRect.anchorMax = new Vector2(1f, 1f);
        itemRect.pivot = new Vector2(0.5f, 1f);
        itemRect.sizeDelta = new Vector2(0f, 30f);

        GameObject itemLabel = new GameObject("Item Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        itemLabel.transform.SetParent(item.transform, false);
        RectTransform itemLabelRect = (RectTransform)itemLabel.transform;
        itemLabelRect.anchorMin = Vector2.zero;
        itemLabelRect.anchorMax = Vector2.one;
        itemLabelRect.offsetMin = new Vector2(8f, 0f);
        itemLabelRect.offsetMax = new Vector2(-8f, 0f);

        TextMeshProUGUI itemText = itemLabel.GetComponent<TextMeshProUGUI>();
        itemText.font = TMP_Settings.defaultFontAsset;
        itemText.fontSize = 16f;
        itemText.color = new Color(0.12f, 0.12f, 0.12f, 1f);
        itemText.alignment = TextAlignmentOptions.MidlineLeft;
        itemText.raycastTarget = false;
        itemText.enableWordWrapping = false;

        Toggle toggle = item.GetComponent<Toggle>();
        toggle.targetGraphic = templateImage;   // 勾选标记留空：下拉靠高亮，不需要 checkmark

        ScrollRect scroll = template.GetComponent<ScrollRect>();
        scroll.content = contentRect;
        scroll.viewport = viewportRect;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;

        TMP_Dropdown dropdown = go.GetComponent<TMP_Dropdown>();
        dropdown.targetGraphic = background;
        dropdown.captionText = captionText;
        dropdown.itemText = itemText;
        dropdown.template = templateRect;

        return dropdown;
    }
}
