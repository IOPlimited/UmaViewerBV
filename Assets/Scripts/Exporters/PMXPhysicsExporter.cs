using Gallop;
using LibMMD.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// 将 CySpring 头发、耳朵、尾巴等物理骨骼链转换为 PMX 2.0 原生刚体与 Joint。
/// 严格基于骨骼局部坐标空间计算刚体姿态，消除全局增量误差与自旋偏差。
/// </summary>
internal static class PMXPhysicsExporter
{
    internal sealed class Context
    {
        internal readonly HashSet<Transform> DynamicBones = new HashSet<Transform>();
        internal SkirtController SkirtController;
        internal PMXSourcePhysicsSnapshot Source;
        internal PMXSourcePhysicsExporter.Options SourceOptions;
    }

    internal sealed class Chain
    {
        internal Transform Root;
        internal readonly HashSet<Transform> Bones = new HashSet<Transform>();
    }

    /// <summary>
    /// 解析角色中的物理链与裙摆数据。
    /// </summary>
    internal static Context Prepare(UmaContainerCharacter character, Transform skeletonRoot, bool includeSourceBones = false)
    {
        Context context = new Context();
        context.Source = PMXSourcePhysicsSnapshot.Capture(character, skeletonRoot);
        if (character == null || skeletonRoot == null || character.cySpringDataContainers == null)
            return context;

        Transform[] hierarchy = skeletonRoot.GetComponentsInChildren<Transform>(true);
        HashSet<Transform> claimedBones = new HashSet<Transform>();
        context.SkirtController = character.GetComponent<SkirtController>() ??
                                  character.GetComponentInChildren<SkirtController>(true);

        // 优先收集裙摆网格列
        PMXSkirtPhysicsExporter.CollectSkirtColumns(character, skeletonRoot, context, claimedBones);

        // 遍历 CySpring 容器收集头发、耳朵、尾巴链
        foreach (CySpringDataContainer container in character.cySpringDataContainers)
        {
            if (container == null || container.springParam == null) continue;

            foreach (CySpringParamDataElement element in container.springParam)
            {
                if (!IsLinearPhysicsElement(container, element)) continue;

                // 若该骨骼已被上游链或裙摆占用，跳过以避免生成重复重叠子链
                Transform root = FindRootTransform(element._boneName, hierarchy);
                if (root == null || claimedBones.Contains(root)) continue;

                Chain chain = BuildChain(element, root, claimedBones);
                if (chain == null || chain.Bones.Count == 0) continue;

                claimedBones.UnionWith(chain.Bones);
                context.DynamicBones.UnionWith(chain.Bones);
            }
        }

        if (includeSourceBones)
        {
            context.DynamicBones.UnionWith(context.Source.Nodes.Select(node => node.Source));
            context.DynamicBones.UnionWith(context.Source.Colliders.Select(collider => collider.Target));
        }
        return context;
    }

    /// <summary>
    /// 构建所有 PMX 刚体与 Joint。
    /// </summary>
    internal static void Build(Context context, Transform coordinateRoot, PMXBoneExporter.Result boneResult,
        RawMMDModel model)
    {
        PMXSourcePhysicsExporter.Build(context.Source, coordinateRoot, boneResult, model,
            context.SourceOptions ?? new PMXSourcePhysicsExporter.Options());
        Validate(model.Rigidbodies.ToList(), model.Joints.ToList(), boneResult.Bones.Length);
    }

    private static Transform FindRootTransform(string boneName, Transform[] hierarchy)
    {
        if (string.IsNullOrEmpty(boneName)) return null;
        return hierarchy.FirstOrDefault(t => string.Equals(t.name, boneName, StringComparison.Ordinal));
    }

