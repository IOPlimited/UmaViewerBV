using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;

/// <summary>One control list for bone visibility and the animator's display frames.</summary>
internal static class PMXBoneDisplay
{
    // Match final, unique control names, not source-name fragments or skin weights.
    // Missing controls (for example Mini fingers and toes) are simply omitted.
    private static readonly (string Name, string English, string[] Bones)[] Groups =
    {
        ("体", "Body", new[] { "センター", "グルーブ", "腰", "下半身", "上半身", "上半身2", "上半身3", "首", "頭" }),
        ("目", "Eyes", new[] { "両目", "左目", "右目" }),
        ("腕", "Arms", BothSides("肩P", "肩", "腕", "腕捩", "ひじ", "手捩", "手首")),
        ("左指", "Left Fingers", Fingers("左")),
        ("右指", "Right Fingers", Fingers("右")),
        ("足", "Legs", BothSides("足", "ひざ", "足首", "足先EX")),
        ("ＩＫ", "IK", BothSides("足IK親", "足ＩＫ", "つま先ＩＫ")),
        ("持ち物", "Attachments", BothSides("ダミー"))
    };

    private static readonly (string Name, string English)[] SecondaryGroups =
    {
        ("耳", "Ears"), ("尻尾", "Tail"), ("髪", "Hair"),
        ("スカート", "Skirt"), ("装飾", "Decorations")
    };

    private static readonly HashSet<string> VisibleNames = new HashSet<string>(
        Groups.SelectMany(group => group.Bones)) { "全ての親" };

    // Original MMD ignores VMD keys on invisible bones. These recorder channels
    // remain animatable, while AddDisplayFrames keeps them in Auxiliary.
    private static readonly HashSet<string> AnimatedAuxiliaryNames = new HashSet<string>(
        BothSides("上腕補助", "前腕補助", "足首補助", "足指補助", "つま先"))
        { "上体補助", "上半身補助", "顎" };

    internal static void ApplyVisibility(IEnumerable<Bone> bones)
    {
        foreach (Bone bone in bones)
            bone.Visible = bone.Controllable &&
                (VisibleNames.Contains(bone.Name) || AnimatedAuxiliaryNames.Contains(bone.Name) || SecondaryGroup(bone) != null);
    }

    internal static void AddDisplayFrames(RawMMDModel model)
    {
        var indexes = Enumerable.Range(0, model.Bones.Length)
            .ToDictionary(index => model.Bones[index].Name);
        var assigned = new HashSet<int>();
        if (indexes.TryGetValue("全ての親", out int root)) assigned.Add(root);

        void AddFrame(string name, string english, IEnumerable<int> boneIndexes)
        {
            var elements = new List<PMXEntryItem.Element>();
            foreach (int index in boneIndexes)
                if (assigned.Add(index))
                    elements.Add(new PMXEntryItem.Element { BoneIndex = index });
            if (elements.Count == 0) return;
            model.Entrys.Add(new PMXEntryItem
            {
                EntryItemName = name, EntryItemNameEn = english, Elements = elements
            });
        }

        foreach (var group in Groups)
            AddFrame(group.Name, group.English, group.Bones.Where(indexes.ContainsKey)
                .Select(name => indexes[name]).Where(index => model.Bones[index].Visible));
        foreach (var group in SecondaryGroups)
            AddFrame(group.Name, group.English, Enumerable.Range(0, model.Bones.Length)
                .Where(index => model.Bones[index].Visible && SecondaryGroup(model.Bones[index]) == group.Name));
        // Include hidden and automatic bones for inspection, without unhiding them
        // or changing whether they can be operated or animated.
        AddFrame("補助", "Auxiliary", Enumerable.Range(0, model.Bones.Length)
            .Where(index => !assigned.Contains(index)));
        // The PMX writer owns Root; facial export owns the expression frame.
    }

    private static string SecondaryGroup(Bone bone)
    {
        string source = bone.NameEn;
        bool Starts(string prefix) => source.StartsWith(prefix, StringComparison.Ordinal);
        if (Starts("Ear_") || Starts("Sp_He_Ear")) return "耳";
        if (source == "Tail_Ctrl" || Starts("Sp_Hi_Tail")) return "尻尾";
        if (Starts("Sp_He_Hair")) return "髪";
        if (Starts("Sp_Hi_MSkirt") || Starts("Sp_Hi_CSkirt") || Starts("Sp_Hi_Skirt")) return "スカート";
        if (Starts("Sp_") && !Starts("Sp_Ch_Bust")) return "装飾";
        return null;
    }

    private static string[] BothSides(params string[] roles) => new[] { "左", "右" }
        .SelectMany(side => roles.Select(role => side + role)).ToArray();

    private static string[] Fingers(string side) => new[] { "親指", "人指", "中指", "薬指", "小指" }
        .SelectMany(finger => Enumerable.Range(finger == "親指" ? 0 : 1, 3)
            .Select(joint => side + finger + (char)('０' + joint))).ToArray();
}
