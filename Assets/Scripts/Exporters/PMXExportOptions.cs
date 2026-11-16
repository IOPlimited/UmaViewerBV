using System;
using System.Linq;
using UnityEngine;

/// <summary>Options belong to one export, so a cancelled dialog cannot change later exports.</summary>
public sealed class PMXExportOptions
{
    public bool GenerateRigidBodies { get; }
    public bool RenameBones { get; }
    public bool OrganizeBones { get; }

    public PMXExportOptions(bool generateRigidBodies = false, bool renameBones = true, bool organizeBones = true)
    {
        GenerateRigidBodies = generateRigidBodies;
        RenameBones = renameBones;
        OrganizeBones = organizeBones;
    }

    internal static Transform SkeletonRoot(UmaContainer container) =>
        container.transform.Find("Position") ?? container.transform;

    /// <summary>Normal and Mini share these roles; fingers, toes and optional roll bones are not required.</summary>
    public static bool IsHumanoid(UmaContainer container)
    {
        if (container == null) return false;
        Transform[] hierarchy = SkeletonRoot(container).GetComponentsInChildren<Transform>(true);
        Transform Below(Transform parent, string name) => hierarchy.FirstOrDefault(t =>
            t != parent && t.name.Equals(name, StringComparison.OrdinalIgnoreCase) && t.IsChildOf(parent));
        foreach (Transform hip in hierarchy.Where(t => t.name.Equals("Hip", StringComparison.OrdinalIgnoreCase)))
        {
            Transform upper = Below(hip, "Waist") ?? Below(hip, "Spine") ?? Below(hip, "Chest");
            if (upper == null) continue;
            Transform neck = Below(upper, "Neck");
            if (neck == null || Below(neck, "Head") == null) continue;
            bool complete = true;
            foreach (string side in new[] { "L", "R" })
            {
                Transform arm = Below(upper, "Arm_" + side);
                Transform elbow = arm != null ? Below(arm, "Elbow_" + side) : null;
                Transform thigh = Below(hip, "Thigh_" + side);
                Transform knee = thigh != null ? Below(thigh, "Knee_" + side) : null;
                complete &= elbow != null && Below(elbow, "Wrist_" + side) != null &&
                    knee != null && Below(knee, "Ankle_" + side) != null;
            }
            if (complete) return true;
        }
        return false;
    }

    internal static PMXExportOptions Resolve(UmaContainer container, PMXExportOptions requested) =>
        IsHumanoid(container) ? requested ?? new PMXExportOptions(ModelExporter.ExportPhysics) :
            new PMXExportOptions(false, false, false);
}
