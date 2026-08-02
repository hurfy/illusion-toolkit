using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Illusion.Assets.Sds;

namespace Illusion.Views;

/// <summary>
/// One icon and one colour per <see cref="SdsResourceKind"/> — what the content browser draws the inside of
/// an archive with. The library's own table (<see cref="ResourceTypeIcons"/>) answers a different question
/// ("what kind of thing is this archive"), so the two are separate tables on purpose: a car archive and a
/// texture inside it have nothing to say to each other.
/// <para>
/// Same 12x12 grid and the same stroked-not-filled drawing as the scene tree's icons, so one geometry serves
/// a 34px tile and would serve a 13px row.
/// </para>
/// </summary>
public static class ArchiveIcons
{
    // Each shape says what the payload IS: a scene as a wireframe box, a texture as a framed picture, a
    // mipmap as the same picture stacked, collision as the tree's shield, a script as a page of angle
    // brackets, a mem file as a chip. Colours run in section families, so a band of tiles reads as one
    // kind of thing before any of the labels do.
    private static readonly Dictionary<SdsResourceKind, Geometry> Glyphs = new()
    {
        // A plain page — the fallback for a type this build has not been taught.
        [SdsResourceKind.Unknown] = Parse(
            "M2.7,1.8 L7.3,1.8 L9.3,3.9 L9.3,10.2 L2.7,10.2 Z M7.3,1.8 L7.3,3.9 L9.3,3.9"),
        [SdsResourceKind.Mesh] = Parse(
            "M6,1.5 L10.5,4 L10.5,8.5 L6,11 L1.5,8.5 L1.5,4 Z M1.5,4 L6,6.5 L10.5,4 M6,6.5 L6,11"),
        [SdsResourceKind.NameTable] = Parse(
            "M1.5,6.2 L6.2,1.5 L10.5,1.5 L10.5,5.8 L5.8,10.5 Z " +
            "M8.0,3.6 A0.9,0.9 0 1 1 9.8,3.6 A0.9,0.9 0 1 1 8.0,3.6 Z"),
        [SdsResourceKind.Buffer] = Parse(
            "M1.5,3.4 L6,1.6 L10.5,3.4 L6,5.2 Z M1.5,6.0 L6,7.8 L10.5,6.0 M1.5,8.4 L6,10.2 L10.5,8.4"),
        [SdsResourceKind.Texture] = Parse(
            "M1.5,2.6 L10.5,2.6 L10.5,9.4 L1.5,9.4 Z M1.5,8.2 L4.4,5.3 L6.6,7.3 L8.3,5.7 L10.5,7.7 " +
            "M7.25,4.6 A0.95,0.95 0 1 1 9.15,4.6 A0.95,0.95 0 1 1 7.25,4.6 Z"),
        [SdsResourceKind.Mipmap] = Parse(
            "M3.2,1.6 L10.6,1.6 L10.6,7.4 M1.4,4.0 L8.8,4.0 L8.8,10.4 L1.4,10.4 Z " +
            "M1.4,9.0 L3.8,6.4 L5.6,8.2 L7.0,6.9 L8.8,8.6"),
        [SdsResourceKind.AnimatedTexture] = Parse(
            "M1.5,2.6 L10.5,2.6 L10.5,9.4 L1.5,9.4 Z M5.0,4.4 L8.4,6.0 L5.0,7.6 Z"),
        [SdsResourceKind.Effect] = Parse(
            "M5.2,2.3 L6.1,5.2 L9.0,6.1 L6.1,7.0 L5.2,9.9 L4.3,7.0 L1.4,6.1 L4.3,5.2 Z " +
            "M9.5,1.7 L9.9,2.9 L11.1,3.3 L9.9,3.7 L9.5,4.9 L9.1,3.7 L7.9,3.3 L9.1,2.9 Z"),
        // The scene tree's shield: same thing, same icon.
        [SdsResourceKind.Collision] = Parse(
            "M6,1.5 L10,3 L10,6.2 C10,8.8 8.2,10.3 6,11 C3.8,10.3 2,8.8 2,6.2 L2,3 Z"),
        // A shape inside its bounds, which is what an ItemDesc entry is.
        [SdsResourceKind.Shape] = Parse(
            "M2.0,2.4 L10.0,2.4 L10.0,9.6 L2.0,9.6 Z M4.0,6.0 A2.0,2.0 0 1 1 8.0,6.0 A2.0,2.0 0 1 1 4.0,6.0 Z"),
        [SdsResourceKind.Actor] = Parse(
            "M6,10.8 C6,10.8 9.6,7.2 9.6,4.9 A3.6,3.6 0 1 0 2.4,4.9 C2.4,7.2 6,10.8 6,10.8 Z " +
            "M4.8,4.8 A1.2,1.2 0 1 1 7.2,4.8 A1.2,1.2 0 1 1 4.8,4.8 Z"),
        [SdsResourceKind.EntityData] = Parse(
            "M2.2,3.0 A3.8,1.5 0 1 1 9.8,3.0 A3.8,1.5 0 1 1 2.2,3.0 Z " +
            "M2.2,3.0 L2.2,9.0 A3.8,1.5 0 0 0 9.8,9.0 L9.8,3.0 M2.2,6.0 A3.8,1.5 0 0 0 9.8,6.0"),
        // Parts assembled around a core — a PREFAB is the assembly, never the pieces.
        [SdsResourceKind.Prefab] = Parse(
            "M4.0,4.0 L8.0,4.0 L8.0,8.0 L4.0,8.0 Z M1.4,1.4 L4.0,1.4 L4.0,4.0 L1.4,4.0 Z " +
            "M8.0,1.4 L10.6,1.4 L10.6,4.0 L8.0,4.0 Z M1.4,8.0 L4.0,8.0 L4.0,10.6 L1.4,10.6 Z " +
            "M8.0,8.0 L10.6,8.0 L10.6,10.6 L8.0,10.6 Z"),
        [SdsResourceKind.Instances] = Parse(
            "M1.4,7.4 L4.4,7.4 L4.4,10.4 L1.4,10.4 Z M4.9,1.6 L7.9,1.6 L7.9,4.6 L4.9,4.6 Z " +
            "M7.6,6.4 L10.6,6.4 L10.6,9.4 L7.6,9.4 Z"),
        [SdsResourceKind.Animation] = Parse(
            "M0.9,6.0 L11.1,6.0 M3.2,3.8 L5.0,6.0 L3.2,8.2 L1.4,6.0 Z M8.8,3.8 L10.6,6.0 L8.8,8.2 L7.0,6.0 Z"),
        [SdsResourceKind.Cutscene] = Parse(
            "M1.5,4.6 L10.5,4.6 L10.5,10.0 L1.5,10.0 Z M1.5,4.6 L2.4,2.2 L10.5,2.2 L10.5,4.6 " +
            "M4.4,2.2 L3.5,4.6 M7.0,2.2 L6.1,4.6"),
        [SdsResourceKind.Sound] = Parse(
            "M1.5,4.7 L3.3,4.7 L5.7,2.5 L5.7,9.5 L3.3,7.3 L1.5,7.3 Z " +
            "M7.4,4.4 C8.5,5.4 8.5,6.6 7.4,7.6 M9.1,2.9 C11.1,5.0 11.1,7.0 9.1,9.1"),
        [SdsResourceKind.Speech] = Parse(
            "M1.5,2.7 L10.5,2.7 L10.5,8.1 L5.5,8.1 L3.3,10.4 L3.3,8.1 L1.5,8.1 Z"),
        // Sound spreading inside a volume: an audio sector is where you can hear something, not the sound.
        [SdsResourceKind.AudioSector] = Parse(
            "M1.6,1.8 L10.4,1.8 L10.4,10.2 L1.6,10.2 Z M3.4,8.4 A2.2,2.2 0 0 0 5.6,6.2 " +
            "M3.4,8.4 A4.4,4.4 0 0 0 7.8,4.0"),
        [SdsResourceKind.Navigation] = Parse(
            "M2.6,9.4 L5.4,6.2 L7.4,8.2 L10,3.4 M1.3,10.4 A1.3,1.3 0 1 1 3.9,10.4 A1.3,1.3 0 1 1 1.3,10.4 Z " +
            "M8.8,2.6 A1.3,1.3 0 1 1 11.4,2.6 A1.3,1.3 0 1 1 8.8,2.6 Z"),
        [SdsResourceKind.TrafficPath] = Parse(
            "M2.2,10.4 C2.2,7.4 9.8,7.4 9.8,4.6 C9.8,2.6 6.4,1.8 4.4,2.6 M3.4,1.5 L4.6,2.7 L3.3,3.7"),
        [SdsResourceKind.Script] = Parse(
            "M2.7,1.8 L7.3,1.8 L9.3,3.9 L9.3,10.2 L2.7,10.2 Z M7.3,1.8 L7.3,3.9 L9.3,3.9 " +
            "M4.8,6.1 L3.8,7.4 L4.8,8.7 M7.2,6.1 L8.2,7.4 L7.2,8.7"),
        [SdsResourceKind.Table] = Parse(
            "M1.5,2.7 L10.5,2.7 L10.5,9.3 L1.5,9.3 Z M1.5,4.9 L10.5,4.9 M1.5,7.1 L10.5,7.1 " +
            "M4.5,2.7 L4.5,9.3 M7.5,2.7 L7.5,9.3"),
        [SdsResourceKind.Xml] = Parse(
            "M4.2,3.0 L1.6,6.0 L4.2,9.0 M7.8,3.0 L10.4,6.0 L7.8,9.0 M6.9,2.4 L5.1,9.6"),
        [SdsResourceKind.Binary] = Parse(
            "M3.4,3.4 L8.6,3.4 L8.6,8.6 L3.4,8.6 Z M5.0,5.0 L7.0,5.0 L7.0,7.0 L5.0,7.0 Z " +
            "M4.6,3.4 L4.6,1.6 M7.4,3.4 L7.4,1.6 M4.6,8.6 L4.6,10.4 M7.4,8.6 L7.4,10.4 " +
            "M3.4,4.6 L1.6,4.6 M3.4,7.4 L1.6,7.4 M8.6,4.6 L10.4,4.6 M8.6,7.4 L10.4,7.4"),
    };

