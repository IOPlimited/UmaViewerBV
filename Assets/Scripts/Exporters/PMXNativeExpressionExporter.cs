using System;
using System.Collections.Generic;
using System.Linq;
using Gallop.Live;
using Gallop.Live.Cutt;
using LibMMD.Model;
using static LibMMD.Model.Morph;

/// <summary>Game-authored presets reference this character's exported controls.</summary>
internal static class PMXNativeExpressionExporter
{
    private static readonly string[] CommonPrefixes = { "EyeHalf", "EyeClose", "Close", "Warai", "Ikari", "Kanasi",
        "Doya", "Futu", "Odoroki", "Jitome", "Komari", "Dere", "Kusyo" };
    private static readonly HashSet<string> CommonNames = new HashSet<string>(StringComparer.Ordinal) {
        "Base", "WinkL", "WinkR", "EyeWarai", "EyeHohoemi", "EyeUrei", "SeriousA", "SeriousB", "OutGameWaraiA", "OutGameWaraiB" };

    internal static void AddNormal(RawMMDModel model, UmaContainerCharacter character)
    {
        if (character == null || character.IsMini || character.FaceEmotionKeyTarget == null) return;
        var morphs = model.Morphs.ToList();
        var names = morphs.Select((m,i) => (m.Name,i)).ToDictionary(x => x.Name,x => x.i,StringComparer.Ordinal);
        foreach (var preset in character.FaceEmotionKeyTarget.FaceEmotionKey)
        {
            bool unique = preset.label.StartsWith("Unique",StringComparison.Ordinal);
            if (!unique && !CommonNames.Contains(preset.label) && !CommonPrefixes.Any(p => preset.label.StartsWith(p,StringComparison.Ordinal))) continue;
            if (preset.label == "Base") continue; // Neutral is all controls at zero.
            var terms = new Dictionary<string,float>(StringComparer.Ordinal);
            foreach (var key in preset.emotionKeys)
            {
                if (key.morph == null) { terms.Clear(); break; }
                string name = key.morph.name;
                terms[name] = (terms.TryGetValue(name,out float weight) ? weight : 0) + key.weight * .01f;
            }
            string liveName = preset.label == "EyeClose" ? "EyeCloseA" : preset.label;
            if (Enum.TryParse(liveName,out LiveTimelineDefine.FacialId id) && LiveFacialIdTable.TryGet((int)id,out var entry) && entry[0] != 0)
                foreach (string side in new[]{"L","R"}) terms["Ear_"+entry[0]+"_"+side] = 1;
            if (terms.Count == 0 || terms.Any(t => !names.ContainsKey(t.Key))) continue;
            AddGroup(morphs,names,preset.label,MorphCategory.MorphCatOther,terms);
        }
        model.Morphs = morphs.ToArray();
    }

    internal static int AddGroup(List<Morph> morphs, Dictionary<string,int> names, string name, MorphCategory category,
        IEnumerable<KeyValuePair<string,float>> terms)
    {
        if (names.TryGetValue(name,out int existing)) return existing;
        var data = terms.Where(t => t.Value != 0 && names.ContainsKey(t.Key)).Select(t => (MorphData)new GroupMorphData {
            MorphIndex = names[t.Key], MorphRate = t.Value }).ToArray();
        if (data.Length == 0) return -1;
        int index = morphs.Count;
        morphs.Add(new Morph { Name = name, NameEn = name, Category = category,
            Type = MorphType.MorphTypeGroup, MorphDatas = data });
        names.Add(name,index); return index;
    }

    internal static void RefreshDisplay(RawMMDModel model)
    {
        // Keep named presets and their standard counterparts prominent. Raw
        // constituents remain in PMX/VMD, but need no duplicate menu entries;
        // oversized expression frames also break the original MMD loader.
        var covered = new HashSet<int>(model.Morphs.Where(m=>m.Type==MorphType.MorphTypeGroup)
            .SelectMany(m=>m.MorphDatas.Cast<GroupMorphData>()).Select(d=>d.MorphIndex).Where(i=>
                model.Morphs[i].Name.StartsWith("Eye_",StringComparison.Ordinal)||
                model.Morphs[i].Name.StartsWith("EyeBrow_",StringComparison.Ordinal)||
                model.Morphs[i].Name.StartsWith("Mouth_",StringComparison.Ordinal)));
        var frame = model.Entrys.FirstOrDefault(f => f.IsSpecial && f.EntryItemName == "表情");
        if (frame == null) { frame = new PMXEntryItem { EntryItemName="表情",EntryItemNameEn="Expressions",IsSpecial=true };model.Entrys.Add(frame); }
        frame.Elements = Enumerable.Range(0,model.Morphs.Length).Where(i => model.Morphs[i].Category != MorphCategory.MorphCatSystem&&!covered.Contains(i))
            .Select(i => new PMXEntryItem.Element { IsMorph=true,MorphIndex=i }).ToList();
    }
}
