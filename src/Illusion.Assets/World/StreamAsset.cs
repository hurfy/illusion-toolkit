using Illusion.Formats.StreamMap;

namespace Illusion.Assets.World;

/// <summary>One line asset: path from StreamMap + resolve to disk + "renderable" flag.</summary>
public sealed class StreamAsset
{
    public StreamGroupType Type { get; init; }
    public string Path { get; init; } = null!;      // "/sds/city/sicily01.sds"
    public string? Entity { get; init; }
    public string DiskPath { get; init; } = null!;  // absolute path on disk
    public bool Renderable { get; init; }  // .sds of a geometry type and the file exists
}
