using Illusion.Formats.IO;

namespace Illusion.Formats.Animations;

/// <summary>
/// A skeletal animation (.an2 / Animation2, magic 0xFA5612BC): a fixed header, primary/secondary event
/// arrays, and per-bone tracks (rotation keyframes + position samples). Ported from MafiaToolkit; the
/// header/events/tracks are typed and the bit-packed rotation/position blobs are preserved raw, so the
/// file round-trips byte-exact. The trailing bone-index shorts ride as an opaque tail.
/// </summary>
public sealed class Animation2File
{
    /// <summary>The typed wire model. Internal until a friendlier surface (decoded keyframes) is needed.</summary>
    internal Native.Model.AnimFileW Wire { get; set; } = new();

    /// <summary>Skeleton id from the header.</summary>
    public int SkeletonId => Wire.SkeletonId;
    /// <summary>Animation duration in seconds.</summary>
    public float Duration => Wire.Duration;
    /// <summary>Number of animation tracks (bones).</summary>
    public int TrackCount => Wire.Tracks.Count;

    public static Animation2File Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static Animation2File Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadAnim2(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.Anim2ToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
