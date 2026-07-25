using System.Numerics;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Geometry;

namespace Illusion.Assets.Sds;

/// <summary>
/// Full-fidelity LOD0 decode of one <see cref="FrameObjectSingleMesh"/>: the float channels the
/// viewport uses PLUS the raw packed vertex bytes and quantization parameters. The raw side is what
/// the Blender bridge keys on — bit-exact weld keys, byte-reuse of untouched vertices on push-back,
/// and pass-through of channels the decode does not surface (colors, extra UV sets, damage groups).
/// </summary>
public sealed class DecodedMesh
{
    public required FrameObjectSingleMesh Frame { get; init; }
    public required VertexFlags Declaration { get; init; }
    public required int Stride { get; init; }
    public required int NumVerts { get; init; }
    public required Vector3 DecompressionOffset { get; init; }
    public required float DecompressionFactor { get; init; }

    /// <summary>The LOD0 vertex buffer bytes (exactly <see cref="NumVerts"/> × <see cref="Stride"/>).</summary>
    public required byte[] RawVertexData { get; init; }

    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] UVs { get; init; }
    public Vector3[]? Tangents { get; init; }
    public Vector3[]? Binormals { get; init; }
    public required uint[] Indices { get; init; }
}
