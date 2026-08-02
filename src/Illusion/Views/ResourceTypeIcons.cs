using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Illusion.Assets.Library;

namespace Illusion.Views;

/// <summary>
/// One icon and one colour per <see cref="LibraryResourceKind"/> — what the content browser draws a folder
/// row and an archive tile with.
/// <para>
/// The geometry lives in code rather than in a resource dictionary because this is a LOOKUP: two dozen kinds
/// picked by an enum is a table, and the XAML spelling of it is two dozen DataTriggers in every template that
/// shows an icon. The paths are drawn on the same 12x12 grid as the scene tree's icons
/// (<c>Views\EditorChrome.xaml</c>) and stroked rather than filled, so one geometry serves both a 13px tree
/// row and a 36px tile — the stroke width is set at the usage, not baked into the path.
/// </para>
/// </summary>
public static class ResourceTypeIcons
{
    /// <summary>The plain folder shape, for a row that is a container rather than a resource. Tiles use it so
    /// "walks into it" and "goes on the stage" never look alike — the two do very different things on a
    /// double click. It is the scene tree's <c>IconFolder</c>, drawn here so the browser stands on its
    /// own.</summary>
    public static Geometry Folder { get; } =
        Parse("M1.5,9.3 L1.5,4.2 L4.4,4.2 L5.6,5.5 L10.5,5.5 L10.5,9.3 Z");