    private static readonly Dictionary<SdsResourceKind, Brush> Tints = new()
    {
        [SdsResourceKind.Unknown] = Ink("#9AA0A6"),
        [SdsResourceKind.Mesh] = Ink("#7FB6EE"),
        [SdsResourceKind.NameTable] = Ink("#86C3E0"),
        [SdsResourceKind.Buffer] = Ink("#6FA8F5"),
        [SdsResourceKind.Texture] = Ink("#C79BF0"),
        [SdsResourceKind.Mipmap] = Ink("#A98BD6"),
        [SdsResourceKind.AnimatedTexture] = Ink("#E884AE"),
        [SdsResourceKind.Effect] = Ink("#FFC24A"),
        [SdsResourceKind.Collision] = Ink("#FF7340"),
        [SdsResourceKind.Shape] = Ink("#E8A05C"),
        [SdsResourceKind.Actor] = Ink("#7FD4B0"),
        [SdsResourceKind.EntityData] = Ink("#5FC9A6"),
        [SdsResourceKind.Prefab] = Ink("#8FD46A"),
        [SdsResourceKind.Instances] = Ink("#A9D96A"),
        [SdsResourceKind.Animation] = Ink("#B99BF2"),
        [SdsResourceKind.Cutscene] = Ink("#D08AE0"),
        [SdsResourceKind.Sound] = Ink("#6FD1C5"),
        [SdsResourceKind.Speech] = Ink("#8FD2F2"),
        [SdsResourceKind.AudioSector] = Ink("#79C7E3"),
        [SdsResourceKind.Navigation] = Ink("#7FCFEA"),
        [SdsResourceKind.TrafficPath] = Ink("#86C7A8"),
        [SdsResourceKind.Script] = Ink("#9FB3D9"),
        [SdsResourceKind.Table] = Ink("#8FAECC"),
        [SdsResourceKind.Xml] = Ink("#BFC6D1"),
        [SdsResourceKind.Binary] = Ink("#9EA6B3"),
    };

