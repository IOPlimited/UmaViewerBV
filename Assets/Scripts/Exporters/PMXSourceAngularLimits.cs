using Gallop;
using UnityEngine;

/// <summary>Encloses source local rotation limits in original Bullet joint coordinates.</summary>
internal static class PMXSourceAngularLimits
{
    private const float SampleStepDegrees = 2f;
    // Bullet uses lower > upper for a free degree of freedom.
    private static readonly Vector3 FreeLower = Vector3.one;
    private static readonly Vector3 FreeUpper = -Vector3.one;

    internal static string Build(NativeClothWorking p, Vector3 axis, Quaternion frame,
        out Vector3 lower, out Vector3 upper)
    {
        if (p.IsLimit != 0 && p.LimitRotationMin.sqrMagnitude == 0f && p.LimitRotationMax.sqrMagnitude == 0f)
        {
            lower = upper = Vector3.zero;
            return "authored-exact-lock";
        }
        Vector3 min = p.LimitRotationMin, max = p.LimitRotationMax;
        if (p.IsLimit == 0 || min.x >= 180 && min.y >= 180 && min.z >= 180 &&
            max.x >= 180 && max.y >= 180 && max.z >= 180)
        {
            lower = FreeLower; upper = FreeUpper;
            return "unrestricted-native-rotation";
        }
        // For the native forward axis, Unity Ry*Rx with local Z locked maps
        // exactly to Bullet (X, Y, Z) = (source X, 0, -source Y).
        if (Vector3.Dot(axis, Vector3.forward) > 0 &&
            Vector3.Cross(axis, Vector3.forward).sqrMagnitude <= 1e-10f && min.z == 0f && max.z == 0f)
        {
            lower = new Vector3(-min.x, 0, -max.y) * Mathf.Deg2Rad;
            upper = new Vector3(max.x, 0, min.y) * Mathf.Deg2Rad;
            for (int i = 0; i < 3; i += 2)
                if (lower[i] <= -Mathf.PI && upper[i] >= Mathf.PI)
                { lower[i] = 1; upper[i] = -1; }
            return "analytic-forward-axis";
        }

        int nx = Mathf.Max(1, Mathf.CeilToInt((min.x + max.x) / SampleStepDegrees));
        int ny = Mathf.Max(1, Mathf.CeilToInt((min.y + max.y) / SampleStepDegrees));
        int nz = Mathf.Max(1, Mathf.CeilToInt((min.z + max.z) / SampleStepDegrees));
        float gap = ((min.x + max.x) / nx + (min.y + max.y) / ny + (min.z + max.z) / nz) * .5f * Mathf.Deg2Rad;
        Quaternion inverseFrame = Quaternion.Inverse(frame);
        lower = upper = Vector3.zero;
        for (int ix = 0; ix <= nx; ix++)
            for (int iy = 0; iy <= ny; iy++)
                for (int iz = 0; iz <= nz; iz++)
                {
                    Quaternion local = Quaternion.Euler(Mathf.Lerp(-min.x, max.x, (float)ix / nx),
                        Mathf.Lerp(-min.y, max.y, (float)iy / ny), Mathf.Lerp(-min.z, max.z, (float)iz / nz));
                    Quaternion q = inverseFrame * local * frame;
                    var matrix = Matrix4x4.Rotate(new Quaternion(-q.x, q.y, -q.z, q.w));
                    var angles = new Vector3(Mathf.Atan2(-matrix.m21, matrix.m22),
                        Mathf.Asin(Mathf.Clamp(matrix.m20, -1, 1)), Mathf.Atan2(-matrix.m10, matrix.m00));
                    lower = Vector3.Min(lower, angles); upper = Vector3.Max(upper, angles);
                }
        // Every input lies within 'gap' geodesic radians of a sample. Euler Y
        // changes by at most that distance; X/Z rates are bounded by 1/cos(Y).
        float maxY = Mathf.Max(Mathf.Abs(lower.y), Mathf.Abs(upper.y)) + gap;
        if (maxY >= Mathf.PI * .5f - 1e-5f)
        {
            lower = FreeLower; upper = FreeUpper;
            return "conservative-singularity-coverage";
        }
        float sidePad = gap / Mathf.Cos(maxY);
        Vector3 pad = new Vector3(sidePad, gap, sidePad);
        lower -= pad; upper += pad;
        for (int i = 0; i < 3; i += 2)
            if (lower[i] <= -Mathf.PI || upper[i] >= Mathf.PI)
            { lower[i] = 1; upper[i] = -1; }
        return "sampled-box-with-geodesic-bound";
    }
}
