using System.Numerics;

namespace Illusion.Domain;

/// <summary>
/// The single source of truth for Mafia's frame-transform math (row-vector convention: a point p maps as
/// <c>p · M</c>, "apply A then B" composes as <c>A * B</c>). These are exact ports of the formulas the game's
/// serialized transforms were authored against (MafiaToolkit's MatrixUtils.SetMatrix /
/// Vector3Utils.TransformCoordinate / FrameObjectBase's world-transform derivation), kept here so the gizmo
/// math, the scene loaders and the diagnostics probes all share one implementation.
/// </summary>
public static class TransformMath
{
    /// <summary>Builds a local transform from rotation, scale and position. Note the order — rotation·scale
    /// with the translation row forced afterwards — matches how the game composes frame matrices; a plain
    /// T·R·S would not round-trip through its decomposition.</summary>
    public static Matrix4x4 Compose(Quaternion rotation, Vector3 scale, Vector3 position)
    {
        Matrix4x4 m = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateScale(scale);
        m.Translation = position;
        return m;
    }

    /// <summary>
    /// Converts a streamed collision instance's Euler rotation (radians) to a quaternion in this toolkit's Z-up
    /// world space. MafiaToolkitV2 places a collision instance in its Y-up (Wicked) engine via
    /// <c>XMQuaternionRotationRollPitchYaw(rot.X, rot.Z, rot.Y)</c> applied to Y↔Z-swapped geometry; de-swapping
    /// that world back into our Z-up space is a conjugation by the Y↔Z swap, which works out to: apply −rot.Y
    /// about Y, then −rot.X about X, then −rot.Z about Z (every angle negated, X/Y order swapped). Verified to
    /// 0.002° against V2's exact formula by <c>--probe-collision-align</c>. This is deliberately NOT the
    /// frame-object gizmo Euler convention — collision uses its own reflected convention.
    /// </summary>
    public static Quaternion CollisionEulerToQuaternion(Vector3 radians)
    {
        Quaternion x = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -radians.X);
        Quaternion y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -radians.Y);
        Quaternion z = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -radians.Z);
        return z * x * y; // System.Numerics multiplies right-to-left: apply Y, then X, then Z (all negated)
    }

    /// <summary>
    /// The inverse of <see cref="CollisionEulerToQuaternion"/>: recovers a collision instance's Euler rotation
    /// (radians) from a world rotation quaternion, so a gizmo-dragged / re-localized world transform can be
    /// written back into the .col placement. Derived from the ZXY(negated) matrix form of the forward convention
    /// and round-trip-verified by <c>--probe-collision-align</c> (euler→quat→euler reproduces the same rotation).
    /// Euler triples are non-unique, so this returns one valid representative — it always composes back to the
    /// same orientation. Gimbal lock (pitch ±90°) folds the Y/Z terms and pins Z to 0.
    /// </summary>
    public static Vector3 CollisionEulerFromQuaternion(Quaternion q)
    {
        Matrix4x4 m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q));
        float s = Math.Clamp(m.M23, -1f, 1f);
        float a = MathF.Asin(s); // = -rx
        float b, c;
        if (MathF.Abs(s) > 0.99999f) // gimbal lock: only (b−c)/(b+c) is determined — pick c = 0
        {
            b = MathF.Atan2(m.M31, m.M11);
            c = 0f;
        }
        else
        {
            b = MathF.Atan2(-m.M13, m.M33); // = -ry
            c = MathF.Atan2(-m.M21, m.M22); // = -rz
        }
        return new Vector3(-a, -b, -c);
    }

    /// <summary>
    /// Decomposes a matrix built by <see cref="Compose"/> (rotation·scale, translation row forced) back into
    /// its parts. <see cref="Matrix4x4.Decompose"/> assumes the opposite scale·rotation order — it extracts
    /// scale from ROW norms and fails for any rotated, non-uniformly scaled frame matrix — while in the R·S
    /// form the scale lives in the COLUMN norms of the 3×3 block, so this is the exact inverse. On failure
    /// (degenerate, sheared or non-finite input) returns false with identity rotation and unit scale;
    /// the translation is always recovered.
    /// </summary>
    public static bool TryDecompose(in Matrix4x4 m, out Vector3 scale, out Quaternion rotation, out Vector3 position)
    {
        position = m.Translation;
        var c0 = new Vector3(m.M11, m.M21, m.M31);
        var c1 = new Vector3(m.M12, m.M22, m.M32);
        var c2 = new Vector3(m.M13, m.M23, m.M33);
        scale = new Vector3(c0.Length(), c1.Length(), c2.Length());

        // A negative determinant means a mirrored basis — fold the flip into the X axis (like D3DX).
        float det =
            m.M11 * (m.M22 * m.M33 - m.M23 * m.M32) -
            m.M12 * (m.M21 * m.M33 - m.M23 * m.M31) +
            m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);
        if (det < 0f)
        {
            scale.X = -scale.X;
        }

        const float eps = 1e-12f;
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(scale.Z) ||
            MathF.Abs(scale.X) < eps || MathF.Abs(scale.Y) < eps || MathF.Abs(scale.Z) < eps)
        {
            scale = Vector3.One;
            rotation = Quaternion.Identity;
            return false;
        }

        c0 /= scale.X;
        c1 /= scale.Y;
        c2 /= scale.Z;

        // The normalized columns must form an orthonormal basis — a sheared matrix is not R·S.
        const float tol = 1e-3f;
        if (MathF.Abs(Vector3.Dot(c0, c1)) > tol || MathF.Abs(Vector3.Dot(c0, c2)) > tol ||
            MathF.Abs(Vector3.Dot(c1, c2)) > tol)
        {
            scale = Vector3.One;
            rotation = Quaternion.Identity;
            return false;
        }

        var r = new Matrix4x4(
            c0.X, c1.X, c2.X, 0f,
            c0.Y, c1.Y, c2.Y, 0f,
            c0.Z, c1.Z, c2.Z, 0f,
            0f, 0f, 0f, 1f);
        rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(r));
        return true;
    }

    /// <summary>Row-vector affine point transform with w-divide.</summary>
    public static Vector3 TransformCoordinate(Vector3 c, Matrix4x4 t)
    {
        float x = c.X * t.M11 + c.Y * t.M21 + c.Z * t.M31 + t.M41;
        float y = c.X * t.M12 + c.Y * t.M22 + c.Z * t.M32 + t.M42;
        float z = c.X * t.M13 + c.Y * t.M23 + c.Z * t.M33 + t.M43;
        float w = c.X * t.M14 + c.Y * t.M24 + c.Z * t.M34 + t.M44;
        float inv = MathF.Abs(w) > 1e-8f ? 1f / w : 1f;
        return new Vector3(x * inv, y * inv, z * inv);
    }

    /// <summary>
    /// Derives a frame's world transform from its local transform and its parent's world transform the way
    /// the game does: world rotation = parentRot·localRot, world position = localPos through parentWorld,
    /// world scale = local scale (the parent's scale does not propagate).
    /// </summary>
    public static Matrix4x4 ComputeWorldTransform(Matrix4x4 local, Matrix4x4 parentWorld)
    {
        // TryDecompose degrades to identity rotation / unit scale (translation kept) on degenerate input.
        TryDecompose(local, out Vector3 scale, out Quaternion rot, out Vector3 pos);
        TryDecompose(parentWorld, out _, out Quaternion parentRot, out _);
        return Compose(parentRot * rot, scale, TransformCoordinate(pos, parentWorld));
    }

    /// <summary>The exact inverse of <see cref="ComputeWorldTransform"/>: re-localizes a desired
    /// world transform against a parent — local rotation = parentRot⁻¹·worldRot, local position =
    /// worldPos through parentWorld⁻¹, local scale = world scale (the same no-parent-scale rule).</summary>
    public static Matrix4x4 ComputeLocalTransform(Matrix4x4 world, Matrix4x4 parentWorld)
    {
        TryDecompose(world, out Vector3 scale, out Quaternion rot, out Vector3 pos);
        TryDecompose(parentWorld, out _, out Quaternion parentRot, out Vector3 parentPos);
        if (!Matrix4x4.Invert(parentWorld, out Matrix4x4 parentInverse))
        {
            // Singular parent (zero scale on an axis): invert its rigid part instead — Invert fills the out
            // matrix with NaN on failure, and a NaN local transform must never reach the scene or a save.
            Matrix4x4.Invert(Compose(parentRot, Vector3.One, parentPos), out parentInverse);
        }
        return Compose(Quaternion.Inverse(parentRot) * rot, scale, TransformCoordinate(pos, parentInverse));
    }
}
