using LibMMD.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>Builds MMD controls around the assembled model's native deformation skeleton.</summary>
internal static class PMXBoneExporter
{
    internal sealed class Result
    {
        public Bone[] Bones { get; set; }
        public Dictionary<Transform, int> BoneIndexes { get; set; }
    }

    // katboi01/UmaViewer 3cb3239: native active hierarchy, only Col_ filtered,
    // no synthetic controls, dependency sorting, visibility policy or display groups.
    internal static Result BuildNative(Transform skeletonRoot, Transform coordinateRoot,
        bool renameBones, bool mini)
    {
        var sources = skeletonRoot.GetComponentsInChildren<Transform>()
            .Where(t => !t.name.Contains("Col_")).ToArray();
        var indexes = new Dictionary<Transform, int>();
        for (int i = 0; i < sources.Length; i++) indexes.Add(sources[i], i);
        var profile = new PMXBoneProfile(sources, new HashSet<Transform>(sources), mini);
        var bones = new Bone[sources.Length];
        for (int i = 0; i < sources.Length; i++)
        {
            Transform source = sources[i];
            int parent = source.parent != null && indexes.TryGetValue(source.parent, out int pi) ? pi : -1;
            int child = source.childCount > 0 && indexes.TryGetValue(source.GetChild(0), out int ci) ? ci : -1;
            bones[i] = new Bone
            {
                Name = renameBones ? profile.Name(source) : source.name, NameEn = source.name,
                Position = coordinateRoot.InverseTransformPoint(source.position), ParentIndex = parent,
                TransformLevel = 0, Visible = true, Movable = true, Rotatable = true, Controllable = true,
                ChildBoneVal = new Bone.ChildBone { ChildUseId = true, Index = child }
            };
        }
        return new Result { Bones = bones, BoneIndexes = indexes };
    }

    // Build visibility and frames using canonical roles before changing the output names.
    internal static void UseEnglishNames(IEnumerable<Bone> bones)
    {
        foreach (Bone bone in bones) bone.Name = bone.NameEn;
    }

    internal static Result Build(Transform skeletonRoot, Transform coordinateRoot, IEnumerable<Renderer> renderers,
        IEnumerable<Transform> additionalBones = null, UmaContainerCharacter character = null)
    {
        var snapshot = new PMXSkeletonSnapshot(skeletonRoot, coordinateRoot, renderers, additionalBones, character != null && character.IsMini);
        var profile = snapshot.Profile;
        var hip = profile.Find("Hip");
        var bones = new List<Bone>();
        var indexes = new Dictionary<Transform, int>();
        Vector3 hipPosition = hip != null ? coordinateRoot.InverseTransformPoint(hip.position) : snapshot.Origin;
        int all = AddVirtualBone(bones, "全ての親", "ParentOfAll", snapshot.Origin, -1, true);
        int center = AddVirtualBone(bones, "センター", "Center", snapshot.Origin, all, true);
        int groove = AddVirtualBone(bones, "グルーブ", "Groove", hipPosition, center, true);
        int waist = AddVirtualBone(bones, "腰", "WaistControl", hipPosition, groove, false);
        indexes[skeletonRoot] = center;

        foreach (var node in snapshot.Nodes)
        {
            int parent = waist;
            if (node.Source != hip)
            {
                Transform ancestor = node.Parent;
                while (ancestor != null && !indexes.ContainsKey(ancestor)) ancestor = ancestor.parent;
                if (ancestor != null) parent = indexes[ancestor];
                Transform upper = profile.Find("Waist") ?? profile.Find("Spine");
                if (ancestor == hip && upper != null && (node.Source == upper || upper.IsChildOf(node.Source))) parent = waist;
            }
            string name = profile.Name(node.Source);
            var bone = new Bone { Name = name, NameEn = node.Source.name, Position = node.Position,
                ParentIndex = parent, Rotatable = true, Movable = name.Contains("補助"),
                Controllable = true, ChildBoneVal = new Bone.ChildBone { Offset = Vector3.up * .04f } };
            indexes[node.Source] = bones.Count;
            bones.Add(bone);
        }

        foreach (string side in new[] { "L", "R" })
        {
            AddTwist(bones, indexes, profile, "Arm_" + side, "ShoulderRoll_" + side, "Elbow_" + side, side, true);
            AddTwist(bones, indexes, profile, "Elbow_" + side, "ArmRoll_" + side, "Wrist_" + side, side, false);
            AddWaistCancel(bones, indexes, profile, waist, side);
        }
        SetDisplayTails(bones, indexes, snapshot, coordinateRoot);
        AddFootIk(bones, indexes, coordinateRoot, all, "L");
        AddFootIk(bones, indexes, coordinateRoot, all, "R");
        foreach (string side in new[] { "L", "R" })
        {
            AddForefootControl(bones, indexes, profile, side);
            AddHandAttachment(bones, indexes, snapshot, side);
        }
        PMXEyeExporter.Build(bones, indexes, coordinateRoot, character);
        AllocateNames(bones);
        SortByDependencies(bones, indexes);
        PMXBoneDisplay.ApplyVisibility(bones);
        Validate(bones);
        return new Result { Bones = bones.ToArray(), BoneIndexes = indexes };
    }

