using System.Numerics;

namespace Illusion.Formats.Frames.Resources;

/// <summary>
/// The geometry block of a mesh: the shared vertex dequantization (offset + factor) and one
/// <see cref="FrameLOD"/> per level of detail. Bytes are the native core's business — this is the
/// editable model only.
/// </summary>
public class FrameGeometry : FrameEntry
{
    public byte NumLods { get; set; }

    public short Unk01 { get; set; }

    public Vector3 DecompressionOffset { get; set; }

    public float DecompressionFactor { get; set; }

    public FrameLOD[] LOD { get; set; } = null!;

    public FrameGeometry(FrameResource OwningResource) : base(OwningResource) { }

    /// <summary>
    /// Deep-copies another geometry block into this one (mesh duplication): every LOD is cloned,
    /// capsules included, so the copy serializes to the same bytes as the source.
    /// </summary>
    public void CopyFrom(FrameGeometry source)
    {
        NumLods = source.NumLods;
        Unk01 = source.Unk01;
        DecompressionOffset = source.DecompressionOffset;
        DecompressionFactor = source.DecompressionFactor;
        LOD = [.. source.LOD.Select(lod => new FrameLOD(lod))];
    }

    public override string ToString()
    {
        return $"Geometry Block";
    }
}
