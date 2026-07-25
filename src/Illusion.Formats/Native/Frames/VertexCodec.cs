using Illusion.Formats.Geometry;

namespace Illusion.Formats.Native.Frames;

/// <summary>
/// The packed-vertex codec facade: maps the wire vertices onto <see cref="Vertex"/> and back.
/// The hot paths cross the boundary once per buffer; the per-vertex entry points ride a
/// one-vertex buffer (an edit-time convenience, not a streaming path).
/// </summary>
internal static class VertexCodec
{
    /// <summary>
    /// The load-path decode: fills the caller's flat channel arrays in place, straight from the
    /// native codec. No wire, no per-vertex object — a district's worth of meshes decodes without
    /// allocating anything beyond the arrays the renderer already needs. Arrays for channels the
    /// declaration does not carry are left untouched; pass null for channels you do not want.
    /// </summary>
    internal static unsafe void DecompressChannels(byte[] data, int numVerts, VertexFlags declaration,
        System.Numerics.Vector3 offset, float scale,
        System.Numerics.Vector3[] positions, System.Numerics.Vector3[]? normals,
        System.Numerics.Vector2[]? uv0, System.Numerics.Vector3[]? tangents,
        System.Numerics.Vector3[]? binormals)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Length < numVerts
            || (normals != null && normals.Length < numVerts)
            || (uv0 != null && uv0.Length < numVerts)
            || (tangents != null && tangents.Length < numVerts)
            || (binormals != null && binormals.Length < numVerts))
        {
            throw new ArgumentException($"channel arrays must hold at least {numVerts} elements");
        }

        int status;
        fixed (byte* p = data)
        fixed (System.Numerics.Vector3* pos = positions)
        fixed (System.Numerics.Vector3* nrm = normals)
        fixed (System.Numerics.Vector2* uv = uv0)
        fixed (System.Numerics.Vector3* tan = tangents)
        fixed (System.Numerics.Vector3* bin = binormals)
        {
            status = FramesNativeMethods.VertexDecompressChannels(
                p, (ulong)data.Length, (uint)declaration, (ulong)numVerts,
                offset.X, offset.Y, offset.Z, scale,
                (float*)pos, (float*)nrm, (float*)uv, (float*)tan, (float*)bin);
        }
        if (status != NativeMethods.Ok)
        {
            string error = NativeFormats.LastError;
            throw new InvalidDataException(
                error.Length != 0 ? error : $"mf_vtx_decompress_channels failed ({status})");
        }
    }

    internal static Vertex[] DecompressBuffer(byte[] data, int numVerts, VertexFlags declaration,
        System.Numerics.Vector3 offset, float scale)
    {
        Model.DecodedVertexBufferW wire = NativeFrames.DecompressVertexBuffer(
            data, (uint)declaration, (ulong)numVerts, offset, scale);
        var vertices = new Vertex[numVerts];
        for (int i = 0; i < numVerts; i++)
        {
            vertices[i] = ToVertex(wire.Vertices[i], declaration);
        }
        return vertices;
    }

    /// <summary>Re-encodes <paramref name="vertices"/> over a copy of <paramref name="baseData"/>
    /// (unmodeled bits survive) and returns the new buffer.</summary>
    internal static byte[] CompressBuffer(byte[] baseData, IReadOnlyList<Vertex> vertices,
        VertexFlags declaration, System.Numerics.Vector3 offset, float scale)
    {
        var wire = new Model.DecodedVertexBufferW();
        foreach (Vertex vertex in vertices)
        {
            wire.Vertices.Add(ToWire(vertex));
        }
        return NativeFrames.CompressVertexBuffer(baseData, wire, (uint)declaration, offset, scale);
    }

    /// <summary>The declared channels only — undeclared ones keep the managed defaults
    /// (notably the (1,0,0) tangent), exactly like the managed decoder.</summary>
    private static Vertex ToVertex(Model.PackedVertexW wire, VertexFlags declaration)
    {
        var vertex = new Vertex();
        if (declaration.HasFlag(VertexFlags.Position))
        {
            vertex.Position = wire.Position;
            vertex.Binormal = wire.Binormal;
        }
        if (declaration.HasFlag(VertexFlags.Tangent))
        {
            vertex.Tangent = wire.Tangent;
        }
        if (declaration.HasFlag(VertexFlags.Normals))
        {
            vertex.Normal = wire.Normal;
        }
        if (declaration.HasFlag(VertexFlags.Skin))
        {
            vertex.BoneWeights = [wire.Weight0, wire.Weight1, wire.Weight2, wire.Weight3];
            vertex.BoneIDs =
            [
                (byte)(wire.BoneIds & 0xFF),
                (byte)((wire.BoneIds >> 8) & 0xFF),
                (byte)((wire.BoneIds >> 16) & 0xFF),
                (byte)((wire.BoneIds >> 24) & 0xFF),
            ];
        }
        if (declaration.HasFlag(VertexFlags.Color))
        {
            vertex.Color0 = UnpackRgba(wire.Color0Rgba);
        }
        if (declaration.HasFlag(VertexFlags.Color1))
        {
            vertex.Color1 = UnpackRgba(wire.Color1Rgba);
        }
        if (declaration.HasFlag(VertexFlags.TexCoords0))
        {
            vertex.UVs[0] = new Half2((Half)wire.Uv0X, (Half)wire.Uv0Y);
        }
        if (declaration.HasFlag(VertexFlags.TexCoords1))
        {
            vertex.UVs[1] = new Half2((Half)wire.Uv1X, (Half)wire.Uv1Y);
        }
        if (declaration.HasFlag(VertexFlags.TexCoords2))
        {
            vertex.UVs[2] = new Half2((Half)wire.Uv2X, (Half)wire.Uv2Y);
        }
        if (declaration.HasFlag(VertexFlags.ShadowTexture))
        {
            vertex.UVs[3] = new Half2((Half)wire.Uv3X, (Half)wire.Uv3Y);
        }
        if (declaration.HasFlag(VertexFlags.BBCoeffs))
        {
            vertex.BBCoeffs = wire.BbCoeffs;
        }
        if (declaration.HasFlag(VertexFlags.DamageGroup))
        {
            vertex.DamageGroup = wire.DamageGroup;
        }
        return vertex;
    }

    // Unconditional — the native side only writes the channels the declaration declares.
    private static Model.PackedVertexW ToWire(Vertex vertex)
    {
        return new Model.PackedVertexW
        {
            Position = vertex.Position,
            Normal = vertex.Normal,
            Tangent = vertex.Tangent,
            Binormal = vertex.Binormal,
            Uv0X = (float)vertex.UVs[0].X,
            Uv0Y = (float)vertex.UVs[0].Y,
            Uv1X = (float)vertex.UVs[1].X,
            Uv1Y = (float)vertex.UVs[1].Y,
            Uv2X = (float)vertex.UVs[2].X,
            Uv2Y = (float)vertex.UVs[2].Y,
            Uv3X = (float)vertex.UVs[3].X,
            Uv3Y = (float)vertex.UVs[3].Y,
            Color0Rgba = PackRgba(vertex.Color0),
            Color1Rgba = PackRgba(vertex.Color1),
            Weight0 = vertex.BoneWeights[0],
            Weight1 = vertex.BoneWeights[1],
            Weight2 = vertex.BoneWeights[2],
            Weight3 = vertex.BoneWeights[3],
            BoneIds = vertex.BoneIDs[0] | ((uint)vertex.BoneIDs[1] << 8)
                | ((uint)vertex.BoneIDs[2] << 16) | ((uint)vertex.BoneIDs[3] << 24),
            BbCoeffs = vertex.BBCoeffs,
            DamageGroup = vertex.DamageGroup,
        };
    }

    private static byte[] UnpackRgba(uint rgba) =>
    [
        (byte)(rgba & 0xFF),
        (byte)((rgba >> 8) & 0xFF),
        (byte)((rgba >> 16) & 0xFF),
        (byte)((rgba >> 24) & 0xFF),
    ];

    private static uint PackRgba(byte[] rgba) =>
        rgba[0] | ((uint)rgba[1] << 8) | ((uint)rgba[2] << 16) | ((uint)rgba[3] << 24);
}
