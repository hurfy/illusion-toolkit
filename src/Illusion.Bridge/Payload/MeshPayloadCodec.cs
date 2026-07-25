using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using static Illusion.Bridge.Payload.ExchangeMarshal;

namespace Illusion.Bridge.Payload;

/// <summary>One material slot of a mesh payload: identity (FNV64 hash as hex), resolved texture
/// paths for Blender preview, and the LOD0 index range it covers.</summary>
public sealed class MeshMaterialInfo
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("diffuse")] public string? Diffuse { get; set; }
    [JsonPropertyName("normal")] public string? Normal { get; set; }
    [JsonPropertyName("normalIsDxt5nm")] public bool NormalIsDxt5nm { get; set; }
    [JsonPropertyName("specular")] public string? Specular { get; set; }
    [JsonPropertyName("startIndex")] public int StartIndex { get; set; }
    [JsonPropertyName("numFaces")] public int NumFaces { get; set; }
}

/// <summary>
/// Typed view of one kind="mesh" exchange object: welded vertices + per-loop (face-corner)
/// attributes — exactly Blender's native mesh model, so the addon builds it with no weld heuristics.
/// Loops run in triangle order (3 per face); UVs carry Blender's V convention (v' = 1 − v).
/// </summary>
public sealed class MeshObjectPayload
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
    public Matrix4x4 World { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 Local { get; set; } = Matrix4x4.Identity;

    public Vector3[] Positions { get; set; } = Array.Empty<Vector3>();
    public uint[] LoopVertexIndices { get; set; } = Array.Empty<uint>();
    public Vector3[] LoopNormals { get; set; } = Array.Empty<Vector3>();
    public Vector2[] LoopUvs { get; set; } = Array.Empty<Vector2>();
    public int[] LoopOrigIndex { get; set; } = Array.Empty<int>();
    public ushort[] FaceMaterials { get; set; } = Array.Empty<ushort>();
    public List<MeshMaterialInfo> Materials { get; set; } = new();

    /// <summary>Source triangles omitted from the payload because welding made them degenerate
    /// (Blender rejects polygons with repeated vertices). The original index buffer still carries
    /// them — only the Blender preview lacks these zero-area faces.</summary>
    public int DroppedDegenerateFaces { get; set; }

    /// <summary>Source triangles omitted because a kept face already covers the same welded vertex
    /// set (double-sided geometry; Blender strips duplicate polygons).</summary>
    public int DroppedDuplicateFaces { get; set; }

    /// <summary>Raw vertex-declaration bits of the source LOD0 (diagnostic + reimport aid).</summary>
    public uint VertexDeclaration { get; set; }
    public Vector3 DecompressionOffset { get; set; }
    public float DecompressionFactor { get; set; }
}

