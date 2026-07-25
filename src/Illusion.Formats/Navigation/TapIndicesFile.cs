using Illusion.Formats.IO;

namespace Illusion.Formats.Navigation;

/// <summary>
/// A traffic-anim-path index table (tapindices.bin, magics "TAP0"/"UAP0"/"VAP0"): three nested magic
/// blocks ending in an array of 3-int mapping segments. Ported from MafiaToolkit; flat and fully
/// typed, so it round-trips byte-exact.
/// </summary>
public sealed class TapIndicesFile
{
    /// <summary>The typed wire model.</summary>
    internal Native.Model.TapIndicesW Wire { get; set; } = new();

    /// <summary>Number of mapping segments.</summary>
    public int MappingCount => Wire.Mappings.Count;

    public static TapIndicesFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static TapIndicesFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadTapIndices(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.TapIndicesToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