    // Each path says what the content IS, so a tile reads before its label does: a car in profile, a bust, a
    // fedora for the one character you play, a police star, a skyline, a film strip, a beamed pair of notes.
    private static readonly Dictionary<LibraryResourceKind, Geometry> Glyphs = new()
    {
        // An archive crate — the fallback for a folder this build has never heard of.
        [LibraryResourceKind.Unknown] = Parse(
            "M2,3.3 L10,3.3 L10,9.6 L2,9.6 Z M2,5.4 L10,5.4 M5.4,7.4 L6.6,7.4"),
        // A sedan in profile, drawn the way a car actually is: bumper, boot, C-pillar, roof, windscreen,
        // bonnet, bumper, with the beltline under the glass. The sill is BROKEN around the wheels and the
        // wheels sit on it, so each gets an arch instead of a circle pasted over the body — the shape only
        // reads as a car when the wheels are part of it.
        [LibraryResourceKind.Car] = Parse(
            "M0.9,7.9 L0.9,6.4 L2.3,5.6 L4.0,3.6 L7.4,3.6 L8.9,5.6 L11.1,6.4 L11.1,7.9 " +
            "M2.3,5.6 L8.9,5.6 " +
            "M0.9,7.9 L2.05,7.9 M4.55,7.9 L7.45,7.9 M9.95,7.9 L11.1,7.9 " +
            "M2.05,8.4 A1.25,1.25 0 1 0 4.55,8.4 A1.25,1.25 0 1 0 2.05,8.4 Z " +
            "M7.45,8.4 A1.25,1.25 0 1 0 9.95,8.4 A1.25,1.25 0 1 0 7.45,8.4 Z"),
        [LibraryResourceKind.Character] = Parse(
            "M4.15,3.3 A1.85,1.85 0 1 1 7.85,3.3 A1.85,1.85 0 1 1 4.15,3.3 Z " +
            "M2.3,10.3 C2.3,7.8 4.0,6.8 6.0,6.8 C8.0,6.8 9.7,7.8 9.7,10.3"),
        // A fedora: the player is the one character the game gives a face, and a second bust would not say so.
        [LibraryResourceKind.Player] = Parse(
            "M1.3,8.8 C1.3,7.6 10.7,7.6 10.7,8.8 C10.7,10.0 1.3,10.0 1.3,8.8 Z " +
            "M3.5,8.2 L3.9,4.5 C4.6,3.2 7.4,3.2 8.1,4.5 L8.5,8.2 M3.7,6.6 L8.3,6.6"),
        [LibraryResourceKind.Police] = Parse(
            "M6,1.7 L7.15,4.72 L10.38,4.88 L7.86,6.9 L8.7,10.02 L6,8.25 " +
            "L3.3,10.02 L4.15,6.9 L1.63,4.88 L4.85,4.72 Z"),
        [LibraryResourceKind.Wardrobe] = Parse(
            "M4.4,2.3 L2.0,3.7 L3.1,5.9 L4.1,5.4 L4.1,9.9 L7.9,9.9 L7.9,5.4 L8.9,5.9 L10.0,3.7 L7.6,2.3 " +
            "C7.2,3.6 4.8,3.6 4.4,2.3 Z"),
        // Two of them: sds\traffic is the street POPULATION, not the vehicles (see LibraryCatalog).
        [LibraryResourceKind.Traffic] = Parse(
            "M2.8,3.6 A1.5,1.5 0 1 1 5.8,3.6 A1.5,1.5 0 1 1 2.8,3.6 Z " +
            "M1.8,10.2 C1.8,7.9 3.0,7.0 4.3,7.0 C5.6,7.0 6.8,7.9 6.8,10.2 " +
            "M7.45,4.6 A1.25,1.25 0 1 1 9.95,4.6 A1.25,1.25 0 1 1 7.45,4.6 Z " +
            "M6.9,10.2 C6.9,8.4 7.7,7.7 8.7,7.7 C9.7,7.7 10.5,8.4 10.5,10.2"),
        [LibraryResourceKind.District] = Parse(
            "M1.0,10.5 L11.0,10.5 M1.6,10.5 L1.6,5.7 L4.2,5.7 L4.2,10.5 " +
            "M4.7,10.5 L4.7,2.5 L7.4,2.5 L7.4,10.5 M7.9,10.5 L7.9,6.9 L10.4,6.9 L10.4,10.5"),
        [LibraryResourceKind.CityCrash] = Parse(
            "M2.2,3.9 L6,2.0 L9.8,3.9 L9.8,8.1 L6,10.0 L2.2,8.1 Z M2.2,3.9 L6,5.8 L9.8,3.9 M6,5.8 L6,10.0"),
        [LibraryResourceKind.Terrain] = Parse(
            "M1.3,8.9 L4.3,5.0 L6.5,7.6 L8.3,5.4 L10.7,8.9 Z M0.9,10.5 L11.1,10.5"),
        [LibraryResourceKind.Sky] = Parse(
            "M3.5,9.3 C2.1,9.3 1.5,7.4 2.6,6.5 C2.7,4.4 5.6,3.6 7.0,5.1 " +
            "C8.6,4.6 10.1,5.9 9.9,7.4 C10.7,8.0 10.3,9.3 9.2,9.3 Z"),
        [LibraryResourceKind.Interface] = Parse(
            "M1.4,2.7 L10.6,2.7 L10.6,9.3 L1.4,9.3 Z M1.4,4.7 L10.6,4.7 M4.4,4.7 L4.4,9.3"),
        [LibraryResourceKind.Video] = Parse(
            "M1.5,2.8 L10.5,2.8 L10.5,9.2 L1.5,9.2 Z M3.5,2.8 L3.5,9.2 M8.5,2.8 L8.5,9.2 " +
            "M1.5,4.9 L3.5,4.9 M1.5,7.1 L3.5,7.1 M8.5,4.9 L10.5,4.9 M8.5,7.1 L10.5,7.1"),
        [LibraryResourceKind.Sound] = Parse(
            "M1.5,4.7 L3.3,4.7 L5.7,2.5 L5.7,9.5 L3.3,7.3 L1.5,7.3 Z " +
            "M7.4,4.4 C8.5,5.4 8.5,6.6 7.4,7.6 M9.1,2.9 C11.1,5.0 11.1,7.0 9.1,9.1"),
        [LibraryResourceKind.Music] = Parse(
            "M2.3,8.7 A1.25,1.15 0 1 0 4.8,8.7 A1.25,1.15 0 1 0 2.3,8.7 Z " +
            "M7.7,7.6 A1.25,1.15 0 1 0 10.2,7.6 A1.25,1.15 0 1 0 7.7,7.6 Z " +
            "M4.8,8.7 L4.8,3.0 L10.2,2.0 L10.2,7.6 M4.8,4.9 L10.2,3.9"),
        [LibraryResourceKind.Speech] = Parse(
            "M1.5,2.7 L10.5,2.7 L10.5,8.1 L5.5,8.1 L3.3,10.4 L3.3,8.1 L1.5,8.1 Z"),
        // Keyframes on a track, not a running figure: the tool shelf already spends a walking man on its
        // walk mode, and two figures for two different things is how an icon set stops meaning anything.
        [LibraryResourceKind.Animation] = Parse(
            "M0.9,6.0 L11.1,6.0 M3.2,3.8 L5.0,6.0 L3.2,8.2 L1.4,6.0 Z M8.8,3.8 L10.6,6.0 L8.8,8.2 L7.0,6.0 Z"),
        [LibraryResourceKind.Script] = Parse(
            "M2.7,1.8 L7.3,1.8 L9.3,3.9 L9.3,10.2 L2.7,10.2 Z M7.3,1.8 L7.3,3.9 L9.3,3.9 " +
            "M4.8,6.1 L3.8,7.4 L4.8,8.7 M7.2,6.1 L8.2,7.4 L7.2,8.7"),
        [LibraryResourceKind.Mission] = Parse(
            "M3.0,10.6 L3.0,1.6 M3.0,2.2 C5.2,1.3 7.4,3.4 9.6,2.5 L9.6,6.3 C7.4,7.2 5.2,5.1 3.0,6.0 Z"),
        [LibraryResourceKind.Particle] = Parse(
            "M5.2,2.3 L6.1,5.2 L9.0,6.1 L6.1,7.0 L5.2,9.9 L4.3,7.0 L1.4,6.1 L4.3,5.2 Z " +
            "M9.5,1.7 L9.9,2.9 L11.1,3.3 L9.9,3.7 L9.5,4.9 L9.1,3.7 L7.9,3.3 L9.1,2.9 Z"),
        [LibraryResourceKind.Shop] = Parse(
            "M1.6,4.6 L10.4,4.6 L10.4,10.2 L1.6,10.2 Z M1.6,4.6 L2.9,2.2 L9.1,2.2 L10.4,4.6 " +
            "M4.7,10.2 L4.7,7.0 L7.3,7.0 L7.3,10.2"),
        [LibraryResourceKind.Table] = Parse(
            "M1.5,2.7 L10.5,2.7 L10.5,9.3 L1.5,9.3 Z M1.5,4.9 L10.5,4.9 M1.5,7.1 L10.5,7.1 " +
            "M4.5,2.7 L4.5,9.3 M7.5,2.7 L7.5,9.3"),
        [LibraryResourceKind.Text] = Parse(
            "M1.6,2.9 L10.4,2.9 M1.6,5.3 L10.4,5.3 M1.6,7.7 L8.6,7.7 M1.6,10.1 L6.2,10.1"),
        [LibraryResourceKind.Weapon] = Parse(
            "M1.5,3.6 L10.5,3.6 L10.5,5.6 L6.1,5.6 L4.7,10.4 L2.7,10.4 L3.5,5.6 L1.5,5.6 Z " +
            "M5.0,5.6 C5.7,7.3 7.3,7.3 7.7,5.6"),
        [LibraryResourceKind.Cloth] = Parse(
            "M1.4,3.4 C3.2,1.9 4.8,4.6 6.6,3.4 C8.4,2.2 9.4,3.6 10.6,3.2 " +
            "M1.4,6.2 C3.2,4.7 4.8,7.4 6.6,6.2 C8.4,5.0 9.4,6.4 10.6,6.0 " +
            "M1.4,9.0 C3.2,7.5 4.8,10.2 6.6,9.0 C8.4,7.8 9.4,9.2 10.6,8.8"),
        [LibraryResourceKind.Generated] = Parse(
            "M6,1.4 L6,3.2 M6,8.8 L6,10.6 M1.4,6 L3.2,6 M8.8,6 L10.6,6 " +
            "M2.75,2.75 L4.0,4.0 M8.0,8.0 L9.25,9.25 M9.25,2.75 L8.0,4.0 M4.0,8.0 L2.75,9.25 " +
            "M3.9,6 A2.1,2.1 0 1 1 8.1,6 A2.1,2.1 0 1 1 3.9,6 Z"),
        [LibraryResourceKind.Map] = Parse(
            "M1.5,3.2 L4.5,1.9 L7.5,3.5 L10.5,2.2 L10.5,8.8 L7.5,10.1 L4.5,8.5 L1.5,9.8 Z " +
            "M4.5,1.9 L4.5,8.5 M7.5,3.5 L7.5,10.1"),
    };

