using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using static Illusion.Bridge.Payload.ExchangeMarshal;

namespace Illusion.Bridge.Payload;

/// <summary>One surface-material slot of a collision payload. Deliberately NOT
/// <see cref="MeshMaterialInfo"/>: a collision material is a PhysX surface id resolved through
/// <c>CollisionMaterialCatalog</c> to a token and an overlay colour — it has no textures, and it is
/// assigned per triangle rather than over an index range.</summary>
public sealed class CollisionMaterialInfo
{
    /// <summary>Raw PhysX slot id as stored per triangle in the cooked mesh.</summary>
    [JsonPropertyName("rawId")] public int RawId { get; set; }

    /// <summary>The game's own spelling from <c>MaterialsPhysics.tbl</c>, when known.</summary>
    [JsonPropertyName("token")] public string? Token { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary>Overlay colour as [r, g, b] in 0..1 — the addon tints the reference material with it.</summary>
    [JsonPropertyName("color")] public float[]? Color { get; set; }
}

/// <summary>
/// Typed view of one kind="collision" exchange object: the decoded hull of a single placement.
/// Deliberately narrower than <see cref="MeshObjectPayload"/> — a cooked collision mesh has no
/// normals, no UVs and no split vertices, so those channels are absent rather than empty. The hull
/// travels as reference geometry: its shape cannot be pushed back without re-cooking the PhysX blob,
/// so <see cref="MeshHash"/> and the placement fields are what a push may legitimately change.
/// </summary>
public sealed class CollisionObjectPayload
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }

    /// <summary>Placement world matrix. A collision instance is parentless, so local == world.</summary>
    public Matrix4x4 World { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 Local { get; set; } = Matrix4x4.Identity;

    /// <summary>Hull vertices in the mesh's local space, Mafia axes, metres — verbatim from the
    /// cooked blob (no quantization, no decompression).</summary>
    public Vector3[] Positions { get; set; } = Array.Empty<Vector3>();

    /// <summary>Vertex index per loop, three per triangle.</summary>
    public uint[] LoopVertexIndices { get; set; } = Array.Empty<uint>();

    /// <summary>Material slot per kept triangle — an index into <see cref="Materials"/>, not a raw
    /// PhysX id, so the addon can assign it straight to a Blender material slot.</summary>
    public ushort[] FaceMaterials { get; set; } = Array.Empty<ushort>();

    public List<CollisionMaterialInfo> Materials { get; set; } = new();

    /// <summary>FNV64 hash of the cooked hull this placement references.</summary>
    public ulong MeshHash { get; set; }

    /// <summary>Per-placement group byte (meaning unknown; stock data uses a small value set).</summary>
    public int Group { get; set; }

    /// <summary>Index of the frame object this placement belongs to, or -1 for none.</summary>
    public int Unk4 { get; set; } = -1;

    /// <summary>The placement's authored .col Euler triple, carried so a push that did not touch the
    /// orientation can be detected and the authored spelling left byte-identical.</summary>
    public Vector3 Rotation { get; set; }

    /// <summary>Hull triangles omitted because they are degenerate (Blender rejects polygons with a
    /// repeated vertex, and its validate() would desync the per-face material array).</summary>
    public int DroppedDegenerateFaces { get; set; }

    /// <summary>Hull triangles omitted because a kept face already covers the same vertex set.</summary>
    public int DroppedDuplicateFaces { get; set; }
}

