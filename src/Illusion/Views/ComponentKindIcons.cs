using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Illusion.Assets.Cars;

namespace Illusion.Views;

/// <summary>
/// One icon and one colour per part kind, for the component tree's rows.
///
/// <para>
/// A component's KIND is the thing the tree has to show that its name cannot: a cover names <c>doorBL</c> on
/// seven shipped cars, so reading the kind off the name misleads on exactly the cars a modder opens the tool
/// for. The word is on the row; this is the cue that reads before the word does.
/// </para>
/// <para>
/// The geometry lives in code rather than in a resource dictionary for the same reason
/// <see cref="ResourceTypeIcons"/> does: this is a LOOKUP over a dozen kinds, and the XAML spelling of a
/// lookup is a DataTrigger per kind in every template that draws one. The paths are on the same 12x12 grid as
/// the scene tree's icons and stroked rather than filled, so the stroke width belongs to the usage.
/// </para>
/// </summary>
public static class ComponentKindIcons
{
    /// <summary>What a component with no deform part is called — the kind the aggregate mints for a bone
    /// that carries geometry and that no part claims.</summary>
    public const string Bare = "bare";

    // Each path says what the part IS at a glance: a shell for the body, a leaf on its hinge for a door, a
    // pane for a window, a rim for a wheel, a bar for a bumper. The kinds with nothing to draw — "normal",
    // and the numeric ones no reading has named — fall back to the plain part plate.
    private static readonly Dictionary<string, Geometry> Glyphs = new(StringComparer.Ordinal)
    {
        // The shell of the car in profile: roof, screen, bonnet, sill.
        ["body"] = Parse("M1.4,8.4 L1.4,7 L3,6.2 L4.6,3.9 L7.9,3.9 L9.3,6.2 L10.6,7 L10.6,8.4 Z M3,6.2 L9.3,6.2"),
        // A leaf hung on its hinge: the hinge stile is the doubled edge on the left, the handle the notch.
        ["door"] = Parse("M2.4,2.2 L9.6,2.2 L9.6,9.8 L2.4,9.8 Z M3.9,2.2 L3.9,9.8 M8.3,6 L6.7,6"),
        // A pane, with the corner reflection glass is always drawn with.
        ["window"] = Parse("M2.2,2.6 L9.8,2.6 L9.8,9.4 L2.2,9.4 Z M2.2,7.4 L5.4,4.2"),
        // A lid on its hinge, seen from the side as it lifts.
        ["lid"] = Parse("M1.8,8.6 L10.2,8.6 M2.6,8.6 L4.6,3.6 L9.4,3.6 L10.2,8.6"),
        // A panel that covers something: a plate with the seam that says it comes off.
        ["cover"] = Parse("M2.2,3 L9.8,3 L9.8,9 L2.2,9 Z M2.2,6 L9.8,6"),
        // A bar across the front with its two mounting stalks.
        ["bumper"] = Parse("M1.3,5.6 L10.7,5.6 L10.7,7.4 L1.3,7.4 Z M3.6,7.4 L3.6,9.2 M8.4,7.4 L8.4,9.2"),
        // A rim: the hub inside the tyre.
        ["wheel"] = Parse("M2,6 A4,4 0 1 1 10,6 A4,4 0 1 1 2,6 Z M4.6,6 A1.4,1.4 0 1 1 7.4,6 A1.4,1.4 0 1 1 4.6,6 Z"),
        // The tyre alone — the same circle without a hub, with the tread marks that tell them apart.
        ["tyre"] = Parse("M2,6 A4,4 0 1 1 10,6 A4,4 0 1 1 2,6 Z M6,2 L6,3.4 M6,8.6 L6,10 M2,6 L3.4,6 M8.6,6 L10,6"),
        // A block with its shaft — what an engine is from the side.
        ["motor"] = Parse("M2.4,4.2 L8,4.2 L8,8.4 L2.4,8.4 Z M8,5.6 L9.8,5.6 L9.8,7 L8,7 M4,4.2 L4,2.9 L6.4,2.9 L6.4,4.2"),
        // A pipe with its plume.
        ["exhaust"] = Parse("M1.6,7.4 L7,7.4 L7,9 L1.6,9 Z M7.8,8.2 C9,8.2 9,6.6 10.2,6.6 M8.4,5.4 C9.4,5.4 9.4,4 10.4,4"),
        // A blade at its working angle, with the edge it pushes with.
        ["plow"] = Parse("M2.2,3 C2.2,7 4.4,9 9.8,9.4 M2.2,3 L4.6,2.6 C4.6,6.6 6.6,8.4 10.2,8.8 L9.8,9.4"),
        // The one kind that is weather rather than a panel: a flake.
        ["snow"] = Parse("M6,1.8 L6,10.2 M2.4,3.9 L9.6,8.1 M9.6,3.9 L2.4,8.1"),
        // No deform part claims this bone — a licence plate, a light, a wiper. A plate with a dashed edge:
        // it is a real, shootable part of the car that simply has no row anywhere in the prefab.
        [Bare] = Parse("M2.4,3.4 L4.4,3.4 M7.6,3.4 L9.6,3.4 M9.6,3.4 L9.6,5.4 M9.6,7.6 L9.6,8.6 "
            + "L7.6,8.6 M4.4,8.6 L2.4,8.6 M2.4,8.6 L2.4,6.6 M2.4,4.4 L2.4,3.4"),
    };

    /// <summary>The plate a kind with no drawing of its own gets: a part, and nothing claimed about it.</summary>
    private static readonly Geometry Plate = Parse("M2.2,3 L9.8,3 L9.8,9 L2.2,9 Z");

