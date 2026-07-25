using Illusion.Formats.IO;

namespace Illusion.Formats.City;

/// <summary>
/// A shop-menu table (shopmenu2.bin, magic "2mhs"): a 12-byte header (magic, version, string-pool
/// size) then a string pool and shop/menu records with a back-patched offset table. Ported from
/// MafiaToolkit; the header is typed and the rest is kept as one opaque body (MafiaToolkit rebuilds
/// the pool and patches offsets on write), so the file round-trips byte-exact.
/// </summary>
public sealed class ShopMenu2File
{
    /// <summary>The typed wire model (header typed; pool+records are an opaque capsule).</summary>
    internal Native.Model.ShopMenu2FileW Wire { get; set; } = new();

    /// <summary>The format version.</summary>
    public int Version => Wire.Version;

    public static ShopMenu2File Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static ShopMenu2File Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadShopMenu2(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.ShopMenu2ToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
