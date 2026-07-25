using System.Numerics;

namespace Illusion.Formats.Mathematics;

internal static class Vector3Extensions
{
    public static Vector3 TransformCoordinate(this Vector3 coordinate, Matrix4x4 transform)
    {
        Vector4 vector = new Vector4();
        vector.X = (coordinate.X * transform.M11) + (coordinate.Y * transform.M21) + (coordinate.Z * transform.M31) + transform.M41;
        vector.Y = (coordinate.X * transform.M12) + (coordinate.Y * transform.M22) + (coordinate.Z * transform.M32) + transform.M42;
        vector.Z = (coordinate.X * transform.M13) + (coordinate.Y * transform.M23) + (coordinate.Z * transform.M33) + transform.M43;
        vector.W = 1f / ((coordinate.X * transform.M14) + (coordinate.Y * transform.M24) + (coordinate.Z * transform.M34) + transform.M44);

        return new Vector3(vector.X * vector.W, vector.Y * vector.W, vector.Z * vector.W);
    }

    public static Vector3 FromVector4(Vector4 vector4)
    {
        Vector3 vec = new Vector3();
        vec.X = vector4.X;
        vec.Y = vector4.Y;
        vec.Z = vector4.Z;
        return vec;
    }

    public static Vector3 Swap(this Vector3 pos)
    {
        float z = pos.Z;
        pos.Z = pos.X;
        pos.X = z;
        return pos;
    }

    public static bool IsNaN(this Vector3 vector)
    {
        return float.IsNaN(vector.X) || float.IsNaN(vector.Y) || float.IsNaN(vector.Z);
    }
}