/// <summary>Converts <see cref="MeshObjectPayload"/> to and from the generic container encoding.
/// Other kinds (collision, skeleton) get their own codec beside this one.</summary>
public static class MeshPayloadCodec
{
    public static void Add(ExchangeContainer container, MeshObjectPayload mesh)
    {
        var obj = new ExchangeObject
        {
            Kind = ExchangeSchema.KindMesh,
            Id = mesh.Id,
            Name = mesh.Name,
            ParentId = mesh.ParentId,
            World = ToFloats(mesh.World),
            Local = ToFloats(mesh.Local),
            Meta = new JsonObject
            {
                ["vertexDeclaration"] = mesh.VertexDeclaration,
                ["decompressionOffset"] = new JsonArray(
                    mesh.DecompressionOffset.X, mesh.DecompressionOffset.Y, mesh.DecompressionOffset.Z),
                ["decompressionFactor"] = mesh.DecompressionFactor,
                ["droppedDegenerateFaces"] = mesh.DroppedDegenerateFaces,
                ["droppedDuplicateFaces"] = mesh.DroppedDuplicateFaces,
                ["materials"] = JsonSerializer.SerializeToNode(mesh.Materials),
            },
        };

        obj.Arrays[ExchangeSchema.ArrayPositions] = container.AddBlock(
            ExchangeSchema.DtypeF32, 3, mesh.Positions.Length, ToBytes(mesh.Positions));
        obj.Arrays[ExchangeSchema.ArrayIndices] = container.AddBlock(
            ExchangeSchema.DtypeU32, 1, mesh.LoopVertexIndices.Length, ToBytes(mesh.LoopVertexIndices));
        obj.Arrays[ExchangeSchema.ArrayLoopNormals] = container.AddBlock(
            ExchangeSchema.DtypeF32, 3, mesh.LoopNormals.Length, ToBytes(mesh.LoopNormals));
        obj.Arrays[ExchangeSchema.ArrayLoopUv0] = container.AddBlock(
            ExchangeSchema.DtypeF32, 2, mesh.LoopUvs.Length, ToBytes(mesh.LoopUvs));
        obj.Arrays[ExchangeSchema.ArrayOrigIndex] = container.AddBlock(
            ExchangeSchema.DtypeI32, 1, mesh.LoopOrigIndex.Length, ToBytes(mesh.LoopOrigIndex));
        obj.Arrays[ExchangeSchema.ArrayFaceMaterials] = container.AddBlock(
            ExchangeSchema.DtypeU16, 1, mesh.FaceMaterials.Length, ToBytes(mesh.FaceMaterials));

        container.Objects.Add(obj);
    }

    /// <summary>Materializes a kind="mesh" object; throws on a malformed mesh (missing arrays).</summary>
    public static MeshObjectPayload Read(ExchangeContainer container, ExchangeObject obj)
    {
        if (obj.Kind != ExchangeSchema.KindMesh)
            throw new InvalidDataException($"Object '{obj.Id}' is kind '{obj.Kind}', not a mesh.");

        var mesh = new MeshObjectPayload
        {
            Id = obj.Id,
            Name = obj.Name,
            ParentId = obj.ParentId,
            World = FromFloats(obj.World),
            Local = FromFloats(obj.Local),
            Positions = FromBytes<Vector3>(Block(container, obj, ExchangeSchema.ArrayPositions)),
            LoopVertexIndices = FromBytes<uint>(Block(container, obj, ExchangeSchema.ArrayIndices)),
            LoopNormals = FromBytes<Vector3>(Block(container, obj, ExchangeSchema.ArrayLoopNormals)),
            LoopUvs = FromBytes<Vector2>(Block(container, obj, ExchangeSchema.ArrayLoopUv0)),
            LoopOrigIndex = FromBytes<int>(Block(container, obj, ExchangeSchema.ArrayOrigIndex)),
            FaceMaterials = FromBytes<ushort>(Block(container, obj, ExchangeSchema.ArrayFaceMaterials)),
        };

        if (obj.Meta is JsonObject meta)
        {
            mesh.VertexDeclaration = (uint?)meta["vertexDeclaration"] ?? 0;
            mesh.DroppedDegenerateFaces = (int?)meta["droppedDegenerateFaces"] ?? 0;
            mesh.DroppedDuplicateFaces = (int?)meta["droppedDuplicateFaces"] ?? 0;
            mesh.DecompressionFactor = (float?)meta["decompressionFactor"] ?? 0f;
            if (meta["decompressionOffset"] is JsonArray off && off.Count == 3)
                mesh.DecompressionOffset = new Vector3((float?)off[0] ?? 0f, (float?)off[1] ?? 0f, (float?)off[2] ?? 0f);
            if (meta["materials"] is JsonNode mats)
                mesh.Materials = mats.Deserialize<List<MeshMaterialInfo>>() ?? new List<MeshMaterialInfo>();
        }
        return mesh;
    }

    private static ExchangeBlock Block(ExchangeContainer container, ExchangeObject obj, string array)
    {
        if (!obj.Arrays.TryGetValue(array, out int index) || index < 0 || index >= container.Blocks.Count)
            throw new InvalidDataException($"Mesh '{obj.Id}' is missing the '{array}' array.");
        return container.Blocks[index];
    }

}
