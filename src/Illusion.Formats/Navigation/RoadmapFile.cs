using Illusion.Formats.IO;

namespace Illusion.Formats.Navigation;

/// <summary>
/// A traffic roadmap (.gsd / RoadmapCe): a 60-byte header of seven list-headers then a
/// recursively-serialized road-graph tree. Ported from MafiaToolkit; the header is typed and the
/// tree body is kept as one opaque body, so the file round-trips byte-exact.
/// </summary>
public sealed class RoadmapFile
{
    /// <summary>The typed wire model (header typed; the road-graph tree is an opaque capsule).</summary>
    internal Native.Model.GsdFileW Wire { get; set; } = new();

    /// <summary>The seven top-level list-header record counts (splines, roads, crossroads, …).</summary>
    public IReadOnlyList<int> ListCounts => [.. Wire.Headers.ConvertAll(h => (int)h.Count)];

    public static RoadmapFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static RoadmapFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadRoadmap(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.RoadmapToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
