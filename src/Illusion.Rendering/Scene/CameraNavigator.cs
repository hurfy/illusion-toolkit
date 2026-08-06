using System.Numerics;

namespace Illusion.Rendering.Scene;

/// <summary>
/// Mouse-only camera navigation, Blender-style: the camera swings around, slides along and moves toward a pivot
/// held a fixed distance ahead of it. Pure functions over a <see cref="Camera"/> plus that distance — the viewport
/// control owns the distance and the input events, so this stays drivable by a headless probe.
/// </summary>
public static class CameraNavigator
{
    /// <summary>Closest the camera may come to the pivot — at zero it would pass through the thing it orbits.</summary>
    public const float MinPivotDistance = 0.5f;

    /// <summary>
    /// Smallest a parallel view may be zoomed to, in world units of visible height. It can go far below what a
    /// perspective view can reach (<see cref="MinPivotDistance"/> worth of standoff) because nothing approaches
    /// anything: the extent shrinks while the camera stays put, so there is no object to end up inside of.
    /// </summary>
    public const float MinOrthoHeight = 0.05f;

    /// <summary>
    /// What the walk-mode modifiers do to the camera's speed: held Shift multiplies by this, held Ctrl divides by
    /// it. One number rather than two, so the two are exact opposites and holding both cancels out precisely —
    /// and so it mirrors what the same keys do to a transform: Shift for the coarse move, Ctrl for the careful one.
    /// </summary>
    public const float SpeedStep = 2.5f;

    /// <summary>Walk speed multiplier for the modifiers held right now.</summary>
    public static float SpeedMultiplier(bool boost, bool crawl) =>
        (boost ? SpeedStep : 1f) / (crawl ? SpeedStep : 1f);

    /// <summary>The point the camera turns around: straight ahead, <paramref name="distance"/> away.</summary>
    public static Vector3 PivotOf(Camera cam, float distance) => cam.Position + cam.Forward * distance;

    /// <summary>Camera-space up (Z-up world), derived from the same basis <see cref="Camera.Right"/> uses.</summary>
    public static Vector3 UpOf(Camera cam) => Vector3.Normalize(Vector3.Cross(cam.Right, cam.Forward));

    /// <summary>Swings the camera around its pivot by the given yaw/pitch (radians); the pivot stays put, so
    /// whatever was centred stays centred.</summary>
    public static void Orbit(Camera cam, float distance, float deltaYaw, float deltaPitch)
    {
        Vector3 pivot = PivotOf(cam, distance);
        cam.Yaw += deltaYaw;
        cam.Pitch = Math.Clamp(cam.Pitch + deltaPitch, -Camera.MaxPitch, Camera.MaxPitch);
        cam.Position = pivot - cam.Forward * distance;
    }

    /// <summary>
    /// Slides the camera (and its pivot with it) across the view plane by a pointer movement in pixels. The world
    /// distance per pixel is taken from how wide the view is AT the pivot, so a drag keeps pace with the scene at
    /// any zoom: near the ground it creeps, far out it sweeps.
    /// </summary>
    public static void Pan(Camera cam, float distance, float dxPixels, float dyPixels, double viewportHeight)
    {
        float perPixel = WorldPerPixel(cam, distance, viewportHeight);
        // Drag right → the scene should follow the pointer right, which means the camera goes left.
        cam.Position += -cam.Right * (dxPixels * perPixel) + UpOf(cam) * (dyPixels * perPixel);
    }

    /// <summary>
    /// Moves the camera toward (positive) or away from (negative) its pivot by wheel notches, geometrically — each
    /// notch closes a fixed fraction of what is left, so the approach slows down as it gets close and never
    /// overshoots. Returns the new pivot distance; the pivot itself does not move.
    /// </summary>
    public static float Dolly(Camera cam, float distance, float notches)
    {
        float wanted = distance * MathF.Pow(1f - ZoomPerNotch, notches);
        float next = MathF.Max(MinPivotDistance, wanted);
        cam.Position += cam.Forward * (distance - next);
        return next;
    }