    // The colour is the SECOND cue, after the shape: it is what makes a wall of a hundred car tiles read as
    // one kind of thing at a glance. Neighbouring kinds are kept in different hue families on purpose —
    // people are warm, places are blue, sound is green, data is grey.
    private static readonly Dictionary<LibraryResourceKind, Brush> Tints = new()
    {
        [LibraryResourceKind.Unknown] = Ink("#B392F0"),
        [LibraryResourceKind.Car] = Ink("#6FA8F5"),
        [LibraryResourceKind.Character] = Ink("#F0A868"),
        [LibraryResourceKind.Player] = Ink("#FFD166"),
        [LibraryResourceKind.Police] = Ink("#4E8FE0"),
        [LibraryResourceKind.Wardrobe] = Ink("#C79BF0"),
        [LibraryResourceKind.Traffic] = Ink("#7FD4B0"),
        [LibraryResourceKind.District] = Ink("#7FB6EE"),
        [LibraryResourceKind.CityCrash] = Ink("#FF9A5E"),
        [LibraryResourceKind.Terrain] = Ink("#A9B96A"),
        [LibraryResourceKind.Sky] = Ink("#7FCFEA"),
        [LibraryResourceKind.Interface] = Ink("#9EA6B3"),
        [LibraryResourceKind.Video] = Ink("#E884AE"),
        [LibraryResourceKind.Sound] = Ink("#6FD1C5"),
        [LibraryResourceKind.Music] = Ink("#5FC9A6"),
        [LibraryResourceKind.Speech] = Ink("#8FD2F2"),
        [LibraryResourceKind.Animation] = Ink("#B99BF2"),
        [LibraryResourceKind.Script] = Ink("#8FD46A"),
        [LibraryResourceKind.Mission] = Ink("#F0705A"),
        [LibraryResourceKind.Particle] = Ink("#FFC24A"),
        [LibraryResourceKind.Shop] = Ink("#E0A96D"),
        [LibraryResourceKind.Table] = Ink("#8FAECC"),
        [LibraryResourceKind.Text] = Ink("#BFC6D1"),
        [LibraryResourceKind.Weapon] = Ink("#C9605A"),
        [LibraryResourceKind.Cloth] = Ink("#9FE0CF"),
        [LibraryResourceKind.Generated] = Ink("#A3B4DA"),
        [LibraryResourceKind.Map] = Ink("#D0C46A"),
    };

