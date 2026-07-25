using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Rendering.Gizmos;

namespace Illusion.Rendering.Controls;

/// <summary>
/// Blender-style navigation gizmo overlaid at the top-right of the viewport: three colored axis
/// balls (X/Y/Z with negative counterparts) that track the camera orientation. Clicking a ball
/// snaps the camera to that axis view; dragging inside the gizmo orbits the camera.
/// </summary>
public sealed class ViewportGizmo : FrameworkElement
{
    // World axes: positive ends are filled + labeled, negative ends are hollow rings (label on hover).
    private static readonly (Vector3 Dir, int Color, bool Positive, string Label)[] Axes =
    {
        (new Vector3(1f, 0f, 0f), 0, true, "X"),
        (new Vector3(0f, 1f, 0f), 1, true, "Y"),
        (new Vector3(0f, 0f, 1f), 2, true, "Z"),
        (new Vector3(-1f, 0f, 0f), 0, false, "X"),
        (new Vector3(0f, -1f, 0f), 1, false, "Y"),
        (new Vector3(0f, 0f, -1f), 2, false, "Z"),
    };

    private static readonly Color[] AxisColors =
    {
        Color.FromRgb(0xE6, 0x46, 0x46), // X — red
        Color.FromRgb(0x82, 0xC8, 0x3C), // Y — green
        Color.FromRgb(0x46, 0x82, 0xDC), // Z — blue
    };

    private const double BallRadius = 9.0;
    private const double HitRadius = 12.0;
    private const double LabelSize = 10.5;
    private const double DragThreshold = 3.0;
    private const float OrbitSensitivity = 0.01f;

    private static readonly SolidColorBrush[] Fills = new SolidColorBrush[3];
    private static readonly SolidColorBrush[] DimFills = new SolidColorBrush[3];
    private static readonly Pen[] Rings = new Pen[3];
    private static readonly Pen[] Arms = new Pen[3];
    private static readonly SolidColorBrush HoverBg;
    private static readonly SolidColorBrush LabelBrush;
    private static readonly Pen BallOutline;
    private static readonly Pen HoverRing;
    private static readonly Typeface LabelTypeface;

