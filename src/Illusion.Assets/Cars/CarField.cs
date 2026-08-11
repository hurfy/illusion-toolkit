using System.Globalization;
using System.Numerics;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>How a value reads, and what may be typed into it.</summary>
public enum CarFieldKind
{
    /// <summary>A measurement: metres, kilograms, a coefficient.</summary>
    Number,

    /// <summary>A whole number the game switches on — a seat's type, an axle's type.</summary>
    Count,

    /// <summary>On or off.</summary>
    Flag,

    /// <summary>A point in space, in the car's own coordinates.</summary>
    Point,
}

/// <summary>
/// One value a component or one of its markers carries in a PARALLEL LIST of the prefab — a seat's type, a
/// climb box's extents, a window's depth, an axle's masses.
///
/// <para>
/// A door is written down in four lists that share nothing but a hash, and until now the only way to change
/// one of those numbers was to find the list it lives in and count rows. A field is that number brought to
/// where the thing it describes already sits: it carries its own label, its own reading and the address it is
/// written back through, and the address is the aggregate's business rather than the modder's.
/// </para>
/// <para>
/// An edit is <c>field with { Number = … }</c> handed back to <see cref="Car.SetMarker"/> or
/// <see cref="Car.SetRow"/> — the address rides along, so a row and the write behind it cannot end up
/// addressing two different fields.
/// </para>
/// </summary>
/// <param name="Label">What to call it on a row.</param>
/// <param name="Hint">What it does, in one line, for the modder who has never seen this list.</param>
/// <param name="Kind">How it reads and what may be typed into it.</param>
/// <param name="Number">Its value, for every kind but <see cref="CarFieldKind.Point"/>. A flag is 0 or 1.</param>
/// <param name="Point">Its value, for a <see cref="CarFieldKind.Point"/>.</param>
public sealed record CarField(
    string Label, string Hint, CarFieldKind Kind, float Number, Vector3 Point)
{
    /// <summary>Which of the prefab's numbers this is. The file's own address, and never shown.</summary>
    internal CarValueSlot Slot { get; init; }

    /// <summary>Which row of that slot's list. Flat across the car, the way the slot itself is addressed.</summary>
    internal int At { get; init; }

    /// <summary>
    /// Which part of the addressed value this field is: an axis of a point, or the BIT a flag lives in inside a
    /// word that holds several. Zero — the whole value — for everything else.
    ///
    /// <para>
    /// A <see cref="CarFieldKind.Point"/> ignores it and writes all three axes; nothing else has ever needed
    /// more than the first. A flag does: five of a deform part's flags share one word, and each has to be
    /// written without disturbing the other thirty-one bits — including the meanings nobody has named yet.
    /// </para>
    /// </summary>
    internal int Axis { get; init; }

    /// <summary>The value as a row shows it.</summary>
    public string Text => Kind switch
    {
        CarFieldKind.Flag => Number != 0f ? "yes" : "no",
        CarFieldKind.Count => ((int)Number).ToString(CultureInfo.InvariantCulture),
        CarFieldKind.Point => $"{Shown(Point.X)}, {Shown(Point.Y)}, {Shown(Point.Z)}",
        _ => Number.ToString("0.###", CultureInfo.InvariantCulture),
    };

    public override string ToString() => $"{Label} {Text}";

    private static string Shown(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// A row of one of the prefab's parallel lists that names a component's OWN bone — the door points that say
/// where a door's handle and lock are, the window record that says how deep the pane sits and whether it
/// rolls down, the axle that carries the brake drum and the masses.
///
/// <para>
/// It is not a marker: a marker hangs a helper frame off a bone, while this names the bone itself and so
/// belongs to that component the way a fingerprint belongs to a finger. It is shown beneath the component for
/// the same reason a marker is — the modder should never have to know which of the four lists a number lives
/// in, let alone count rows to find it.
/// </para>
/// </summary>
/// <param name="Label">What to call it: "Door points", "Window", "Axle 2".</param>
/// <param name="Kind">Which list it came out of, in one word.</param>
/// <param name="Index">Its position in that list — how an edit addresses it.</param>
/// <param name="Fields">The numbers it carries.</param>
public sealed record CarComponentRow(
    string Label, string Kind, int Index, IReadOnlyList<CarField> Fields)
{
    public override string ToString() => Label;
}