    private static Chain BuildChain(CySpringParamDataElement element, Transform root, HashSet<Transform> claimedBones)
    {
        if (element == null || root == null) return null;

        HashSet<string> configuredBones = new HashSet<string>(StringComparer.Ordinal);
        configuredBones.Add(element._boneName);
        if (element._childElements != null)
        {
            foreach (CySpringParamDataChildElement child in element._childElements)
            {
                if (child != null && !string.IsNullOrEmpty(child._boneName))
                    configuredBones.Add(child._boneName);
            }
        }

        Chain chain = new Chain { Root = root };

        CollectContinuousBones(root, configuredBones, chain, claimedBones);
        return chain.Bones.Count > 0 ? chain : null;
    }

    internal static void CollectContinuousBones(Transform bone, HashSet<string> configuredBones,
        Chain chain, HashSet<Transform> claimedBones = null)
    {
        if (!configuredBones.Contains(bone.name)) return;
        if (claimedBones != null && claimedBones.Contains(bone)) return;

        chain.Bones.Add(bone);
        for (int i = 0; i < bone.childCount; i++)
        {
            Transform child = bone.GetChild(i);
            if (configuredBones.Contains(child.name))
                CollectContinuousBones(child, configuredBones, chain, claimedBones);
        }
    }

    internal static Vector3 ConvertUnityRotationToWriterEuler(Quaternion unityRotation)
    {
        // 坐标基 S=diag(-1, 1, -1)，完整旋转按 R_pmx=S*R_unity*S^-1 转换
        Quaternion pmxRotation = new Quaternion(
            -unityRotation.x, unityRotation.y, -unityRotation.z, unityRotation.w);
        Vector3 pmxEuler = ExtractPmxEulerDegrees(pmxRotation);

        // Writer 落盘时执行 (x,y,z)->(-x,y,-z)，此处返回内存坐标约定
        return new Vector3(
            NormalizeDegrees(-pmxEuler.x),
            NormalizeDegrees(pmxEuler.y),
            NormalizeDegrees(-pmxEuler.z));
    }

    private static Vector3 ExtractPmxEulerDegrees(Quaternion rotation)
    {
        Matrix4x4 matrix = Matrix4x4.Rotate(rotation);
        // PMX 与 mmd_tools 使用 YXZ 欧拉旋转顺序（R = Ry * Rx * Rz）
        float x = Mathf.Asin(Mathf.Clamp(-matrix.m12, -1f, 1f));
        float cosX = Mathf.Cos(x);
        float y;
        float z;

        if (Mathf.Abs(cosX) > 0.00001f)
        {
            y = Mathf.Atan2(matrix.m02, matrix.m22);
            z = Mathf.Atan2(matrix.m10, matrix.m11);
        }
        else
        {
            y = Mathf.Atan2(matrix.m01, matrix.m00);
            z = 0f;
        }

        return new Vector3(x, y, z) * Mathf.Rad2Deg;
    }

    private static float NormalizeDegrees(float value)
    {
        return value > 180f ? value - 360f : value;
    }

    internal static Transform GetSingleChainChild(Transform bone, HashSet<Transform> chainBones)
    {
        Transform result = null;
        for (int i = 0; i < bone.childCount; i++)
        {
            Transform child = bone.GetChild(i);
            if (!chainBones.Contains(child)) continue;
            if (result != null) return null;
            result = child;
        }
        return result;
    }

    internal static Transform FindNearestExportedParentTransform(Transform parent,
        Dictionary<Transform, int> boneIndexes)
    {
        for (Transform current = parent; current != null; current = current.parent)
            if (boneIndexes.ContainsKey(current)) return current;
        return null;
    }

    private static bool IsLinearPhysicsElement(CySpringDataContainer container, CySpringParamDataElement element)
    {
        if (container == null || element == null) return false;

        string boneText = (element._boneName ?? string.Empty).ToLowerInvariant();
        if (element._childElements != null)
        {
            foreach (CySpringParamDataChildElement child in element._childElements)
                if (child != null) boneText += " " + (child._boneName ?? string.Empty).ToLowerInvariant();
        }

        // 排除裙摆与胸部/披风/袖子
        if (ContainsAny(boneText, "skirt", "cloth", "dress", "bust", "breast", "mune", "sleeve", "cape", "mskirt"))
            return false;

        // 匹配头发、耳朵、马尾、呆毛及尾巴
        if (ContainsAny(boneText, "sp_he_", "sp_hi_tail", "hair", "ear", "tail", "head", "mimi", "shippo", "ponytail", "twin", "ahoge", "bang", "side", "back", "front"))
            return true;

        string path = GetTransformPath(container.transform).ToLowerInvariant();
        if (ContainsAny(path, "skirt", "cloth", "dress", "bust", "breast", "mune", "sleeve", "cape", "mskirt"))
            return false;
        return ContainsAny(path, "sp_he_", "head", "hair", "ear", "tail", "mimi", "shippo");
    }

