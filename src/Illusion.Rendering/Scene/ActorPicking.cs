using System.Numerics;

namespace Illusion.Rendering.Scene;

/// <summary>
/// Ray-picking for actor glyphs. A glyph is a small octahedron with no geometry behind it, so the test is
/// ray vs sphere around the actor's position — and the sphere grows with distance, holding a roughly constant
/// angular size: a marker two hundred metres away is a couple of pixels wide, and a strict world-space radius
/// would make it unclickable. Pure math, no D3D — the probes drive it directly.
/// </summary>
public static class ActorPicking
{
    /// <summary>Angular half-size a glyph is treated as having, in radians (~0.6°). Multiplied by the distance
    /// to the camera, so the clickable disc matches what is on screen.</summary>
    private const float AngularRadius = 0.011f;

    /// <summary>
    /// Nearest glyph under the ray, or -1 when it misses everything. <paramref name="worldRadius"/> is the
    /// glyph's own size — the test uses whichever is larger, that or the angular allowance.
    /// </summary>
    public static int Pick(IReadOnlyList<Vector3> markers, Vector3 origin, Vector3 dir, float worldRadius,
        out float bestT)
    {
        int best = -1;
        bestT = float.MaxValue;

        for (int i = 0; i < markers.Count; i++)
        {
            Vector3 toCentre = markers[i] - origin;
            float along = Vector3.Dot(toCentre, dir);
            if (along <= 0f) continue; // behind the camera

            float radius = MathF.Max(worldRadius, along * AngularRadius);
            float perpSq = toCentre.LengthSquared() - along * along;
            if (perpSq > radius * radius) continue;

            // Entry point of the ray into the sphere; ties (concentric markers) keep the first found.
            float half = MathF.Sqrt(MathF.Max(0f, radius * radius - perpSq));
            float t = along - half;
            if (t < 0f) t = along;
            if (t < bestT)
            {
                bestT = t;
                best = i;
            }
        }

        if (best < 0) bestT = 0f;
        return best;
    }
}
