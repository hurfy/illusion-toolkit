using System.Numerics;

namespace Illusion.Rendering.Gizmos;

/// <summary>
/// Pure pointer-ray math behind a gizmo drag: where a view ray meets an axis line or a plane, and the signed
/// angle swept around an axis. Split out of the overlay control so the constrained-drag solve (the keyboard
/// axis lock) is plain math a headless probe can drive — the control only turns mouse points into rays.
/// </summary>
public static class GizmoRayMath
{
    // How square-on the view has to be for a plane solve to be trustworthy: below this the ray lies almost
    // inside the plane, its intersection runs off to infinity, and the object would fling across the map. The
    // drag simply holds its last value instead (~3° of grazing angle).
    private const float PlaneGrazeLimit = 0.05f;

    /// <summary>Parameter t along the axis line (<c>P + t·A</c>, A unit) of the point closest to the view ray
    /// (<c>O + s·D</c>, D unit). Zero when the axis is within rounding of parallel to the ray.</summary>
    public static float ClosestAxisParam(Vector3 p, Vector3 a, Vector3 o, Vector3 d)
    {
        Vector3 r = p - o;
        float b = Vector3.Dot(a, d);
        float c = Vector3.Dot(a, r);
        float f = Vector3.Dot(d, r);
        float denom = 1f - b * b;
        if (denom < 1e-6f) return 0f;   // axis nearly parallel to the view ray
        return (b * f - c) / denom;
    }

    /// <summary>Intersects the ray with the plane through <paramref name="planePoint"/> with normal
    /// <paramref name="n"/>. False (and the plane point) when the ray is parallel to it or the hit is behind
    /// the viewer.</summary>
    public static bool RayPlane(Vector3 o, Vector3 d, Vector3 planePoint, Vector3 n, out Vector3 hit)
    {
        float dn = Vector3.Dot(d, n);
        if (MathF.Abs(dn) < 1e-6f) { hit = planePoint; return false; }
        float t = Vector3.Dot(planePoint - o, n) / dn;
        hit = o + d * t;
        return t > 0f;
    }

    /// <summary>Angle from <paramref name="v1"/> to <paramref name="v2"/> measured around
    /// <paramref name="axis"/> (right-handed, radians). Zero if either vector has collapsed.</summary>
    public static float SignedAngle(Vector3 v1, Vector3 v2, Vector3 axis)
    {
        if (v1.LengthSquared() < 1e-10f || v2.LengthSquared() < 1e-10f) return 0f;
        v1 = Vector3.Normalize(v1);
        v2 = Vector3.Normalize(v2);
        float cos = Math.Clamp(Vector3.Dot(v1, v2), -1f, 1f);
        float ang = MathF.Acos(cos);
        if (Vector3.Dot(Vector3.Cross(v1, v2), axis) < 0f) ang = -ang;
        return ang;
    }

    /// <summary>
    /// Solves a MOVE under an axis lock: the world translation that keeps the locked axis (or the plane across
    /// it) under the pointer. Measured from the drag-START ray, not the previous frame — so locking mid-drag
    /// re-solves the whole drag on the new axis instead of continuing from wherever the free drag had got to.
    /// False when the lock cannot be solved from this viewpoint (grazing plane, or no lock at all); the caller
    /// then leaves the object where it is.
    /// </summary>
    public static bool TryConstrainedMove(Vector3 pivot, AxisConstraint constraint,
        (Vector3 Origin, Vector3 Dir) start, (Vector3 Origin, Vector3 Dir) now, out Vector3 delta)
    {
        delta = Vector3.Zero;
        if (!constraint.IsSome) return false;
        Vector3 axis = TransformOps.WorldAxes[constraint.Axis];

        if (!constraint.IsPlane)
        {
            float t0 = ClosestAxisParam(pivot, axis, start.Origin, start.Dir);
            float t1 = ClosestAxisParam(pivot, axis, now.Origin, now.Dir);
            delta = axis * (t1 - t0);
            return true;
        }

        // Plane lock: the pointer drives a point on the plane through the pivot whose normal is the EXCLUDED axis.
        if (!TryPlanePoint(pivot, axis, start, out Vector3 from) || !TryPlanePoint(pivot, axis, now, out Vector3 to))
            return false;
        delta = to - from;
        return true;
    }

    /// <summary>
    /// Solves a ROTATE under an axis lock: the angle (radians) swept around <paramref name="axis"/> through the
    /// pivot, again measured from the drag-start ray. False when the ring is too edge-on to read an angle from.
    /// </summary>
    public static bool TryConstrainedRotate(Vector3 pivot, int axis,
        (Vector3 Origin, Vector3 Dir) start, (Vector3 Origin, Vector3 Dir) now, out float radians)
    {
        radians = 0f;
        Vector3 normal = TransformOps.WorldAxes[axis];
        if (!TryPlanePoint(pivot, normal, start, out Vector3 from) || !TryPlanePoint(pivot, normal, now, out Vector3 to))
            return false;
        radians = SignedAngle(from - pivot, to - pivot, normal);
        return true;
    }

    // Ray → plane through the pivot, refusing grazing views (see PlaneGrazeLimit).
    private static bool TryPlanePoint(Vector3 pivot, Vector3 normal, (Vector3 Origin, Vector3 Dir) ray, out Vector3 hit)
    {
        hit = pivot;
        if (MathF.Abs(Vector3.Dot(ray.Dir, normal)) < PlaneGrazeLimit) return false;
        return RayPlane(ray.Origin, ray.Dir, pivot, normal, out hit);
    }
}
