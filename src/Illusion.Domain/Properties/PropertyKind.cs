namespace Illusion.Domain.Properties;

/// <summary>
/// The shape of a <see cref="PropertyDescriptor"/>'s value — it tells a UI which editor to render and fixes the
/// CLR type its <c>Get</c> returns / its <c>Set</c> expects (the "boxed-value contract" below). The adapter that
/// builds the descriptor and the control that renders it are the two ends of this contract; nothing else needs to
/// know the concrete backend type.
/// </summary>
public enum PropertyKind
{
    /// <summary>Signed integer up to 32 bits, boxed as <see cref="long"/>. <see cref="PropertyDescriptor.Min"/>
    /// / <see cref="PropertyDescriptor.Max"/> carry the real range (byte 0..255, short, int, or an index 0..n-1).</summary>
    Int,

    /// <summary>64-bit value shown and edited as hexadecimal, boxed as <see cref="ulong"/> (e.g. a collision hash).</summary>
    UInt64Hex,

    /// <summary>Single-precision float, boxed as <see cref="float"/>.</summary>
    Float,

    /// <summary>Boolean, boxed as <see cref="bool"/>.</summary>
    Bool,

    /// <summary>Plain string, boxed as <see cref="string"/>.</summary>
    Text,

    /// <summary>A name paired with its hash, boxed as <see cref="HashNameValue"/>. Setting re-derives the hash from
    /// the name (adapter side); an empty name is rejected by the editor (it would keep a stale hash).</summary>
    HashName,

    /// <summary><see cref="System.Numerics.Vector3"/>, boxed as-is (reuses the viewport's 3-component editor).</summary>
    Vector3,

    /// <summary>A flags enum, boxed as <see cref="long"/> (the combined bits). <see cref="PropertyDescriptor.FlagItems"/>
    /// names the individual bits the editor offers as checkboxes.</summary>
    Flags,

    /// <summary><see cref="System.Numerics.Matrix4x4"/>, boxed as-is. Read-only in this version.</summary>
    Matrix,

    /// <summary>A read-only, expandable list of preformatted lines (arrays of structs whose length fields make
    /// in-place editing unsafe), boxed as <see cref="IReadOnlyList{T}"/> of string.</summary>
    StructList,
}
