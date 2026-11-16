using System;
using System.Collections.Generic;
using System.Linq;
using Gallop;
using LibMMD.Model;
using UnityEngine;

/// <summary>Exports the native hidden cheek layer as a VMD-driven material expression.</summary>
internal sealed class PMXCheekExporter : IDisposable
{
    internal readonly SkinnedMeshRenderer Renderer;
    internal readonly Material Material;
    internal readonly List<int> MaterialIndexes = new List<int>();

    private PMXCheekExporter(SkinnedMeshRenderer renderer, Texture texture)
    {
        Renderer = renderer;
        Material = new Material(renderer.sharedMaterial);
        Material.mainTexture = texture;
    }

    internal static PMXCheekExporter Prepare(UmaContainerCharacter character)
    {
        if (character == null) return null;
        var renderer = character.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .FirstOrDefault(r => r.name == "M_Cheek" && r.sharedMesh != null && r.sharedMaterial != null);
        if (renderer == null) return null;
        Texture texture = character.CheekTex_0;
        if (texture == null && character.IsMini) texture = renderer.sharedMaterial.mainTexture;
        return texture != null ? new PMXCheekExporter(renderer, texture) : null;
    }

    internal void AddMorph(RawMMDModel model)
    {
        var data = new List<Morph.MorphData>();
        foreach (int index in MaterialIndexes)
        {
            var material = model.Parts[index].Material;
            Color color = material.DiffuseColor; color.a = 0;
            material.DiffuseColor = color;
            data.Add(new Morph.MaterialMorphData
            {
                MaterialIndex = index, Method = Morph.MaterialMorphData.MaterialMorphMethod.MorphMatAdd,
                Diffuse = new Color(0, 0, 0, 1)
            });
        }
        if (data.Count == 0) return;
        model.Morphs = model.Morphs.Concat(new[] { new Morph
        {
            Name = "照れ", NameEn = "Blush", Category = Morph.MorphCategory.MorphCatOther,
            Type = Morph.MorphType.MorphTypeMaterial, MorphDatas = data.ToArray()
        } }).ToArray();
    }

    public void Dispose() => UnityEngine.Object.DestroyImmediate(Material);
}
