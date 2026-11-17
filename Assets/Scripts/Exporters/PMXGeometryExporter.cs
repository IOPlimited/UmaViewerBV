using System;
using System.Collections.Generic;
using System.Linq;
using LibMMD.Model;
using UnityEngine;
using UnityEngine.Rendering;
using static LibMMD.Model.SkinningOperator;

/// <summary>One renderer order and one coordinate space for geometry, materials and morphs.</summary>
internal static class PMXGeometryExporter
{
    internal static List<Renderer> Collect(GameObject root) => root.GetComponentsInChildren<Renderer>()
        .Where(r => r.enabled && (r is SkinnedMeshRenderer || r is MeshRenderer && r.GetComponent<MeshFilter>() != null))
        .ToList();

    internal static Mesh MeshOf(Renderer renderer) => renderer is SkinnedMeshRenderer skin
        ? skin.sharedMesh : renderer.GetComponent<MeshFilter>().sharedMesh;

    internal static Vertex[] Read(List<Renderer> renderers, Dictionary<Transform, int> bones,
        ref List<int> triangles, Transform root)
    {
        var result = new List<Vertex>();
        Vector3[] facePositions = null, faceNormals = null;
        var character = root.GetComponent<UmaContainerCharacter>();
        if (character != null && character.IsMini &&
            renderers.FirstOrDefault(r => r.name == "M_Face") is SkinnedMeshRenderer face)
        {
            var posedFace = new Mesh();
            try
            {
                face.BakeMesh(posedFace, false);
                Matrix4x4 matrix = root.worldToLocalMatrix * face.localToWorldMatrix;
                facePositions = posedFace.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
                faceNormals = posedFace.normals.Select(n => matrix.inverse.transpose.MultiplyVector(n).normalized).ToArray();
            }
            finally { UnityEngine.Object.DestroyImmediate(posedFace); }
        }
        foreach (Renderer renderer in renderers)
        {
            Mesh source = MeshOf(renderer);
            Mesh readable = source.isReadable ? source : ModelExporter.MakeReadableMeshCopy(source);
            Mesh baked = null;
            try
            {
                var positions = readable.vertices;
                var normals = readable.normals;
                Matrix4x4 toRoot = root.worldToLocalMatrix * renderer.localToWorldMatrix;
                // Bake without renderer scale, then apply the complete renderer->root
                // matrix exactly once. Normals follow the same baked pose as positions.
                if (renderer is SkinnedMeshRenderer skin)
                {
                    baked = new Mesh(); skin.BakeMesh(baked, false);
                    positions = baked.vertices; normals = baked.normals;
                }
                Matrix4x4 normalMatrix = toRoot.inverse.transpose;
                Vector2[] uv = readable.uv, uv1 = readable.uv2, uv2 = readable.uv3;
                Color[] colors = readable.colors;
                int offset = result.Count;
                SkinningOperator[] weights = ReadWeights(renderer, source, bones);
                for (int i = 0; i < positions.Length; i++)
                    result.Add(new Vertex
                    {
                        Coordinate = toRoot.MultiplyPoint3x4(positions[i]),
                        // The mini mouth is an unlit skin patch with inward
                        // pouch normals. In PMX those normals introduce a false
                        // dent. Match the surrounding face's lighting instead.
                        Normal = renderer.name == "M_Mouth" && facePositions != null
                            ? FaceNormal(toRoot.MultiplyPoint3x4(positions[i]), facePositions, faceNormals)
                            : normalMatrix.MultiplyVector(normals.Length == positions.Length ? normals[i] : Vector3.up).normalized,
                        UvCoordinate = uv.Length == positions.Length ? Flip(uv[i]) : Vector2.zero,
                        ExtraUvCoordinate = new Vector4[] {
                            uv1.Length == positions.Length ? (Vector4)Flip(uv1[i]) : Vector4.zero,
                            uv2.Length == positions.Length ? (Vector4)Flip(uv2[i]) : Vector4.zero,
                            colors.Length == positions.Length ? (Vector4)colors[i] : Vector4.zero },
                        SkinningOperator = weights[i], EdgeScale = 1f
                    });
                bool mirrored = toRoot.determinant < 0;
                for (int part = 0; part < readable.subMeshCount; part++)
                {
                    if (readable.GetTopology(part) != MeshTopology.Triangles)
                        throw new NotSupportedException($"PMX requires triangles: {renderer.name}/{part}");
                    int[] indices = readable.GetTriangles(part);
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        triangles.Add(offset + indices[i]);
                        triangles.Add(offset + indices[i + (mirrored ? 2 : 1)]);
                        triangles.Add(offset + indices[i + (mirrored ? 1 : 2)]);
                    }
                }
            }
            finally
            {
                if (baked != null) UnityEngine.Object.DestroyImmediate(baked);
                if (readable != source) UnityEngine.Object.DestroyImmediate(readable);
            }
        }
        return result.ToArray();
    }

    private static Vector2 Flip(Vector2 uv) => new Vector2(uv.x, 1f - uv.y);

    private static Vector3 FaceNormal(Vector3 position, Vector3[] positions, Vector3[] normals)
    {
        var closest = new int[3] { -1, -1, -1 };
        var distances = new float[3] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
        for (int i = 0; i < positions.Length; i++)
        {
            float distance = (positions[i] - position).sqrMagnitude;
            for (int j = 0; j < 3; j++)
            {
                if (distance >= distances[j]) continue;
                for (int k = 2; k > j; k--) { distances[k] = distances[k - 1]; closest[k] = closest[k - 1]; }
                distances[j] = distance; closest[j] = i; break;
            }
        }
        Vector3 normal = Vector3.zero;
        for (int i = 0; i < 3; i++)
            if (closest[i] >= 0) normal += normals[closest[i]] / Mathf.Max(distances[i], 1e-12f);
        return normal.normalized;
    }

    private static SkinningOperator[] ReadWeights(Renderer renderer, Mesh mesh, Dictionary<Transform, int> bones)
    {
        int Index(Transform bone) { while (bone != null) { if (bones.TryGetValue(bone, out int index)) return index; bone = bone.parent; } return 0; }
        var output = new SkinningOperator[mesh.vertexCount];
        int fallback = Index(renderer.transform);
        if (!(renderer is SkinnedMeshRenderer skin))
        {
            for (int i = 0; i < output.Length; i++) output[i] = Single(fallback);
            return output;
        }
        var counts = mesh.GetBonesPerVertex();
        var all = mesh.GetAllBoneWeights();
        // These NativeArrays are read-only views owned by Mesh, not allocations
        // owned by the exporter. Do not Dispose them.
        Transform[] skinBones=skin.bones;
        int cursor = 0;
        for (int i = 0; i < output.Length; i++)
        {
            var combined = new Dictionary<int, float>();
            int count = counts.Length > i ? counts[i] : 0;
            for (int j = 0; j < count; j++)
            {
                BoneWeight1 w = all[cursor++];
                if (w.weight <= 0 || w.boneIndex < 0 || w.boneIndex >= skinBones.Length) continue;
                int id = Index(skinBones[w.boneIndex]);
                combined[id] = (combined.TryGetValue(id, out float previous) ? previous : 0) + w.weight;
            }
            var selected = combined.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(4).ToArray();
            float total = selected.Sum(x => x.Value);
            if (selected.Length == 0 || total <= 0) output[i] = Single(fallback);
            else if (selected.Length == 1) output[i] = Single(selected[0].Key);
            else if (selected.Length == 2)
                output[i] = new SkinningOperator { Type = SkinningType.SkinningBdef2,
                    Param = new Bdef2 { BoneId = selected.Select(x => x.Key).ToArray(), BoneWeight = selected[0].Value / total } };
            else
            {
                var ids = new int[4]; var weights = new float[4];
                for (int j = 0; j < selected.Length; j++) { ids[j] = selected[j].Key; weights[j] = selected[j].Value / total; }
                output[i] = new SkinningOperator { Type = SkinningType.SkinningBdef4, Param = new Bdef4 { BoneId = ids, BoneWeight = weights } };
            }
        }
        return output;
    }

    private static SkinningOperator Single(int id) => new SkinningOperator { Type = SkinningType.SkinningBdef1, Param = new Bdef1 { BoneId = id } };
}
