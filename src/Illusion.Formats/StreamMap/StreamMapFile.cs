namespace Illusion.Formats.StreamMap;

/// <summary>
/// Read-only parser for Mafia II <c>StreamMap*.bin</c> (magic "StrM"=1299346515, version 6).
/// The layout was verified against a real file; the line↔loader relation logic is as in
/// the toolkit's <c>StreamEditor.BuildData</c>: a loader is active for a line if LineID ∈ [Start, End].
/// The toolkit's writing path is intentionally not ported here (only reading is needed for the viewport).
/// </summary>
public sealed class StreamMapFile
{
    public const uint Magic = 1299346515; // "StrM"

    public string[] GroupHeaders { get; private set; } = Array.Empty<string>();
    public StreamMapLine[] Lines { get; private set; } = Array.Empty<StreamMapLine>();
    public StreamMapLoader[] Loaders { get; private set; } = Array.Empty<StreamMapLoader>();

    public static StreamMapFile Load(string path) => Read(File.ReadAllBytes(path));

    /// <summary>
    /// Parses a StreamMap already in memory — the same bytes <see cref="Load"/> would read off disk.
    /// Exists for callers holding the file as a payload rather than a path (the MCP tools decode one
    /// straight out of base64).
    /// </summary>
    public static StreamMapFile Read(ReadOnlySpan<byte> bytes)
    {
        var file = new StreamMapFile();
        file.Parse(bytes);
        return file;
    }

    private void Parse(ReadOnlySpan<byte> b)
    {
        (GroupHeaders, Lines, Loaders) = Native.Misc.NativeMiscFiles.ReadStreamMap(b);
    }

}
