using System.Numerics;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Import;

/// <summary>What one imported mesh becomes, decided by its name prefix.</summary>
public enum ImportKind
{
    RenderMesh,
    CollisionHull,
}

/// <summary>How one of a mesh's material slots resolved against the game.</summary>
public enum MaterialState
{
    /// <summary>A game material with this name exists — the slot binds to it.</summary>
    Found,

    /// <summary>No game material with this name — it can be created on import.</summary>
    Missing,

    /// <summary>A collision surface with this name exists in the physics catalog.</summary>
    Surface,

    /// <summary>The name matches no collision surface — the hull cannot cook with it.</summary>
    UnknownSurface,
}

/// <summary>One material slot of an import item and how it resolved.</summary>
public sealed record MaterialResolution(string Name, MaterialState State, ulong Hash, int SurfaceRawId);

/// <summary>
/// One mesh of the imported file, routed and resolved — the dialog's preview row and the payload source.
/// <see cref="Refusal"/> is set when the mesh cannot import as-is (mirrored transform, a face without a
/// material, an unknown collision surface).
/// </summary>
public sealed class ImportItem
{
    public GltfMeshInstance Source = null!;

    /// <summary>Scene-facing name: the source name with its routing prefix stripped.</summary>
    public string Name = "";

    public ImportKind Kind;
    public List<MaterialResolution> Materials = new();
    public string? Refusal;

    public bool HasMissingMaterials => Materials.Any(m => m.State == MaterialState.Missing);
}

/// <summary>
/// Turns loaded glTF meshes into the payload shapes the creation pipelines consume. Routing is by name:
/// a <c>COL_</c> prefix marks a collision hull whose material names must be physics surfaces; everything
/// else is a render mesh whose material names bind to game materials by name (a <c>MESH_</c> prefix is
/// allowed and stripped). Axes are converted glTF (+Y up) → game (+Z up) throughout — geometry AND node
/// transforms — so imported objects stand upright with clean local data.
/// </summary>
public static class ModelImport
{
    /// <summary>Placement/scaling choices from the dialog.</summary>
    /// <param name="Scale">Uniform scale baked into vertices and node translations.</param>
    /// <param name="Offset">World offset added to every item (zero = keep the file's placement).</param>
    public sealed record Options(float Scale, Vector3 Offset);

    /// <summary>Routes and resolves every loaded mesh. Never throws — problems land in per-item
    /// <see cref="ImportItem.Refusal"/> so one bad mesh does not block the rest of the file.</summary>
    public static List<ImportItem> Plan(IReadOnlyList<GltfMeshInstance> meshes)
    {
        MafiaMaterials.EnsureLoaded();
        var items = new List<ImportItem>(meshes.Count);
        foreach (GltfMeshInstance mesh in meshes)
        {
            var item = new ImportItem { Source = mesh };
            (item.Kind, item.Name) = Route(mesh.Name);
            ResolveMaterials(item);
            if (item.Refusal == null && HasMirror(mesh.World))
            {
                item.Refusal = "the node is mirrored (negative scale) — apply the mirror to the geometry "
                    + "in the DCC tool instead";
            }
            items.Add(item);
        }
        return items;
    }

    private static (ImportKind Kind, string Name) Route(string sourceName)
    {
        string name = sourceName.Trim();
        if (name.StartsWith("COL_", StringComparison.OrdinalIgnoreCase))
            return (ImportKind.CollisionHull, name[4..]);
        if (name.StartsWith("MESH_", StringComparison.OrdinalIgnoreCase))
            return (ImportKind.RenderMesh, name[5..]);
        return (ImportKind.RenderMesh, name);
    }

