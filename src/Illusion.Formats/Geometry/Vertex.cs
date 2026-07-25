using System.Numerics;

namespace Illusion.Formats.Geometry;

/// <summary>
/// One decompressed vertex — the output of <see cref="VertexTranslator.DecompressVertex"/>. Channels a
/// declaration lacks keep their defaults. (The compress side was dropped with the model-import path;
/// the packed layouts are documented in VertexTranslator.)
/// </summary>
public sealed class Vertex
{
    public Vector3 Position { get; set; }
    public Vector3 Normal { get; set; }
    public Vector3 Tangent { get; set; } = new(1.0f, 0.0f, 0.0f);
    public Vector3 Binormal { get; set; }
    public Half2[] UVs { get; } = new Half2[4];
    public float[] BoneWeights { get; set; } = new float[4];
    public byte[] BoneIDs { get; set; } = new byte[4];
    public int DamageGroup { get; set; }
    public byte[] Color0 { get; set; } = new byte[4];
    public byte[] Color1 { get; set; } = new byte[4];
    public Vector3 BBCoeffs { get; set; }

    public override string ToString() => Position.ToString();
}
