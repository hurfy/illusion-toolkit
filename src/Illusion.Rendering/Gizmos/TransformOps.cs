using System.Numerics;
using Illusion.Domain;

namespace Illusion.Rendering.Gizmos;

/// <summary>
/// Pure transform-editing math for the manipulation gizmo. A gizmo drag is expressed as a world-space delta
/// matrix (translate / rotate-about-pivot / scale-about-pivot); <see cref="WorldDeltaToLocal"/> folds it into a
/// frame's world transform and converts back to a LOCAL transform using the same (slightly lossy) parent
/// relations the vendor FrameObjectBase.SetWorldTransform uses — so a save round-trips. Row-vector convention
/// throughout: a point p maps as <c>p · M</c>, and "apply A then B" composes as <c>A * B</c>.
/// </summary>
public static class TransformOps
{
    /// <summary>World-space translation delta.</summary>
    public static Matrix4x4 MoveDelta(Vector3 worldDelta) => Matrix4x4.CreateTranslation(worldDelta);

    /// <summary>World-space rotation of <paramref name="radians"/> about <paramref name="worldAxis"/> through <paramref name="pivot"/>.</summary>
    public static Matrix4x4 RotateDelta(Vector3 pivot, Vector3 worldAxis, float radians) =>
        Matrix4x4.CreateTranslation(-pivot)
        * Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(worldAxis), radians)
        * Matrix4x4.CreateTranslation(pivot);

    /// <summary>World-axis scale by <paramref name="factorPerAxis"/> about <paramref name="pivot"/>.</summary>
    public static Matrix4x4 ScaleDelta(Vector3 pivot, Vector3 factorPerAxis) =>
        Matrix4x4.CreateTranslation(-pivot)
        * Matrix4x4.CreateScale(factorPerAxis)
        * Matrix4x4.CreateTranslation(pivot);

    /// <summary>
    /// Applies a world-space delta to a frame's world transform and returns the new LOCAL transform.
    /// <paramref name="oldWorld"/> is the frame's current world matrix; <paramref name="parentWorld"/> is its
    /// parent's world matrix (identity for a root). Mirrors the vendor's decomposition: world rotation =
    /// parentRot · localRot, world position = localPos transformed by parentWorld, world scale = local scale.
    /// </summary>
    public static Matrix4x4 WorldDeltaToLocal(Matrix4x4 oldWorld, Matrix4x4 parentWorld, Matrix4x4 worldDelta)
    {
        Matrix4x4 newWorld = oldWorld * worldDelta;

        // R·S decomposition (degrades to identity rotation / unit scale on genuinely degenerate input) —
        // Matrix4x4.Decompose would reject every rotated, non-uniformly scaled frame matrix.
        TransformMath.TryDecompose(newWorld, out Vector3 worldScale, out Quaternion worldRot, out Vector3 worldPos);

        Quaternion parentRot = Quaternion.Identity;
        Matrix4x4 invParent = Matrix4x4.Identity;
        bool hasParent = parentWorld != Matrix4x4.Identity;
        if (hasParent)
        {
            TransformMath.TryDecompose(parentWorld, out _, out parentRot, out _);
            if (!Matrix4x4.Invert(parentWorld, out invParent)) invParent = Matrix4x4.Identity;
        }

        Quaternion localRot = hasParent ? Quaternion.Inverse(parentRot) * worldRot : worldRot;
        Vector3 localPos = hasParent ? TransformMath.TransformCoordinate(worldPos, invParent) : worldPos;
        Vector3 localScale = worldScale;

        return TransformMath.Compose(localRot, localScale, localPos);
    }

    /// <summary>The three world-space directions a global-orientation gizmo draws its handles along.</summary>
    public static readonly Vector3[] WorldAxes = { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };

    // ── Shift-snap: quantize a gizmo drag to fixed increments (held-Shift stepped manipulation) ──

    /// <summary>Rounds each component of a translation delta to the nearest multiple of <paramref name="step"/>.</summary>
    public static Vector3 SnapVector(Vector3 v, float step) => new(
        MathF.Round(v.X / step) * step,
        MathF.Round(v.Y / step) * step,
        MathF.Round(v.Z / step) * step);

    /// <summary>Rounds an angle (radians) to the nearest <paramref name="stepDeg"/>°, returned in radians.</summary>
    public static float SnapAngle(float radians, float stepDeg)
    {
        const float toDeg = 180f / MathF.PI, toRad = MathF.PI / 180f;
        return MathF.Round(radians * toDeg / stepDeg) * stepDeg * toRad;
    }

    /// <summary>Rounds a scale factor to the nearest multiple of <paramref name="step"/>, kept strictly positive.</summary>
    public static float SnapScale(float factor, float step) => MathF.Max(0.01f, MathF.Round(factor / step) * step);

    // ── Euler (X,Y,Z degrees) ↔ quaternion, intrinsic Z-Y-X (apply X, then Y, then Z) ──
    // A self-consistent inverse pair for the property fields: the numbers are just a UI parameterization —
    // the rotation actually applied is always the exact quaternion.

    public static Vector3 QuatToEulerDeg(Quaternion q)
    {
        float sinrCosp = 2f * (q.W * q.X + q.Y * q.Z);
        float cosrCosp = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        float x = MathF.Atan2(sinrCosp, cosrCosp);

        float sinp = 2f * (q.W * q.Y - q.Z * q.X);
        float y = MathF.Abs(sinp) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinp) : MathF.Asin(sinp);

        float sinyCosp = 2f * (q.W * q.Z + q.X * q.Y);
        float cosyCosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        float z = MathF.Atan2(sinyCosp, cosyCosp);

        const float toDeg = 180f / MathF.PI;
        return new Vector3(x * toDeg, y * toDeg, z * toDeg);
    }

    public static Quaternion EulerDegToQuat(Vector3 deg)
    {
        const float toRad = MathF.PI / 180f;
        Quaternion x = Quaternion.CreateFromAxisAngle(Vector3.UnitX, deg.X * toRad);
        Quaternion y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, deg.Y * toRad);
        Quaternion z = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg.Z * toRad);
        return z * y * x; // System.Numerics operator: apply X, then Y, then Z
    }

}
