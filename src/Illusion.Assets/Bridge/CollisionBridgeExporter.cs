using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Bridge.Geometry;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Collisions;

namespace Illusion.Assets.Bridge;

/// <summary>
/// Turns one collision placement into a kind="collision" exchange payload: the referenced cooked hull
/// decoded to plain triangles, degenerate and duplicate faces filtered out, and per-triangle PhysX
/// surface ids resolved to material slots.
/// <para>
/// The hull rides the bridge as <b>reference</b> geometry. Many placements share one cooked blob by
/// hash, and reshaping it would require re-cooking the PhysX mesh, so only the placement itself
/// (transform) is pushable — the push path refuses shape edits rather than silently dropping them.
/// </para>
/// </summary>
public static class CollisionBridgeExporter
{
    /// <summary>Exports a placement's hull; null with a human-readable <paramref name="skipReason"/>
    /// when the referenced mesh is missing or undecodable.</summary>
    public static CollisionObjectPayload? TryExport(CollisionInstanceAdapter adapter, out string? skipReason)
    {
        skipReason = null;

        CollisionInstance instance = adapter.Instance;
        CollisionMesh? mesh = adapter.Document.MeshFor(instance.Hash);
        if (mesh?.CookedMesh is not { Length: > 0 })
        {
            skipReason = $"no cooked mesh for hash 0x{instance.Hash:X16}";
            return null;
        }

        CookedTriangleMesh decoded;
        try
        {
            decoded = CookedTriangleMesh.Decode(mesh.CookedMesh);
        }
        catch (CollisionDecodeException ex)
        {
            skipReason = "undecodable collision hull: " + ex.Message;
            return null;
        }
        if (decoded.Vertices.Length == 0 || decoded.TriangleCount == 0)
        {
            skipReason = "collision hull has no geometry";
            return null;
        }

        // A cooked hull is already position-indexed, so there is nothing to weld — but Blender's
        // validate() still strips degenerate and duplicate polygons, which would desync the per-face
        // material array. Running the shared builder with identity keys performs exactly that
        // filtering while leaving the vertex list untouched.
        var identityKeys = new ulong[decoded.Vertices.Length];
        for (int i = 0; i < identityKeys.Length; i++) identityKeys[i] = (ulong)i;

        var indices = new uint[decoded.Triangles.Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = (uint)decoded.Triangles[i];

        WeldedMesh welded = WeldMapBuilder.Build(
            identityKeys, decoded.Vertices, new Vector3[decoded.Vertices.Length], uvs: null, indices);

        (List<CollisionMaterialInfo> materials, ushort[] faceMaterials) = BuildMaterials(decoded, welded);

        Matrix4x4 world = adapter.WorldTransform;
        return new CollisionObjectPayload
        {
            Id = MakeId(adapter),
            Name = MakeName(adapter),
            World = world,
            Local = world, // parentless placement — local == world
            Positions = welded.Positions,
            LoopVertexIndices = welded.LoopVertexIndices,
            FaceMaterials = faceMaterials,
            Materials = materials,
            MeshHash = instance.Hash,
            Group = instance.Group,
            Unk4 = instance.Unk4,
            Rotation = instance.Rotation,
            DroppedDegenerateFaces = welded.DroppedDegenerateTriangles,
            DroppedDuplicateFaces = welded.DroppedDuplicateTriangles,
        };
    }

    /// <summary>Stable-within-session object id: archive-relative path + hull hash + the placement's
    /// index in the .col. Like the mesh exporter's id this is NOT stable across toolkit runs — the
    /// session controller resolves ids only through its own export map.</summary>
    public static string MakeId(CollisionInstanceAdapter adapter)
    {
        FileInfo archive = adapter.Document.SourceArchive;
        string rel = MafiaEnvironment.IsInitialized
            ? Path.GetRelativePath(MafiaEnvironment.GameRoot, archive.FullName)
            : archive.Name;
        int index = adapter.Document.Collision.Instances.IndexOf(adapter.Instance);
        return $"{rel.Replace('\\', '/')}|col|{adapter.Instance.Hash:X16}|{index}";
    }

    private static string MakeName(CollisionInstanceAdapter adapter)
    {
        int index = adapter.Document.Collision.Instances.IndexOf(adapter.Instance);
        return $"col_{adapter.Instance.Hash:X8}_{index}";
    }

    // Distinct raw PhysX ids become material slots in first-appearance order; each kept triangle then
    // carries its slot, remapped through the weld's kept-triangle list.
    private static (List<CollisionMaterialInfo>, ushort[]) BuildMaterials(
        CookedTriangleMesh decoded, WeldedMesh welded)
    {
        var materials = new List<CollisionMaterialInfo>();
        var faces = new ushort[welded.KeptTriangles.Length];

        // No stock Mafia II hull lacks the material array, but a hull without one is still valid
        // geometry — give it a single unknown slot rather than refusing the export.
        if (decoded.TriangleMaterials.Length == 0)
        {
            materials.Add(new CollisionMaterialInfo
            {
                RawId = -1,
                Name = "Unknown",
                Color = ToRgb(CollisionMaterialCatalog.UnknownColor),
            });
            return (materials, faces);
        }

        var slotByRawId = new Dictionary<ushort, ushort>();
        for (int i = 0; i < faces.Length; i++)
        {
            int sourceTriangle = welded.KeptTriangles[i];
            ushort rawId = sourceTriangle < decoded.TriangleMaterials.Length
                ? decoded.TriangleMaterials[sourceTriangle]
                : (ushort)0;

            if (!slotByRawId.TryGetValue(rawId, out ushort slot))
            {
                CollisionMaterial material = CollisionMaterialCatalog.ForRawId(rawId);
                slot = (ushort)materials.Count;
                slotByRawId[rawId] = slot;
                materials.Add(new CollisionMaterialInfo
                {
                    RawId = rawId,
                    Token = material.Token,
                    Name = material.Name,
                    Color = ToRgb(material.Color),
                });
            }
            faces[i] = slot;
        }
        return (materials, faces);
    }

    private static float[] ToRgb(Vector3 c) => new[] { c.X, c.Y, c.Z };
}
