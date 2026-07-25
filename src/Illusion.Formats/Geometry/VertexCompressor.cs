using System.Numerics;

namespace Illusion.Formats.Geometry;

/// <summary>
/// Encodes a decompressed <see cref="Vertex"/> back into the engine's packed layout — the exact
/// inverse of <see cref="VertexTranslator.DecompressVertex"/>. The caller passes the vertex's
/// ORIGINAL packed bytes as the destination base: only channels the declaration declares are
/// overwritten, so bits the decoder does not model (Unk05, the position W bytes of tangent-less
/// declarations) survive untouched. For new vertices with no original, pass zeros.
/// Quantized fields are encoded with a ±1 candidate search that re-decodes and prefers the exact
/// float match — this makes <c>Compress(Decompress(x)) == x</c> byte-exact even where the forward
/// float rounding is not analytically invertible (large offsets vs small factors).
/// </summary>
public static class VertexCompressor
{
    /// <summary>Re-encodes a whole buffer of vertices over a copy of <paramref name="baseData"/>
    /// through the native codec (one boundary crossing; unmodeled bits survive).</summary>
    public static byte[] CompressBuffer(byte[] baseData, IReadOnlyList<Vertex> vertices,
        VertexFlags declaration, Vector3 offset, float scale)
    {
        return Native.Frames.VertexCodec.CompressBuffer(baseData, vertices, declaration, offset, scale);
    }

    /// <summary>Encodes <paramref name="vertex"/> into <paramref name="slice"/> (one vertex,
    /// little-endian — a one-vertex ride through the native codec).</summary>
    public static void CompressVertex(Vertex vertex, byte[] slice, VertexFlags declaration, Vector3 offset, float scale,
        IReadOnlyDictionary<VertexFlags, VertexOffset> offsets)
    {
        _ = offsets; // the native codec derives the channel layout from the declaration itself
        byte[] encoded = Native.Frames.VertexCodec.CompressBuffer(slice, [vertex], declaration, offset, scale);
        encoded.CopyTo(slice, 0);
    }

    /// <summary>Whether the packed handedness bit is set for this vertex's tangent frame. Decode
    /// yields <c>binormal = cross(normal, tangent) · −W</c> with W = −1 when the bit is set — so the
    /// bit is set exactly when the stored binormal points WITH the cross product.</summary>
    public static bool BinormalSignBit(Vertex vertex, VertexFlags declaration)
    {
        if (!declaration.HasFlag(VertexFlags.Normals) || !declaration.HasFlag(VertexFlags.Tangent)) return false;
        return Vector3.Dot(vertex.Binormal, Vector3.Cross(vertex.Normal, vertex.Tangent)) > 0f;
    }
}