    /// <summary>The icon for a kind. An unmapped kind falls back to the archive crate rather than to
    /// nothing — an empty cell reads as a broken row, which is worse than a vague one.</summary>
    public static Geometry Glyph(LibraryResourceKind kind) =>
        Glyphs.GetValueOrDefault(kind, Glyphs[LibraryResourceKind.Unknown]);

    /// <summary>The colour for a kind, on the same fallback as <see cref="Glyph"/>.</summary>
    public static Brush Tint(LibraryResourceKind kind) =>
        Tints.GetValueOrDefault(kind, Tints[LibraryResourceKind.Unknown]);

    private static Geometry Parse(string data)
    {
        Geometry geometry = Geometry.Parse(data);
        geometry.Freeze();      // shared by every row and tile — frozen so WPF may reuse it across threads
        return geometry;
    }

    private static Brush Ink(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Resource kind to its icon, for the browser's tree rows and tiles.</summary>
public sealed class ResourceGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ResourceTypeIcons.Glyph(value as LibraryResourceKind? ?? LibraryResourceKind.Unknown);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Resource kind to its colour. Used for the stroke of both the icon and, dimmed, its backplate.</summary>
public sealed class ResourceTintConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ResourceTypeIcons.Tint(value as LibraryResourceKind? ?? LibraryResourceKind.Unknown);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
