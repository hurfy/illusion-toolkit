using System.Globalization;
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
/// Mid-drag, <see cref="HandleAxisKey"/> takes Blender's axis-lock keys (X/Y/Z, Shift+X/Y/Z) and re-solves the
/// drag against that axis or plane instead of the grabbed handle.
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
    private static readonly Pen[] GuidePens = new Pen[3];   // axis-lock guide lines (thin, dashed)
    private static readonly Brush[] AxisBrushes = new Brush[3];
    private static readonly Pen HighlightPen;
    private static readonly Brush HighlightBrush;
    private static readonly Brush CenterBrush;
    private static readonly Pen CenterPen;
    private static readonly Typeface LabelFace = new("Segoe UI");

    static TransformGizmo()
    {
        for (int i = 0; i < 3; i++)
        {
            AxisPens[i] = Freeze(new Pen(new SolidColorBrush(AxisColors[i]), 2.0)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            AxisBrushes[i] = Freeze(new SolidColorBrush(AxisColors[i]));
            GuidePens[i] = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(
                0xB4, AxisColors[i].R, AxisColors[i].G, AxisColors[i].B)), 1.0)
            { DashStyle = new DashStyle(new double[] { 5, 4 }, 0) });
        }
        HighlightPen = Freeze(new Pen(new SolidColorBrush(HighlightColor), 2.6)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        HighlightBrush = Freeze(new SolidColorBrush(HighlightColor));
        CenterBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xEC, 0xEC, 0xEC)));
        CenterPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)), 1.4));
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private ITransformGizmoHost? _host;

    // RotateView is the modal free rotate: it turns about the axis pointing at the viewer, so the object spins
    // in the screen plane. There is no ring for it — only the keyboard starts it.
    private enum HandleKind { None, MoveAxis, MovePlane, RotateAxis, RotateView, ScaleAxis, ScaleUniform }
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
    private double _dragStartScreenDist; // uniform / axis-locked scale
    private Point _dragStartMouse;      // pointer at drag-start — an axis lock re-solves the drag from here
    private Point _lastDragMouse;       // last pointer seen, so a key press can re-solve without mouse motion

    // Keyboard axis lock (X/Y/Z, Shift+X/Y/Z) for the drag in progress. Cleared when the drag ends.
    private AxisConstraint _constraint = AxisConstraint.None;

    private bool _modal;            // the drag was started from the keyboard, with no button held
    private bool _swallowRelease;   // a modal ended on a button PRESS; its release must not also click the viewport

    public TransformGizmo()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Focusable = false;
        // WPF does not clip an element's drawing to its own bounds. Without this the overlay paints across the
        // whole window — the axis-lock guide lines run far past the pivot by design, and even a plain handle
        // overflows once the pivot sits near an edge. The element fills the viewport cell, so bounds == viewport.
        ClipToBounds = true;
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
        l.PivotWorld = _active.Kind is HandleKind.RotateAxis or HandleKind.RotateView
            or HandleKind.ScaleAxis or HandleKind.ScaleUniform
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

        if (EffectiveMode == GizmoMode.Rotate)
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
        // A modal transform runs under any tool, Select included — so a drag in progress is reason enough to
        // draw even when the tool shelf would show no gizmo at all.
        if (_host == null || (!_host.HasGizmoTarget && !_active.IsSome)) return;
        Layout l = BuildLayout();
        if (!l.Valid) return;

        DrawConstraint(dc, l);  // under the handles, so the guide lines never hide them

        switch (EffectiveMode)
        {
            case GizmoMode.Move: DrawTranslate(dc, l); break;
            case GizmoMode.Scale: DrawScale(dc, l); break;
            case GizmoMode.Rotate: DrawRotate(dc, l); break;
        }
    }

    // The axis lock made visible: a dashed guide line through the pivot along every axis the drag may still act
    // on (one for an axis lock, two for a plane lock), plus a short label naming it.
    private void DrawConstraint(DrawingContext dc, Layout l)
    {
        if (!_constraint.IsSome) return;
        for (int i = 0; i < 3; i++)
        {
            if (!_constraint.Includes(i)) continue;
            DrawGuideHalf(dc, GuidePens[i], l, TransformOps.WorldAxes[i]);
            DrawGuideHalf(dc, GuidePens[i], l, -TransformOps.WorldAxes[i]);
        }

        Brush brush = _constraint.IsPlane ? CenterPen.Brush : AxisBrushes[_constraint.Axis];
        var text = new FormattedText(_constraint.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelFace, 12.0, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(l.Pivot.X + CenterR + 5, l.Pivot.Y + CenterR - 2));
    }

    // One half of a guide line, pivot outwards. A world-long line easily reaches behind the camera (where it
    // cannot be projected), so the length is halved until the far end lands in front of it — and the result is
    // then cut down to the viewport. That trim is not cosmetic: an endpoint that ends up just barely in front of
    // the camera projects millions of pixels away, and handing a dashed line that long to the graphics layer
    // costs whole frames (measured at 17 ms for one overlay repaint) even though almost none of it is visible.
    private void DrawGuideHalf(DrawingContext dc, Pen pen, Layout l, Vector3 dir)
    {
        Matrix4x4 vp = _host!.GizmoViewProjection;
        double w = ActualWidth, h = ActualHeight;
        for (double len = l.HandleWorld * 40; len >= l.HandleWorld; len *= 0.5)
        {
            if (!Project(vp, l.PivotWorld + dir * (float)len, w, h, out Point end, out _)) continue;
            if (ScreenClip.SegmentToRect(l.Pivot, end, w, h, out Point from, out Point to)) dc.DrawLine(pen, from, to);
            return;
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

    // Which handle draws highlighted: normally the hovered (or dragged) one — but an axis lock overrides it, so
    // the axes the drag may still act on light up instead of whichever handle the pointer happened to grab.
    private bool IsHot(HandleKind kind, int axis)
    {
        if (_constraint.IsSome) return axis >= 0 && _constraint.Includes(axis);
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

        // A click is how a modal transform is accepted. Capture is held until the button comes back up, so the
        // release cannot fall through to the viewport and re-pick whatever is under the pointer.
        if (_modal)
        {
            Finish(commit: true, releaseCapture: false);
            _swallowRelease = true;
            e.Handled = true;
            return;
        }

        Point p = e.GetPosition(this);
        Handle h = HitTest(p);
        if (!h.IsSome) return;

        BeginDrag(h, p);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_swallowRelease) { _swallowRelease = false; ReleaseMouseCapture(); e.Handled = true; return; }
        if (!_active.IsSome) return;
        Finish(commit: true, releaseCapture: true);
        e.Handled = true;
    }

    // Right-click abandons a modal transform, the way it does in Blender. Same capture trick as the left button:
    // letting the release through would pop the viewport's context menu on top of the cancel.
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (!_modal) return;
        Finish(commit: false, releaseCapture: false);
        _swallowRelease = true;
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (!_swallowRelease) return;
        _swallowRelease = false;
        ReleaseMouseCapture();
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
        // Keep what the user had built up rather than silently reverting it — a wrong result is one Ctrl+Z away,
        // a lost one is not.
        Finish(commit: true, releaseCapture: false);
    }

    // The single exit from any drag or modal: hand the result to the host (or put it back), then drop the state.
    private void Finish(bool commit, bool releaseCapture)
    {
        _active = Handle.None;
        _constraint = AxisConstraint.None;
        _modal = false;
        if (commit) _host?.GizmoEndDrag(); else _host?.GizmoCancelDrag();
        if (releaseCapture && IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    private void BeginDrag(Handle h, Point mouse)
    {
        if (_host == null) return;
        _active = h;
        _constraint = AxisConstraint.None;   // every drag starts free; the lock is per-drag
        _swallowRelease = false;             // no stale "eat the next button release" from an earlier modal
        _precision.Reset();                  // Ctrl-precision is per-drag too: no offset carried in from the last
        _host.GizmoBeginDrag(ModeOf(h.Kind));
        _dragPivot = _host.GizmoPivot;
        Layout layout = BuildLayout();
        _dragHandleWorld = Math.Max(1e-4, layout.HandleWorld);
        // Kept for every handle, not just the uniform one: an axis lock measures scale by pointer distance to
        // the pivot whatever was grabbed, and re-solves a move/rotate from the pointer position it started at.
        _dragStartScreenDist = Math.Max(1.0, Dist(mouse, layout.Pivot));
        _dragStartMouse = mouse;
        _lastDragMouse = mouse;
        (Vector3 o, Vector3 d) = Ray(mouse);

        switch (h.Kind)
        {
            case HandleKind.MoveAxis:
            case HandleKind.ScaleAxis:
                _dragAxis = TransformOps.WorldAxes[h.Axis];
                _dragStartAxisT = GizmoRayMath.ClosestAxisParam(_dragPivot, _dragAxis, o, d);
                break;
            case HandleKind.MovePlane:
                _dragPlaneNormal = ViewDir();
                GizmoRayMath.RayPlane(o, d, _dragPivot, _dragPlaneNormal, out _dragStartHit);
                break;
            case HandleKind.RotateAxis:
            case HandleKind.RotateView:
                // A ring turns about its own world axis; the modal free rotate turns about the line of sight.
                _dragAxis = h.Kind == HandleKind.RotateView ? ViewDir() : TransformOps.WorldAxes[h.Axis];
                _dragPlaneNormal = _dragAxis;
                GizmoRayMath.RayPlane(o, d, _dragPivot, _dragPlaneNormal, out Vector3 hit);
                _dragStartVec = hit - _dragPivot;
                break;
        }
        CaptureMouse();
        InvalidateVisual();
    }

    // Held Shift quantizes a drag to fixed increments (stepped manipulation).
    private const float MoveStep = 1.0f;      // world units
    private const float RotateStepDeg = 15f;  // degrees
    private const float ScaleStep = 0.1f;     // factor

    // Held Ctrl is the opposite of stepping: the transform follows a tenth of the pointer's movement, so the
    // last little bit can be dialled in without the mouse fighting back. It works by slowing the POINTER the
    // tools are solved against, which is why no tool has to know about it.
    private readonly PrecisionPointer _precision = new();

    private Point SolvePointer(Point raw) =>
        _precision.Solve(raw, (Keyboard.Modifiers & ModifierKeys.Control) != 0);

    /// <summary>True while a keyboard-started transform is running and owns the pointer.</summary>
    public bool IsModalActive => _modal;

    /// <summary>
    /// Starts a modal transform (Blender's <c>G</c> / <c>R</c> / <c>S</c>): the selection follows the pointer with
    /// no button held until it is accepted (left click / Enter) or abandoned (right click / Esc). With no handle
    /// grabbed the tools behave the way Blender's do — move across the screen plane, turn about the line of sight,
    /// resize about the pivot — and the axis-lock keys narrow that down from there. Starting one while another is
    /// running restarts from the ORIGINAL state, so <c>G</c> then <c>R</c> rotates instead of rotating what was
    /// already moved. False when there is nothing to transform or a handle is being dragged with the mouse.
    /// </summary>
    public bool BeginModal(GizmoMode mode, Point pointer)
    {
        if (_host == null || mode == GizmoMode.None || !_host.CanTransformSelection) return false;
        if (_active.IsSome && !_modal) return false;      // a handle drag is under way — leave it alone
        // Everything is measured from where the pointer started, so a pointer resting on the scene tree or the
        // property panel would fling the object the moment it crossed into the viewport. Blender scopes its
        // keymaps to the editor under the mouse for the same reason.
        if (pointer.X < 0 || pointer.Y < 0 || pointer.X > ActualWidth || pointer.Y > ActualHeight) return false;
        if (_modal) Finish(commit: false, releaseCapture: false);

        Handle handle = mode switch
        {
            GizmoMode.Rotate => new Handle(HandleKind.RotateView, -1),
            GizmoMode.Scale => new Handle(HandleKind.ScaleUniform, -1),
            _ => new Handle(HandleKind.MovePlane, -1),
        };
        BeginDrag(handle, pointer);
        _modal = true;   // after BeginDrag: it clears the per-drag state this flag is part of
        return true;
    }

    /// <summary>Ends the modal transform: <paramref name="commit"/> keeps the result, otherwise everything goes
    /// back exactly as it was and nothing is recorded. No-op when none is running.</summary>
    public void EndModal(bool commit)
    {
        if (_modal) Finish(commit, releaseCapture: true);
    }

    /// <summary>
    /// Which keys a running transform answers to. Defaults to Blender's (G/R/S, X/Y/Z, Enter/Space/Esc); an
    /// application that lets the user rebind them assigns a different map.
    /// </summary>
    public GizmoKeyMap Keys { get; set; } = GizmoKeyMap.Default;

    /// <summary>
    /// The keys a running modal transform owns: the commit keys accept it, the cancel key abandons it, the
    /// move/rotate/scale keys switch which transform it is, and anything else falls through to the axis lock.
    /// True when the key was consumed — while one is running that is nearly everything, which is what "modal"
    /// means. Which key is which comes from <see cref="Keys"/>.
    /// </summary>
    public bool HandleModalKey(Key key, ModifierKeys modifiers)
    {
        if (!_modal) return false;
        if ((modifiers & ~ModifierKeys.Shift) != 0) return false;   // Ctrl/Alt shortcuts still belong to the app

        GizmoKeyMap keys = Keys;
        if (key == Key.None) return false;
        if (key == keys.Commit || key == keys.CommitAlt) { EndModal(commit: true); return true; }
        if (key == keys.Cancel) { EndModal(commit: false); return true; }
        if (key == keys.Move) return BeginModal(GizmoMode.Move, _lastDragMouse);
        if (key == keys.Rotate) return BeginModal(GizmoMode.Rotate, _lastDragMouse);
        if (key == keys.Scale) return BeginModal(GizmoMode.Scale, _lastDragMouse);
        return HandleAxisKey(key, modifiers);
    }

    /// <summary>
    /// Blender's axis-lock keys, offered to the gizmo by the window while a drag is in progress (which key is
    /// which comes from <see cref="Keys"/>; by default <c>X</c>/<c>Y</c>/<c>Z</c>): a key pins the drag to that
    /// world axis, <c>Shift</c>+the key pins it to the plane across that axis (excluding it), and the same
    /// combination again releases the lock. The drag re-solves at once, so
    /// the object jumps onto the axis without waiting for the pointer to move. Returns true when the key was
    /// consumed — false leaves it to whatever else the window does with it.
    /// </summary>
    public bool HandleAxisKey(Key key, ModifierKeys modifiers)
    {
        if (_host == null || !_active.IsSome) return false;         // only meaningful inside a drag
        if ((modifiers & ~ModifierKeys.Shift) != 0) return false;   // Ctrl/Alt combinations belong to the app
        int axis = Keys.AxisOf(key);
        if (axis < 0) return false;

        // A rotation happens about ONE axis, so there is no such thing as a plane-locked rotate: swallow the key
        // (it is unmistakably aimed at the drag) but leave the lock alone.
        bool plane = (modifiers & ModifierKeys.Shift) != 0;
        if (plane && _host.GizmoMode == GizmoMode.Rotate) return true;

        _constraint = AxisConstraint.Toggle(_constraint, axis, plane);
        DragTo(_lastDragMouse);
        return true;
    }

    private void DragTo(Point mouse)
    {
        if (_host == null) return;
        _lastDragMouse = mouse;                 // the RAW pointer, so a key press re-solves from the same place
        Point solve = SolvePointer(mouse);      // ...which Ctrl may slow down before anything is solved against it
        (Vector3 o, Vector3 d) = Ray(solve);
        bool snap = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // An axis lock replaces the grabbed handle entirely — the pointer drives the locked axis (or plane)
        // whatever was originally clicked, so Move-centre + X becomes a move along X.
        if (_constraint.IsSome)
        {
            DragConstrained(solve, (o, d), snap);
            InvalidateVisual();
            return;
        }

        switch (_active.Kind)
        {
            case HandleKind.MoveAxis:
            {
                float t = GizmoRayMath.ClosestAxisParam(_dragPivot, _dragAxis, o, d);
                Vector3 delta = _dragAxis * (t - _dragStartAxisT);
                if (snap) delta = TransformOps.SnapVector(delta, MoveStep);
                _host.GizmoApplyWorldDelta(TransformOps.MoveDelta(delta));
                break;
            }
            case HandleKind.MovePlane:
            {
                if (GizmoRayMath.RayPlane(o, d, _dragPivot, _dragPlaneNormal, out Vector3 hit))
                {
                    Vector3 delta = hit - _dragStartHit;
                    if (snap) delta = TransformOps.SnapVector(delta, MoveStep);
                    _host.GizmoApplyWorldDelta(TransformOps.MoveDelta(delta));
                }
                break;
            }
            case HandleKind.RotateAxis:
            case HandleKind.RotateView:
            {
                if (GizmoRayMath.RayPlane(o, d, _dragPivot, _dragPlaneNormal, out Vector3 hit))
                {
                    float ang = GizmoRayMath.SignedAngle(_dragStartVec, hit - _dragPivot, _dragAxis);
                    if (snap) ang = TransformOps.SnapAngle(ang, RotateStepDeg);
                    _host.GizmoApplyWorldDelta(TransformOps.RotateDelta(_dragPivot, _dragAxis, ang));
                }
                break;
            }
            case HandleKind.ScaleAxis:
            {
                float t = GizmoRayMath.ClosestAxisParam(_dragPivot, _dragAxis, o, d);
                float f = MathF.Max(0.01f, 1f + (float)((t - _dragStartAxisT) / _dragHandleWorld));
                if (snap) f = TransformOps.SnapScale(f, ScaleStep);
                _host.GizmoApplyWorldDelta(TransformOps.ScaleDelta(_dragPivot, AxisFactor(_active.Axis, f)));
                break;
            }
            case HandleKind.ScaleUniform:
            {
                Layout l = BuildLayout();
                if (!l.Valid) break; // pivot behind the camera mid-drag — a default (0,0) pivot yields garbage
                float f = MathF.Max(0.01f, (float)(Dist(solve, l.Pivot) / _dragStartScreenDist));
                if (snap) f = TransformOps.SnapScale(f, ScaleStep);
                _host.GizmoApplyWorldDelta(TransformOps.ScaleDelta(_dragPivot, new Vector3(f)));
                break;
            }
        }
        InvalidateVisual();
    }

    // The axis-locked drag. Everything is measured from the pointer position the drag STARTED at (not from the
    // previous frame), so locking mid-drag re-solves the whole drag on the new axis — Blender's behaviour — and
    // toggling the lock on and off cannot accumulate drift. A solve the viewpoint cannot answer (a plane seen
    // edge-on) simply leaves the object where it is.
    private void DragConstrained(Point mouse, (Vector3 Origin, Vector3 Dir) now, bool snap)
    {
        (Vector3 Origin, Vector3 Dir) start = Ray(_dragStartMouse);
        switch (ModeOf(_active.Kind))
        {
            case GizmoMode.Move:
                if (GizmoRayMath.TryConstrainedMove(_dragPivot, _constraint, start, now, out Vector3 delta))
                {
                    if (snap) delta = TransformOps.SnapVector(delta, MoveStep);
                    _host!.GizmoApplyWorldDelta(TransformOps.MoveDelta(delta));
                }
                break;

            case GizmoMode.Rotate:
                if (GizmoRayMath.TryConstrainedRotate(_dragPivot, _constraint.Axis, start, now, out float ang))
                {
                    if (snap) ang = TransformOps.SnapAngle(ang, RotateStepDeg);
                    _host!.GizmoApplyWorldDelta(
                        TransformOps.RotateDelta(_dragPivot, TransformOps.WorldAxes[_constraint.Axis], ang));
                }
                break;

            case GizmoMode.Scale:
            {
                // A locked scale has no single axis line to slide along (a plane lock resizes two axes at once),
                // so its magnitude comes from the pointer's distance to the pivot — the uniform-handle measure —
                // and the lock only decides which axes it lands on.
                Layout l = BuildLayout();
                if (!l.Valid) break;
                float f = MathF.Max(0.01f, (float)(Dist(mouse, l.Pivot) / _dragStartScreenDist));
                if (snap) f = TransformOps.SnapScale(f, ScaleStep);
                _host!.GizmoApplyWorldDelta(TransformOps.ScaleDelta(_dragPivot, _constraint.ScaleFactors(f)));
                break;
            }
        }
    }

    // Which transform a grabbed handle performs — the axis lock replaces the handle but never the tool. This is
    // also what the host is told a drag is (GizmoBeginDrag): the tool shelf cannot answer for a keyboard-started
    // transform, which never touches it.
    private static GizmoMode ModeOf(HandleKind kind) => kind switch
    {
        HandleKind.RotateAxis or HandleKind.RotateView => GizmoMode.Rotate,
        HandleKind.ScaleAxis or HandleKind.ScaleUniform => GizmoMode.Scale,
        _ => GizmoMode.Move,
    };

    // What the overlay is currently doing: the drag in progress if there is one (a modal transform is started
    // from the keyboard under ANY tool, Select included), otherwise whatever the tool shelf has selected.
    private GizmoMode EffectiveMode => _active.IsSome ? ModeOf(_active.Kind) : _host?.GizmoMode ?? GizmoMode.None;

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
