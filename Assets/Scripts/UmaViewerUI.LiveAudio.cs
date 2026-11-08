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

        RectTransform root = LiveSelectInfoText.transform as RectTransform;

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

            LiveRenderUI.Text("语音选择：", root, new Vector2(-300f, -178f), new Vector2(150f, 32f), 21);

            _vocalModeDropdown = CreateDropdown(root, new Vector2(-140f, -178f), new Vector2(200f, 34f));

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
                // Solo 用 0 号（和模式下拉同一行）；Mix 时 1..6 号在下面两行铺开，
                // 每个 130 宽，够放「大和赤骥」这类四字名
                Vector2 slotPos;

                if (i == 0)
                    slotPos = new Vector2(100f, -178f);
                else
                {
                    int row = (i - 1) / 4;
                    int col = (i - 1) % 4;
                    slotPos = new Vector2(-300f + col * 140f, -222f - row * 40f);
                }

                _vocalSlotDropdowns[i] = CreateDropdown(
                    root,
                    slotPos,
                    new Vector2(i == 0 ? 130f : 130f, 32f));

                if (_vocalSlotDropdowns[i] != null)
                {
                    int slot = i;
                    _vocalSlotDropdowns[i].onValueChanged.AddListener(index => OnVocalSlotChanged(slot, index));
                }
            }
        }

        Debug.Log($"[LiveAudio] 面板 root={root.name} size={root.rect.size} pos={root.anchoredPosition} "
            + $"父级={(root.parent != null ? root.parent.name : "<无>")}");

        for (int i = 0; i < VocalSlotCount; i++)
        {
            TMP_Dropdown dd = _vocalSlotDropdowns[i];

            if (dd == null)
                continue;

            RectTransform ddRect = (RectTransform)dd.transform;
            Transform tpl = ddRect.Find("Template");

            Debug.Log($"[LiveAudio] 槽{i} dd=({ddRect.anchoredPosition.x:0},{ddRect.anchoredPosition.y:0}) "
                + $"size={ddRect.rect.size} options={dd.options.Count} value={dd.value} "
                + $"template={(tpl != null ? tpl.gameObject.activeSelf.ToString() : "<无>")}");
        }

        FillVocalSlotDropdown(0);

        // Solo concert 只有一个演唱位，多余的槽位直接收起来，不占地方也不误导。
        bool solo = _vocalModeDropdown == null || _vocalModeDropdown.value == 0;

        for (int i = 1; i < VocalSlotCount; i++)
        {
            if (_vocalSlotDropdowns[i] == null)
                continue;

            _vocalSlotDropdowns[i].gameObject.SetActive(!solo);
        }

        if (!solo)
        {
            for (int i = 1; i < VocalSlotCount; i++)
                FillVocalSlotDropdown(i);
        }
    }

    private void RefreshVocalSingerList(int songId)
    {
        _vocalSingerIds.Clear();

        if (Main == null || Main.AbSounds == null)
            return;

        // Name 可能是 "sound/live/snd_bgm_live_1001_chara_1001"，也可能是带扩展名/带前缀的写法，
        // 所以整条串上找，不依赖文件名位置。
        System.Text.RegularExpressions.Regex pattern = new System.Text.RegularExpressions.Regex(
            "snd_bgm_live_" + songId + "_chara_?([0-9]{4})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        HashSet<int> seen = new HashSet<int>();

        foreach (UmaDatabaseEntry entry in Main.AbSounds)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Name))
                continue;

            System.Text.RegularExpressions.Match m = pattern.Match(entry.Name);

            if (!m.Success)
                continue;

            if (int.TryParse(m.Groups[1].Value, out int id) && id > 0 && seen.Add(id))
                _vocalSingerIds.Add(id);
        }

        _vocalSingerIds.Sort();

        if (_vocalSingerIds.Count == 0)
        {
            // 一个都没筛到：把 AbSounds 里像音频的样本打出来，好确认 naming 到底长什么样
            int shown = 0;

            foreach (UmaDatabaseEntry entry in Main.AbSounds)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Name))
                    continue;

                if (!entry.Name.Contains("bgm_live") && !entry.Name.EndsWith("awb"))
                    continue;

                Debug.Log("[LiveAudio] AbSounds 样本: " + entry.Name);

                if (++shown >= 8)
                    break;
            }

            Debug.LogWarning($"[LiveAudio] 歌曲 {songId} 没有筛到任何歌手（prefix=snd_bgm_live_{songId}_chara*）");
        }
        else
        {
            Debug.Log($"[LiveAudio] 歌曲 {songId}: {_vocalSingerIds.Count} 位歌手 -> "
                + string.Join(",", _vocalSingerIds.ConvertAll(x => x.ToString()).ToArray()));
        }
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

        // Solo 是一个人唱全部声部：0 号和 1 号轨都归这个歌手，否则副声部还是原角色
        bool solo = _vocalModeDropdown == null || _vocalModeDropdown.value == 0;

        if (slot == 0 && solo && director.VocalCharaIds.Length > 1)
        {
            director.VocalCharaIds[1] = id;
            Debug.Log($"[LiveAudio] solo:轨道 0/1 都用歌手 {id}");
        }

        Debug.Log($"[LiveAudio] slot {slot} -> singer {id}");
    }

    private void OnVocalModeChanged(int mode)
    {
        bool solo = mode == 0;

        for (int i = 1; i < VocalSlotCount; i++)
        {
            // 首次建面板时若是 Solo，这些下拉压根没造出来，这里补上再显示
            if (_vocalSlotDropdowns[i] == null && !solo)
                BuildVocalSlotDropdown(i);

            if (_vocalSlotDropdowns[i] == null)
                continue;

            _vocalSlotDropdowns[i].gameObject.SetActive(!solo);

            if (!solo)
                FillVocalSlotDropdown(i);
        }
    }

    private void BuildVocalSlotDropdown(int i)
    {
        if (LiveSelectInfoText == null)
            return;

        RectTransform root = LiveSelectInfoText.transform as RectTransform;

        if (root == null)
            return;

        int row = (i - 1) / 4;
        int col = (i - 1) % 4;

        _vocalSlotDropdowns[i] = CreateDropdown(
            root,
            new Vector2(-300f + col * 140f, -222f - row * 40f),
            new Vector2(130f, 32f));

        if (_vocalSlotDropdowns[i] != null)
        {
            int slot = i;
            _vocalSlotDropdowns[i].onValueChanged.AddListener(index => OnVocalSlotChanged(slot, index));
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
        background.color = Color.white;

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
        captionText.fontStyle = FontStyles.Bold;
        captionText.color = new Color(0.11f, 0.11f, 0.13f, 1f);
        captionText.alignment = TextAlignmentOptions.MidlineLeft;
        captionText.raycastTarget = false;
        captionText.enableWordWrapping = false;

        // 右侧展开箭头，和设置面板里 MSAA 那个下拉一致
        GameObject arrow = new GameObject("Arrow", typeof(RectTransform), typeof(TextMeshProUGUI));
        arrow.transform.SetParent(go.transform, false);
        RectTransform arrowRect = (RectTransform)arrow.transform;
        arrowRect.anchorMin = new Vector2(1f, 0.5f);
        arrowRect.anchorMax = new Vector2(1f, 0.5f);
        arrowRect.pivot = new Vector2(1f, 0.5f);
        arrowRect.anchoredPosition = new Vector2(-10f, 0f);
        arrowRect.sizeDelta = new Vector2(16f, 16f);

        TextMeshProUGUI arrowText = arrow.GetComponent<TextMeshProUGUI>();
        arrowText.text = "▼";
        arrowText.font = TMP_Settings.defaultFontAsset;
        arrowText.fontSize = 12f;
        arrowText.color = new Color(0.11f, 0.11f, 0.13f, 0.75f);
        arrowText.alignment = TextAlignmentOptions.Center;
        arrowText.raycastTarget = false;

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
        itemText.fontStyle = FontStyles.Bold;
        itemText.color = new Color(0.12f, 0.12f, 0.12f, 1f);
        itemText.alignment = TextAlignmentOptions.MidlineLeft;
        itemText.raycastTarget = false;
        itemText.enableWordWrapping = false;

        // 勾选标记：选中项左边显示一个对勾，跟 MSAA 下拉的列表一致
        GameObject check = new GameObject("Item Checkmark", typeof(RectTransform), typeof(TextMeshProUGUI));
        check.transform.SetParent(item.transform, false);
        RectTransform checkRect = (RectTransform)check.transform;
        checkRect.anchorMin = new Vector2(0f, 0.5f);
        checkRect.anchorMax = new Vector2(0f, 0.5f);
        checkRect.pivot = new Vector2(0f, 0.5f);
        checkRect.anchoredPosition = new Vector2(4f, 0f);
        checkRect.sizeDelta = new Vector2(16f, 16f);

        TextMeshProUGUI checkText = check.GetComponent<TextMeshProUGUI>();
        checkText.text = "✓";
        checkText.font = TMP_Settings.defaultFontAsset;
        checkText.fontSize = 14f;
        checkText.color = new Color(0.11f, 0.11f, 0.13f, 1f);
        checkText.alignment = TextAlignmentOptions.Center;
        checkText.raycastTarget = false;

        Toggle toggle = item.GetComponent<Toggle>();
        toggle.targetGraphic = templateImage;
        toggle.graphic = checkText;

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

        // 模板必须保持关闭：TMP_Dropdown 只在自己 Show() 的瞬间克隆它
        template.SetActive(false);

        return dropdown;
    }
}
