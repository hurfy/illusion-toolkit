using System.Numerics;

namespace Illusion.Formats.Actors;

/// <summary>How one behavior field is stored. Fixed by the core — the numbering is part of the wire.</summary>
public enum ActorPropertyKind
{
    Bool = 0,
    Int8 = 1,
    UInt8 = 2,
    Int16 = 3,
    UInt16 = 4,
    Int32 = 5,
    UInt32 = 6,
    Int64 = 7,
    UInt64 = 8,
    Float = 9,
    Vector3 = 10,
    /// <summary>A fixed-width NUL-padded buffer; <see cref="ActorPropertyField.Capacity"/> is its width in bytes,
    /// so the text can be replaced but never outgrow its slot.</summary>
    Text = 11,
    Hash64 = 12,
}

/// <summary>
/// One named value of an actor's behavior blob — a live view over the pack's wire model, so a write here is
/// already in the file the next save produces. The blob itself is never re-encoded from these: the core keeps the
/// original bytes and pokes back only the values that actually moved, which is what leaves the padding and the
/// still-unmapped tails of the shipped data untouched.
/// </summary>
public sealed class ActorPropertyField
{
    private readonly Native.Model.ActorPropFieldW wire;

    internal ActorPropertyField(Native.Model.ActorPropFieldW wire)
    {
        this.wire = wire;
    }

    public string Name => wire.Name;
    public ActorPropertyKind Kind => (ActorPropertyKind)wire.Kind;

    /// <summary>Byte offset inside the behavior blob — the field's identity across rebuilds.</summary>
    public uint Offset => wire.Offset;

    /// <summary>The field's width in bytes (the buffer size for <see cref="ActorPropertyKind.Text"/>).</summary>
    public uint Capacity => wire.Size;

    /// <summary>The integer value, for every integral and boolean kind.</summary>
    public long Number
    {
        get => wire.Num;
        set => wire.Num = value;
    }

    public bool Flag
    {
        get => wire.Num != 0;
        set => wire.Num = value ? 1 : 0;
    }

    /// <summary>The hash value, for <see cref="ActorPropertyKind.Hash64"/> and <see cref="ActorPropertyKind.UInt64"/>.</summary>
    public ulong Hash
    {
        get => unchecked((ulong)wire.Num);
        set => wire.Num = unchecked((long)value);
    }

    public float Single
    {
        get => wire.F0;
        set => wire.F0 = value;
    }

    public Vector3 Vector
    {
        get => new(wire.F0, wire.F1, wire.F2);
        set
        {
            wire.F0 = value.X;
            wire.F1 = value.Y;
            wire.F2 = value.Z;
        }
    }

    /// <summary>The text of a fixed-width buffer. Anything longer than <see cref="Capacity"/> minus its
    /// terminator is cut by the core rather than allowed to overrun the slot.</summary>
    public string Text
    {
        get => wire.Text;
        set => wire.Text = value ?? string.Empty;
    }

    /// <summary>The value as text, for a read-only listing.</summary>
    public string Display => Kind switch
    {
        ActorPropertyKind.Bool => Flag ? "true" : "false",
        ActorPropertyKind.Float => Single.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
        ActorPropertyKind.Vector3 => $"{Vector.X:0.###}, {Vector.Y:0.###}, {Vector.Z:0.###}",
        ActorPropertyKind.Text => Text,
        ActorPropertyKind.Hash64 or ActorPropertyKind.UInt64 => "0x" + Hash.ToString("X16"),
        _ => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// One entity-init property row: the behavior of an actor kind, and the row several actors point at through their
/// <see cref="ActorEntry.InitPropId"/>. Editing a field here changes it for every actor that shares the row —
/// <see cref="SharerCount"/> says how many that is.
/// </summary>
public sealed class ActorPropertyRow
{
    private readonly ActorsFile owner;
    private readonly Native.Model.ActorPropRowW wire;

    internal ActorPropertyRow(ActorsFile owner, Native.Model.ActorPropRowW wire, int index)
    {
        this.owner = owner;
        this.wire = wire;
        Index = index;
        var fields = new List<ActorPropertyField>(wire.Fields.Count);
        foreach (Native.Model.ActorPropFieldW field in wire.Fields)
        {
            fields.Add(new ActorPropertyField(field));
        }
        Fields = fields;
    }

    /// <summary>Row in the pack's property table — what <see cref="ActorEntry.InitPropId"/> holds.</summary>
    public int Index { get; }

    /// <summary>The entity type the row describes. Zero in an uncompressed pack, which names the type instead.</summary>
    public EntityType Type => (EntityType)wire.BufferType;

    public int TypeId => wire.BufferType;

    /// <summary>The class name, in an uncompressed pack; empty in a compressed one, which stores only the id.</summary>
    public string TypeName => wire.TypeName;

    /// <summary>Size of the behavior blob in bytes.</summary>
    public int PayloadSize => wire.Payload.Length;

    /// <summary>How many actors of this pack point at this row. Above one, an edit is an edit for all of them.</summary>
    public int SharerCount => owner.CountSharersOf(Index);

    /// <summary>The named fields, empty when the core has no layout for this entity type (the row still
    /// round-trips — it just has nothing to show).</summary>
    public IReadOnlyList<ActorPropertyField> Fields { get; }
}
