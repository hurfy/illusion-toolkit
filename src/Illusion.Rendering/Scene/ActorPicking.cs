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
    /// The allowance a PARALLEL view wants instead: one fixed world radius rather than one that opens up with
    /// distance, because that is the whole difference — a glyph two hundred metres back is drawn exactly as
    /// large as one under your nose, so growing its clickable sphere only lets it steal clicks from what is
    /// actually under the cursor. Sized to the same few pixels the angular allowance gives a perspective view
    /// framed the same way. A perspective camera answers -1, which the pick reads as "grow it with distance" —
    /// so a caller hands this straight through instead of asking which projection it is looking through.
    /// </summary>
    public static float ParallelSlack(Camera cam) => cam.Orthographic
        ? AngularRadius * cam.OrthoHeight / (2f * MathF.Tan(cam.Fov * 0.5f))
        : -1f;

    /// <summary>
    /// Nearest glyph under the ray, or -1 when it misses everything. <paramref name="worldRadius"/> is the
    /// glyph's own size — the test uses whichever is larger, that or the allowance.
    /// <paramref name="parallelSlack"/> is <see cref="ParallelSlack"/>; below zero the allowance grows with
    /// distance, which is right for a perspective view and only for one.
    /// </summary>
    public static int Pick(IReadOnlyList<Vector3> markers, Vector3 origin, Vector3 dir, float worldRadius,
        out float bestT, float parallelSlack = -1f)
    {
        int best = -1;
        bestT = float.MaxValue;

        for (int i = 0; i < markers.Count; i++)
        {
            Vector3 toCentre = markers[i] - origin;
            float along = Vector3.Dot(toCentre, dir);
            if (along <= 0f) continue; // behind the camera

            float allowance = parallelSlack >= 0f ? parallelSlack : along * AngularRadius;
            float radius = MathF.Max(worldRadius, allowance);
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

    /// <summary>
    /// The same test where each glyph has its OWN size: helper glyphs are not one shape, and a two-metre
    /// volume box and a bare point cannot share a clickable radius. <paramref name="radii"/> is read in step
    /// with <paramref name="markers"/>; a shorter list treats the rest as sizeless (angular allowance only).
    /// </summary>
    public static int Pick(IReadOnlyList<Vector3> markers, IReadOnlyList<float> radii, Vector3 origin,
        Vector3 dir, out float bestT, float parallelSlack = -1f)
    {
        int best = -1;
        bestT = float.MaxValue;

        for (int i = 0; i < markers.Count; i++)
        {
            float own = i < radii.Count ? radii[i] : 0f;
            int hit = Pick([markers[i]], origin, dir, own, out float t, parallelSlack);
            if (hit < 0 || t >= bestT) continue;
            bestT = t;
            best = i;
        }

        if (best < 0) bestT = 0f;
        return best;
    }
}
