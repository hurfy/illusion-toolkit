using Illusion.Formats.IO;

namespace Illusion.Formats.Tyres;

/// <summary>
/// A tyre-settings table (tyres.bin, magic 0x12345678): a 28-byte block-offset header then a
/// little-endian data block plus an identical big-endian copy. Ported from MafiaToolkit; the header
/// is typed and the two data blocks are kept as one opaque body (MafiaToolkit recomputes the offsets
/// and re-serializes both blocks), so the file round-trips byte-exact.
/// </summary>
public sealed class TyresFile
{
    /// <summary>The typed wire model (header typed; data blocks are an opaque capsule).</summary>
    internal Native.Model.TyresFileW Wire { get; set; } = new();

    /// <summary>The format version.</summary>
    public int Version => Wire.Version;

    public static TyresFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static TyresFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadTyres(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.TyresToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
