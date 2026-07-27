using System.Windows;
using System.Windows.Controls;

namespace Illusion.Views;

/// <summary>
/// A toolbar group that has a narrower second form to fall back on when the row runs short of room. Only
/// <see cref="ToolbarRowPanel"/> sets this — the group itself never decides.
/// </summary>
internal interface ICompactable
{
    /// <summary>True while the group is showing its narrow form.</summary>
    bool IsCompact { get; set; }
}

/// <summary>
/// The map editor's toolbar row. It lays out exactly three groups, in child order: the tool tray (left), the
/// action bar (centre — Play · Multiplayer · Build) and the shading-mode selector (right).
/// <para>
/// The action bar is centred on the whole row, which is how it reads on a wide window. What a plain Grid
/// cannot express is what has to happen when the window is narrow: sharing one cell (the old layout) draws
/// the groups on top of each other — on a 1280-wide screen the tray's last toggles ended up underneath the
/// Play button — while three columns would keep them apart but lose the row-centred action bar on every
/// window size. Here the action bar is pushed aside instead, and only once the tray has actually reached it.
/// </para>
/// <para>
/// Room is given up in the order it is missed least: first the action bar's slack, then the right-hand group
/// folds into its narrow form if it offers one (<see cref="ICompactable"/>), and only then is the tray
/// squeezed — at which point its <see cref="ToolBar"/> moves the surplus buttons into its own overflow menu
/// rather than clipping them.
/// </para>
/// </summary>
internal sealed class ToolbarRowPanel : Panel
{
    /// <summary>Clear space kept between neighbouring groups, so a pushed-aside action bar never touches them.</summary>
    private const double Gap = 8;

    private UIElement? Group(int index) =>
        index < InternalChildren.Count ? InternalChildren[index] : null;

    private static double Wide(UIElement? e) => e?.DesiredSize.Width ?? 0;

    private static double Tall(UIElement? e) => e?.DesiredSize.Height ?? 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        UIElement? left = Group(0), centre = Group(1), right = Group(2);

        // Everyone is measured unconstrained first: a ToolBar reports only what fits once it is given a width,
        // so its natural width — the one that decides whether the row is short — has to be asked for outright.
        var free = new Size(double.PositiveInfinity, availableSize.Height);
        centre?.Measure(free);
        if (right is ICompactable foldable) foldable.IsCompact = false;
        right?.Measure(free);
        left?.Measure(free);

        // Short row: fold the right-hand group into its narrow form first, so the tools keep their buttons
        // that much longer. The decision is made from natural widths only, so it cannot oscillate.
        double needed = Wide(left) + Wide(centre) + Wide(right) + (2 * Gap);
        if (right is ICompactable narrow && needed > availableSize.Width)
        {
            narrow.IsCompact = true;
            // Its subtree changed under it, but that only reaches the element itself through the layout
            // queue — which does not run mid-pass. Without this, the group would report the width of the
            // form it no longer shows.
            right.InvalidateMeasure();
            right.Measure(free);
        }

        // Whatever is left over is the tray's budget: it — and not one of the small groups — is what gives way.
        double reserved = Wide(centre) + Wide(right) + (2 * Gap);
        if (!double.IsInfinity(availableSize.Width))
        {
            left?.Measure(new Size(Math.Max(0, availableSize.Width - reserved), availableSize.Height));
        }

        double width = Wide(left) + reserved;
        double height = Math.Max(Tall(left), Math.Max(Tall(centre), Tall(right)));
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UIElement? left = Group(0), centre = Group(1), right = Group(2);
        double leftWidth = Wide(left), centreWidth = Wide(centre), rightWidth = Wide(right);

        // Full-height slots: each group's own VerticalAlignment does the rest.
        left?.Arrange(new Rect(0, 0, leftWidth, finalSize.Height));
        right?.Arrange(new Rect(Math.Max(0, finalSize.Width - rightWidth), 0, rightWidth, finalSize.Height));

        // Centred on the row, then clamped between its neighbours — the clamp is what turns an overlap into a
        // shift. The lower bound wins ties, so a row too narrow for all three keeps the action bar clear of the
        // tools (below MinWidth, where nothing can fit, it may reach the shading modes).
        double lowest = leftWidth + Gap;
        double highest = Math.Max(lowest, finalSize.Width - rightWidth - Gap - centreWidth);
        double x = Math.Clamp((finalSize.Width - centreWidth) / 2, lowest, highest);
        centre?.Arrange(new Rect(x, 0, centreWidth, finalSize.Height));

        return finalSize;
    }
}
