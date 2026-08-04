using System.Windows;
using System.Windows.Media;

namespace Illusion.Views;

/// <summary>
/// A drawn icon for a property tab, instead of the Segoe glyph it normally carries in <c>Tag</c>.
///
/// <para>
/// It exists for one reason: a tab that stands for a resource the CONTENT BROWSER also lists must not be a
/// different picture from the tile. The browser draws its kinds from vector geometry (see
/// <see cref="ArchiveIcons"/>), and a font glyph beside it is a second icon for the same thing — which is
/// exactly what a user reads as two different things.
/// </para>
/// <para>
/// Attached rather than a property on the style, because <c>VerticalTab</c> is a plain <c>TabItem</c> and
/// the geometry has to reach its template without giving every tab a subclass.
/// </para>
/// </summary>
public static class TabIcon
{
    /// <summary>The geometry the tab draws. Null (the default) leaves the Segoe glyph in place.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(TabIcon),
        new PropertyMetadata(null, (d, e) => d.SetValue(HasIconProperty, e.NewValue != null)));

    public static Geometry? GetIcon(DependencyObject element) =>
        (Geometry?)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, Geometry? value) =>
        (element ?? throw new ArgumentNullException(nameof(element))).SetValue(IconProperty, value);

    /// <summary>Whether an icon was given — what the template's trigger hides the glyph on. A trigger cannot
    /// test "not null" on its own, so the setter keeps this in step.</summary>
    public static readonly DependencyProperty HasIconProperty = DependencyProperty.RegisterAttached(
        "HasIcon", typeof(bool), typeof(TabIcon), new PropertyMetadata(false));

    public static bool GetHasIcon(DependencyObject element) =>
        (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(HasIconProperty);

    public static void SetHasIcon(DependencyObject element, bool value) =>
        (element ?? throw new ArgumentNullException(nameof(element))).SetValue(HasIconProperty, value);
}

/// <summary>The Prefab tab's picture and colour, taken straight from the content browser's own table so the
/// tab and the tile can never drift into being two different icons for one resource.</summary>
public static class PrefabTabIcon
{
    public static Geometry Geometry { get; } = ArchiveIcons.Icon(Assets.Sds.SdsResourceKind.Prefab);

    public static Brush Tint { get; } = ArchiveIcons.Tint(Assets.Sds.SdsResourceKind.Prefab);
}

/// <summary>The Tuning tab's picture and colour — the entity-data storage's own, for the same reason.</summary>
public static class TuningTabIcon
{
    public static Geometry Geometry { get; } = ArchiveIcons.Icon(Assets.Sds.SdsResourceKind.EntityData);

    public static Brush Tint { get; } = ArchiveIcons.Tint(Assets.Sds.SdsResourceKind.EntityData);
}