/// <summary>Converts <see cref="CollisionObjectPayload"/> to and from the generic container encoding —
/// the collision counterpart of <see cref="MeshPayloadCodec"/>.</summary>
public static class CollisionPayloadCodec
{
    /// <param name="container">The exchange container the hull is added to.</param>
    /// <param name="hull">The collision hull to publish.</param>
    /// <param name="canCookShapes">Whether this machine can re-cook a reshaped hull — decides whether the
    /// addon is told Edit Mode work will be applied or ignored. Defaults to false so a caller that does not
    /// know keeps the cautious advice.</param>
    public static void Add(ExchangeContainer container, CollisionObjectPayload hull, bool canCookShapes = false)
    {
        var obj = new ExchangeObject
        {
            // Explicit: ExchangeObject defaults to KindMesh, and a collision object that inherits
            // that default is silently fed to the mesh path on the way back.
            Kind = ExchangeSchema.KindCollision,
            Id = hull.Id,
            Name = hull.Name,
            ParentId = hull.ParentId,
            World = ToFloats(hull.World),
            Local = ToFloats(hull.Local),
            Meta = new JsonObject
            {
                // u64 does not survive JSON number handling intact — carry the hash as hex text.
                ["meshHash"] = "0x" + hull.MeshHash.ToString("X16"),
                ["group"] = hull.Group,
                ["unk4"] = hull.Unk4,
                ["rotation"] = new JsonArray(hull.Rotation.X, hull.Rotation.Y, hull.Rotation.Z),
                ["droppedDegenerateFaces"] = hull.DroppedDegenerateFaces,
                ["droppedDuplicateFaces"] = hull.DroppedDuplicateFaces,
                // Advisory for the addon; the authority is the C# push path. Shape edits ARE applied now, but
                // only where the PhysX cooker can run, so this reflects what this machine can actually do —
                // telling a modder their Edit Mode work is unsupported when it is about to be cooked would be
                // its own kind of wrong.
                ["geometryReadOnly"] = !canCookShapes,
                ["geometryNote"] = canCookShapes
                    ? "Edit Mode changes are re-cooked on push"
                    : "collision shape edits need NVIDIA PhysX System Software, which is not installed here",
                ["materials"] = JsonSerializer.SerializeToNode(hull.Materials),
            },
        };

        obj.Arrays[ExchangeSchema.ArrayPositions] = container.AddBlock(
            ExchangeSchema.DtypeF32, 3, hull.Positions.Length, ToBytes(hull.Positions));
        obj.Arrays[ExchangeSchema.ArrayIndices] = container.AddBlock(
            ExchangeSchema.DtypeU32, 1, hull.LoopVertexIndices.Length, ToBytes(hull.LoopVertexIndices));
        obj.Arrays[ExchangeSchema.ArrayFaceMaterials] = container.AddBlock(
            ExchangeSchema.DtypeU16, 1, hull.FaceMaterials.Length, ToBytes(hull.FaceMaterials));

        container.Objects.Add(obj);
    }

    /// <summary>Materializes a kind="collision" object. Only the geometry arrays are required:
    /// faceMaterials is optional, because a transform-only push has no reason to send it back and
    /// demanding it would reject every such push.</summary>
    public static CollisionObjectPayload Read(ExchangeContainer container, ExchangeObject obj)
    {
        if (obj.Kind != ExchangeSchema.KindCollision)
            throw new InvalidDataException($"Object '{obj.Id}' is kind '{obj.Kind}', not a collision hull.");

        var hull = new CollisionObjectPayload
        {
            Id = obj.Id,
            Name = obj.Name,
            ParentId = obj.ParentId,
            World = FromFloats(obj.World),
            Local = FromFloats(obj.Local),
            Positions = FromBytes<Vector3>(RequiredBlock(container, obj, ExchangeSchema.ArrayPositions)),
            LoopVertexIndices = FromBytes<uint>(RequiredBlock(container, obj, ExchangeSchema.ArrayIndices)),
            FaceMaterials = OptionalBlock(container, obj, ExchangeSchema.ArrayFaceMaterials) is { } faces
                ? FromBytes<ushort>(faces)
                : Array.Empty<ushort>(),
        };

        if (obj.Meta is JsonObject meta)
        {
            if ((string?)meta["meshHash"] is { } hex)
            {
                ReadOnlySpan<char> digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? hex.AsSpan(2)
                    : hex.AsSpan();
                if (ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h))
                    hull.MeshHash = h;
            }
            hull.Group = (int?)meta["group"] ?? 0;
            hull.Unk4 = (int?)meta["unk4"] ?? -1;
            hull.DroppedDegenerateFaces = (int?)meta["droppedDegenerateFaces"] ?? 0;
            hull.DroppedDuplicateFaces = (int?)meta["droppedDuplicateFaces"] ?? 0;
            if (meta["rotation"] is JsonArray rot && rot.Count == 3)
                hull.Rotation = new Vector3((float?)rot[0] ?? 0f, (float?)rot[1] ?? 0f, (float?)rot[2] ?? 0f);
            if (meta["materials"] is JsonNode mats)
                hull.Materials = mats.Deserialize<List<CollisionMaterialInfo>>() ?? new List<CollisionMaterialInfo>();
        }
        return hull;
    }

    /// <summary>Reads just the placement's world matrix, without touching the geometry blocks.
    /// A collision push is transform-only, and this way it survives an addon build that echoes the
    /// wrong <c>kind</c> or omits arrays it was never asked to change.</summary>
    public static Matrix4x4 ReadWorld(ExchangeObject obj) => FromFloats(obj.World);

    private static ExchangeBlock RequiredBlock(ExchangeContainer container, ExchangeObject obj, string array) =>
        OptionalBlock(container, obj, array)
        ?? throw new InvalidDataException($"Collision hull '{obj.Id}' is missing the '{array}' array.");

    private static ExchangeBlock? OptionalBlock(ExchangeContainer container, ExchangeObject obj, string array) =>
        obj.Arrays.TryGetValue(array, out int index) && index >= 0 && index < container.Blocks.Count
            ? container.Blocks[index]
            : null;
}