    // Warm for the panels a modder actually opens and closes, cool for the glass, grey for the running gear,
    // and one red for a component whose bone no longer resolves — the only row here that is a fault.
    private static readonly Dictionary<string, Brush> Tints = new(StringComparer.Ordinal)
    {
        ["body"] = PaletteInk.Mint,
        ["door"] = PaletteInk.Amber,
        ["lid"] = PaletteInk.Gold,
        ["cover"] = PaletteInk.Tan,
        ["bumper"] = PaletteInk.Denim,
        ["window"] = PaletteInk.Cyan,
        ["wheel"] = PaletteInk.Steel,
        ["tyre"] = PaletteInk.Pewter,
        ["motor"] = PaletteInk.Coral,
        ["exhaust"] = PaletteInk.Peach,
        ["plow"] = PaletteInk.Violet,
        ["snow"] = PaletteInk.Ice,
        [Bare] = PaletteInk.Slate,
    };

    // A marker is not a panel of the car, it is something the car hangs off a bone — so it gets its own set
    // of glyphs on the same 12x12 grid. Each says what the thing IS: a chair, a step, a can, a pipe, a blade,
    // a lamp.
    private static readonly Dictionary<CarMarkerRole, Geometry> MarkerGlyphs = new()
    {
        // A chair in profile: back, seat, front leg.
        [CarMarkerRole.Seat] = Parse("M3.6,2.6 L3.6,7.4 L9.8,7.4 M3.6,9.6 L3.6,7.4 M9.8,7.4 L9.8,9.6"),
        // Two steps — the shape of the thing a player climbs.
        [CarMarkerRole.ClimbBox] = Parse("M1.6,9.6 L1.6,6.6 L6,6.6 L6,3.4 L10.4,3.4 L10.4,9.6 Z"),
        // A can with its handle and its cap.
        [CarMarkerRole.FuelTank] = Parse("M3,3.6 L9,3.6 L9,9.6 L3,9.6 Z M4.6,3.6 L4.6,2.4 L7.4,2.4 L7.4,3.6"),
        // A pipe with its plume — the same reading the exhaust PART gets, because it is the same thing.
        [CarMarkerRole.ExhaustEmitter] =
            Parse("M1.6,7.4 L7,7.4 L7,9 L1.6,9 Z M7.8,8.2 C9,8.2 9,6.6 10.2,6.6 M8.4,5.4 C9.4,5.4 9.4,4 10.4,4"),
        // An arm with its blade, on its pivot.
        [CarMarkerRole.Wiper] = Parse("M2.6,9.4 L9,3 M7.8,2.2 L9.8,4.2 M2.6,9.4 L4.4,9.4"),
        // A lamp throwing light.
        [CarMarkerRole.Light] =
            Parse("M3.8,6 A2.2,2.2 0 1 1 8.2,6 A2.2,2.2 0 1 1 3.8,6 Z M6,1.6 L6,2.8 M6,9.2 L6,10.4 "
                + "M1.6,6 L2.8,6 M9.2,6 L10.4,6"),
    };

    private static readonly Dictionary<CarMarkerRole, Brush> MarkerTints = new()
    {
        [CarMarkerRole.Seat] = PaletteInk.Periwinkle,
        [CarMarkerRole.ClimbBox] = PaletteInk.Sage,
        [CarMarkerRole.FuelTank] = PaletteInk.Brass,
        [CarMarkerRole.ExhaustEmitter] = PaletteInk.Peach,
        [CarMarkerRole.Wiper] = PaletteInk.Cornflower,
        [CarMarkerRole.Light] = PaletteInk.Gold,
    };

    /// <summary>The icon for a part kind, in the words the format's reader gives it.</summary>
    public static Geometry Glyph(string? kind) =>
        kind != null && Glyphs.TryGetValue(kind, out Geometry? found) ? found : Plate;

    /// <summary>The icon for a marker role.</summary>
    public static Geometry MarkerGlyph(CarMarkerRole role) =>
        MarkerGlyphs.TryGetValue(role, out Geometry? found) ? found : Plate;

    /// <summary>The colour for a marker role.</summary>
    public static Brush MarkerTint(CarMarkerRole role) =>
        MarkerTints.TryGetValue(role, out Brush? found) ? found : PaletteInk.Ash;

    /// <summary>The colour for a part kind. Unnamed kinds — the numeric ones — share the neutral grey.</summary>
    public static Brush Tint(string? kind) =>
        kind != null && Tints.TryGetValue(kind, out Brush? found) ? found : PaletteInk.Ash;

    private static Geometry Parse(string data)
    {
        Geometry geometry = Geometry.Parse(data);
        geometry.Freeze();      // shared by every row — frozen so WPF may reuse it across threads
        return geometry;
    }
}

/// <summary>Part kind to its icon, for the component tree's rows.</summary>
public sealed class ComponentGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ComponentKindIcons.Glyph(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Part kind to its colour, for the stroke of the row's icon.</summary>
public sealed class ComponentTintConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ComponentKindIcons.Tint(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Marker role to its icon, for the rows under a component.</summary>
public sealed class MarkerGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ComponentKindIcons.MarkerGlyph(value is CarMarkerRole role ? role : CarMarkerRole.Seat);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Marker role to its colour.</summary>
public sealed class MarkerTintConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ComponentKindIcons.MarkerTint(value is CarMarkerRole role ? role : CarMarkerRole.Seat);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