    private static void ResolveMaterials(ImportItem item)
    {
        foreach (GltfPrimitive primitive in item.Source.Primitives)
        {
            string? name = primitive.MaterialName;
            if (string.IsNullOrWhiteSpace(name))
            {
                item.Refusal = item.Kind == ImportKind.CollisionHull
                    ? "a face set has no material — collision faces need a surface name (concrete, wood, …)"
                    : "a face set has no material — assign named materials in the DCC tool";
                return;
            }

            if (item.Materials.Any(m => string.Equals(m.Name, name, StringComparison.Ordinal))) continue;

            if (item.Kind == ImportKind.CollisionHull)
            {
                CollisionMaterial? surface = FindSurface(name);
                if (surface is { } s)
                {
                    item.Materials.Add(new MaterialResolution(
                        name, MaterialState.Surface, 0, s.Index + CollisionMaterialCatalog.RawToTableBias));
                }
                else
                {
                    item.Materials.Add(new MaterialResolution(name, MaterialState.UnknownSurface, 0, -1));
                    item.Refusal = $"'{name}' is not a physics surface — name collision materials after "
                        + "game surfaces (e.g. " + SurfaceExamples() + ")";
                }
            }
            else
            {
                ulong hash = MafiaMaterials.FindHashByName(name) ?? 0;
                item.Materials.Add(hash != 0
                    ? new MaterialResolution(name, MaterialState.Found, hash, -1)
                    : new MaterialResolution(name, MaterialState.Missing, Fnv64.Hash(name), -1));
            }
        }
        if (item.Materials.Count == 0 && item.Refusal == null)
            item.Refusal = "the mesh has no faces";
    }

    private static CollisionMaterial? FindSurface(string name)
    {
        foreach (CollisionMaterial m in CollisionMaterialCatalog.All)
        {
            if (string.Equals(m.Token, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return m;
            }
        }
        return null;
    }

    private static string SurfaceExamples()
    {
        var tokens = new List<string>();
        foreach (CollisionMaterial m in CollisionMaterialCatalog.All)
        {
            if (m.Token == "unknown") continue;
            tokens.Add(m.Token);
            if (tokens.Count == 4) break;
        }
        return string.Join(", ", tokens);
    }

    // ── Payloads ──

    /// <summary>A render-mesh payload for an item routed <see cref="ImportKind.RenderMesh"/>. Every
    /// slot must be Found (create Missing ones first — the applier verifies hashes against the MTL
    /// libraries).</summary>
    public static MeshObjectPayload ToMeshPayload(ImportItem item, Options options)
    {
        (Vector3[] positions, uint[] loops, Vector3[] loopNormals, Vector2[] loopUvs, ushort[] faceSlots)
            = MergePrimitives(item);

        for (int i = 0; i < positions.Length; i++) positions[i] = YToZ(positions[i]) * options.Scale;
        for (int i = 0; i < loopNormals.Length; i++) loopNormals[i] = YToZ(loopNormals[i]);
        // Payloads carry Blender-convention UVs (bottom-left origin) — the applier flips V back to the
        // game's top-left, which is also glTF's convention, so the double flip round-trips exactly.
        for (int i = 0; i < loopUvs.Length; i++) loopUvs[i] = new Vector2(loopUvs[i].X, 1f - loopUvs[i].Y);

        var payload = new MeshObjectPayload
        {
            Id = "import:" + Guid.NewGuid().ToString("N")[..12],
            Name = item.Name,
            World = ConvertWorld(item.Source.World, options),
            Positions = positions,
            LoopVertexIndices = loops,
            LoopNormals = loopNormals,
            LoopUvs = loopUvs,
            LoopOrigIndex = new int[loops.Length],
            FaceMaterials = faceSlots,
        };
        Array.Fill(payload.LoopOrigIndex, -1); // every corner is new — nothing to donor-match
        foreach (MaterialResolution m in item.Materials)
            payload.Materials.Add(new MeshMaterialInfo { Hash = m.Hash.ToString("x16"), Name = m.Name });
        return payload;
    }

    /// <summary>A collision payload for an item routed <see cref="ImportKind.CollisionHull"/>. The
    /// node's scale (and the import scale) is baked into the vertices — a placement cannot carry one.</summary>
    public static CollisionObjectPayload ToCollisionPayload(ImportItem item, Options options)
    {
        (Vector3[] positions, uint[] loops, _, _, ushort[] faceSlots) = MergePrimitives(item);

        Matrix4x4 world = ConvertWorld(item.Source.World, options);
        if (!TransformMath.TryDecompose(world, out Vector3 nodeScale, out Quaternion rotation, out Vector3 position))
        {
            nodeScale = Vector3.One;
            rotation = Quaternion.Identity;
            position = world.Translation;
        }
        for (int i = 0; i < positions.Length; i++)
            positions[i] = YToZ(positions[i]) * options.Scale * nodeScale;

        var payload = new CollisionObjectPayload
        {
            Id = "import:" + Guid.NewGuid().ToString("N")[..12],
            Name = item.Name,
            World = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position),
            Positions = positions,
            LoopVertexIndices = loops,
            FaceMaterials = faceSlots,
        };
        foreach (MaterialResolution m in item.Materials)
        {
            CollisionMaterial surface = CollisionMaterialCatalog.ForRawId(m.SurfaceRawId);
            payload.Materials.Add(new CollisionMaterialInfo
            {
                RawId = m.SurfaceRawId,
                Token = surface.Token,
                Name = surface.Name,
            });
        }
        return payload;
    }