    static ViewportGizmo()
    {
        for (int i = 0; i < 3; i++)
        {
            Color c = AxisColors[i];
            Fills[i] = Freeze(new SolidColorBrush(c));
            DimFills[i] = Freeze(new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B)));
            Rings[i] = Freeze(new Pen(new SolidColorBrush(c), 1.6));
            Arms[i] = Freeze(new Pen(new SolidColorBrush(c), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        }
        HoverBg = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
        LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)));
        BallOutline = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)), 1.0));
        HoverRing = Freeze(new Pen(new SolidColorBrush(Colors.White), 1.6));
        LabelTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private IGizmoTarget? _viewport;
    private Point _center;

    private int _hover = -1;
    private int _pressIndex = -1;
    private bool _pressed;
    private bool _dragging;
    private Point _pressPoint;
    private Point _lastOrbit;

    public ViewportGizmo()
    {
        Width = 92;
        Height = 92;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 14, 14, 0);
        Focusable = false;
    }

    /// <summary>Bind the gizmo to a camera target; it redraws whenever the camera moves.</summary>
    public void Attach(IGizmoTarget viewport)
    {
        if (_viewport != null) return;
        _viewport = viewport;
        viewport.CameraMoved += InvalidateVisual;
    }

    private struct Ball
    {
        public Point Pos;
        public double Depth; // view-space Z: larger = nearer the viewer (drawn on top)
        public int Index;
    }

    // Projects each world axis into gizmo screen space through the camera view rotation.
    private Ball[] Project()
    {
        double half = Math.Min(ActualWidth, ActualHeight) / 2.0;
        _center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);
        double arm = half - BallRadius - 3.0;
        Matrix4x4 view = _viewport?.CameraView ?? Matrix4x4.Identity;

        var balls = new Ball[Axes.Length];
        for (int i = 0; i < Axes.Length; i++)
        {
            Vector3 v = Vector3.TransformNormal(Axes[i].Dir, view);
            balls[i] = new Ball
            {
                Pos = new Point(_center.X + v.X * arm, _center.Y - v.Y * arm),
                Depth = v.Z,
                Index = i,
            };
        }
        return balls;
    }

    private int HitTest(Point p)
    {
        Ball[] balls = Project();
        int best = -1;
        double bestDepth = double.NegativeInfinity;
        for (int i = 0; i < balls.Length; i++)
        {
            double dx = p.X - balls[i].Pos.X, dy = p.Y - balls[i].Pos.Y;
            if (dx * dx + dy * dy <= HitRadius * HitRadius && balls[i].Depth > bestDepth)
            {
                best = i;
                bestDepth = balls[i].Depth;
            }
        }
        return best;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        Ball[] balls = Project();

        // Transparent disc makes the circular area hit-testable (orbit-drag anywhere inside) while the
        // square corners stay pass-through so right-button camera look still works around the gizmo.
        Brush bg = (_hover >= 0 || _dragging) ? HoverBg : Brushes.Transparent;
        dc.DrawEllipse(bg, null, _center, ActualWidth / 2.0, ActualHeight / 2.0);

        Array.Sort(balls, static (a, b) => a.Depth.CompareTo(b.Depth)); // far first
        foreach (Ball ball in balls) DrawAxis(dc, ball);
    }

    private void DrawAxis(DrawingContext dc, Ball ball)
    {
        (Vector3 _, int color, bool positive, string label) = Axes[ball.Index];
        bool hovered = ball.Index == _hover;
        double alpha = hovered ? 1.0 : 0.4 + 0.3 * (ball.Depth + 1.0); // fade the far-facing hemisphere

        dc.PushOpacity(alpha);

        if (positive) dc.DrawLine(Arms[color], _center, ball.Pos);

        if (positive || hovered)
        {
            dc.DrawEllipse(Fills[color], BallOutline, ball.Pos, BallRadius, BallRadius);
            DrawLabel(dc, ball.Pos, label);
        }
        else
        {
            dc.DrawEllipse(DimFills[color], Rings[color], ball.Pos, BallRadius - 0.8, BallRadius - 0.8);
        }

        if (hovered) dc.DrawEllipse(null, HoverRing, ball.Pos, BallRadius + 2.0, BallRadius + 2.0);

        dc.Pop();
    }

    // The axis labels are a fixed, tiny set (X/Y/Z); building a FormattedText is expensive (full
    // text shaping + glyph run) and OnRender ran three per frame. Cache them, keyed by the DPI they
    // were shaped at, and rebuild only when the DPI changes.
    private readonly Dictionary<string, FormattedText> _labelCache = new();
    private double _labelDpi = -1;

    private void DrawLabel(DrawingContext dc, Point p, string text)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi != _labelDpi)
        {
            _labelCache.Clear();
            _labelDpi = dpi;
        }
        if (!_labelCache.TryGetValue(text, out FormattedText? ft))
        {
            ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelTypeface, LabelSize, LabelBrush, dpi);
            _labelCache[text] = ft;
        }
        dc.DrawText(ft, new Point(p.X - ft.Width / 2.0, p.Y - ft.Height / 2.0));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point pos = e.GetPosition(this);

        if (_pressed)
        {
            if (!_dragging && (pos - _pressPoint).Length > DragThreshold)
            {
                _dragging = true;
                _lastOrbit = pos;
                InvalidateVisual();
            }
            if (_dragging)
            {
                System.Windows.Vector d = pos - _lastOrbit;
                _lastOrbit = pos;
                // Mirror the right-button look feel of the hosting viewport's mouse-look.
                _viewport?.OrbitCamera((float)(-d.X * OrbitSensitivity), (float)(-d.Y * OrbitSensitivity));
            }
            return;
        }

        int idx = HitTest(pos);
        if (idx != _hover) { _hover = idx; InvalidateVisual(); }
        Cursor = idx >= 0 ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressPoint = e.GetPosition(this);
        _pressIndex = HitTest(_pressPoint);
        _pressed = true;
        _dragging = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed) return;
        _pressed = false;
        ReleaseMouseCapture();

        if (!_dragging && _pressIndex >= 0 && HitTest(e.GetPosition(this)) == _pressIndex)
            _viewport?.SnapCameraToAxis(Axes[_pressIndex].Dir);

        _dragging = false;
        _pressIndex = -1;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_pressed && _hover != -1) { _hover = -1; InvalidateVisual(); }
    }

    // Capture can vanish without a button-up (window deactivation / Alt+Tab) — drop the press state so a
    // later bare mouse-move cannot orbit the camera against a stale anchor. Mirrors TransformGizmo.
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_pressed && !_dragging) return;
        _pressed = false;
        _dragging = false;
        _pressIndex = -1;
        InvalidateVisual();
    }
}
