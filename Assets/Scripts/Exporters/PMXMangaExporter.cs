using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using UnityEngine;
using static LibMMD.Model.Morph;
using static LibMMD.Model.SkinningOperator;

/// <summary>Native manga-eye attachments and their iris-hide/visibility controls.</summary>
internal sealed class PMXMangaExporter
{
    private sealed class Layer
    {
        internal int Variant;
        internal Renderer Renderer;
        internal Transform Eye;
        internal readonly List<int> Materials = new List<int>();
    }

    private readonly Transform head;
    private readonly List<Layer> layers = new List<Layer>();
    private static readonly string[] Names = { "ぐるぐる", "しいたけ", "はぅ", "漫画目丸" };

    private PMXMangaExporter(Transform head) => this.head = head;

    internal static PMXMangaExporter Prepare(UmaContainerCharacter character)
    {
        if (character == null || character.IsMini || character.FaceDrivenKeyTarget == null) return null;
        var nodes = character.GetComponentsInChildren<Transform>(true);
        var head = nodes.FirstOrDefault(t => t.name == "Head");
        if (head == null) return null;
        var result = new PMXMangaExporter(head);
        void Collect(List<GameObject> objects, string side)
        {
            var eye = nodes.FirstOrDefault(t => t.name == "Eye_" + side && t.IsChildOf(head));
            if (eye == null) return;
            for (int variant = 0; variant < Math.Min(Names.Length, objects.Count); variant++)
            {
                if (objects[variant] == null) continue;
                foreach (var renderer in objects[variant].GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mesh = renderer.GetComponent<MeshFilter>();
                    if (renderer.enabled && mesh != null && mesh.sharedMesh != null &&
                        renderer.sharedMaterial != null && renderer.sharedMaterial.mainTexture != null)
                        result.layers.Add(new Layer { Variant = variant, Renderer = renderer, Eye = eye });
                }
            }
        }
        Collect(character.LeftMangaObject, "L"); Collect(character.RightMangaObject, "R");
        return result.layers.Count > 0 ? result : null;
    }

    internal bool Contains(Renderer renderer) => layers.Any(l => l.Renderer == renderer);
    internal void RemoveSourceRenderers(List<Renderer> renderers) => renderers.RemoveAll(Contains);
    internal void AppendRenderers(List<Renderer> renderers) => renderers.AddRange(layers.Select(l => l.Renderer));
    internal void RecordMaterial(Renderer renderer, int index) => layers.First(l => l.Renderer == renderer).Materials.Add(index);

    internal void AddMorphs(RawMMDModel model, List<Renderer> renderers, Transform root, Dictionary<Transform, int> bones)
    {
        var morphs = model.Morphs.ToList();
        var hide = new List<VertexMorphData>();
        foreach (string name in new[] { "Eye_22_L", "Eye_22_R" })
        {
            var source = morphs.FirstOrDefault(m => m.Name == name && m.Type == MorphType.MorphTypeVertex);
            if (source == null) throw new InvalidOperationException("Native manga eyes require " + name);
            hide.AddRange(source.MorphDatas.Cast<VertexMorphData>());
        }
        int offset = 0;
        foreach (Renderer renderer in renderers)
        {
            var layer = layers.FirstOrDefault(l => l.Renderer == renderer);
            int count = PMXGeometryExporter.MeshOf(renderer).vertexCount;
            if (layer != null)
            {
                // The original shader ignores depth, placing its quad behind the
                // face. PMX uses depth testing: retain its authored plane/size and
                // mirror, and move only its depth onto the native iris plane.
                Vector3 center = Vector3.zero;
                for (int i = 0; i < count; i++) center += model.Vertices[offset + i].Coordinate;
                center /= count;
                Vector3 normal = root.InverseTransformDirection(-renderer.transform.forward).normalized;
                Vector3 eye = root.InverseTransformPoint(layer.Eye.position);
                // EyeHideA also reshapes the eyelids/skin in front of the iris.
                // Fit against its actual endpoint rather than guessing a fixed
                // character-independent offset or drawing through all geometry.
                string side = layer.Eye.name.EndsWith("_L", StringComparison.Ordinal) ? "L" : "R";
                var eyeHide = morphs.First(m => m.Name == "Eye_22_" + side);
                float distance = Vector3.Dot(eye - center, normal);
                foreach (VertexMorphData point in eyeHide.MorphDatas)
                    distance = Mathf.Max(distance, Vector3.Dot(
                        model.Vertices[point.VertexIndex].Coordinate + point.Offset - center, normal));
                Vector3 depth = normal * (distance + .0005f);
                for (int i = 0; i < count; i++)
                {
                    var vertex = model.Vertices[offset + i];
                    vertex.Coordinate += depth;
                    vertex.SkinningOperator = new SkinningOperator { Type = SkinningType.SkinningBdef1,
                        Param = new Bdef1 { BoneId = bones[head] } };
                }
            }
            offset += count;
        }
        for (int variant = 0; variant < Names.Length; variant++)
        {
            var selected = layers.Where(l => l.Variant == variant).ToArray();
            if (selected.Length == 0) continue;
            var visibility = new List<MorphData>();
            foreach (int index in selected.SelectMany(l => l.Materials))
            {
                var material = model.Parts[index].Material;
                Color color = material.DiffuseColor; color.a = 0; material.DiffuseColor = color;
                material.AmbientColor = Color.white; material.DrawDoubleFace = true;
                visibility.Add(new MaterialMorphData { MaterialIndex = index,
                    Method = MaterialMorphData.MaterialMorphMethod.MorphMatAdd, Diffuse = new Color(0, 0, 0, 1) });
            }
            int visible = morphs.Count;
            morphs.Add(new Morph { Name = "MangaShow" + variant, NameEn = "MangaShow" + variant,
                Category = MorphCategory.MorphCatSystem, Type = MorphType.MorphTypeMaterial, MorphDatas = visibility.ToArray() });
            int hidden = morphs.Count;
            morphs.Add(new Morph { Name = "MangaHide" + variant, NameEn = "MangaHide" + variant,
                Category = MorphCategory.MorphCatSystem, Type = MorphType.MorphTypeVertex, MorphDatas = hide.Cast<MorphData>().ToArray() });
            morphs.Add(new Morph { Name = Names[variant], NameEn = "NativeManga" + variant,
                Category = MorphCategory.MorphCatEye, Type = MorphType.MorphTypeGroup,
                MorphDatas = new MorphData[] { new GroupMorphData { MorphIndex = visible, MorphRate = 1 },
                    new GroupMorphData { MorphIndex = hidden, MorphRate = 1 } } });
        }
        model.Morphs = morphs.ToArray();
    }
}
