using System.Numerics;

namespace Illusion.Bridge.Geometry;

/// <summary>
/// Per-vertex tangent frames from positions/normals/UVs (Lengyel accumulation + Gram–Schmidt,
/// handedness from the UV winding). Used only for vertices whose attributes actually changed in
/// Blender — untouched vertices keep their original packed tangent bytes, so the generator's exact
/// flavor (vs the engine's original tool or MikkTSpace) only matters where the user sculpted.
/// </summary>
public static class TangentGenerator
{
    public static (Vector3[] Tangents, Vector3[] Binormals) Compute(
        Vector3[] positions, Vector3[] normals, Vector2[] uvs, uint[] indices)
    {
        var accumT = new Vector3[positions.Length];
        var accumB = new Vector3[positions.Length];

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            uint i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
            Vector3 e1 = positions[i1] - positions[i0];
            Vector3 e2 = positions[i2] - positions[i0];
            Vector2 duv1 = uvs[i1] - uvs[i0];
            Vector2 duv2 = uvs[i2] - uvs[i0];

            float det = duv1.X * duv2.Y - duv2.X * duv1.Y;
            if (MathF.Abs(det) < 1e-12f) continue; // degenerate UV mapping — contributes nothing

            float r = 1.0f / det;
            Vector3 sdir = (e1 * duv2.Y - e2 * duv1.Y) * r;
            Vector3 tdir = (e2 * duv1.X - e1 * duv2.X) * r;

            accumT[i0] += sdir; accumT[i1] += sdir; accumT[i2] += sdir;
            accumB[i0] += tdir; accumB[i1] += tdir; accumB[i2] += tdir;
        }

        var tangents = new Vector3[positions.Length];
        var binormals = new Vector3[positions.Length];
        for (int v = 0; v < positions.Length; v++)
        {
            Vector3 n = normals[v];
            Vector3 t = accumT[v] - n * Vector3.Dot(n, accumT[v]); // Gram–Schmidt against the normal
            if (t.LengthSquared() < 1e-12f)
            {
                // No UV gradient (unreferenced or degenerate) — any stable perpendicular will do.
                Vector3 axis = MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
                t = Vector3.Cross(n, axis);
            }
            t = Vector3.Normalize(t);

            float handedness = Vector3.Dot(Vector3.Cross(n, t), accumB[v]) < 0f ? -1f : 1f;
            tangents[v] = t;
            binormals[v] = Vector3.Cross(n, t) * handedness;
        }
        return (tangents, binormals);
    }
}
