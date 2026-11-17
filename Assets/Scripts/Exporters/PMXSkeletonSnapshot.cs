using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>Shared PMX/VMD bone roles on the already assembled model.</summary>
internal sealed class PMXBoneProfile
{
    internal static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "Hip", "下半身" }, { "UpBody_Ctrl", "上体補助" }, { "Waist", "上半身" }, { "Spine", "上半身補助" }, { "Chest", "上半身2" }, { "Neck", "首" }, { "Head", "頭" },
        { "Shoulder_L", "左肩" }, { "Arm_L", "左腕" }, { "Elbow_L", "左ひじ" },
        { "ShoulderRoll_L", "左上腕補助" }, { "ArmRoll_L", "左前腕補助" }, { "Wrist_L", "左手首" },
        { "Shoulder_R", "右肩" }, { "Arm_R", "右腕" }, { "Elbow_R", "右ひじ" },
        { "ShoulderRoll_R", "右上腕補助" }, { "ArmRoll_R", "右前腕補助" }, { "Wrist_R", "右手首" },
        { "Thumb_01_L", "左親指０" }, { "Thumb_02_L", "左親指１" }, { "Thumb_03_L", "左親指２" },
        { "Index_01_L", "左人指１" }, { "Index_02_L", "左人指２" }, { "Index_03_L", "左人指３" },
        { "Middle_01_L", "左中指１" }, { "Middle_02_L", "左中指２" }, { "Middle_03_L", "左中指３" },
        { "Ring_01_L", "左薬指１" }, { "Ring_02_L", "左薬指２" }, { "Ring_03_L", "左薬指３" },
        { "Pinky_01_L", "左小指１" }, { "Pinky_02_L", "左小指２" }, { "Pinky_03_L", "左小指３" },
        { "Thumb_01_R", "右親指０" }, { "Thumb_02_R", "右親指１" }, { "Thumb_03_R", "右親指２" },
        { "Index_01_R", "右人指１" }, { "Index_02_R", "右人指２" }, { "Index_03_R", "右人指３" },
        { "Middle_01_R", "右中指１" }, { "Middle_02_R", "右中指２" }, { "Middle_03_R", "右中指３" },
        { "Ring_01_R", "右薬指１" }, { "Ring_02_R", "右薬指２" }, { "Ring_03_R", "右薬指３" },
        { "Pinky_01_R", "右小指１" }, { "Pinky_02_R", "右小指２" }, { "Pinky_03_R", "右小指３" },
        { "Thigh_L", "左足" }, { "Knee_L", "左ひざ" }, { "Ankle_L", "左足首" }, { "Toe_L", "左つま先" }, { "Ankle_offset_L", "左足首補助" }, { "Toe_offset_L", "左足指補助" },
        { "Thigh_R", "右足" }, { "Knee_R", "右ひざ" }, { "Ankle_R", "右足首" }, { "Toe_R", "右つま先" }, { "Ankle_offset_R", "右足首補助" }, { "Toe_offset_R", "右足指補助" },
        { "Eye_L", "左目" }, { "Eye_R", "右目" },
        { "Ear_01_L", "左耳" }, { "Ear_02_L", "左耳1" }, { "Ear_03_L", "左耳2" },
        { "Ear_01_R", "右耳" }, { "Ear_02_R", "右耳1" }, { "Ear_03_R", "右耳2" },
        { "Mouth", "口" }, { "Jaw", "顎" }, { "Chin", "顎" }
    };


    internal readonly Dictionary<string, Transform> Sources = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, Transform> CanonicalSources = new Dictionary<string, Transform>(StringComparer.Ordinal);
    internal readonly bool IsMini;

    internal PMXBoneProfile(IEnumerable<Transform> hierarchy, ISet<Transform> preferred, bool mini)
    {
        IsMini = mini;
        foreach (var group in hierarchy.GroupBy(t => t.name, StringComparer.OrdinalIgnoreCase))
            Sources[group.Key] = group.OrderByDescending(t => preferred.Contains(t)).First();
        foreach (var pair in Sources)
        {
            string name = NameForSource(pair.Key);
            if (name == null) continue;
            if (!CanonicalSources.TryGetValue(name, out var old) || !preferred.Contains(old) && preferred.Contains(pair.Value))
                CanonicalSources[name] = pair.Value;
        }
    }

    internal Transform Find(string name) => Sources.TryGetValue(name, out var source) ? source : null;
    internal string Name(Transform source)
    {
        string name = NameForSource(source.name);
        return name != null && CanonicalSources.TryGetValue(name, out var chosen) && chosen == source ? name : source.name;
    }
    private string NameForSource(string source)
    {
        if (!Names.TryGetValue(source, out string name)) return null;
        if (IsMini)
        {
            foreach (string side in new[] { "L", "R" })
                if (Find("Thumb_02_" + side) == null && source == "Thumb_01_" + side)
                    return Names["Thumb_02_" + side];
            // Mini fingers can skip the middle native index while retaining two joints.
            foreach (string finger in new[] { "Index", "Middle", "Ring", "Pinky" })
                foreach (string side in new[] { "L", "R" })
                    if (source == finger + "_03_" + side && Find(finger + "_02_" + side) == null)
                        return Names[finger + "_02_" + side];
        }
        return name;
    }
}

/// <summary>Native selection and coordinates captured before building export controls.</summary>
internal sealed class PMXSkeletonSnapshot
{
    internal sealed class Node
    {
        internal Transform Source;
        internal Transform Parent;
        internal Vector3 Position, Right, Forward;
        internal bool HasWeights;
    }
    internal readonly List<Node> Nodes = new List<Node>();
    internal readonly PMXBoneProfile Profile;
    internal readonly Transform Root;
    internal readonly Vector3 Origin;

    internal PMXSkeletonSnapshot(Transform root, Transform coordinates, IEnumerable<Renderer> renderers,
        IEnumerable<Transform> physics, bool mini)
    {
        Root = root; Origin = coordinates.InverseTransformPoint(root.position);
        var hierarchy = root.GetComponentsInChildren<Transform>(true);
        var weighted = new HashSet<Transform>();
        var selected = new HashSet<Transform>();
        foreach (var renderer in renderers)
        {
            if (renderer is SkinnedMeshRenderer skin && skin.sharedMesh != null)
            {
                var references = skin.bones;
                foreach (var weight in skin.sharedMesh.GetAllBoneWeights())
                    if (weight.weight > 0 && weight.boneIndex >= 0 && weight.boneIndex < references.Length && references[weight.boneIndex] != null)
                        weighted.Add(references[weight.boneIndex]);
            }
            else selected.Add(renderer.transform);
        }
        Profile = new PMXBoneProfile(hierarchy, weighted, mini);
        selected.UnionWith(weighted);
        selected.UnionWith(Profile.CanonicalSources.Values);
        foreach (string name in new[] { "Hand_Attach_L", "Hand_Attach_R" })
            if (Profile.Find(name) != null) selected.Add(Profile.Find(name));
        if (physics != null) selected.UnionWith(physics.Where(t => t != null));
        foreach (var source in selected.ToArray())
            for (var parent = source.parent; parent != null && parent != root; parent = parent.parent)
                selected.Add(parent);
        foreach (var source in hierarchy)
        {
            if (source == root || !selected.Contains(source)) continue;
            Nodes.Add(new Node { Source = source, Parent = source.parent,
                HasWeights = weighted.Contains(source),
                Position = coordinates.InverseTransformPoint(source.position),
                Right = coordinates.InverseTransformDirection(source.right).normalized,
                Forward = coordinates.InverseTransformDirection(source.forward).normalized });
        }
    }
}