    private static string GetTransformPath(Transform transform)
    {
        string path = string.Empty;
        for (Transform current = transform; current != null; current = current.parent)
            path = current.name + "/" + path;
        return path;
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        return values.Any(value => text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static void Validate(List<MMDRigidBody> rigidBodies, List<MMDJoint> joints, int boneCount)
    {
        for (int i = 0; i < rigidBodies.Count; i++)
        {
            MMDRigidBody body = rigidBodies[i];
            if (body.AssociatedBoneIndex < 0 || body.AssociatedBoneIndex >= boneCount)
                throw new InvalidOperationException("PMX 刚体骨骼索引无效: " + body.Name);
            if (body.CollisionGroup < 0 || body.CollisionGroup > 15)
                throw new InvalidOperationException("PMX 刚体碰撞组无效: " + body.Name);
            if (body.Type != MMDRigidBody.RigidBodyType.RigidTypeKinematic &&
                body.Type != MMDRigidBody.RigidBodyType.RigidTypePhysics &&
                body.Type != MMDRigidBody.RigidBodyType.RigidTypePhysicsStrict)
                throw new InvalidOperationException("PMX 导出不允许 Type 3 刚体: " + body.Name);
            if (!IsFinite(body.Position) || !IsFinite(body.Rotation) || !IsFinite(body.Dimemsions))
                throw new InvalidOperationException("PMX 刚体包含 NaN 或 Infinity: " + body.Name);
            if (body.Dimemsions.x <= 0f ||
                (body.Shape != MMDRigidBody.RigidBodyShape.RigidShapeSphere && body.Dimemsions.y <= 0f) ||
                (body.Shape == MMDRigidBody.RigidBodyShape.RigidShapeBox && body.Dimemsions.z <= 0f))
                throw new InvalidOperationException("PMX 刚体尺寸无效: " + body.Name);
        }

        foreach (MMDJoint joint in joints)
        {
            int a = joint.AssociatedRigidBodyIndex[0];
            int b = joint.AssociatedRigidBodyIndex[1];
            if (a < 0 || a >= rigidBodies.Count || b < 0 || b >= rigidBodies.Count)
                throw new InvalidOperationException("PMX Joint 刚体索引无效: " + joint.Name);
            // Authored zero-swing roots can follow their animated bone while
            // retaining the source joint. MMD skips solving a kinematic pair;
            // the joint is valid and must still pass the checks below.
            if (!IsFinite(joint.Position) || !IsFinite(joint.Rotation) ||
                !IsFinite(joint.PositionLowLimit) || !IsFinite(joint.PositionHiLimit) ||
                !IsFinite(joint.RotationLowLimit) || !IsFinite(joint.RotationHiLimit) ||
                !IsFinite(joint.SpringTranslate) || !IsFinite(joint.SpringRotate))
                throw new InvalidOperationException("PMX Joint 包含 NaN 或 Infinity: " + joint.Name);
            if (joint.PositionLowLimit.x > joint.PositionHiLimit.x ||
                joint.PositionLowLimit.y > joint.PositionHiLimit.y ||
                joint.PositionLowLimit.z > joint.PositionHiLimit.z)
                throw new InvalidOperationException("PMX Joint 平移上下限倒置: " + joint.Name);
            // For angular limits, lower > upper is Bullet's free-axis encoding.
            // Source unrestricted rotations intentionally use this convention.
        }
    }

    internal static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }
}
