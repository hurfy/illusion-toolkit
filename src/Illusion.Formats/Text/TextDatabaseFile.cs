using Illusion.Formats.IO;

namespace Illusion.Formats.Text;

/// <summary>
/// A localized text table (.dat / TextDatabase / TextIconsMap): an optional UTF-8 BOM then a list of
/// CRLF-terminated <c>ID:TEXT</c> entries. Split only on the first colon so multi-colon values and
/// blank lines survive; key/value are kept as raw bytes. A file whose re-emit does not match rides
/// whole as an opaque capsule, so it round-trips byte-exact either way.
/// </summary>
public sealed class TextDatabaseFile
{
    /// <summary>The typed wire model. <see cref="IsTyped"/> is false when it rides opaque.</summary>
    internal Native.Model.DatFileW Wire { get; set; } = new();

    /// <summary>Whether the file parsed into typed entries (false = opaque capsule).</summary>
    public bool IsTyped => Wire.Typed != 0;

    /// <summary>Number of text entries (0 when the file rides opaque).</summary>
    public int EntryCount => Wire.Entries.Count;

    public static TextDatabaseFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static TextDatabaseFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadTextDatabase(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.TextDatabaseToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
