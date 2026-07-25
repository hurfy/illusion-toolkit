using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using Illusion.Rendering.Gizmos;
using Illusion.Rendering.Scene;

namespace Illusion.Rendering.Controls;

/// <summary>
/// Blender-style manipulation gizmo overlaid on the viewport: three world axes for Move/Scale and three rings
/// for Rotate, drawn at a screen-constant size at the selection pivot. A WPF <see cref="FrameworkElement"/>
/// (like the navigation gizmo) — it projects world handles to screen and maps drags back to world deltas.
/// Hit-testing is limited to the handles (<see cref="HitTestCore"/> returns null elsewhere) so empty-space
/// clicks fall through to the viewport for picking and camera look.
/// </summary>
public sealed class TransformGizmo : FrameworkElement
{
    private const double HandlePixels = 90;   // axis / ring radius in screen pixels
    private const double LineHitPx = 8;        // pointer proximity to grab a line or ring
    private const double CenterR = 9;          // centre handle radius (screen-plane move / uniform scale)
    private const int RingSegments = 48;

    private static readonly Color[] AxisColors =
    {
        Color.FromRgb(0xE6, 0x46, 0x46), // X — red
        Color.FromRgb(0x82, 0xC8, 0x3C), // Y — green
        Color.FromRgb(0x46, 0x82, 0xDC), // Z — blue
    };
    private static readonly Color HighlightColor = Color.FromRgb(0xFF, 0xD2, 0x4A); // hovered / active handle

    private static readonly Pen[] AxisPens = new Pen[3];
    private static readonly Brush[] AxisBrushes = new Brush[3];
    private static readonly Pen HighlightPen;
    private static readonly Brush HighlightBrush;
    private static readonly Brush CenterBrush;
    private static readonly Pen CenterPen;

