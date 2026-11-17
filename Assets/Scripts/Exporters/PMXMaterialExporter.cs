using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Material;
using LibMMD.Model;
using UnityEngine;

/// <summary>Merges simplified PMX material records without editing Unity assets or vertex indices.</summary>
internal static class PMXMaterialExporter
{
    private sealed class Group
    {
        internal int Source;
        internal Part Part;
        internal readonly List<int> Indices = new List<int>();
        internal readonly HashSet<(int, int, int)> Faces = new HashSet<(int, int, int)>();
    }

    internal static void Merge(RawMMDModel model, IReadOnlyList<Material> sources)
    {
        if (model.Parts.Length != sources.Count)
            throw new ArgumentException("PMX material/source count mismatch");

        // Independent material animation must retain its own target. Global
        // morphs can still apply once to the merged material table.
        var controlled = new HashSet<int>();
        foreach (var morph in model.Morphs)
            if (morph.Type == Morph.MorphType.MorphTypeMaterial)
                foreach (Morph.MaterialMorphData data in morph.MorphDatas)
                    if (!data.Global) controlled.Add(data.MaterialIndex);

        var groups = new List<Group>();
        int[] remap = new int[model.Parts.Length];
        for (int i = 0; i < model.Parts.Length; i++)
        {
            Part part = model.Parts[i];
            int destination = controlled.Contains(i) ? -1 : groups.FindIndex(g =>
                !controlled.Contains(g.Source) && Equivalent(g.Part.Material, part.Material) &&
                CompatibleSource(sources[g.Source], sources[i]));
            if (destination < 0)
            {
                destination = groups.Count;
                groups.Add(new Group { Source = i, Part = part });
            }
            remap[i] = destination;
            Group group = groups[destination];
            var partFaces = new List<(int, int, int)>();
            for (int t = part.BaseShift; t < part.BaseShift + part.TriangleIndexNum; t += 3)
            {
                int a = model.TriangleIndexes[t], b = model.TriangleIndexes[t + 1], c = model.TriangleIndexes[t + 2];
                // Cyclic rotations describe the same facing; reversed winding
                // remains distinct. Only exact vertex-index duplicates are removed.
                var face = a <= b && a <= c ? (a, b, c) : b <= c ? (b, c, a) : (c, a, b);
                if (group.Faces.Contains(face)) continue;
                partFaces.Add(face);
                group.Indices.Add(a); group.Indices.Add(b); group.Indices.Add(c);
            }
            // Preserve repeated draws inside an authored part; remove only
            // overlap introduced by another equivalent opaque material part.
            group.Faces.UnionWith(partFaces);
        }

        var triangles = new List<int>();
        foreach (Group group in groups)
        {
            group.Part.BaseShift = triangles.Count;
            group.Part.TriangleIndexNum = group.Indices.Count;
            triangles.AddRange(group.Indices);
        }
        model.Parts = groups.Select(g => g.Part).ToArray();
        model.TriangleIndexes = triangles.ToArray();
        foreach (var morph in model.Morphs)
            if (morph.Type == Morph.MorphType.MorphTypeMaterial)
                foreach (Morph.MaterialMorphData data in morph.MorphDatas)
                    if (!data.Global) data.MaterialIndex = remap[data.MaterialIndex];
    }

    private static bool SameTexture(MMDTexture a, MMDTexture b) =>
        a == b || (a != null && b != null && a.IsGlobalToon == b.IsGlobalToon &&
            string.Equals(a.TexturePath, b.TexturePath, StringComparison.Ordinal));

    private static bool Equivalent(MMDMaterial a, MMDMaterial b) =>
        // Keep editable roles such as face, iris and eyebrows distinct. Runtime
        // instances of the same role (face + line, repeated body) share a name.
        a.Name == b.Name && a.NameEn == b.NameEn &&
        a.DiffuseColor.Equals(b.DiffuseColor) && a.SpecularColor.Equals(b.SpecularColor) &&
        a.AmbientColor.Equals(b.AmbientColor) && a.Shiness.Equals(b.Shiness) &&
        a.DrawDoubleFace == b.DrawDoubleFace && a.DrawGroundShadow == b.DrawGroundShadow &&
        a.CastSelfShadow == b.CastSelfShadow && a.DrawSelfShadow == b.DrawSelfShadow &&
        a.DrawEdge == b.DrawEdge && a.EdgeColor.Equals(b.EdgeColor) && a.EdgeSize.Equals(b.EdgeSize) &&
        SameTexture(a.Texture, b.Texture) && SameTexture(a.Toon, b.Toon) &&
        SameTexture(a.SubTexture, b.SubTexture) && a.SubTextureType == b.SubTextureType &&
        a.MetaInfo == b.MetaInfo;

    private static bool CompatibleSource(Material a, Material b)
    {
        if (a == null || b == null) return false;
        // Reordering transparent parts can change compositing, even with the
        // same texture. The approved merge applies to opaque simplified parts.
        if (Transparent(a) || Transparent(b)) return false;
        if (a == b) return true;
        bool hasA = a.HasProperty("_MainTex"), hasB = b.HasProperty("_MainTex");
        if (hasA != hasB) return false;
        if (hasA && (!a.mainTextureScale.Equals(b.mainTextureScale) ||
            !a.mainTextureOffset.Equals(b.mainTextureOffset))) return false;
        // PMX cannot encode every alpha-test/UV rule. Keep different ordinary
        // surface settings separate rather than merging their source semantics.
        if (!SameProperty(a,b,"_Cutoff") || !SameProperty(a,b,"_Cull")) return false;
        foreach (string property in new[] { "_MaskColorTex", "_ZekkenNumberTex", "_ZekkenNameTex", "_EmissiveTex", "_UVEmissiveTex", "_UVEmissiveMaskTex", "_High0Tex", "_High1Tex", "_High2Tex" })
        {
            if(a.HasProperty(property)!=b.HasProperty(property))return false;
            if(a.HasProperty(property) && (a.GetTexture(property)!=b.GetTexture(property) ||
                a.GetTextureScale(property)!=b.GetTextureScale(property) || a.GetTextureOffset(property)!=b.GetTextureOffset(property)))return false;
        }
        foreach (string property in new[] { "_EmissiveColor", "_EmissionColor", "_UVEmissiveScroll", "_UVEmissiveRange", "_MaskColorR1", "_MaskColorR2", "_MaskColorG1", "_MaskColorG2", "_MaskColorB1", "_MaskColorB2" })
            if(a.HasProperty(property)!=b.HasProperty(property) || a.HasProperty(property) && a.GetVector(property)!=b.GetVector(property))return false;
        foreach(string property in new[]{"_UVEmissivePower","_EmissiveIntensity"})if(!SameProperty(a,b,property))return false;
        return a.IsKeywordEnabled("USE_MASK_COLOR")==b.IsKeywordEnabled("USE_MASK_COLOR");
    }

    private static bool Transparent(Material material) => material.renderQueue >= 3000 ||
        (material.shader != null && material.shader.name.IndexOf("Alpha", StringComparison.OrdinalIgnoreCase) >= 0);

    private static bool SameProperty(Material a, Material b, string name) =>
        a.HasProperty(name) == b.HasProperty(name) &&
        (!a.HasProperty(name) || a.GetFloat(name).Equals(b.GetFloat(name)));
}
