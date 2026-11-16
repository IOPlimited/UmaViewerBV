using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using UnityEngine;
using static LibMMD.Model.Morph;

/// <summary>Native face-material expressions and a black variation of the face shade.</summary>
internal sealed class PMXFaceMaterialExporter
{
    private sealed class Surface
    {
        internal Renderer Renderer;
        internal int Submesh, Part, Mode;
        internal Material Material;
    }

    private readonly List<Renderer> tears = new List<Renderer>();
    private readonly List<int> tearMaterials = new List<int>();
    private readonly List<Surface> surfaces = new List<Surface>();
    private Vector3 faceForward;

    internal static PMXFaceMaterialExporter Prepare(UmaContainerCharacter actor)
    {
        if (actor == null || actor.IsMini || actor.FaceDrivenKeyTarget == null) return null;
        var result = new PMXFaceMaterialExporter {
            faceForward = actor.transform.InverseTransformDirection(actor.HeadToonBaseTransform.forward).normalized
        };
        foreach (var obj in new[] { actor.StaticTear_L, actor.StaticTear_R })
            if (obj != null)
                result.tears.AddRange(obj.GetComponentsInChildren<Renderer>(true).Where(r =>
                    r.enabled && (r is SkinnedMeshRenderer || r is MeshRenderer && r.GetComponent<MeshFilter>() != null)));
        return result;
    }

    internal void IncludeTears(List<Renderer> renderers)
    {
        renderers.RemoveAll(tears.Contains);
        renderers.AddRange(tears);
    }

    internal bool IsTear(Renderer renderer) => tears.Contains(renderer);

    internal void Record(Renderer renderer, int submesh, Material material, int part)
    {
        if (IsTear(renderer)) { tearMaterials.Add(part); return; }
        string shader = material.shader.name;
        int mode = shader.Contains("ToonEye/") ? 1 : shader.Contains("ToonFace/") ? 2 : 0;
        if (mode != 0) surfaces.Add(new Surface { Renderer = renderer, Submesh = submesh,
            Material = material, Part = part, Mode = mode });
    }

