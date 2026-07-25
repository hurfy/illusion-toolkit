namespace Illusion.Formats;

/// <summary>The games this library supports. Mafia III / Mafia: DE (version-20 archives) are not.</summary>
public enum GameVariant
{
    MafiaII,
    MafiaIIDefinitiveEdition,
}

/// <summary>
/// Per-game knobs the formats need. Both supported games share archive version 19; the differences are
/// header cosmetics, patch-file support and the material library version.
/// </summary>
public sealed record GameProfile(GameVariant Variant)
{
    public static readonly GameProfile MafiaII = new(GameVariant.MafiaII);
    public static readonly GameProfile MafiaIIDe = new(GameVariant.MafiaIIDefinitiveEdition);

    public uint ArchiveVersion => 19;

    /// <summary>Classic Mafia II archives carry a fixed byte pattern in the header's Unknown20 field;
    /// the Definitive Edition zeroes it.</summary>
    public bool WritesLegacyHeaderBytes => Variant == GameVariant.MafiaII;

    /// <summary>Only the Definitive Edition ships .sds.patch companions.</summary>
    public bool SupportsPatchFiles => Variant == GameVariant.MafiaIIDefinitiveEdition;

    /// <summary>Material library version (.mtl): 57 classic, 58 DE.</summary>
    public ushort MaterialVersion => Variant == GameVariant.MafiaII ? (ushort)57 : (ushort)58;
}

/// <summary>
/// Knobs for writing an SDS archive. Defaults reproduce how the app has always packed (zlib, the
/// MafiaToolkit ini defaults) — these were previously scattered over ToolkitSettings statics.
/// </summary>
public sealed class SdsWriteOptions
{
    public bool Compress { get; init; } = true;

    /// <summary>A compressed block is kept only when smaller than this fraction of the raw block;
    /// 0 disables compression.</summary>
    public float CompressionRatio { get; init; } = 0.9f;

    /// <summary>Opt-in oodle block compression (Mafia II DE parity). The game accepts zlib archives
    /// for both variants, so this stays off by default.</summary>
    public bool UseOodle { get; init; }

    /// <summary>Flush thresholds for the index/vertex buffer pools when serializing geometry.</summary>
    public int IndexBufferBudget { get; init; } = 945_000;
    public int VertexBufferBudget { get; init; } = 6_000_000;
}
