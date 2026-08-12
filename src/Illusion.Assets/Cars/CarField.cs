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

    /// <summary>
    /// A frame of the archive the car points at — its root frame, its scale bone, the door a seat is entered
    /// through, the frame a wind emitter blows from.
    ///
    /// <para>
    /// Chosen from the frames the archive HAS and never typed. A prefab addresses a frame by the FNV64 of its
    /// name and a hash that names nothing does not fail — the part simply stops working, with no error
    /// anywhere — so a picker is not a convenience here, it is the only safe way to write one.
    /// </para>
    /// </summary>
    Frame,
}

/// <summary>One frame of the archive, as an option a <see cref="CarFieldKind.Frame"/> field offers.</summary>
/// <param name="Hash">FNV64 of its name — what the prefab actually stores. Zero is "not set".</param>
public sealed record CarFrameChoice(ulong Hash, string Name)
{
    public override string ToString() => Name;
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
    /// <summary>Which of the prefab's numbers this is. The file's own address, and never shown. Meaningless
    /// on a <see cref="CarFieldKind.Frame"/>, which is addressed by <see cref="FrameSlot"/> instead.</summary>
    internal CarValueSlot Slot { get; init; }

    /// <summary>Which of the prefab's frame references this is, for a <see cref="CarFieldKind.Frame"/>. The
    /// kind is the discriminator: both slot enums start at zero, so neither can carry "not this one".</summary>
    internal CarFrameSlot FrameSlot { get; init; }

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

    /// <summary>FNV64 of the frame a <see cref="CarFieldKind.Frame"/> names. Zero is an empty slot — which a
    /// car with no top light legitimately is, so it is not a fault.</summary>
    public ulong Frame { get; init; }

    /// <summary>That frame's name, or the bare hash when nothing in the archive resolves to it — which is the
    /// signature of a rename made in Blender and the one thing this row exists to make visible.</summary>
    public string FrameName { get; init; } = "";

    /// <summary>Whether the hash this field holds names a frame the archive actually has.</summary>
    public bool FrameResolves { get; init; } = true;

    /// <summary>What a <see cref="CarFieldKind.Frame"/> may be pointed at: every frame of the archive, plus
    /// the empty slot. Shared across the fields of one car, so it costs one list rather than one per row.</summary>
    public IReadOnlyList<CarFrameChoice> Choices { get; init; } = [];

    /// <summary>The value as a row shows it.</summary>
    public string Text => Kind switch
    {
        CarFieldKind.Flag => Number != 0f ? "yes" : "no",
        CarFieldKind.Count => ((int)Number).ToString(CultureInfo.InvariantCulture),
        CarFieldKind.Point => $"{Shown(Point.X)}, {Shown(Point.Y)}, {Shown(Point.Z)}",
        CarFieldKind.Frame => FrameName.Length > 0 ? FrameName : "—",
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
    /// <summary>
    /// What this row IS, in one line, when the default sentence would be wrong.
    ///
    /// <para>
    /// The car-level rows need it: the chassis and the steering-wheel grip belong to the CAR and sit on the
    /// body because the body is the car rather than a panel of it, so "what this car's chassis list says
    /// about the scale bone" would be a sentence about the wrong thing.
    /// </para>
    /// </summary>
    public string Hint { get; init; } = "";

    public override string ToString() => Label;
}