    internal void Add(RawMMDModel model, PMXExportMaterials materials, List<Material> sources)
    {
        var triangles = model.TriangleIndexes.ToList();
        var parts = model.Parts.ToList();
        var morphs = model.Morphs.ToList();
        var replacements = new List<(int Source, int Layer)>();
        var high = new List<int>(); var shade = new List<int>(); var blackShade = new List<int>();
        foreach (Surface surface in surfaces)
        {
            // Draw a replacement surface with the same geometry/skin/expressions.
            // Alpha blending gives a continuous weight, without sliding UVs
            // between unrelated images or hiding the iris itself.
            var converted = materials.Convert(surface.Renderer, surface.Submesh, surface.Material,
                model, facialSurface: surface.Mode);
            var original = model.Parts[surface.Part];
            // A replacement must receive the same lighting/shadows as the
            // original surface; only its own shadow casting is disabled below.
            converted.DrawSelfShadow = original.Material.DrawSelfShadow;
            var part = new Part { BaseShift = triangles.Count, TriangleIndexNum = original.TriangleIndexNum,
                Material = converted };
            // Draw the same triangles again, with the same vertex indices. MMD's
            // LESSEQUAL depth test lets this later material blend at equal depth.
            // A displaced shell opens cracks at the eye rim and can intersect
            // the source after morphing; shared geometry has no separation or
            // independently rounded skin/morph result, including with EyeHideA.
            for (int i = original.BaseShift; i < original.BaseShift + original.TriangleIndexNum; i++)
                triangles.Add(model.TriangleIndexes[i]);
            (surface.Mode == 1 ? high : shade).Add(parts.Count);
            replacements.Add((surface.Part, parts.Count));
            parts.Add(part); sources.Add(surface.Material);
            if (surface.Mode == 2)
            {
                // The native Shade_Ctrl has one authored (usually blue) color.
                // Add the community "腹黒" expression with the same mask and
                // height gradient, overriding only the baked target color.
                var black = materials.Convert(surface.Renderer, surface.Submesh, surface.Material,
                    model, facialSurface: 2, faceShadeColor: Color.black);
                black.DrawSelfShadow = original.Material.DrawSelfShadow;
                blackShade.Add(parts.Count); replacements.Add((surface.Part, parts.Count));
                parts.Add(new Part { BaseShift = triangles.Count, TriangleIndexNum = original.TriangleIndexNum, Material = black });
                sources.Add(surface.Material);
                for (int i = original.BaseShift; i < original.BaseShift + original.TriangleIndexNum; i++)
                    triangles.Add(model.TriangleIndexes[i]);
                // Green-tagged reversed triangles are the authored nose line.
                // ToonFace renders these through its front-culling Outline
                // pass. PMX's ordinary back-culling face otherwise has a hole.
                var line = new List<int>();
                // R is the authored outline width and can be nonzero at the
                // nose tips. Only the green marker identifies this region.
                bool Nose(Vertex v) => v.ExtraUvCoordinate.Length > 2 &&
                    v.ExtraUvCoordinate[2].y > .999f && v.ExtraUvCoordinate[2].z < .001f;
                for (int i = original.BaseShift; i < original.BaseShift + original.TriangleIndexNum; i += 3)
                {
                    int a = model.TriangleIndexes[i], b = model.TriangleIndexes[i + 1], c = model.TriangleIndexes[i + 2];
                    var va = model.Vertices[a]; var vb = model.Vertices[b]; var vc = model.Vertices[c];
                    // Some heads (1033) author reversed shading normals on the
                    // nose. Culling follows winding, so compare to head front.
                    if (Nose(va) && Nose(vb) && Nose(vc) && Vector3.Dot(Vector3.Cross(
                        vb.Coordinate - va.Coordinate, vc.Coordinate - va.Coordinate), faceForward) < 0)
                    { line.Add(a); line.Add(c); line.Add(b); }
                }
                if (line.Count > 0)
                {
                    var ink = materials.Convert(surface.Renderer, surface.Submesh, surface.Material, model, facialSurface: 3);
                    ink.AmbientColor = Color.white;
                    ink.DrawGroundShadow = ink.CastSelfShadow = ink.DrawSelfShadow = false;
                    replacements.Add((surface.Part, parts.Count));
                    parts.Add(new Part { BaseShift = triangles.Count, TriangleIndexNum = line.Count, Material = ink });
                    triangles.AddRange(line); sources.Add(surface.Material);
                }
            }
        }
        void AddControl(string name, string english, List<int> indexes, MorphCategory category)
        {
            if (indexes.Count == 0) return;
            var data = new List<MorphData>();
            foreach (int index in indexes)
            {
                var material = parts[index].Material;
                var color = material.DiffuseColor; color.a = 0; material.DiffuseColor = color;
                material.DrawGroundShadow = material.CastSelfShadow = false;
                if (name == "涙") material.AmbientColor = Color.white;
                data.Add(new MaterialMorphData { MaterialIndex = index,
                    Method = MaterialMorphData.MaterialMorphMethod.MorphMatAdd, Diffuse = new Color(0, 0, 0, 1) });
            }
            morphs.Add(new Morph { Name = name, NameEn = english, Category = category,
                Type = MorphType.MorphTypeMaterial, MorphDatas = data.ToArray() });
        }
        AddControl("ハイライト消し", "HighlightOff", high, MorphCategory.MorphCatEye);
        AddControl("涙", "StaticTears", tearMaterials, MorphCategory.MorphCatOther);
        AddControl("青ざめ", "FaceShade", shade, MorphCategory.MorphCatOther);
        AddControl("腹黒", "BlackFaceShade", blackShade, MorphCategory.MorphCatOther);
        // Finish each original face/eye surface before drawing cheek, tears or
        // manga attachments. In ordinary MMD even a translucent tear edge writes
        // depth: drawing the face replacement later leaves an unshaded cutout
        // underneath it. It also paints over a previously drawn cheek layer.
        // Tears are foreground translucent attachments. Draw manga first so its
        // pattern remains visible through a partially transparent tear, rather
        // than being rejected by the tear's earlier depth write.
        var order = Enumerable.Range(0, model.Parts.Length).Where(i => !tearMaterials.Contains(i))
            .Concat(tearMaterials).SelectMany(source =>
            new[] { source }.Concat(replacements.Where(p => p.Source == source).Select(p => p.Layer))).ToArray();
        var remap = new int[parts.Count];
        var sourceMaterials = sources.ToArray();
        var rects = materials.UvRects.ToArray();
        var orderedTriangles = new List<int>(triangles.Count);
        for (int index = 0; index < order.Length; index++)
        {
            int old = order[index]; remap[old] = index;
            var part = parts[old];
            orderedTriangles.AddRange(triangles.GetRange(part.BaseShift, part.TriangleIndexNum));
            part.BaseShift = orderedTriangles.Count - part.TriangleIndexNum;
            sources[index] = sourceMaterials[old]; materials.UvRects[index] = rects[old];
        }
        foreach (Morph morph in morphs.Where(m => m.Type == MorphType.MorphTypeMaterial))
            foreach (MaterialMorphData point in morph.MorphDatas)
                if (!point.Global) point.MaterialIndex = remap[point.MaterialIndex];
        model.TriangleIndexes = orderedTriangles.ToArray();
        model.Parts = order.Select(i => parts[i]).ToArray(); model.Morphs = morphs.ToArray();
    }
}
