using System.Numerics;

namespace Illusion.Formats.Navigation;

/// <summary>
/// Shared helpers for turning navigation data into viewport-space overlay geometry: the Kynapse→engine
/// axis swap and box-wireframe emission. Used by both <see cref="ObjDataFile"/> (.nov) and
/// <see cref="AiWorldFile"/> (.nav).
/// </summary>
internal static class NavViewGeometry
{
    // Kynapse (Y-up) → engine/viewport (Z-up): (x, y, z) → (x, -z, y). Nav data is stored in the AI
    // middleware's frame; the meshes/collision are in the engine frame, so nav needs this swap.
    public static Vector3 ToViewSpace(Vector3 p) => new(p.X, -p.Z, p.Y);

    // The 12 edges of a box as pairs of corner indices (into the 8-corner array built in AddBox).
    private static readonly int[] BoxEdges =
    {
        0,1, 1,2, 2,3, 3,0,   // bottom face
        4,5, 5,6, 6,7, 7,4,   // top face
        0,4, 1,5, 2,6, 3,7,   // verticals
    };

    /// <summary>Appends a Kynapse-space (Y-up) AABB's 12 wireframe edges in viewport space (.nov data).</summary>
    public static void AddBox(List<Vector3> lines, Vector3 fileMin, Vector3 fileMax) =>
        EmitBox(lines, ToViewSpace(fileMin), ToViewSpace(fileMax));

    /// <summary>Appends an already-engine-space (Z-up) AABB's 12 wireframe edges verbatim (.nav data,
    /// which is stored in the engine frame — no swap needed, unlike .nov).</summary>
    public static void AddBoxRaw(List<Vector3> lines, Vector3 min, Vector3 max) =>
        EmitBox(lines, min, max);

    private static void EmitBox(List<Vector3> lines, Vector3 a, Vector3 b)
    {
        Vector3 mn = Vector3.Min(a, b);
        Vector3 mx = Vector3.Max(a, b);
        Span<Vector3> c = stackalloc Vector3[8]
        {
            new(mn.X, mn.Y, mn.Z), new(mx.X, mn.Y, mn.Z), new(mx.X, mx.Y, mn.Z), new(mn.X, mx.Y, mn.Z),
            new(mn.X, mn.Y, mx.Z), new(mx.X, mn.Y, mx.Z), new(mx.X, mx.Y, mx.Z), new(mn.X, mx.Y, mx.Z),
        };
        foreach (int idx in BoxEdges) lines.Add(c[idx]);
    }

    /// <summary>
    /// Appends an oriented box (engine Z-up frame): centered at <paramref name="center"/>, its Y axis turned
    /// to face <paramref name="forward"/> (world up = +Z), with per-axis half-sizes (X→right, Y→forward,
    /// Z→up). Also appends a short facing line so the direction is visible. Falls back to an axis-aligned box
    /// when there is no direction. Used for .nav path objects (cover / vault-over markers) which are stored
    /// with a facing direction but no contour.
    /// </summary>
    public static void AddOrientedBox(List<Vector3> lines, Vector3 center, Vector3 half, Vector3 forward)
    {
        if (forward.LengthSquared() < 1e-8f) { AddBoxRaw(lines, center - half, center + half); return; }

        Vector3 f = Vector3.Normalize(forward);
        Vector3 up = new(0, 0, 1);
        Vector3 r = Vector3.Cross(up, f);
        if (r.LengthSquared() < 1e-8f) { up = new(0, 1, 0); r = Vector3.Cross(up, f); } // forward nearly vertical
        r = Vector3.Normalize(r);
        up = Vector3.Normalize(Vector3.Cross(f, r));

        Vector3 ex = r * half.X, ey = f * half.Y, ez = up * half.Z;
        Span<Vector3> c = stackalloc Vector3[8]
        {
            center - ex - ey - ez, center + ex - ey - ez, center + ex + ey - ez, center - ex + ey - ez,
            center - ex - ey + ez, center + ex - ey + ez, center + ex + ey + ez, center - ex + ey + ez,
        };
        foreach (int idx in BoxEdges) lines.Add(c[idx]);

        // Facing line: from the center out through the front face, so orientation reads at a glance.
        lines.Add(center);
        lines.Add(center + f * (half.Y + 0.5f));
    }
}