    static TransformGizmo()
    {
        for (int i = 0; i < 3; i++)
        {
            AxisPens[i] = Freeze(new Pen(new SolidColorBrush(AxisColors[i]), 2.0)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            AxisBrushes[i] = Freeze(new SolidColorBrush(AxisColors[i]));
        }
        HighlightPen = Freeze(new Pen(new SolidColorBrush(HighlightColor), 2.6)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        HighlightBrush = Freeze(new SolidColorBrush(HighlightColor));
        CenterBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xEC, 0xEC, 0xEC)));
        CenterPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)), 1.4));
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private ITransformGizmoHost? _host;

    private enum HandleKind { None, MoveAxis, MovePlane, RotateAxis, ScaleAxis, ScaleUniform }
    private readonly record struct Handle(HandleKind Kind, int Axis)
    {
        public static readonly Handle None = new(HandleKind.None, -1);
        public bool IsSome => Kind != HandleKind.None;
    }

    private Handle _hover = Handle.None;
    private Handle _active = Handle.None;

    // Drag references, captured at drag-start.
    private Vector3 _dragPivot;
    private Vector3 _dragAxis;
    private double _dragHandleWorld;
    private float _dragStartAxisT;
    private Vector3 _dragStartVec;      // rotate
    private Vector3 _dragPlaneNormal;   // move-plane / rotate
    private Vector3 _dragStartHit;      // move-plane
    private double _dragStartScreenDist; // uniform scale

    public TransformGizmo()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Focusable = false;
    }

    /// <summary>Binds the gizmo to a host; it repaints whenever the camera moves.</summary>
    public void Attach(ITransformGizmoHost host)
    {
        if (_host != null) return;
        _host = host;
        host.CameraMoved += InvalidateVisual;
    }

    // ── Layout (world handles → screen) ──

    private struct Layout
    {
        public bool Valid;
        public Point Pivot;
        public double HandleWorld;
        public Vector3 PivotWorld;
        public Point[] AxisEnd;          // screen endpoint of each axis
        public bool[] AxisVisible;       // false if the endpoint is behind the camera
        public Point[][]? Rings;         // screen ring polylines (rotate mode only — null otherwise)
    }

    private Layout BuildLayout()
    {
        var l = new Layout { AxisEnd = new Point[3], AxisVisible = new bool[3] };
        if (_host == null) return l;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return l;

        Matrix4x4 vp = _host.GizmoViewProjection;
        // During a rotate/scale drag, anchor the whole gizmo to the FIXED drag pivot: the selection's AABB centre
        // (GizmoPivot) wanders as the object rotates/scales (a rotated box has a different AABB centre), which
        // otherwise makes the gizmo visibly jitter mid-drag. Move keeps following the object (its pivot is exact).
        l.PivotWorld = _active.Kind is HandleKind.RotateAxis or HandleKind.ScaleAxis or HandleKind.ScaleUniform
            ? _dragPivot : _host.GizmoPivot;
        if (!Project(vp, l.PivotWorld, w, h, out Point sp, out double ndcZ)) return l;
        l.Pivot = sp;

        Vector3 pRight = Unproject(vp, sp.X + 1, sp.Y, ndcZ, w, h);
        double worldPerPixel = (pRight - l.PivotWorld).Length();
        if (worldPerPixel <= 1e-9 || double.IsNaN(worldPerPixel)) return l;
        l.HandleWorld = HandlePixels * worldPerPixel;

        for (int i = 0; i < 3; i++)
        {
            Vector3 end = l.PivotWorld + TransformOps.WorldAxes[i] * (float)l.HandleWorld;
            l.AxisVisible[i] = Project(vp, end, w, h, out Point se, out _);
            l.AxisEnd[i] = se;
        }

        if (_host.GizmoMode == GizmoMode.Rotate)
        {
            l.Rings = new Point[3][];
            for (int i = 0; i < 3; i++) l.Rings[i] = BuildRing(vp, l.PivotWorld, TransformOps.WorldAxes[i], l.HandleWorld, w, h);
        }

        l.Valid = true;
        return l;
    }

    private static Point[] BuildRing(Matrix4x4 vp, Vector3 center, Vector3 normal, double radius, double w, double h)
    {
        // Two in-plane basis vectors orthogonal to the ring normal.
        Vector3 helper = MathF.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        Vector3 u = Vector3.Normalize(Vector3.Cross(normal, helper));
        Vector3 v = Vector3.Cross(normal, u);
        var pts = new Point[RingSegments + 1];
        for (int k = 0; k <= RingSegments; k++)
        {
            float a = (float)(k / (double)RingSegments * Math.PI * 2.0);
            Vector3 wp = center + (u * MathF.Cos(a) + v * MathF.Sin(a)) * (float)radius;
            Project(vp, wp, w, h, out Point p, out _);
            pts[k] = p;
        }
        return pts;
    }

    // Row-vector projection done in DOUBLE precision on purpose: at Mafia's large world coordinates (thousands
    // of units) a float clip-space product wobbles by up to ~1px as the camera moves, making the world-anchored
    // gizmo tremble. Doubling the multiply keeps the projected pivot/handles rock-steady during camera motion.
    private static bool Project(Matrix4x4 m, Vector3 world, double w, double h, out Point screen, out double ndcZ)
    {
        double x = world.X, y = world.Y, z = world.Z;
        double cx = x * m.M11 + y * m.M21 + z * m.M31 + m.M41;
        double cy = x * m.M12 + y * m.M22 + z * m.M32 + m.M42;
        double cz = x * m.M13 + y * m.M23 + z * m.M33 + m.M43;
        double cw = x * m.M14 + y * m.M24 + z * m.M34 + m.M44;
        if (cw <= 1e-4) { screen = default; ndcZ = 0; return false; }
        double inv = 1.0 / cw;
        ndcZ = cz * inv;
        screen = new Point((cx * inv * 0.5 + 0.5) * w, (0.5 - cy * inv * 0.5) * h);
        return true;
    }

    private static Vector3 Unproject(Matrix4x4 vp, double sx, double sy, double ndcZ, double w, double h)
    {
        if (!Matrix4x4.Invert(vp, out Matrix4x4 m)) return Vector3.Zero;
        double ndcX = 2.0 * sx / w - 1.0;
        double ndcY = 1.0 - 2.0 * sy / h;
        double px = ndcX * m.M11 + ndcY * m.M21 + ndcZ * m.M31 + m.M41;
        double py = ndcX * m.M12 + ndcY * m.M22 + ndcZ * m.M32 + m.M42;
        double pz = ndcX * m.M13 + ndcY * m.M23 + ndcZ * m.M33 + m.M43;
        double pw = ndcX * m.M14 + ndcY * m.M24 + ndcZ * m.M34 + m.M44;
        double inv = Math.Abs(pw) > 1e-8 ? 1.0 / pw : 1.0;
        return new Vector3((float)(px * inv), (float)(py * inv), (float)(pz * inv));
    }

    // ── Render ──

    protected override void OnRender(DrawingContext dc)
    {
        if (_host == null || !_host.HasGizmoTarget) return;
        Layout l = BuildLayout();
        if (!l.Valid) return;

        switch (_host.GizmoMode)
        {
            case GizmoMode.Move: DrawTranslate(dc, l); break;
            case GizmoMode.Scale: DrawScale(dc, l); break;
            case GizmoMode.Rotate: DrawRotate(dc, l); break;
        }
    }

    private void DrawTranslate(DrawingContext dc, Layout l)
    {
        for (int i = 0; i < 3; i++)
        {
            if (!l.AxisVisible[i]) continue;
            bool hot = IsHot(HandleKind.MoveAxis, i);
            Pen pen = hot ? HighlightPen : AxisPens[i];
            Brush br = hot ? HighlightBrush : AxisBrushes[i];
            dc.DrawLine(pen, l.Pivot, l.AxisEnd[i]);
            DrawArrowHead(dc, br, l.Pivot, l.AxisEnd[i]);
        }
        DrawCenter(dc, l.Pivot, IsHot(HandleKind.MovePlane, -1));
    }

    private void DrawScale(DrawingContext dc, Layout l)
    {
        for (int i = 0; i < 3; i++)
        {
            if (!l.AxisVisible[i]) continue;
            bool hot = IsHot(HandleKind.ScaleAxis, i);
            Pen pen = hot ? HighlightPen : AxisPens[i];
            Brush br = hot ? HighlightBrush : AxisBrushes[i];
            dc.DrawLine(pen, l.Pivot, l.AxisEnd[i]);
            dc.DrawRectangle(br, null, SquareAt(l.AxisEnd[i], 4.5));
        }
        DrawCenter(dc, l.Pivot, IsHot(HandleKind.ScaleUniform, -1));
    }

    private void DrawRotate(DrawingContext dc, Layout l)
    {
        if (l.Rings == null) return;
        for (int i = 0; i < 3; i++)
        {
            bool hot = IsHot(HandleKind.RotateAxis, i);
            Pen pen = hot ? HighlightPen : AxisPens[i];
            var geo = new StreamGeometry();
            using (StreamGeometryContext ctx = geo.Open())
            {
                ctx.BeginFigure(l.Rings[i][0], false, false);
                ctx.PolyLineTo(l.Rings[i], true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
    }

    private bool IsHot(HandleKind kind, int axis)
    {
        Handle h = _active.IsSome ? _active : _hover;
        return h.Kind == kind && h.Axis == axis;
    }

    private static void DrawArrowHead(DrawingContext dc, Brush brush, Point from, Point to)
    {
        var dir = to - from;
        double len = dir.Length;
        if (len < 1e-3) return;
        dir /= len;
        var perp = new System.Windows.Vector(-dir.Y, dir.X);
        const double s = 9, wdt = 4;
        Point b1 = to - dir * s + perp * wdt;
        Point b2 = to - dir * s - perp * wdt;
        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(to, true, true);
            ctx.LineTo(b1, false, false);
            ctx.LineTo(b2, false, false);
        }
        geo.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private static void DrawCenter(DrawingContext dc, Point p, bool hot)
    {
        Brush br = hot ? HighlightBrush : CenterBrush;
        dc.DrawRectangle(br, CenterPen, SquareAt(p, CenterR * 0.7));
    }

    private static Rect SquareAt(Point p, double half) => new(p.X - half, p.Y - half, half * 2, half * 2);

    // ── Hit-testing (only the handles are hit-testable → empty space passes through to the viewport) ──

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        if (_active.IsSome) return new PointHitTestResult(this, hitTestParameters.HitPoint);
        return HitTest(hitTestParameters.HitPoint).IsSome
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;
    }

    private Handle HitTest(Point p)
    {
        if (_host == null || !_host.HasGizmoTarget) return Handle.None;
        Layout l = BuildLayout();
        if (!l.Valid) return Handle.None;

        switch (_host.GizmoMode)
        {
            case GizmoMode.Move:
                if (Dist(p, l.Pivot) <= CenterR) return new Handle(HandleKind.MovePlane, -1);
                for (int i = 0; i < 3; i++)
                    if (l.AxisVisible[i] && DistToSegment(p, l.Pivot, l.AxisEnd[i]) <= LineHitPx)
                        return new Handle(HandleKind.MoveAxis, i);
                break;
            case GizmoMode.Scale:
                if (Dist(p, l.Pivot) <= CenterR) return new Handle(HandleKind.ScaleUniform, -1);
                for (int i = 0; i < 3; i++)
                    if (l.AxisVisible[i] && DistToSegment(p, l.Pivot, l.AxisEnd[i]) <= LineHitPx)
                        return new Handle(HandleKind.ScaleAxis, i);
                break;
            case GizmoMode.Rotate:
                if (l.Rings != null)
                    for (int i = 0; i < 3; i++)
                        if (DistToPolyline(p, l.Rings[i]) <= LineHitPx)
                            return new Handle(HandleKind.RotateAxis, i);
                break;
        }
        return Handle.None;
    }

    // ── Interaction ──

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_host == null) return;
        Point p = e.GetPosition(this);

        if (_active.IsSome)
        {
            DragTo(p);
            e.Handled = true;
            return;
        }

        Handle h = HitTest(p);
        if (h != _hover) { _hover = h; InvalidateVisual(); }
        Cursor = h.IsSome ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_host == null) return;
        Point p = e.GetPosition(this);
        Handle h = HitTest(p);
        if (!h.IsSome) return;

        BeginDrag(h, p);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_active.IsSome) return;
        _active = Handle.None;
        _host?.GizmoEndDrag();
        ReleaseMouseCapture();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_active.IsSome && _hover.IsSome) { _hover = Handle.None; InvalidateVisual(); }
    }

    // Capture can vanish without a button-up (window deactivation / Alt+Tab). Finalize the drag exactly once so
    // the edit is recorded, the delta HUD hides, and a subsequent bare mouse-move can't keep transforming the
    // object (a normal release runs OnMouseLeftButtonUp first, clearing _active before its ReleaseMouseCapture).
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_active.IsSome) return;
        _active = Handle.None;
        _host?.GizmoEndDrag();
        InvalidateVisual();
    }

    private void BeginDrag(Handle h, Point mouse)
    {
        if (_host == null) return;
        _active = h;
        _host.GizmoBeginDrag();
        _dragPivot = _host.GizmoPivot;
        _dragHandleWorld = Math.Max(1e-4, BuildLayout().HandleWorld);
        (Vector3 o, Vector3 d) = Ray(mouse);

        switch (h.Kind)
        {
            case HandleKind.MoveAxis:
            case HandleKind.ScaleAxis:
                _dragAxis = TransformOps.WorldAxes[h.Axis];
                _dragStartAxisT = ClosestAxisParam(_dragPivot, _dragAxis, o, d);
                break;
            case HandleKind.MovePlane:
                _dragPlaneNormal = ViewDir();
                RayPlane(o, d, _dragPivot, _dragPlaneNormal, out _dragStartHit);
                break;
            case HandleKind.RotateAxis:
                _dragAxis = TransformOps.WorldAxes[h.Axis];
                _dragPlaneNormal = _dragAxis;
                RayPlane(o, d, _dragPivot, _dragPlaneNormal, out Vector3 hit);
                _dragStartVec = hit - _dragPivot;
                break;
            case HandleKind.ScaleUniform:
                Layout l = BuildLayout();
                _dragStartScreenDist = Math.Max(1.0, Dist(mouse, l.Pivot));
                break;
        }
        CaptureMouse();
        InvalidateVisual();
    }

    // Held Shift quantizes a drag to fixed increments (stepped manipulation).
    private const float MoveStep = 1.0f;      // world units
    private const float RotateStepDeg = 15f;  // degrees
    private const float ScaleStep = 0.1f;     // factor

    private void DragTo(Point mouse)
    {
        if (_host == null) return;
        (Vector3 o, Vector3 d) = Ray(mouse);
        bool snap = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (_active.Kind)
        {
            case HandleKind.MoveAxis:
            {
                float t = ClosestAxisParam(_dragPivot, _dragAxis, o, d);
                Vector3 delta = _dragAxis * (t - _dragStartAxisT);
                if (snap) delta = TransformOps.SnapVector(delta, MoveStep);
                _host.GizmoApplyWorldDelta(TransformOps.MoveDelta(delta));
                break;
            }
            case HandleKind.MovePlane:
            {
                if (RayPlane(o, d, _dragPivot, _dragPlaneNormal, out Vector3 hit))
                {
                    Vector3 delta = hit - _dragStartHit;
                    if (snap) delta = TransformOps.SnapVector(delta, MoveStep);
                    _host.GizmoApplyWorldDelta(TransformOps.MoveDelta(delta));
                }
                break;
            }
            case HandleKind.RotateAxis:
            {
                if (RayPlane(o, d, _dragPivot, _dragPlaneNormal, out Vector3 hit))
                {
                    float ang = SignedAngle(_dragStartVec, hit - _dragPivot, _dragAxis);
                    if (snap) ang = TransformOps.SnapAngle(ang, RotateStepDeg);
                    _host.GizmoApplyWorldDelta(TransformOps.RotateDelta(_dragPivot, _dragAxis, ang));
                }
                break;
            }
            case HandleKind.ScaleAxis:
            {
                float t = ClosestAxisParam(_dragPivot, _dragAxis, o, d);
                float f = MathF.Max(0.01f, 1f + (float)((t - _dragStartAxisT) / _dragHandleWorld));
                if (snap) f = TransformOps.SnapScale(f, ScaleStep);
                _host.GizmoApplyWorldDelta(TransformOps.ScaleDelta(_dragPivot, AxisFactor(_active.Axis, f)));
                break;
            }
            case HandleKind.ScaleUniform:
            {
                Layout l = BuildLayout();
                if (!l.Valid) break; // pivot behind the camera mid-drag — a default (0,0) pivot yields garbage
                float f = MathF.Max(0.01f, (float)(Dist(mouse, l.Pivot) / _dragStartScreenDist));
                if (snap) f = TransformOps.SnapScale(f, ScaleStep);
                _host.GizmoApplyWorldDelta(TransformOps.ScaleDelta(_dragPivot, new Vector3(f)));
                break;
            }
        }
        InvalidateVisual();
    }

    private (Vector3 Origin, Vector3 Dir) Ray(Point mouse) =>
        Picking.BuildRay(_host!.GizmoViewProjection, _host.GizmoCameraPosition, mouse.X, mouse.Y, ActualWidth, ActualHeight);

    private Vector3 ViewDir()
    {
        Vector3 v = _dragPivot - _host!.GizmoCameraPosition;
        float len = v.Length();
        return len > 1e-6f ? v / len : Vector3.UnitX;
    }

    private static Vector3 AxisFactor(int axis, float f) =>
        axis == 0 ? new Vector3(f, 1, 1) : axis == 1 ? new Vector3(1, f, 1) : new Vector3(1, 1, f);

    // Parameter t along the axis line (P + t·A, A unit) of the point closest to the ray (O + s·D, D unit).
    private static float ClosestAxisParam(Vector3 p, Vector3 a, Vector3 o, Vector3 d)
    {
        Vector3 r = p - o;
        float b = Vector3.Dot(a, d);
        float c = Vector3.Dot(a, r);
        float f = Vector3.Dot(d, r);
        float denom = 1f - b * b;
        if (denom < 1e-6f) return 0f;   // axis nearly parallel to the view ray
        return (b * f - c) / denom;
    }

    private static bool RayPlane(Vector3 o, Vector3 d, Vector3 planePoint, Vector3 n, out Vector3 hit)
    {
        float dn = Vector3.Dot(d, n);
        if (MathF.Abs(dn) < 1e-6f) { hit = planePoint; return false; }
        float t = Vector3.Dot(planePoint - o, n) / dn;
        hit = o + d * t;
        return t > 0f;
    }

    private static float SignedAngle(Vector3 v1, Vector3 v2, Vector3 axis)
    {
        if (v1.LengthSquared() < 1e-10f || v2.LengthSquared() < 1e-10f) return 0f;
        v1 = Vector3.Normalize(v1);
        v2 = Vector3.Normalize(v2);
        float cos = Math.Clamp(Vector3.Dot(v1, v2), -1f, 1f);
        float ang = MathF.Acos(cos);
        if (Vector3.Dot(Vector3.Cross(v1, v2), axis) < 0f) ang = -ang;
        return ang;
    }

    private static double Dist(Point a, Point b) => (a - b).Length;

    private static double DistToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.X * ab.X + ab.Y * ab.Y;
        if (len2 < 1e-9) return Dist(p, a);
        double t = ((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2;
        t = Math.Clamp(t, 0.0, 1.0);
        var proj = new Point(a.X + ab.X * t, a.Y + ab.Y * t);
        return Dist(p, proj);
    }

    private static double DistToPolyline(Point p, Point[] pts)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < pts.Length; i++)
            best = Math.Min(best, DistToSegment(p, pts[i], pts[i + 1]));
        return best;
    }
}
