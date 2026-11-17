using System;
using System.Collections.Generic;
using System.Linq;
using Gallop.Live;
using LibMMD.Model;
using UnityEngine;

/// <summary>Builds a rotation/return rig without changing the native eye geometry.</summary>
internal static class PMXEyeExporter
{
    // Opposite offsets cancel the component normal to the eye plane. The complete
    // q * q^-2 * q chain also cancels orientation, including combined pitch/yaw.
    internal const float RotationRatio = 0.02f;
    internal const float ReferenceAngleDegrees = 30f;
    private const float DefaultTravel = 0.006f;

    internal static void Build(List<Bone> bones, Dictionary<Transform, int> indexes,
        Transform coordinateRoot, UmaContainerCharacter character)
    {
        Transform Find(string name) => indexes.Keys.FirstOrDefault(t =>
            t.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Transform head = Find("Head");
        Transform left = Find("Eye_L"), right = Find("Eye_R");
        if (head == null || (left == null && right == null)) return;

        Vector3 eyeCenter = coordinateRoot.InverseTransformPoint(
            left != null && right != null ? (left.position + right.position) * 0.5f : (left ?? right).position);
        int bothEyes = bones.Count;
        bones.Add(Control("両目", "BothEyes", eyeCenter + Vector3.up * 0.08f, indexes[head]));

        LiveEyeTraceController.Eye nativeLeft = null, nativeRight = null;
        if (character != null) NativeEyes(character, out nativeLeft, out nativeRight);
        var travels = new List<float>();
        foreach (var source in new[] { nativeLeft, nativeRight })
        {
            if (source == null) continue;
            Vector3 horizontal = coordinateRoot.InverseTransformVector(source.Locator.right);
            Vector3 vertical = coordinateRoot.InverseTransformVector(source.Locator.up);
            foreach (float distance in new[] { source.Min.x * horizontal.x, source.Max.x * horizontal.x,
                         source.Min.y * vertical.y, source.Max.y * vertical.y })
                if (Mathf.Abs(distance) > 0.000001f) travels.Add(Mathf.Abs(distance));
        }
        // Range groups encode inner/outer and upper/lower limits, not per-eye gains.
        // Standard MMD rotation uses a shared safe travel; native direction morphs
        // below retain each individual EyeTrace boundary and locator axis.
        float travel = travels.Count > 0 ? travels.Min() : DefaultTravel;
        float leverZ = travel / Mathf.Sin(ReferenceAngleDegrees * RotationRatio * Mathf.Deg2Rad);
        BuildEye(bones, indexes, coordinateRoot, left, indexes[head], bothEyes,
            nativeLeft, leverZ, "左", "L");
        BuildEye(bones, indexes, coordinateRoot, right, indexes[head], bothEyes,
            nativeRight, leverZ, "右", "R");
    }

    private static bool NativeEyes(UmaContainerCharacter character,
        out LiveEyeTraceController.Eye left, out LiveEyeTraceController.Eye right)
    {
        var live = character.FaceDrivenKeyTarget != null ? character.FaceDrivenKeyTarget.LiveEyeTrace : null;
        if (live != null) { left = live.Left; right = live.Right; return true; }
        return LiveEyeTraceController.TryCreateEyes(character, out left, out right);
    }

    private static void BuildEye(List<Bone> bones, Dictionary<Transform, int> indexes,
        Transform coordinateRoot, Transform eye, int head, int bothEyes,
        LiveEyeTraceController.Eye source, float leverZ, string prefix, string side)
    {
        if (eye == null || !indexes.TryGetValue(eye, out int returnIndex)) return;
        Vector3 position = coordinateRoot.InverseTransformPoint(eye.position);
        Vector3 normal = source != null ? coordinateRoot.InverseTransformDirection(
            Vector3.Cross(source.Locator.right, source.Locator.up)).normalized : Vector3.forward;
        Vector3 lever = Mathf.Abs(normal.z) > 0.0001f ? normal * (leverZ / normal.z) : normal * leverZ;

        int controlIndex = bones.Count;
        Bone control = Control(prefix + "目", "Eye_" + side, position, head);
        control.AppendRotate = true;
        control.AppendBoneVal = new Bone.AppendBone { Index = bothEyes, Ratio = 1f };
        bones.Add(control);

        int driverIndex = bones.Count;
        Bone driver = Control(prefix + "目駆動", "EyeDriver_" + side, position, head);
        driver.Visible = false;
        driver.Controllable = false;
        driver.TransformLevel = 1;
        driver.AppendRotate = true;
        driver.AppendBoneVal = new Bone.AppendBone { Index = controlIndex, Ratio = RotationRatio };
        bones.Add(driver);

        int counterIndex = bones.Count;
        Bone counter = Control(prefix + "目補正", "EyeCounter_" + side, position + lever * 0.5f, driverIndex);
        counter.Visible = false;
        counter.Controllable = false;
        counter.TransformLevel = 2;
        counter.AppendRotate = true;
        counter.AppendBoneVal = new Bone.AppendBone { Index = driverIndex, Ratio = -2f };
        bones.Add(counter);
        // Keep all grant bones on the same local axes. Importers implement grants
        // in local space; aiming their display tails at the next pivot changes it.

        // Keep the native Transform->bone mapping and all skin weights/children on the return.
        // Head/eyelid weights stay on their original bones.
        Bone eyeReturn = bones[returnIndex];
        eyeReturn.Name = prefix + "目戻";
        eyeReturn.NameEn = "EyeReturn_" + side;
        eyeReturn.Position = position;
        eyeReturn.ParentIndex = counterIndex;
        eyeReturn.TransformLevel = 3;
        eyeReturn.Visible = false;
        eyeReturn.Controllable = false;
        eyeReturn.AppendRotate = true;
        eyeReturn.AppendBoneVal = new Bone.AppendBone { Index = driverIndex, Ratio = 1f };
        eyeReturn.ChildBoneVal = new Bone.ChildBone { Offset = Vector3.forward * 0.025f };
    }

    internal static void AddNativeGazeMorphs(RawMMDModel model, UmaContainerCharacter character,
        Transform coordinateRoot, Dictionary<Transform, int> indexes)
    {
        if (character == null || !NativeEyes(character, out var left, out var right)) return;
        var morphs = model.Morphs.ToList();
        var directions = new[] { "左", "右", "上", "下" };
        var groups = new List<Morph.GroupMorphData>[4];
        for (int i = 0; i < 4; i++) groups[i] = new List<Morph.GroupMorphData>();
        void Add(LiveEyeTraceController.Eye eye, string prefix, string side)
        {
            if (!indexes.TryGetValue(eye.Transform, out int index)) return;
            Vector3 r = coordinateRoot.InverseTransformVector(eye.Locator.right);
            Vector3 u = coordinateRoot.InverseTransformVector(eye.Locator.up);
            Vector3[] offsets = { r * eye.Min.x, r * eye.Max.x, u * eye.Max.y, u * eye.Min.y };
            for (int i = 0; i < offsets.Length; i++)
            {
                groups[i].Add(new Morph.GroupMorphData { MorphIndex = morphs.Count, MorphRate = 1f });
                morphs.Add(new Morph
                {
                    Name = prefix + "目" + directions[i], NameEn = "EyeGaze" + i + "_" + side,
                    Category = Morph.MorphCategory.MorphCatEye, Type = Morph.MorphType.MorphTypeBone,
                    MorphDatas = new Morph.MorphData[] { new Morph.BoneMorphData
                        { BoneIndex = index, Translation = offsets[i], Rotation = Quaternion.identity } }
                });
            }
        }
        Add(left, "左", "L"); Add(right, "右", "R");
        for (int i = 0; i < groups.Length; i++)
            if (groups[i].Count > 0)
                morphs.Add(new Morph { Name = "両目" + directions[i], NameEn = "Gaze" + i,
                    Category = Morph.MorphCategory.MorphCatEye, Type = Morph.MorphType.MorphTypeGroup,
                    MorphDatas = groups[i].Cast<Morph.MorphData>().ToArray() });
        model.Morphs = morphs.ToArray();
        model.Entrys.Add(new PMXEntryItem
        {
            EntryItemName = "表情", EntryItemNameEn = "Expressions", IsSpecial = true,
            Elements = Enumerable.Range(0, model.Morphs.Length)
                .Where(i => model.Morphs[i].Category != Morph.MorphCategory.MorphCatSystem).Select(i =>
                new PMXEntryItem.Element { IsMorph = true, MorphIndex = i }).ToList()
        });
    }

    private static Bone Control(string name, string englishName, Vector3 position, int parent)
    {
        return new Bone
        {
            Name = name, NameEn = englishName, Position = position, ParentIndex = parent,
            Rotatable = true, Visible = true, Controllable = true,
            ChildBoneVal = new Bone.ChildBone { Offset = Vector3.forward * 0.025f }
        };
    }
}
