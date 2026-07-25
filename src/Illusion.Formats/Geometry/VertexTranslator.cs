using System.Numerics;

namespace Illusion.Formats.Geometry;

/// <summary>
/// Decodes the packed vertex layouts of Mafia II's vertex buffers. Positions are quantized u16 triples
/// (x·factor+offset), with the binormal's handedness sign stored in Z's top bit and the tangent's X/Y
/// bytes packed into what would be Position.W; normals/tangents are (byte−127)/127; UVs are half floats.
/// Endianness is an explicit parameter (console data is big-endian) — no process-global flag.
/// </summary>
public static class VertexTranslator
{
    /// <summary>Decodes a whole packed buffer through the native codec — one boundary crossing
    /// for the hot paths (the viewport loader, the bridge exporter).</summary>
    public static Vertex[] DecompressBuffer(byte[] data, int numVerts, VertexFlags declaration,
        Vector3 offset, float scale)
    {
        return Native.Frames.VertexCodec.DecompressBuffer(data, numVerts, declaration, offset, scale);
    }

    /// <summary>
    /// Decodes straight into the caller's per-channel arrays — the load path. Avoids the
    /// full-fidelity wire and the per-vertex <see cref="Vertex"/> object entirely, which is what
    /// makes streaming a district cheap; use <see cref="DecompressBuffer"/> when you need every
    /// channel (editing, the Blender bridge). Channels the declaration lacks are left untouched.
    /// </summary>
    public static void DecompressChannels(byte[] data, int numVerts, VertexFlags declaration,
        Vector3 offset, float scale, Vector3[] positions, Vector3[]? normals, Vector2[]? uv0,
        Vector3[]? tangents, Vector3[]? binormals)
    {
        Native.Frames.VertexCodec.DecompressChannels(
            data, numVerts, declaration, offset, scale, positions, normals, uv0, tangents, binormals);
    }

    /// <summary>Decodes one vertex slice according to its LOD's declaration (a one-vertex ride
    /// through the native codec; console big-endian data is not supported — the toolkit is
    /// PC-only).</summary>
    public static Vertex DecompressVertex(byte[] data, VertexFlags declaration, Vector3 offset, float scale,
        IReadOnlyDictionary<VertexFlags, VertexOffset> offsets)
    {
        _ = offsets; // the native codec derives the channel layout from the declaration itself
        return Native.Frames.VertexCodec.DecompressBuffer(data, 1, declaration, offset, scale)[0];
    }
}