    // The colour a section's header dot takes — the family its kinds are drawn in, so the header belongs to
    // the band under it rather than floating above it.
    private static readonly Dictionary<SdsResourceSection, Brush> SectionTints = new()
    {
        [SdsResourceSection.Geometry] = Tints[SdsResourceKind.Mesh],
        [SdsResourceSection.Textures] = Tints[SdsResourceKind.Texture],
        [SdsResourceSection.Effects] = Tints[SdsResourceKind.Effect],
        [SdsResourceSection.Collision] = Tints[SdsResourceKind.Collision],
        [SdsResourceSection.Entities] = Tints[SdsResourceKind.Actor],
        [SdsResourceSection.Animation] = Tints[SdsResourceKind.Animation],
        [SdsResourceSection.Audio] = Tints[SdsResourceKind.Sound],
        [SdsResourceSection.Navigation] = Tints[SdsResourceKind.Navigation],
        [SdsResourceSection.Data] = Tints[SdsResourceKind.Script],
        [SdsResourceSection.Other] = Tints[SdsResourceKind.Unknown],
    };

    /// <summary>The icon for a kind; an unmapped one falls back to the plain page rather than to nothing.</summary>
    public static Geometry Glyph(SdsResourceKind kind) =>
        Glyphs.GetValueOrDefault(kind, Glyphs[SdsResourceKind.Unknown]);

