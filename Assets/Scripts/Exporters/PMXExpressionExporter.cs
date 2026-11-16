using System;
using System.Collections.Generic;
using System.Linq;
using Gallop;
using Gallop.Live;
using LibMMD.Model;
using UnityEngine;
using static LibMMD.Model.Morph;

/// <summary>Organized facial controls assembled from the character's own geometry.</summary>
internal static class PMXExpressionExporter
{
    private const float NoiseSqr = 1e-12f;

    // These are export interpretations of stored author data, not a change to
    // game playback or the shared Live scale whitelist. Eye_28 is deliberately
    // excluded: the audited source produces no visible lower-lash geometry.
    internal static IDisposable ApplyAuthorScales(FacialMorph morph)
    {
        bool selected = morph.name == "Mouth_54_0" || morph.name == "Mouth_55_0" ||
            morph.name == "Eye_27_L" || morph.name == "Eye_27_R";
        if (!selected) return null;
        return new AuthorScaleSample(morph);
    }

    private sealed class AuthorScaleSample : IDisposable
    {
        private readonly Dictionary<Transform, Vector3> scales = new Dictionary<Transform, Vector3>();

        internal AuthorScaleSample(FacialMorph morph)
        {
            foreach (var trs in morph.trsArray)
            {
                Transform node = trs.transform;
                if (node == null || LiveFacialTargetProperties.IsScaleBone(trs._path) || trs._scale.sqrMagnitude == 0) continue;
                if (!scales.ContainsKey(node)) scales.Add(node, node.localScale);
                node.localScale += trs._scale;
            }
        }

        public void Dispose()
        {
            foreach (var entry in scales) entry.Key.localScale = entry.Value;
        }
    }

    internal static void Add(RawMMDModel model, UmaContainerCharacter character)
    {
        var morphs = model.Morphs.ToList();
        var source = morphs.ToDictionary(m => m.Name, StringComparer.Ordinal);

        void AddControl(string name, MorphCategory category, params (string name, float weight)[] terms)
            => AddVertex(name, category, terms, null);

        void AddVertex(string name, MorphCategory category, (string name, float weight)[] terms, Vector3? axis)
        {
            if (source.ContainsKey(name) || terms.Any(t => !source.TryGetValue(t.name, out var m) ||
                m.Type != MorphType.MorphTypeVertex || m.MorphDatas.Length == 0)) return;
            var data = Combine(terms.SelectMany(t => source[t.name].MorphDatas.Cast<VertexMorphData>()
                .Select(d => new VertexMorphData { VertexIndex = d.VertexIndex, Offset = d.Offset * t.weight })), axis);
            if (data.Length == 0) return;
            var control = new Morph { Name = name, NameEn = name, Category = category,
                Type = MorphType.MorphTypeVertex, MorphDatas = data };
            morphs.Add(control); source.Add(name, control);
        }

        const MorphCategory eye = MorphCategory.MorphCatEye, mouth = MorphCategory.MorphCatMouth,
            brow = MorphCategory.MorphCatEyebrow, other = MorphCategory.MorphCatOther;

        // Keep the original raw controls and existing standard names. The exact
        // full-/half-width spelling matters when a VMD looks up a morph name.
        AddControl("ウィンク２", eye, ("Eye_2_L", 1));
        AddControl("ｳｨﾝｸ２右", eye, ("Eye_2_R", 1));
        AddControl("なごみ", eye, ("Eye_16_L", 1), ("Eye_16_R", 1));
        AddControl("にぃー", mouth, ("Mouth_26_0", 1));
        AddControl("ぷくぅ", mouth, ("Mouth_2_0", 1), ("Mouth_3_0", 1));
        AddControl("歯消し", other, ("Mouth_42_0", 1));
        AddControl("口角上げ", mouth, ("Mouth_13_0", 1));
        AddControl("口角下げ", mouth, ("Mouth_41_0", 1));
        AddControl("瞳小", eye, ("Eye_14_L", 1), ("Eye_14_R", 1));

        // Functional counterparts in the native face. Their shapes follow the
        // game character, rather than copying another model's vertex offsets.
        AddControl("悲しい", brow, ("EyeBrow_6_L", 1), ("EyeBrow_6_R", 1));
        AddControl("へ", mouth, ("Mouth_41_0", 1));
        AddControl("わはー", mouth, ("Mouth_7_0", 1));
        AddControl("キリッ", eye, ("Eye_23_L", 1), ("Eye_23_R", 1));
        AddControl("睨む", eye, ("Eye_10_L", 1), ("Eye_10_R", 1));
        AddControl("一重瞼", other, ("Eye_27_L", 1), ("Eye_27_R", 1));
        // The reference 真面目２ includes both eyebrows and the eyelid crease.
        AddControl("真面目２", brow, ("EyeBrow_18_L", 1), ("EyeBrow_18_R", 1), ("一重瞼", 1));

        // The reference width controls are mostly horizontal. Keep the native
        // full scale controls as raw morphs; expose only their horizontal
        // displacement through the two width compatibility names.
        Transform head = character == null ? null : character.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(t => t.name == "Head");
        Vector3 horizontal = head == null ? Vector3.right : character.transform.InverseTransformDirection(head.right).normalized;
        AddVertex("口広い", mouth, new[] { ("Mouth_54_0", 1f) }, horizontal);
        AddVertex("口狭い", mouth, new[] { ("Mouth_55_0", 1f) }, horizontal);

        // Reference group: surprised eyes + shouting mouth + angry eyebrows +
        // 0.4 small iris. TalkB_A_L is the native wide shouting-mouth substitute.
        // Bake the result so it needs no standalone アゲマセン or nested groups.
        AddControl("あげません", other, ("Eye_13_L", 1), ("Eye_13_R", 1), ("Mouth_34_0", 1),
            ("EyeBrow_5_L", 1), ("EyeBrow_5_R", 1), ("瞳小", .4f));

        model.Morphs = morphs.ToArray();
    }

    private static VertexMorphData[] Combine(IEnumerable<VertexMorphData> source, Vector3? axis)
    {
        var offsets = new SortedDictionary<int, Vector3>();
        foreach (var data in source)
            offsets[data.VertexIndex] = (offsets.TryGetValue(data.VertexIndex, out Vector3 previous) ? previous : Vector3.zero) + data.Offset;
        return offsets.Select(p => new VertexMorphData { VertexIndex = p.Key,
                Offset = axis.HasValue ? Vector3.Project(p.Value, axis.Value) : p.Value })
            .Where(d => d.Offset.sqrMagnitude > NoiseSqr).ToArray();
    }
}
