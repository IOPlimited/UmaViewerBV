using System;
using System.Collections.Generic;
using System.Linq;
using Gallop;
using LibMMD.Model;
using UnityEngine;

/// <summary>Round-one source geometry and filters; dynamics remain an explicit baseline.</summary>
internal static class PMXSourcePhysicsExporter
{
    internal sealed class Options
    {
        internal string Root;
        internal bool SegmentCapsules;
        internal bool PositionAlign = true;
        internal float Mass = .1f, LinearDamping = .4f, AngularDamping = .8f, Spring = 2f;
    }

    internal static Vector3[] CageNormals()
    {
        var normals = new List<Vector3>();
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                    if (x != 0 || y != 0 || z != 0) normals.Add(new Vector3(x, y, z).normalized);
        return normals.ToArray();
    }

    internal static void Build(PMXSourcePhysicsSnapshot snapshot, Transform coordinates,
        PMXBoneExporter.Result rig, RawMMDModel model, Options options)
    {
        var nodes = snapshot.Nodes.Where(node => node.Simulate && IsSupportedComponent(node.Root, node.SkirtIndex) &&
                (options.Root == null || node.Root == options.Root))
            .OrderBy(node => Depth(node.Source)).ThenBy(node => node.Index).ToArray();
        if (nodes.Select(node => node.Source).Distinct().Count() != nodes.Length)
            throw new InvalidOperationException("Multiple CySpring solvers drive the same source bone.");
        var filters = PMXPhysicsCollisionGroups.Compile(nodes);
        var bodies = new List<MMDRigidBody>();
        var joints = new List<MMDJoint>();
        var dynamic = new Dictionary<Transform, int>();
        var nodeRows = new List<object>();
        var colliderRows = new List<object>();
        float scale = coordinates.InverseTransformVector(Vector3.up).magnitude;
        foreach (var node in nodes)
        {
            if (!rig.BoneIndexes.TryGetValue(node.Source, out int boneIndex))
                throw new InvalidOperationException("Source physics bone was not selected: " + node.Source.name);
            var p = node.Parameters;
            bool animatedZeroRoot = IsAnimatedZeroRoot(p, node.Slot);
            Vector3 start = coordinates.InverseTransformPoint(node.Source.position);
            Vector3 axis = p.BoneAxis.sqrMagnitude > 1e-10f ? p.BoneAxis.normalized : Vector3.up;
            Vector3 end = coordinates.InverseTransformPoint(node.Source.position + node.Source.rotation * axis * p.InitBoneDistance);
            float length = Vector3.Distance(start, end);
            if (length <= 1e-6f) throw new InvalidOperationException("Source physics segment has zero length: " + node.Source.name);
            Quaternion localFrame = Quaternion.FromToRotation(Vector3.up, axis);
            Quaternion frame = Quaternion.Inverse(coordinates.rotation) * node.Source.rotation * localFrame;
            float radius = Mathf.Max(0.00001f, p.CollisionRadius * scale);
            int parent = -1;
            for (Transform ancestor = node.Source.parent; ancestor != null; ancestor = ancestor.parent)
                if (dynamic.TryGetValue(ancestor, out int candidate)) { parent = candidate; break; }
            if (parent < 0)
            {
                Transform anchor = PMXPhysicsExporter.FindNearestExportedParentTransform(node.Source.parent, rig.BoneIndexes);
                if (anchor == null) throw new InvalidOperationException("No source anchor: " + node.Source.name);
                parent = bodies.Count;
                bodies.Add(Body(node.Root + "_anchor", rig.BoneIndexes[anchor], start, frame,
                    MMDRigidBody.RigidBodyShape.RigidShapeSphere, Vector3.one * .00001f, 0, 0, false, options));
            }
            int index = bodies.Count;
            var body = Body(node.Source.name + "_source", boneIndex, options.SegmentCapsules ? (start + end) * .5f : end,
                frame, options.SegmentCapsules ? MMDRigidBody.RigidBodyShape.RigidShapeCapsule : MMDRigidBody.RigidBodyShape.RigidShapeSphere,
                new Vector3(radius, options.SegmentCapsules ? Mathf.Max(.00001f, length - 2 * radius) : 0, 0),
                filters.Groups[node], filters.Masks[node], !animatedZeroRoot, options);
            bodies.Add(body);
            dynamic[node.Source] = index;
            // A zero Euler Y angle is not equivalent to zero geometric twist.
            // Preserve the source rotation box in Bullet's actual coordinates.
            string angularPolicy = PMXSourceAngularLimits.Build(p, axis, localFrame, out Vector3 lo, out Vector3 hi);
            joints.Add(new MMDJoint
            {
                Name = node.Source.name + "_source_joint", NameEn = node.Source.name + "_source_joint",
                AssociatedRigidBodyIndex = new[] { parent, index }, Position = start,
                Rotation = PMXPhysicsExporter.ConvertUnityRotationToWriterEuler(frame),
                PositionLowLimit = Vector3.zero, PositionHiLimit = Vector3.zero,
                RotationLowLimit = lo,
                RotationHiLimit = hi,
                SpringTranslate = Vector3.zero,
                SpringRotate = new Vector3(options.Spring, lo.y == hi.y ? 0 : options.Spring, options.Spring)
            });
            nodeRows.Add(new
            {
                source = node.Source.name, node.Root, node.Part, node.Slot, node.SkirtIndex, body = index, joint = joints.Count - 1,
                bone = boneIndex, start = V(start), end = V(end), radius = p.CollisionRadius * scale,
                exportedRadius = radius, axis = V(axis), frame = Q(frame), collisionGroup = body.CollisionGroup,
                collisionMask = body.CollisionMask, colliders = node.Colliders.Select(c => c.Index).ToArray(),
                native = new { p.StiffnessForce, p.DragForce, p.Gravity, p.CollisionRadius, p.InitBoneDistance,
                    p.MoveSpringApplyRate, p.DynamicRatio, p.IsLimit, limitMin = V(p.LimitRotationMin), limitMax = V(p.LimitRotationMax),
                    p.CheckCharaCollision, p.ActiveCollision, p.VerticalWindRateSlow, p.VerticalWindRateFast,
                    p.HorizontalWindRateSlow, p.HorizontalWindRateFast, p.IsSkip, p.IsAddSpring,
                    skirtKneeNormal = V(p.SkirtKneeNormal), skirtNormalPos = V(p.SkirtNormalPos),
                    p.IsCheckSkirtKnee },
                sourceState = new { node.SimulationReason, node.SourceExists, node.NeedSimulateEndBone, node.IsSkip, node.IsAddSpring },
                kinematicZeroSwingRoot = animatedZeroRoot,
                jointLimitApproximation = angularPolicy
            });
        }
        foreach (var copy in filters.Copies)
        {
            var c = copy.Source;
            Transform target = PMXPhysicsExporter.FindNearestExportedParentTransform(c.Target, rig.BoneIndexes);
            if (target == null) throw new InvalidOperationException("Missing source collider bone: " + c.Name);
            int bone = rig.BoneIndexes[target];
            Vector3 start = coordinates.InverseTransformPoint(c.WorldCenter), end = coordinates.InverseTransformPoint(c.WorldEnd);
            float radius = c.Radius * scale;
            if (radius <= 0 || !PMXPhysicsExporter.IsFinite(radius))
                throw new InvalidOperationException("Invalid effective source collider radius: " + c.Name);
            int first = bodies.Count;
            string name = c.Name + "_g" + copy.Mask;
            if (c.Type == CySpringCollisionData.CollisionType.Sphere || c.Type == CySpringCollisionData.CollisionType.Capsule)
            {
                bool capsule = c.Type == CySpringCollisionData.CollisionType.Capsule;
                Vector3 center = capsule ? (start + end) * .5f : start;
                Vector3 segment = capsule ? end - start : Vector3.zero;
                float halfLength = segment.magnitude * .5f;
                if (!c.Inner)
                {
                    Quaternion rotation = halfLength > 1e-6f ? Quaternion.FromToRotation(Vector3.up, segment.normalized) : Quaternion.identity;
                    // Source endpoints are CAP SPHERE CENTERS, not outer tips.
                    bodies.Add(Body(name, bone, center, rotation,
                        capsule && halfLength > 1e-6f ? MMDRigidBody.RigidBodyShape.RigidShapeCapsule : MMDRigidBody.RigidBodyShape.RigidShapeSphere,
                        new Vector3(radius, capsule ? halfLength * 2 : 0, 0), copy.Group, copy.Mask, false, options));
                }
                else
                {
                    Quaternion attachment = Quaternion.Inverse(coordinates.rotation) * c.Target.rotation;
                    float thickness = Mathf.Max(.01f, radius * .15f);
                    float width = (radius + halfLength + thickness) * 3;
                    foreach (Vector3 localNormal in CageNormals())
                    {
                        Vector3 normal = attachment * localNormal;
                        float support = radius + (capsule ? Mathf.Abs(Vector3.Dot(normal, segment.normalized)) * halfLength : 0);
                        bodies.Add(Body(name + "_wall" + (bodies.Count - first), bone,
                            center + normal * (support + thickness), Quaternion.FromToRotation(Vector3.up, normal),
                            MMDRigidBody.RigidBodyShape.RigidShapeBox, new Vector3(width, thickness, width), copy.Group, copy.Mask, false, options));
                    }
                }
            }
            else throw new InvalidOperationException("Round-one source collider shape not implemented: " + c.Name + " / " + c.Type);
            colliderRows.Add(new { source = c.Index, name = c.Name, attachment = c.Target.name, bone,
                type = c.Type.ToString(), inner = c.Inner, radius, start = V(start), end = V(end),
                group = copy.Group, mask = copy.Mask, firstBody = first, bodyCount = bodies.Count - first,
                approximation = c.Inner ? "26 tangent boxes; circumscribed inner cavity, uninflated source support planes" : "native outer primitive" });
        }
        model.Rigidbodies = bodies.ToArray(); model.Joints = joints.ToArray();
        // Non-simulated endpoints and weighted helpers must inherit the solved
        // parent pose after physics, rather than retain the animation pose.
        var driven = new HashSet<int>(nodes.Select(node => rig.BoneIndexes[node.Source]));
        var followers = new HashSet<int>();
        for (int pass = 0; pass < rig.Bones.Length; pass++)
        {
            bool changed = false;
            for (int i = 0; i < rig.Bones.Length; i++)
            {
                if (driven.Contains(i) || followers.Contains(i)) continue;
                var bone = rig.Bones[i];
                bool parentDriven = driven.Contains(bone.ParentIndex) || followers.Contains(bone.ParentIndex);
                bool grantDriven = (bone.AppendRotate || bone.AppendTranslate) &&
                    (driven.Contains(bone.AppendBoneVal.Index) || followers.Contains(bone.AppendBoneVal.Index));
                if (!parentDriven && !grantDriven) continue;
                bone.PostPhysics = true; followers.Add(i); changed = true;
            }
            if (!changed) break;
        }
        model.PhysicsExportInfo = new
        {
            schema = "UmaViewer.SourcePhysics/1", enabled = true, experimental = true,
            units = "Unity model coordinates; PMX writer scales lengths by 12.5",
            options.Root, options.SegmentCapsules, options.PositionAlign,
            baseline = new { options.Mass, options.LinearDamping, options.AngularDamping, options.Spring, calibrated = false },
            filtersExact = true, groupCount = filters.GroupCount, sourceColliderCount = nodes.SelectMany(n => n.Colliders).Distinct().Count(),
            nodes = nodeRows, colliders = colliderRows,
            decorationSeeds = snapshot.Nodes.Where(node => node.Simulate && DecorationRule(node.Root, node.SkirtIndex) != null &&
                    (options.Root == null || node.Root == options.Root)).Select(node =>
            {
                var p = node.Parameters;
                Vector3 axis = p.BoneAxis.sqrMagnitude > 1e-10f ? p.BoneAxis.normalized : Vector3.up;
                Quaternion frame = Quaternion.Inverse(coordinates.rotation) * node.Source.rotation * Quaternion.FromToRotation(Vector3.up, axis);
                return new
                {
                    source = node.Source.name, node.Root, node.Part, node.Slot, node.SkirtIndex,
                    rule = DecorationRule(node.Root, node.SkirtIndex), bone = rig.BoneIndexes[node.Source],
                    start = V(coordinates.InverseTransformPoint(node.Source.position)),
                    end = V(coordinates.InverseTransformPoint(node.Source.position + node.Source.rotation * axis * p.InitBoneDistance)),
                    radius = p.CollisionRadius * scale, frame = Q(frame),
                    kinematicZeroSwingRoot = IsAnimatedZeroRoot(p, node.Slot),
                    native = new { p.StiffnessForce, p.DragForce, p.Gravity, p.CollisionRadius, p.InitBoneDistance,
                        p.IsLimit, limitMin = V(p.LimitRotationMin), limitMax = V(p.LimitRotationMax) },
                    contactPolicy = "anatomical-or-fitted-surface-only; exclude-source-envelope-colliders",
                    enabledInBaseline = false
                };
            }).ToArray(),
            excludedDecorationNodes = snapshot.Nodes.Where(node => node.Simulate && !IsSupportedComponent(node.Root, node.SkirtIndex) &&
                    !node.Root.StartsWith("Sp_Ch_Bust", StringComparison.Ordinal))
                .Select(node => new { source = node.Source.name, node.Root, reason = "outside-component-scope" }).ToArray(),
            disabledBreastNodes = snapshot.Nodes.Where(node => node.Simulate && node.Root.StartsWith("Sp_Ch_Bust", StringComparison.Ordinal))
                .Select(node => new { source = node.Source.name, node.Root, reason = "breast-physics-disabled-by-user" }).ToArray(),
            // External rebuilders need the complete runtime inventory, not
            // just the subset selected by today's hair/skirt contact lists.
            // These are reference inputs, not additional exported PMX bodies.
            sourceInventory = snapshot.Colliders.Select(c =>
            {
                Transform target = PMXPhysicsExporter.FindNearestExportedParentTransform(c.Target, rig.BoneIndexes);
                return new
                {
                    source = c.Index, name = c.Name, attachment = c.Target.name,
                    bone = target != null ? rig.BoneIndexes[target] : -1,
                    type = c.Type.ToString(), inner = c.Inner, enabled = c.Enabled,
                    radius = c.Radius * scale,
                    start = V(coordinates.InverseTransformPoint(c.WorldCenter)),
                    end = V(coordinates.InverseTransformPoint(c.WorldEnd))
                };
            }).ToArray(),
            unmodeled = new[] { "per-node gravity and native force integration", "dynamic animation target blending", "add-spring move-rate selection", "wind and environment collisions", "SkirtController specialized leg response", "connected-bone force coupling" },
            sourceStateSummary = snapshot.Nodes.GroupBy(n => n.SimulationReason).OrderBy(g => g.Key).Select(g => new { reason = g.Key, count = g.Count() }).ToArray(),
            retainedInactiveNodes = snapshot.Nodes.Where(n => !n.Simulate).Select(n => new
            {
                source = n.Source.name, n.Root, n.Part, n.Slot, reason = n.SimulationReason,
                n.SourceExists, n.NeedSimulateEndBone, n.IsSkip, n.IsAddSpring
            }).ToArray(),
            postPhysicsFollowers = followers.OrderBy(i => i).Select(i => new { bone = i, source = rig.Bones[i].NameEn }).ToArray(),
            notes = snapshot.Notes.ToArray()
        };
    }