    /// <summary>The colour for a kind, on the same fallback as <see cref="Glyph"/>.</summary>
    public static Brush Tint(SdsResourceKind kind) =>
        Tints.GetValueOrDefault(kind, Tints[SdsResourceKind.Unknown]);

    /// <summary>The colour a section's header is marked with.</summary>
    public static Brush SectionTint(SdsResourceSection section) =>
        SectionTints.GetValueOrDefault(section, Tints[SdsResourceKind.Unknown]);

    private static Geometry Parse(string data)
    {
        Geometry geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Brush Ink(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Resource kind to its icon, for the tiles inside an opened archive.</summary>
public sealed class ArchiveGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ArchiveIcons.Glyph(value as SdsResourceKind? ?? SdsResourceKind.Unknown);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Resource kind to its colour.</summary>
public sealed class ArchiveTintConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ArchiveIcons.Tint(value as SdsResourceKind? ?? SdsResourceKind.Unknown);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A group header's key to its colour. The key the browser groups by is the section itself, so the header's
/// DataContext carries it as <c>Name</c> — hence taking an <see cref="object"/> rather than the enum.
/// </summary>
public sealed class SectionTintConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ArchiveIcons.SectionTint(value as SdsResourceSection? ?? SdsResourceSection.Other);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A resource's size for its tile — bound to the resource itself rather than to its size, because the number
/// alone cannot tell the two interesting cases apart. A payload the manifest names and the folder does not
/// have is not a 0-byte resource, it is a broken archive, and a tile reading "0 B" would hide exactly the
/// thing worth seeing; while an entry that names no payload at all (a Script package) has nothing to be
/// missing, so it says what it is instead.
/// </summary>
public sealed class ResourceSizeConverter : IValueConverter
{
    private static readonly ByteSizeConverter Bytes = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not SdsResource resource ? ""
            : !resource.NamesFile ? resource.Type
            : resource.Size > 0 ? Bytes.Convert(resource.Size, targetType, parameter, culture)
            : "missing";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A section to the title its header shows.</summary>
public sealed class SectionTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SdsResourceSection section ? SdsResourceKinds.TitleOf(section) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
