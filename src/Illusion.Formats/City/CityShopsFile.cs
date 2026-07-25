using Illusion.Formats.IO;

namespace Illusion.Formats.City;

/// <summary>
/// A city-shops table (cityshops.bin, magic "hstc"): a 32-byte header (version, counts, name-buffer
/// size) then a name buffer and area/area-data records that reference names by offset. Ported from
/// MafiaToolkit; the header is typed and the buffer+records are kept as one opaque body
/// (MafiaToolkit rebuilds the name buffer and keys on write), so the file round-trips byte-exact.
/// </summary>
public sealed class CityShopsFile
{
    /// <summary>The typed wire model (header typed; buffer+records are an opaque capsule).</summary>
    internal Native.Model.CityShopsFileW Wire { get; set; } = new();

    /// <summary>The file version (vanilla = 8, Joe's Adventures = 9).</summary>
    public int FileVersion => Wire.FileVersion;

    /// <summary>Number of areas declared in the header.</summary>
    public int AreaCount => Wire.NumAreas;

    public static CityShopsFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static CityShopsFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadCityShops(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.CityShopsToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