    internal static bool IsSupportedComponent(string root, int skirtIndex)
    {
        switch (SourceKind(root))
        {
            case "sleeve":
            case "jacket":
            case "mantle":
            case "belt":
            case "tie":
            case "ribbon":
            case "acc":
            case "collar":
            case "clooar": return false;
        }
        return !root.StartsWith("Sp_Ch_Bust", StringComparison.Ordinal) &&
            (skirtIndex >= 0 || root.StartsWith("Sp_He_Ear", StringComparison.Ordinal) ||
            root.StartsWith("Sp_He_Hair", StringComparison.Ordinal) ||
            root.StartsWith("Sp_Hi_Tail", StringComparison.Ordinal) ||
            root.StartsWith("Sp_Hi_", StringComparison.Ordinal) && root.Contains("Skirt"));
    }

    private static string SourceKind(string root)
    {
        string[] fields = root.Split('_');
        return fields.Length >= 3 && fields[0] == "Sp" ?
            new string(fields[2].TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant() : "";
    }

    private static string DecorationRule(string root, int skirtIndex)
    {
        if (IsSupportedComponent(root, skirtIndex)) return null;
        switch (SourceKind(root))
        {
            case "sleeve": return "arm-follower";
            case "mantle": return "long-hair";
            case "belt": return "ponytail";
            case "tie": return "chest-tie";
            default: return null;
        }
    }

    private static MMDRigidBody Body(string name, int bone, Vector3 position, Quaternion rotation,
        MMDRigidBody.RigidBodyShape shape, Vector3 size, int group, ushort mask, bool dynamic, Options options)
    {
        return new MMDRigidBody
        {
            Name = name, NameEn = name, AssociatedBoneIndex = bone, Position = position,
            Rotation = PMXPhysicsExporter.ConvertUnityRotationToWriterEuler(rotation), Shape = shape, Dimemsions = size,
            CollisionGroup = group, CollisionMask = mask, Mass = dynamic ? options.Mass : 0,
            TranslateDamp = dynamic ? options.LinearDamping : 0, RotateDamp = dynamic ? options.AngularDamping : 0,
            Friction = 0, Restitution = 0,
            Type = !dynamic ? MMDRigidBody.RigidBodyType.RigidTypeKinematic : options.PositionAlign
                ? MMDRigidBody.RigidBodyType.RigidTypePhysicsStrict : MMDRigidBody.RigidBodyType.RigidTypePhysics
        };
    }

    private static bool HasZeroSwingEnvelope(NativeClothWorking p) =>
        p.IsLimit != 0 && p.LimitRotationMin.sqrMagnitude == 0f && p.LimitRotationMax.sqrMagnitude == 0f;

    // Source traces show that zero-swing roots follow the current animation
    // exactly. A dynamic body adds contact/constraint error to that FK result.
    private static bool IsAnimatedZeroRoot(NativeClothWorking p, int slot) => slot == 0 && HasZeroSwingEnvelope(p);

    private static int Depth(Transform source) { int depth = 0; for (; source != null; source = source.parent) depth++; return depth; }
    private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
    private static float[] Q(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
}