    private static void AddTwist(List<Bone> bones, Dictionary<Transform, int> indexes, PMXBoneProfile profile,
        string parentName, string rollName, string childName, string side, bool upper)
    {
        var parent = profile.Find(parentName); var roll = profile.Find(rollName); var child = profile.Find(childName);
        if (parent == null || roll == null || child == null || !indexes.ContainsKey(roll)) return;
        int parentIndex = indexes[parent], rollIndex = indexes[roll], childIndex = indexes[child];
        Vector3 axis = (bones[childIndex].Position - bones[parentIndex].Position).normalized;
        if (axis.sqrMagnitude < .5f) return;
        int control = AddVirtualBone(bones, (side == "L" ? "左" : "右") + (upper ? "腕捩" : "手捩"),
            (upper ? "ArmTwist_" : "WristTwist_") + side, bones[rollIndex].Position, parentIndex, false);
        bones[control].RotAxisFixed = true;
        bones[control].RotAxis = axis;
        bones[control].ChildBoneVal = new Bone.ChildBone { Offset = axis * .04f };
        // The native Roll remains a deformation bone, with its authored weights
        // and attachments. A separate control drives it and the distal chain.
        bones[rollIndex].AppendRotate = true;
        bones[rollIndex].AppendBoneVal = new Bone.AppendBone { Index = control, Ratio = 1 };
        bones[rollIndex].Visible = false;
        bones[rollIndex].ChildBoneVal = new Bone.ChildBone { Offset = axis * .04f };
        int branch = childIndex;
        while (bones[branch].ParentIndex != parentIndex && bones[branch].ParentIndex >= 0)
            branch = bones[branch].ParentIndex;
        if (branch != rollIndex && bones[branch].ParentIndex == parentIndex) bones[branch].ParentIndex = control;
    }

    private static void AddWaistCancel(List<Bone> bones, Dictionary<Transform, int> indexes,
        PMXBoneProfile profile, int waist, string side)
    {
        var thigh = profile.Find("Thigh_" + side);
        if (thigh == null || !indexes.TryGetValue(thigh, out int index)) return;
        int cancel = AddVirtualBone(bones, "腰キャンセル" + (side == "L" ? "左" : "右"),
            "WaistCancel_" + side, bones[index].Position, bones[index].ParentIndex, false);
        bones[cancel].Visible = false; bones[cancel].Controllable = false;
        bones[cancel].AppendRotate = true;
        bones[cancel].AppendBoneVal = new Bone.AppendBone { Index = waist, Ratio = -1 };
        bones[index].ParentIndex = cancel;
    }

