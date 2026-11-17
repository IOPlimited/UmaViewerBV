using System;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_STANDALONE || UNITY_EDITOR
using SFB;
#endif

/// <summary>PMX-specific options, shown only for a complete normal/Mini humanoid skeleton.</summary>
public static class PMXExportPanel
{
    public static void Open(UmaContainer container)
    {
        if (container == null) return;
#if UNITY_STANDALONE || UNITY_EDITOR
        if (!PMXExportOptions.IsHumanoid(container))
        {
            Save(container, new PMXExportOptions(false, false, false));
            return;
        }

        Toggle physics = null, rename = null, organize = null;
        Popup popup = Popup.Create("", 560, 360, "导出", () =>
        {
            if (container != null)
                Save(container, new PMXExportOptions(physics.isOn, rename.isOn, organize.isOn));
        }, "取消");
        Transform live = UmaViewerUI.Instance.LiveSelectPannel.transform;
        var panel = (RectTransform)popup.transform.Find("Panel");
        // Keep the existing full-screen modal blocker, replacing only its contents
        // with the actual Live entry controls, fonts and button feedback.
        foreach (Transform child in panel) child.gameObject.SetActive(false);
        Image background = panel.GetComponent<Image>(), template = live.GetComponent<Image>();
        background.sprite = template.sprite;
        background.type = template.type;
        background.color = template.color;

        var header = (RectTransform)UnityEngine.Object.Instantiate(live.Find("Title_BG"), panel);
        Place(header, new Vector2(.5f, 1), new Vector2(0, 5), new Vector2(0, 50));
        header.anchorMin = new Vector2(0, 1); header.anchorMax = Vector2.one;
        header.pivot = new Vector2(.5f, 1);
        Text title = header.GetComponentInChildren<Text>();
        title.text = "PMX 导出";
        title.rectTransform.anchorMin = Vector2.zero; title.rectTransform.anchorMax = Vector2.one;
        title.rectTransform.offsetMin = new Vector2(55, 0); title.rectTransform.offsetMax = new Vector2(-55, 0);

        Button close = UnityEngine.Object.Instantiate(live.Find("CancelButtton").GetComponent<Button>(), panel);
        close.name = "CloseButton";
        close.onClick = new Button.ButtonClickedEvent();
        close.onClick.AddListener(popup.OnRejectButtonClick);
        Place((RectTransform)close.transform, Vector2.one, new Vector2(-5, 5), new Vector2(50, 50));
        ((RectTransform)close.transform).pivot = Vector2.one;

        Toggle toggleTemplate = live.Find("StageToggle").GetComponent<Toggle>();
        physics = AddToggle(panel, toggleTemplate, "刚体生成 (WIP)", 88, false);
        rename = AddToggle(panel, toggleTemplate, "骨骼重命名", 148, true);
        organize = AddToggle(panel, toggleTemplate, "骨骼整理", 208, true);

        Button export = UnityEngine.Object.Instantiate(live.Find("PlayButtton").GetComponent<Button>(), panel);
        export.name = "ExportButton";
        export.onClick = new Button.ButtonClickedEvent();
        export.onClick.AddListener(popup.OnAcceptButtonClick);
        export.GetComponentInChildren<Text>().text = "导出";
        Place((RectTransform)export.transform, new Vector2(.5f, 0), new Vector2(0, 40), new Vector2(220, 50));
#else
        UmaViewerUI.Instance.ShowMessage("Not supported on this platform", UIMessageType.Warning);
#endif
    }

    private static void Place(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = anchor;
        rect.pivot = new Vector2(.5f, .5f);
        rect.anchoredPosition = position; rect.sizeDelta = size;
        rect.localScale = Vector3.one;
        rect.gameObject.SetActive(true);
    }

    private static Toggle AddToggle(Transform parent, Toggle template, string label, float top, bool value)
    {
        Toggle toggle = UnityEngine.Object.Instantiate(template, parent);
        toggle.name = label;
        // Drop Live's persistent callbacks and group before assigning any state.
        toggle.onValueChanged = new Toggle.ToggleEvent();
        toggle.group = null;
        toggle.interactable = true;
        toggle.toggleTransition = Toggle.ToggleTransition.None;
        Place((RectTransform)toggle.transform, new Vector2(.5f, 1), new Vector2(0, -top), new Vector2(430, 42));
        Text text = toggle.transform.Find("Label").GetComponent<Text>();
        text.text = label; text.fontSize = 22;
        text.alignment = TextAnchor.MiddleLeft;
        text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = new Vector2(58, 0); text.rectTransform.offsetMax = Vector2.zero;
        Place((RectTransform)toggle.transform.Find("Background"), new Vector2(0, .5f), new Vector2(18, 0), new Vector2(35, 35));
        toggle.SetIsOnWithoutNotify(value);
        toggle.graphic.canvasRenderer.SetAlpha(value ? 1 : 0);
        return toggle;
    }

#if UNITY_STANDALONE || UNITY_EDITOR
    private static void Save(UmaContainer container, PMXExportOptions options)
    {
        try
        {
            var character = container as UmaContainerCharacter;
            var entry = character != null ? character.CharaEntry : null;
            string name = entry != null ? $"{entry.Id}_{entry.GetName()}" : container.name;
            string path = StandaloneFileBrowser.SaveFilePanel("Save PMX File", Config.Instance.MainPath, name, "pmx");
            if (string.IsNullOrEmpty(path)) return;
            if (character != null) ModelExporter.ExportModel(character, path, options);
            else ModelExporter.ExportModel(container, path, options);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            UmaViewerUI.Instance.ShowMessage(error.Message, UIMessageType.Warning);
        }
    }
#endif
}
