using Illusion.Formats.IO;

namespace Illusion.Formats.Navigation;

/// <summary>
/// A Kynapse AI-partition dump (.nhv / NAV_HPD, magic 0xD6D6F0F0): a size/magic/buffer-size header
/// then an opaque payload (build stamp, class name, numeric header, entry table, tail). Ported from
/// a corpus survey; the header is typed and the payload is kept as one opaque body, so the file
/// round-trips byte-exact.
/// </summary>
public sealed class NavHpdFile
{
    /// <summary>The typed wire model (header typed; the payload is an opaque capsule).</summary>
    internal Native.Model.NhvFileW Wire { get; set; } = new();

    /// <summary>The container magic (0xD6D6F0F0 in the retail corpus).</summary>
    public uint Magic => Wire.Magic;

    public static NavHpdFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static NavHpdFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadNavHpd(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.NavHpdToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