    // Concatenates a mesh's primitives into one vertex pool + per-corner loops, faces tagged with the
    // slot index of their primitive's material (slots ordered as in item.Materials).
    private static (Vector3[] Positions, uint[] Loops, Vector3[] Normals, Vector2[] Uvs, ushort[] FaceSlots)
        MergePrimitives(ImportItem item)
    {
        var positions = new List<Vector3>();
        var loops = new List<uint>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var faceSlots = new List<ushort>();

        foreach (GltfPrimitive primitive in item.Source.Primitives)
        {
            int slot = item.Materials.FindIndex(m => string.Equals(m.Name, primitive.MaterialName, StringComparison.Ordinal));
            if (slot < 0) slot = 0; // unreachable after a clean Plan; a defensive default, not a guess with consequences
            int baseVertex = positions.Count;
            positions.AddRange(primitive.Positions);
            for (int i = 0; i + 2 < primitive.Indices.Length; i += 3)
            {
                for (int c = 0; c < 3; c++)
                {
                    uint v = primitive.Indices[i + c];
                    loops.Add((uint)(baseVertex + v));
                    normals.Add(primitive.Normals[v]);
                    uvs.Add(primitive.Uvs[v]);
                }
                faceSlots.Add((ushort)slot);
            }
        }
        return (positions.ToArray(), loops.ToArray(), normals.ToArray(), uvs.ToArray(), faceSlots.ToArray());
    }

    /// <summary>Centroid of the items' world positions in GAME axes at the given scale — the dialog
    /// subtracts it from the camera drop point so a multi-mesh file lands centred, placement preserved.</summary>
    public static Vector3 PlacementCenter(IEnumerable<ImportItem> items, float scale)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (ImportItem item in items)
        {
            sum += YToZ(item.Source.World.Translation) * scale;
            count++;
        }
        return count == 0 ? Vector3.Zero : sum / count;
    }

    // World conversion glTF → game: rotate the whole space +90° about X. Conjugating (not just
    // right-multiplying) keeps each object's LOCAL data upright too — the vertices are converted with
    // the same rotation, so world = local-verts × matrix stays consistent and nothing lies on its side.
    private static Matrix4x4 ConvertWorld(Matrix4x4 world, Options options)
    {
        Matrix4x4 converted = Transpose(AxisRotation) * world * AxisRotation;
        converted.M41 *= options.Scale;
        converted.M42 *= options.Scale;
        converted.M43 *= options.Scale;
        converted.Translation += options.Offset;
        return converted;
    }

    private static readonly Matrix4x4 AxisRotation = new(
        1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, -1f, 0f, 0f,
        0f, 0f, 0f, 1f);

    private static Matrix4x4 Transpose(Matrix4x4 m) => Matrix4x4.Transpose(m);

    private static bool HasMirror(Matrix4x4 world) =>
        TransformMath.TryDecompose(world, out Vector3 scale, out _, out _)
        && (scale.X < 0f || scale.Y < 0f || scale.Z < 0f);

    // +90° about X: (x, y, z) → (x, −z, y) — glTF's +Y up becomes the game's +Z up.
    private static Vector3 YToZ(Vector3 v) => new(v.X, -v.Z, v.Y);
}
