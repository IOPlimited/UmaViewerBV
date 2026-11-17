using System;
using System.Collections.Generic;
using System.Linq;
using Gallop;
using UnityEngine;

/// <summary>Effective CySpring inputs, independent of the temporary export freeze.</summary>
internal sealed class PMXSourcePhysicsSnapshot
{
    internal sealed class Collider
    {
        internal string Name;
        internal Transform Target;
        internal CySpringCollisionData.CollisionType Type;
        internal bool Inner, Enabled;
        internal float Radius;
        internal Vector3 LocalCenter, LocalEnd, LocalNormal;
        internal int Index;

        internal Vector3 WorldCenter => Target.position + Target.rotation * LocalCenter;
        internal Vector3 WorldEnd => Target.position + Target.rotation * LocalEnd;
        internal Vector3 WorldNormal => Target.rotation * LocalNormal;
    }

    internal sealed class Node
    {
        internal Transform Source, Parent;
        internal string Root;
        internal int Part, Slot, Index, SkirtIndex;
        internal bool Simulate, SourceExists, NeedSimulateEndBone, IsSkip, IsAddSpring;
        internal string SimulationReason;
        internal NativeClothWorking Parameters;
        internal readonly List<Collider> Colliders = new List<Collider>();
    }

    internal readonly List<Node> Nodes = new List<Node>();
    internal readonly List<Collider> Colliders = new List<Collider>();
    internal readonly List<string> Notes = new List<string>();

    internal static PMXSourcePhysicsSnapshot Capture(UmaContainerCharacter character, Transform skeletonRoot)
    {
        var snapshot = new PMXSourcePhysicsSnapshot();
        if (character == null || skeletonRoot == null) return snapshot;
        var controller = character.GetComponentInChildren<CySpringController>(true);
        if (controller == null)
        {
            snapshot.Notes.Add("No effective CySpring runtime; no inferred physics was generated.");
            return snapshot;
        }
        int part = -1;
        foreach (var spring in controller.SpringSystems)
        {
            part++;
            if (spring == null) continue;
            var colliders = new List<Collider>();
            for (int colliderIndex = 0; colliderIndex < spring.CharaCollisionList.Count; colliderIndex++)
            {
                var runtime = spring.CharaCollisionList[colliderIndex];
                if (colliderIndex >= spring.NativeCollisionInputs.Count)
                    throw new InvalidOperationException("Missing effective CySpring collider buffer: " + runtime.Name);
                var input = spring.NativeCollisionInputs[colliderIndex];
                var parents = spring.ParentWork?.ParentTransformArray;
                Transform parent = parents != null && input.ParentWorkIndex >= 0 && input.ParentWorkIndex < parents.Length
                    ? parents[input.ParentWorkIndex] : null;
                if (parent == null || parent != skeletonRoot && !parent.IsChildOf(skeletonRoot))
                {
                    colliders.Add(null);
                    snapshot.Notes.Add("Unresolved collider attachment: " + runtime.Name);
                    continue;
                }
                // Read the actual solver buffer. Recomputing from Transform
                // scale can apply character/legacy scaling twice or sample a
                // different frame than the effective collision inputs.
                var collider = new Collider
                {
                    Name = runtime.Name, Target = parent, Type = (CySpringCollisionData.CollisionType)input.Type,
                    Inner = input.IsInnerBool, Enabled = input.IsEnableBool, Radius = input.Radius,
                    LocalCenter = input.Position, LocalEnd = input.Position2,
                    LocalNormal = Quaternion.Inverse(parent.rotation) * input.Normal,
                    Index = snapshot.Colliders.Count
                };
                snapshot.Colliders.Add(collider);
                colliders.Add(collider);
            }
            foreach (var root in spring.RootBoneArray ?? Array.Empty<CySpringRootBone>())
            {
                if (root?.NativeArray == null) continue;
                var sources = new List<CySpringBoneBase> { root };
                if (root.ChildBoneList != null) sources.AddRange(root.ChildBoneList);
                for (int i = 0; i < root.NativeArray.Length && i < sources.Count; i++)
                {
                    var source = sources[i];
                    Transform transform = source.Transform;
                    if (transform == null || transform != skeletonRoot && !transform.IsChildOf(skeletonRoot)) continue;
                    NativeClothWorking parameters = root.NativeArray[i];
                    bool insideTrueNativeCount = i < root.TrueNativesCount;
                    bool sourceExists = source.Exist;
                    bool needSimulateEndBone = source.NeedSimulateEndBone;
                    bool isSkip = parameters.IsSkip != 0;
                    string simulationReason = insideTrueNativeCount
                        ? sourceExists
                            ? needSimulateEndBone
                                ? isSkip ? "is-skip" : "simulated"
                                : "need-simulate-end-bone-false"
                            : "source-not-exist"
                        : "outside-true-native-count";
                    var node = new Node
                    {
                        Source = transform, Parent = source.ParentBone?.Transform,
                        Root = root.BoneName, Part = part, Slot = i, Index = snapshot.Nodes.Count,
                        SkirtIndex = root.LinkSkirtIndex, Parameters = parameters,
                        SourceExists = sourceExists, NeedSimulateEndBone = needSimulateEndBone,
                        IsSkip = isSkip, IsAddSpring = parameters.IsAddSpring != 0,
                        SimulationReason = simulationReason,
                        // ShouldReflectCalcResult is deliberately not consulted:
                        // FreezeForExport temporarily disables it for every bone.
                        Simulate = simulationReason == "simulated"
                    };
                    short[] ids = { parameters.CIndex0, parameters.CIndex1, parameters.CIndex2, parameters.CIndex3,
                        parameters.CIndex4, parameters.CIndex5, parameters.CIndex6, parameters.CIndex7,
                        parameters.CIndex8, parameters.CIndex9, parameters.CIndex10, parameters.CIndex11,
                        parameters.CIndex12, parameters.CIndex13, parameters.CIndex14, parameters.CIndex15 };
                    if (parameters.CheckCharaCollision != 0)
                        foreach (int id in ids.Take(Mathf.Clamp(parameters.ActiveCollision, 0, ids.Length)))
                            if (id >= 0 && id < colliders.Count && colliders[id] != null &&
                                colliders[id].Enabled && !node.Colliders.Contains(colliders[id])) node.Colliders.Add(colliders[id]);
                    snapshot.Nodes.Add(node);
                }
            }
        }
        return snapshot;
    }
}