    /// <summary>
    /// Where to put the camera so a sphere (<paramref name="center"/>, <paramref name="radius"/>) fills the view
    /// comfortably, keeping the direction it is already looking from. Returns the eye position and the pivot
    /// distance that goes with it — the caller tweens to the one and stores the other.
    /// </summary>
    public static (Vector3 Eye, float Distance) FrameOn(Camera cam, Vector3 center, float radius)
    {
        // Fit the sphere in the NARROWER of the two half-angles, so a wide window frames it just as fully as a
        // tall one; the margin keeps it off the very edges. A degenerate (point) target still gets a sane standoff.
        float halfV = cam.Fov * 0.5f;
        float halfH = MathF.Atan(MathF.Tan(halfV) * MathF.Max(0.05f, cam.AspectRatio));
        float half = MathF.Min(halfV, halfH);
        float distance = MathF.Max(MinPivotDistance, MathF.Max(radius, 0.25f) * FrameMargin / MathF.Sin(half));
        return (center - cam.Forward * distance, distance);
    }

    /// <summary>
    /// How much world a perspective view spans top to bottom at <paramref name="distance"/> — and therefore the
    /// extent a parallel one needs to frame the very same thing. This pair of conversions is what lets the two
    /// projections be swapped without the picture jumping: whatever sits at the pivot keeps its size on screen.
    /// </summary>
    public static float OrthoHeightFor(Camera cam, float distance) =>
        2f * distance * MathF.Tan(cam.Fov * 0.5f);

    /// <summary>The inverse of <see cref="OrthoHeightFor"/>: how far back a perspective camera has to stand to
    /// span <paramref name="orthoHeight"/>. Never closer than <see cref="MinPivotDistance"/>.</summary>
    public static float DistanceForOrthoHeight(Camera cam, float orthoHeight) =>
        MathF.Max(MinPivotDistance, orthoHeight / (2f * MathF.Tan(cam.Fov * 0.5f)));

    /// <summary>
    /// Switches the camera into the parallel projection at the framing it already has: the extent is the height
    /// the perspective view spanned at the pivot, so whatever was centred does not change size. A camera that
    /// is already parallel is left alone — a second axis snap must not throw away the zoom the user dialled in.
    /// </summary>
    public static void EnterParallel(Camera cam, float distance)
    {
        if (cam.Orthographic) return;
        cam.Orthographic = true;
        cam.OrthoHeight = OrthoHeightFor(cam, distance);
    }

    /// <summary>
    /// Switches back to perspective without moving what is on screen: the extent becomes the standoff that
    /// spans the same height, and the camera is put at it around the pivot it was already turning about — so
    /// the swap costs nothing but depth. Returns the new pivot distance (the old one, for a camera that was
    /// already a perspective one).
    /// </summary>
    public static float LeaveParallel(Camera cam, float distance)
    {
        if (!cam.Orthographic) return distance;
        Vector3 pivot = PivotOf(cam, distance);
        float next = DistanceForOrthoHeight(cam, cam.OrthoHeight);
        cam.Position = pivot - cam.Forward * next;
        cam.Orthographic = false;
        return next;
    }

    /// <summary>
    /// Zooms a parallel view by wheel notches — the same geometric step <see cref="Dolly"/> takes, so one notch
    /// feels identical in either projection, but applied to the EXTENT: with no vanishing point, closing the
    /// distance to something does not make it any bigger. The camera still fixes the near plane, so it only
    /// ever retreats — zooming out pulls it back far enough to keep the whole extent in front of it, zooming in
    /// leaves it where it is rather than driving it into the object being worked on. Returns the pivot distance.
    /// </summary>
    public static float DollyParallel(Camera cam, float distance, float notches)
    {
        cam.OrthoHeight = MathF.Max(MinOrthoHeight, cam.OrthoHeight * MathF.Pow(1f - ZoomPerNotch, notches));
        float standoff = DistanceForOrthoHeight(cam, cam.OrthoHeight);
        if (standoff <= distance) return distance;
        cam.Position -= cam.Forward * (standoff - distance);
        return standoff;
    }

    private const float ZoomPerNotch = 0.15f;  // fraction of the remaining distance closed per wheel notch
    private const float FrameMargin = 1.25f;   // breathing room around a framed object

    // Half the view's world height at the pivot, per screen pixel. A parallel view spans the same height at
    // every depth, so its own extent IS the answer and the pivot distance plays no part.
    private static float WorldPerPixel(Camera cam, float distance, double viewportHeight) =>
        viewportHeight < 1.0 ? 0f
            : cam.Orthographic ? (float)(cam.OrthoHeight / viewportHeight)
            : (float)(2.0 * distance * MathF.Tan(cam.Fov * 0.5f) / viewportHeight);
}
