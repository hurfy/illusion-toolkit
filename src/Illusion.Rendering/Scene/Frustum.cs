using System.Numerics;

namespace Illusion.Rendering.Scene;

/// <summary>
/// View frustum from ViewProjection (Gribb–Hartmann, row-vector / D3D depth 0..1).
/// Planes face inward: a point is inside if a·x+b·y+c·z+d ≥ 0 for all.
/// </summary>
public struct Frustum
{
    private Vector4 _left, _right, _bottom, _top, _near, _far;

    public static Frustum FromMatrix(Matrix4x4 m)
    {
        // Columns of M (clip = v·M): col0=x, col1=y, col2=z, col3=w.
        Vector4 cx = new(m.M11, m.M21, m.M31, m.M41);
        Vector4 cy = new(m.M12, m.M22, m.M32, m.M42);
        Vector4 cz = new(m.M13, m.M23, m.M33, m.M43);
        Vector4 cw = new(m.M14, m.M24, m.M34, m.M44);

        return new Frustum
        {
            _left = Normalize(cw + cx),
            _right = Normalize(cw - cx),
            _bottom = Normalize(cw + cy),
            _top = Normalize(cw - cy),
            _near = Normalize(cz),
            _far = Normalize(cw - cz),
        };
    }

    public bool Intersects(Vector3 min, Vector3 max)
    {
        return !Outside(_left, min, max) && !Outside(_right, min, max)
            && !Outside(_bottom, min, max) && !Outside(_top, min, max)
            && !Outside(_near, min, max) && !Outside(_far, min, max);
    }

    // The box is entirely behind the plane if even its farthest corner along the normal is behind it.
    private static bool Outside(Vector4 p, Vector3 min, Vector3 max)
    {
        float px = p.X >= 0 ? max.X : min.X;
        float py = p.Y >= 0 ? max.Y : min.Y;
        float pz = p.Z >= 0 ? max.Z : min.Z;
        return p.X * px + p.Y * py + p.Z * pz + p.W < 0f;
    }

    private static Vector4 Normalize(Vector4 p)
    {
        float len = new Vector3(p.X, p.Y, p.Z).Length();
        return len > 0f ? p / len : p;
    }
}
