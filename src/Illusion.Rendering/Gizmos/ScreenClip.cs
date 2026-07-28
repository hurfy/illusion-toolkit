using System.Windows;

namespace Illusion.Rendering.Gizmos;

/// <summary>
/// Screen-space clipping for overlay guide lines. A guide runs far past the pivot so it reads as endless, and its
/// far end can land a hair in front of the camera — where the perspective divide throws the projected point
/// millions of pixels away. Letting the graphics layer clip that is expensive (a dashed line is walked along its
/// whole length before anything is discarded), so the segment is cut down to the viewport rectangle first.
/// </summary>
public static class ScreenClip
{
    /// <summary>
    /// Liang–Barsky: trims the segment to the rectangle (0,0)–(<paramref name="width"/>,<paramref name="height"/>).
    /// False when the segment misses it entirely, or when a coordinate is not a finite number (a point projected
    /// right on the camera plane) — either way there is nothing sensible to draw.
    /// </summary>
    public static bool SegmentToRect(Point a, Point b, double width, double height, out Point from, out Point to)
    {
        from = a;
        to = b;
        if (!IsFinite(a.X) || !IsFinite(a.Y) || !IsFinite(b.X) || !IsFinite(b.Y)) return false;

        double dx = b.X - a.X, dy = b.Y - a.Y;
        double t0 = 0.0, t1 = 1.0;

        // One (edge distance, direction) pair per side: left, right, top, bottom.
        if (!Trim(-dx, a.X, ref t0, ref t1)) return false;
        if (!Trim(dx, width - a.X, ref t0, ref t1)) return false;
        if (!Trim(-dy, a.Y, ref t0, ref t1)) return false;
        if (!Trim(dy, height - a.Y, ref t0, ref t1)) return false;

        from = new Point(a.X + dx * t0, a.Y + dy * t0);
        to = new Point(a.X + dx * t1, a.Y + dy * t1);
        return true;
    }

    // Narrows [t0,t1] to the part of the segment inside one edge. False once nothing is left.
    private static bool Trim(double p, double q, ref double t0, ref double t1)
    {
        if (p == 0.0) return q >= 0.0;   // parallel to this edge: in or out for its whole length
        double r = q / p;
        if (p < 0.0)
        {
            if (r > t1) return false;
            if (r > t0) t0 = r;
        }
        else
        {
            if (r < t0) return false;
            if (r < t1) t1 = r;
        }
        return true;
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
