using System.Numerics;

namespace Illusion.Formats.Mathematics;

internal static class MatrixExtensions
{
    public static void SetColumn(ref this Matrix4x4 matrix, int Index, Vector4 NewColumn)
    {
        if (Index == 0) { matrix.M11 = NewColumn.X; matrix.M21 = NewColumn.Y; matrix.M31 = NewColumn.Z; matrix.M41 = NewColumn.W; }
        else if (Index == 1) { matrix.M12 = NewColumn.X; matrix.M22 = NewColumn.Y; matrix.M32 = NewColumn.Z; matrix.M42 = NewColumn.W; }
        else if (Index == 2) { matrix.M13 = NewColumn.X; matrix.M23 = NewColumn.Y; matrix.M33 = NewColumn.Z; matrix.M43 = NewColumn.W; }
        else if (Index == 3) { matrix.M14 = NewColumn.X; matrix.M24 = NewColumn.Y; matrix.M34 = NewColumn.Z; matrix.M44 = NewColumn.W; }
        else { FormatAssert.Ensure(false, "Invalid Index passed into Matrix4x4.GetColumn"); }
    }

    public static Vector4 GetColumn(this Matrix4x4 matrix, int Index)
    {
        if (Index == 0) { return new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41); }
        else if (Index == 1) { return new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42); }
        else if (Index == 2) { return new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43); }
        else if (Index == 3) { return new Vector4(matrix.M14, matrix.M24, matrix.M34, matrix.M44); }
        else { FormatAssert.Ensure(false, "Invalid Index passed into Matrix4x4.GetColumn"); }

        return Vector4.Zero;
    }

    public static void SetRow(ref this Matrix4x4 matrix, int Index, Vector4 NewRow)
    {
        if (Index == 0) { matrix.M11 = NewRow.X; matrix.M12 = NewRow.Y; matrix.M13 = NewRow.Z; matrix.M14 = NewRow.W; }
        else if (Index == 1) { matrix.M21 = NewRow.X; matrix.M22 = NewRow.Y; matrix.M23 = NewRow.Z; matrix.M24 = NewRow.W; }
        else if (Index == 2) { matrix.M31 = NewRow.X; matrix.M32 = NewRow.Y; matrix.M33 = NewRow.Z; matrix.M34 = NewRow.W; }
        else if (Index == 3) { matrix.M41 = NewRow.X; matrix.M42 = NewRow.Y; matrix.M43 = NewRow.Z; matrix.M44 = NewRow.W; }
        else { FormatAssert.Ensure(false, "Invalid Index passed into Matrix4x4.GetColumn"); }
    }

    public static Vector4 GetRow(this Matrix4x4 matrix, int Index)
    {
        if (Index == 0) { return new Vector4(matrix.M11, matrix.M12, matrix.M13, matrix.M14); }
        else if (Index == 1) { return new Vector4(matrix.M21, matrix.M22, matrix.M23, matrix.M24); }
        else if (Index == 2) { return new Vector4(matrix.M31, matrix.M32, matrix.M33, matrix.M34); }
        else if (Index == 3) { return new Vector4(matrix.M41, matrix.M42, matrix.M43, matrix.M44); }
        else { FormatAssert.Ensure(false, "Invalid Index passed into Matrix4x4.GetRow"); }

        return Vector4.Zero;
    }

    public static Matrix4x4 CopyFrom(this Matrix4x4 Other)
    {
        Matrix4x4 NewTransform = new Matrix4x4(
            Other.M11, Other.M12, Other.M13, Other.M14,
            Other.M21, Other.M22, Other.M23, Other.M24,
            Other.M31, Other.M32, Other.M33, Other.M34,
            Other.M41, Other.M42, Other.M43, Other.M44);

        return NewTransform;
    }

    public static bool IsNaN(this Matrix4x4 matrix)
    {
        return matrix.GetColumn(0).IsNaN() || matrix.GetColumn(1).IsNaN() || matrix.GetColumn(2).IsNaN() || matrix.GetColumn(3).IsNaN();
    }

    public static Matrix4x4 SetMatrix(Quaternion rotation, Vector3 scale, Vector3 position)
    {
        // Doing the normal T * R * S does not work; I have to manually push in the vector into the final row.
        Matrix4x4 r = Matrix4x4.CreateFromQuaternion(rotation);
        Matrix4x4 s = Matrix4x4.CreateScale(scale);
        Matrix4x4 final = r * s;
        final.Translation = position;

        return final;
    }

    /// <summary>
    /// Decomposes a matrix built by <see cref="SetMatrix(Quaternion, Vector3, Vector3)"/> (rotation·scale with a
    /// forced translation row) back into its parts. <see cref="Matrix4x4.Decompose"/> assumes the opposite
    /// scale·rotation order and fails for any rotated, non-uniformly scaled frame matrix — in the R·S form the
    /// scale lives in the COLUMN norms of the 3×3 block. On failure (degenerate, sheared or non-finite input)
    /// returns false with identity rotation and unit scale; the translation is always recovered.
    /// </summary>
    public static bool TryDecomposeRS(in Matrix4x4 m, out Vector3 scale, out Quaternion rotation, out Vector3 position)
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

    public static Matrix4x4 CreateFromDirection(Vector3 Direction)
    {
        Vector3 UpDirection = new Vector3(0.0f, 0.0f, 1.0f);

        Matrix4x4 NewMatrix = Matrix4x4.Identity;

        Vector3 XAxis = Vector3.Normalize(Vector3.Cross(UpDirection, Direction));
        Vector3 YAxis = Vector3.Normalize(Vector3.Cross(Direction, XAxis));

        NewMatrix.SetColumn(0, new Vector4(XAxis.X, YAxis.X, Direction.X, 1.0f));
        NewMatrix.SetColumn(1, new Vector4(XAxis.Y, YAxis.Y, Direction.Y, 1.0f));
        NewMatrix.SetColumn(2, new Vector4(XAxis.Z, YAxis.Z, Direction.Z, 1.0f));
        return NewMatrix;
    }

    public static Matrix4x4 SetMatrix(Vector3 rotation, Vector3 scale, Vector3 position)
    {
        float X = MathHelper.ToRadians(rotation.X);
        float Y = MathHelper.ToRadians(rotation.Y);
        float Z = MathHelper.ToRadians(rotation.Z);

        Quaternion rotation1 = Quaternion.CreateFromYawPitchRoll(Y, X, Z);
        return SetMatrix(rotation1, scale, position);
    }
}
