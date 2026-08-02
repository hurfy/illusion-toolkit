using System.Windows;
using System.Windows.Controls;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// Names the helper glyph the cursor is over. Sits in the viewport's own Grid (both editor windows), follows
/// the cursor and hides itself the moment the cursor is over nothing — the label exists so the glyphs can
/// stay unlabelled, which is what keeps a car's seventy helper nodes readable.
/// </summary>
public partial class GlyphHoverLabel : UserControl
{
    /// <summary>Gap between the cursor and the label's top-left corner, in device-independent pixels.</summary>
    private const double CursorGap = 16;

    public GlyphHoverLabel()
    {
        InitializeComponent();
    }

    /// <summary>Starts following <paramref name="viewport"/>'s hover. Call once, after the window is built.</summary>
    public void Attach(D3DImageHost viewport)
    {
        viewport.GlyphHoverChanged += (node, pos) => Show(node, pos);
    }

    private void Show(SceneNode? node, Point cursor)
    {
        if (node == null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        NameRun.Text = string.IsNullOrWhiteSpace(node.Name) ? "(unnamed)" : node.Name;
        KindRun.Text = "  " + node.Kind;
        Visibility = Visibility.Visible;

        // Keep the label inside the viewport: past the right or bottom edge it flips to the other side of the
        // cursor rather than being clipped. Measuring here is what makes the flip exact — the text just changed.
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = DesiredSize.Width, height = DesiredSize.Height;
        double hostWidth = (Parent as FrameworkElement)?.ActualWidth ?? double.MaxValue;
        double hostHeight = (Parent as FrameworkElement)?.ActualHeight ?? double.MaxValue;

        double x = cursor.X + CursorGap;
        double y = cursor.Y + CursorGap;
        if (x + width > hostWidth) x = Math.Max(0, cursor.X - CursorGap - width);
        if (y + height > hostHeight) y = Math.Max(0, cursor.Y - CursorGap - height);
        Margin = new Thickness(x, y, 0, 0);
    }
}
