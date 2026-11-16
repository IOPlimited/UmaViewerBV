using Gallop;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 收集 SkirtController 原始骨链，保留无蒙皮权重的裙摆节点。
/// </summary>
internal static class PMXSkirtPhysicsExporter
{
    /// <summary>
    /// 收集角色裙摆列链。
    /// </summary>
    internal static void CollectSkirtColumns(UmaContainerCharacter character, Transform skeletonRoot,
        PMXPhysicsExporter.Context context, HashSet<Transform> claimedBones)
    {
        SkirtController controller = context.SkirtController;
        if (controller == null || controller.SkirtDataArray == null) return;

        foreach (SkirtController.SkirtData skirtData in controller.SkirtDataArray)
        {
            if (skirtData == null || skirtData.SkirtRoot == null || skirtData.SkirtChild == null) continue;
            if (skirtData.SkirtRoot != skeletonRoot && !skirtData.SkirtRoot.IsChildOf(skeletonRoot)) continue;
            if (claimedBones.Contains(skirtData.SkirtRoot)) continue;

            PMXPhysicsExporter.Chain bestChain = null;
            foreach (CySpringDataContainer container in character.cySpringDataContainers)
            {
                if (container == null || container.springParam == null) continue;
                foreach (CySpringParamDataElement element in container.springParam)
                {
                    if (element == null || !string.Equals(
                            element._boneName, skirtData.SkirtRoot.name, StringComparison.Ordinal)) continue;

                    PMXPhysicsExporter.Chain candidate = BuildChainFromRoot(element, skirtData.SkirtRoot);
                    if (candidate == null || !candidate.Bones.Contains(skirtData.SkirtChild) ||
                        !IsStrictLinearChain(candidate, skirtData.SkirtChild)) continue;
                    if (bestChain == null || candidate.Bones.Count > bestChain.Bones.Count) bestChain = candidate;
                }
            }

            if (bestChain == null) continue;
            claimedBones.UnionWith(bestChain.Bones);
            foreach (Transform bone in bestChain.Bones)
            {
                // 裙摆骨骼即使无蒙皮权重，也决定物理链完整性
                context.DynamicBones.Add(bone);
            }
        }
    }

    private static PMXPhysicsExporter.Chain BuildChainFromRoot(CySpringParamDataElement element, Transform root)
    {
        if (element == null || root == null ||
            !string.Equals(element._boneName, root.name, StringComparison.Ordinal)) return null;

        HashSet<string> configuredBones = new HashSet<string>(StringComparer.Ordinal)
        {
            element._boneName
        };
        if (element._childElements != null)
        {
            foreach (CySpringParamDataChildElement child in element._childElements)
            {
                if (child != null && !string.IsNullOrEmpty(child._boneName))
                    configuredBones.Add(child._boneName);
            }
        }

        PMXPhysicsExporter.Chain chain = new PMXPhysicsExporter.Chain { Root = root };
        PMXPhysicsExporter.CollectContinuousBones(root, configuredBones, chain);
        return chain.Bones.Count > 0 ? chain : null;
    }

    private static bool IsStrictLinearChain(PMXPhysicsExporter.Chain chain, Transform expectedFirstChild)
    {
        if (chain == null || chain.Root == null || expectedFirstChild == null) return false;

        Transform current = chain.Root;
        int visited = 0;
        bool foundExpectedChild = false;
        while (current != null && chain.Bones.Contains(current))
        {
            visited++;
            Transform next = PMXPhysicsExporter.GetSingleChainChild(current, chain.Bones);
            if (current == chain.Root) foundExpectedChild = next == expectedFirstChild;
            if (next == null) break;
            current = next;
        }
        return foundExpectedChild && visited == chain.Bones.Count && visited >= 2;
    }
}