    private static void SetDisplayTails(List<Bone> bones, Dictionary<Transform, int> indexes,
        PMXSkeletonSnapshot snapshot, Transform coordinateRoot)
    {
        var native = snapshot.Nodes.ToDictionary(node => indexes[node.Source]);
        for (int index = 0; index < bones.Count; index++)
        {
            var bone = bones[index];
            if (bone.RotAxisFixed || bone.AppendRotate || bone.UseLocalAxis) continue;
            int child = bones.FindIndex(candidate => candidate.ParentIndex == index &&
                Vector3.SqrMagnitude(candidate.Position - bone.Position) > 1e-10f);
            bone.ChildBoneVal = child >= 0 ? new Bone.ChildBone { ChildUseId = true, Index = child } :
                new Bone.ChildBone { Offset = native.TryGetValue(index, out var node)
                    ? NativeTailOffset(node, snapshot.Root, coordinateRoot,
                        bones.Any(candidate => candidate.ParentIndex == index))
                    : Vector3.up * .04f };
        }
        var profile = snapshot.Profile;
        void Aim(string from, string to)
        {
            var a = profile.Find(from); var b = profile.Find(to);
            if (a == null || b == null || !indexes.TryGetValue(a, out int ai) || !indexes.TryGetValue(b, out int bi)) return;
            if ((bones[ai].Position - bones[bi].Position).sqrMagnitude > 1e-10f)
                bones[ai].ChildBoneVal = new Bone.ChildBone { ChildUseId = true, Index = bi };
        }
        Aim("Hip", "Waist"); Aim("Waist", profile.Find("Spine") != null ? "Spine" : "Chest");
        Aim("Spine", "Chest"); Aim("Chest", "Neck"); Aim("Neck", "Head");
        foreach (string side in new[] { "L", "R" })
        {
            Aim("Shoulder_" + side, "Arm_" + side); Aim("Arm_" + side, "Elbow_" + side);
            Aim("Elbow_" + side, "Wrist_" + side); Aim("Wrist_" + side, "Middle_01_" + side);
            Aim("Thigh_" + side, "Knee_" + side); Aim("Knee_" + side, "Ankle_" + side);
            Aim("Ankle_" + side, "Toe_" + side); Aim("Ankle_offset_" + side, "Toe_" + side);
            foreach (string finger in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
            {
                Aim(finger + "_01_" + side, profile.Find(finger + "_02_" + side) != null ? finger + "_02_" + side : finger + "_03_" + side);
                Aim(finger + "_02_" + side, finger + "_03_" + side);
            }
        }
        var head = profile.Find("Head");
        if (head != null && indexes.TryGetValue(head, out int headIndex))
            bones[headIndex].ChildBoneVal = new Bone.ChildBone { Offset = Vector3.up * .08f };
    }

    private static Vector3 NativeTailOffset(PMXSkeletonSnapshot.Node node, Transform skeletonRoot,
        Transform coordinateRoot, bool hasExportedChildren)
    {
        // An unweighted authored endpoint can be omitted from the exported bone
        // set while still providing the correct display direction and length.
        foreach (Transform child in node.Source)
        {
            if (child.GetComponent<UIHandle>() != null ||
                child.name.StartsWith("Col_", StringComparison.OrdinalIgnoreCase)) continue;
            Vector3 offset = coordinateRoot.InverseTransformPoint(child.position) - node.Position;
            if (offset.sqrMagnitude > 1e-10f) return offset;
        }

        // Pure terminal markers need no artificial length. Do not confuse an
        // unweighted control with a marker when it still has exported children.
        if (!node.HasWeights && !hasExportedChildren) return Vector3.zero;

        // Coincident offset joints are common. Continue the nearest nonzero
        // native segment instead of inventing a model-space upward direction.
        for (Transform parent = node.Parent; parent != null; parent = parent.parent)
        {
            Vector3 incoming = node.Position - coordinateRoot.InverseTransformPoint(parent.position);
            if (incoming.sqrMagnitude > 1e-10f) return incoming * .6f;
            if (parent == skeletonRoot) break;
        }
        return Vector3.zero;
    }

    private static void AddHandAttachment(List<Bone> bones, Dictionary<Transform, int> indexes,
        PMXSkeletonSnapshot snapshot, string side)
    {
        var hand = snapshot.Profile.Find("Hand_Attach_" + side);
        if (hand == null || !indexes.TryGetValue(hand, out int handIndex)) return;
        var node = snapshot.Nodes.First(n => n.Source == hand);
        bones[handIndex].Visible = false;
        bones[handIndex].ChildBoneVal = new Bone.ChildBone { Offset = node.Right * .04f };
        int control = AddVirtualBone(bones, (side == "L" ? "左" : "右") + "ダミー", "Dummy_" + side,
            node.Position, handIndex, true);
        bones[control].UseLocalAxis = true;
        bones[control].LocalAxisVal = new Bone.LocalAxis { AxisX = node.Right, AxisZ = node.Forward };
        bones[control].ChildBoneVal = new Bone.ChildBone { Offset = node.Right * .04f };
        int alias = AddVirtualBone(bones, "ダミー." + side, "LegacyDummy_" + side, node.Position, control, false);
        bones[alias].Visible = false; bones[alias].Controllable = false;
        bones[alias].ChildBoneVal = new Bone.ChildBone { Offset = node.Right * .04f };
    }

    private static void AddForefootControl(List<Bone> bones, Dictionary<Transform, int> indexes,
        PMXBoneProfile profile, string side)
    {
        var toe = profile.Find("Toe_" + side); var deform = profile.Find("Toe_offset_" + side);
        if (toe == null || deform == null || !indexes.TryGetValue(toe, out int toeIndex) ||
            !indexes.TryGetValue(deform, out int deformIndex)) return;
        // Separate the IK endpoint from the editable forefoot. The native toe
        // offset keeps its weights and pivot, including existing EX motions.
        int control = AddVirtualBone(bones, (side == "L" ? "左" : "右") + "足先EX",
            "Forefoot_" + side, bones[deformIndex].Position, toeIndex, false);
        bones[control].ChildBoneVal = new Bone.ChildBone { Offset = Vector3.up * .04f };
        bones[deformIndex].ParentIndex = control;
    }

    private static int AddVirtualBone(List<Bone> bones, string name, string nameEn, Vector3 position, int parent, bool movable)
    {
        bones.Add(new Bone { Name = name, NameEn = nameEn, Position = position, ParentIndex = parent,
            Rotatable = true, Movable = movable, Visible = true, Controllable = true,
            ChildBoneVal = new Bone.ChildBone { Offset = Vector3.up * .04f } });
        return bones.Count - 1;
    }

    private static Transform Find(IEnumerable<Transform> sources, string name) =>
        sources.FirstOrDefault(t => t.name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void AllocateNames(List<Bone> bones)
    {
        string[] names = AllocateUniqueNames(bones.Select(bone => bone.Name).ToArray());
        string[] english = AllocateUniqueNames(bones.Select(bone => bone.NameEn).ToArray());
        for (int index = 0; index < bones.Count; index++)
        {
            bones[index].Name = names[index];
            bones[index].NameEn = english[index];
        }
    }

    private static string[] AllocateUniqueNames(string[] names)
    {
        var reserved = new HashSet<string>(names, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < names.Length; index++)
        {
            string name = names[index];
            if (used.Add(name)) continue;
            int suffix = 2;
            string candidate;
            do { candidate = name + "_" + suffix++; }
            while (reserved.Contains(candidate) || !used.Add(candidate));
            names[index] = candidate;
        }
        return names;
    }

    private static void AddFootIk(List<Bone> bones, Dictionary<Transform, int> indexes,
        Transform coordinateRoot, int parentOfAll, string side)
    {
        Transform thigh = Find(indexes.Keys, "Thigh_" + side);
        Transform knee = Find(indexes.Keys, "Knee_" + side);
        Transform ankle = Find(indexes.Keys, "Ankle_" + side);
        Transform toe = Find(indexes.Keys, "Toe_" + side);
        if (thigh == null || knee == null || ankle == null) return;

        string prefix = side == "L" ? "左" : "右";
        Vector3 anklePosition = coordinateRoot.InverseTransformPoint(ankle.position);
        Vector3 ikParentPosition = new Vector3(anklePosition.x, 0, anklePosition.z);
        int ikParent = AddVirtualBone(bones, prefix + "足IK親", "FootIKParent_" + side, ikParentPosition, parentOfAll, true);
        int footIk = AddVirtualBone(bones, prefix + "足ＩＫ", "FootIK_" + side, anklePosition, ikParent, true);
        bones[footIk].HasIk = true;
        bones[footIk].TransformLevel = 1;
        bones[footIk].IkInfoVal = new Bone.IkInfo
        {
            IkTargetIndex = indexes[ankle],
            CcdIterateLimit = 40,
            CcdAngleLimit = 2.0f,
            IkLinks = new[]
            {
                // 按 PMX 文件坐标语义使用负 X 区间，限制膝盖只向预期方向弯曲。
                new Bone.IkLink { LinkIndex = indexes[knee], HasLimit = true, LoLimit = new Vector3(-Mathf.PI, 0, 0), HiLimit = new Vector3(-0.0087f, 0, 0) },
                new Bone.IkLink { LinkIndex = indexes[thigh], HasLimit = false }
            }
        };

        if (toe == null) return;
        // Keep the native Ankle_offset -> Toe deformation chain intact. MMD's
        // toe IK instead needs an unweighted endpoint directly under the ankle.
        int tip = AddVirtualBone(bones, prefix + "つま先IK先", "ToeIKTarget_" + side,
            coordinateRoot.InverseTransformPoint(toe.position), indexes[ankle], false);
        bones[tip].Visible = false; bones[tip].Controllable = false;
        int toeIk = AddVirtualBone(bones, prefix + "つま先ＩＫ", "ToeIK_" + side,
            coordinateRoot.InverseTransformPoint(toe.position), footIk, true);
        bones[toeIk].HasIk = true;
        bones[toeIk].TransformLevel = 1;
        bones[toeIk].IkInfoVal = new Bone.IkInfo
        {
            IkTargetIndex = tip,
            CcdIterateLimit = 3,
            CcdAngleLimit = 1.0f,
            IkLinks = new[] { new Bone.IkLink { LinkIndex = indexes[ankle], HasLimit = false } }
        };
    }

    private static void SortByDependencies(List<Bone> bones, Dictionary<Transform, int> indexes)
    {
        // IK updates its links and endpoint, but MMD/PMXEditor do not recursively
        // reevaluate their other descendants as Blender does. Those descendants
        // must run after the solver or weighted offset bones keep the FK pose.
        var ikDependencies = new List<int>[bones.Count];
        for (int solver = 0; solver < bones.Count; solver++)
        {
            Bone ik = bones[solver];
            if (!ik.HasIk) continue;
            var prerequisites = new HashSet<int>();
            void IncludeAncestors(int index)
            {
                for (; index >= 0 && prerequisites.Add(index); index = bones[index].ParentIndex) { }
            }
            IncludeAncestors(solver);
            IncludeAncestors(ik.IkInfoVal.IkTargetIndex);
            foreach (var link in ik.IkInfoVal.IkLinks) IncludeAncestors(link.LinkIndex);
            var links = new HashSet<int>(ik.IkInfoVal.IkLinks.Select(link => link.LinkIndex));
            for (int index = 0; index < bones.Count; index++)
            {
                // The solver still needs its target and chain before it can run.
                if (prerequisites.Contains(index)) continue;
                for (int parent = bones[index].ParentIndex; parent >= 0; parent = bones[parent].ParentIndex)
                {
                    if (!links.Contains(parent)) continue;
                    if (ikDependencies[index] == null) ikDependencies[index] = new List<int>();
                    ikDependencies[index].Add(solver);
                    break;
                }
            }
        }

        var order = new List<int>(bones.Count);
        var state = new byte[bones.Count];
        Action<int> visit = null;
        visit = index =>
        {
            if (index < 0 || state[index] == 2) return;
            if (state[index] == 1) throw new InvalidOperationException("PMX bone dependency cycle: " + bones[index].Name);
            state[index] = 1;
            Bone bone = bones[index];
            visit(bone.ParentIndex);
            if (bone.ParentIndex >= 0)
                bone.TransformLevel = Math.Max(bone.TransformLevel, bones[bone.ParentIndex].TransformLevel);
            if (bone.AppendRotate || bone.AppendTranslate)
            {
                visit(bone.AppendBoneVal.Index);
                bone.TransformLevel = Math.Max(bone.TransformLevel, bones[bone.AppendBoneVal.Index].TransformLevel);
            }
            if (ikDependencies[index] != null)
                foreach (int solver in ikDependencies[index])
                {
                    visit(solver);
                    bone.TransformLevel = Math.Max(bone.TransformLevel, bones[solver].TransformLevel);
                }
            if (bone.HasIk)
            {
                visit(bone.IkInfoVal.IkTargetIndex);
                foreach (var link in bone.IkInfoVal.IkLinks) visit(link.LinkIndex);
            }
            state[index] = 2;
            order.Add(index);
        };
        for (int i = 0; i < bones.Count; i++) visit(i);

        var remap = new int[bones.Count];
        for (int i = 0; i < order.Count; i++) remap[order[i]] = i;
        Bone[] sorted = order.Select(i => bones[i]).ToArray();
        foreach (Bone bone in sorted)
        {
            if (bone.ParentIndex >= 0) bone.ParentIndex = remap[bone.ParentIndex];
            if (bone.ChildBoneVal.ChildUseId && bone.ChildBoneVal.Index >= 0)
                bone.ChildBoneVal.Index = remap[bone.ChildBoneVal.Index];
            if (bone.AppendRotate || bone.AppendTranslate) bone.AppendBoneVal.Index = remap[bone.AppendBoneVal.Index];
            if (!bone.HasIk) continue;
            bone.IkInfoVal.IkTargetIndex = remap[bone.IkInfoVal.IkTargetIndex];
            foreach (var link in bone.IkInfoVal.IkLinks) link.LinkIndex = remap[link.LinkIndex];
        }
        foreach (Transform transform in indexes.Keys.ToArray()) indexes[transform] = remap[indexes[transform]];
        bones.Clear();
        bones.AddRange(sorted);
    }

    private static void Validate(List<Bone> bones)
    {
        for (int i = 0; i < bones.Count; i++)
        {
            Bone bone = bones[i];
            if (bone.ParentIndex < -1 || bone.ParentIndex >= bones.Count || bone.ParentIndex == i)
                throw new InvalidOperationException("PMX 骨骼父索引无效: " + bone.Name);
            if (bone.ChildBoneVal.ChildUseId && (bone.ChildBoneVal.Index < 0 || bone.ChildBoneVal.Index >= bones.Count))
                throw new InvalidOperationException("PMX 骨骼尾端索引无效: " + bone.Name);
            if (bone.HasIk && (bone.IkInfoVal == null || bone.IkInfoVal.IkTargetIndex < 0 || bone.IkInfoVal.IkTargetIndex >= bones.Count))
                throw new InvalidOperationException("PMX IK 目标索引无效: " + bone.Name);
            if (!bone.HasIk) continue;

            foreach (Bone.IkLink link in bone.IkInfoVal.IkLinks)
            {
                if (link.LinkIndex < 0 || link.LinkIndex >= bones.Count)
                    throw new InvalidOperationException("PMX IK 链接索引无效: " + bone.Name);
                if (!link.HasLimit) continue;
                ValidateIkLimit(link.LoLimit, link.HiLimit, bone.Name);
            }
        }
    }

    private static void ValidateIkLimit(Vector3 lower, Vector3 upper, string boneName)
    {
        if (!IsFinite(lower) || !IsFinite(upper))
            throw new InvalidOperationException("PMX IK 角度限制包含 NaN 或 Infinity: " + boneName);
        if (lower.x > upper.x || lower.y > upper.y || lower.z > upper.z)
            throw new InvalidOperationException("PMX IK 角度限制上下限倒置: " + boneName);
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
