namespace Illusion.Formats;

/// <summary>Base for all format-layer failures (named to avoid clashing with System.FormatException).
/// The library never shows UI and never signals failure through booleans — a parse or pack that cannot
/// proceed throws.</summary>
public class FileFormatException : Exception
{
    public FileFormatException(string message) : base(message) { }
    public FileFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The native core (Mafia.Formats.dll) is missing, cannot be loaded, or was built against a
/// different boundary revision than this assembly speaks. A deployment problem, not a data problem:
/// the two halves ship together and their revisions must match exactly.</summary>
public sealed class NativeCoreException : Exception
{
    public NativeCoreException(string message) : base(message) { }
    public NativeCoreException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Structurally invalid SDS data: bad magic, platform, block table, checksum.</summary>
public sealed class SdsFormatException : FileFormatException
{
    public SdsFormatException(string message) : base(message) { }
}

/// <summary>Data declares a version this library does not support (e.g. the version-20 archives of
/// Mafia III / Mafia: DE — this toolkit targets Mafia II and Mafia II DE).</summary>
public sealed class UnsupportedVersionException : FileFormatException
{
    public UnsupportedVersionException(string message) : base(message) { }
}

/// <summary>Packing an extracted folder back into an archive failed (missing manifest, missing file,
/// unknown resource type).</summary>
public sealed class ResourcePackException : FileFormatException
{
    public ResourcePackException(string message) : base(message) { }
    public ResourcePackException(string message, Exception inner) : base(message, inner) { }
}
