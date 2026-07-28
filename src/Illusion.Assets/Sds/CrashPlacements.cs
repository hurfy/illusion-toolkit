using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Translokator;

namespace Illusion.Assets.Sds;

/// <summary>
/// The link between a city_crash archive's two halves: the frame resource holds the prop prototypes, the
/// Translokator table says where the copies stand. Loading collapses that into flat matrix arrays for hardware
/// instancing, which loses track of which copy produced which matrix — this keeps the correspondence, so a
/// placement can be selected, moved and deleted, and the affected prototype's matrices rebuilt afterwards.
/// </summary>
public sealed class CrashPlacements
{
    // Prototype mesh -> the table rows that instance it, each with that mesh's transform inside the prototype.
    private readonly Dictionary<FrameObjectSingleMesh, List<(Formats.Translokator.Object Row, Matrix4x4 Local)>> _byMesh = new();

    // The reverse direction: which prototype meshes a row draws — what an edit to that row has to refresh.
    private readonly Dictionary<Formats.Translokator.Object, List<FrameObjectSingleMesh>> _byRow = new();

    private CrashPlacements(TranslokatorDocumentAdapter document) => Document = document;

    /// <summary>The placement table's save unit — the document an edited copy belongs to.</summary>
    public TranslokatorDocumentAdapter Document { get; }

    /// <summary>The table rows that actually resolve to prototype geometry, in table order — what the tree layer
    /// lists and what the "place a new one" picker offers.</summary>
    public IReadOnlyList<Formats.Translokator.Object> Rows { get; private set; } = [];

    /// <summary>Every prototype mesh the table instances — the meshes whose GPU copies an edit can invalidate.</summary>
    public IEnumerable<FrameObjectSingleMesh> Meshes => _byMesh.Keys;

    /// <summary>The prototype meshes a row draws, i.e. what to refresh after one of its copies changed.</summary>
    public IReadOnlyList<FrameObjectSingleMesh> MeshesOf(Formats.Translokator.Object row) =>
        _byRow.TryGetValue(row, out List<FrameObjectSingleMesh>? meshes) ? meshes : [];

    /// <summary>
    /// The world matrices every copy currently puts this prototype mesh at — recomputed from the live table, so
    /// it reflects edits made since the load. Same composition the loader used: the mesh's transform inside its
    /// prototype, then the copy's own placement.
    /// </summary>
    public Matrix4x4[] MatricesFor(FrameObjectSingleMesh mesh)
    {
        if (!_byMesh.TryGetValue(mesh, out List<(Formats.Translokator.Object Row, Matrix4x4 Local)>? uses))
        {
            return [];
        }

        var matrices = new List<Matrix4x4>();
        foreach ((Formats.Translokator.Object row, Matrix4x4 local) in uses)
        {
            foreach (Instance copy in row.Instances)
            {
                matrices.Add(local * TransformMath.Compose(copy.Quaternion, new Vector3(copy.Scale), copy.Position));
            }
        }
        return matrices.ToArray();
    }

    /// <summary>Where this mesh sits inside the row's prototype — the left half of a copy's world matrix.
    /// Identity when the row does not draw the mesh at all.</summary>
    public Matrix4x4 LocalOf(FrameObjectSingleMesh mesh, Formats.Translokator.Object row)
    {
        if (_byMesh.TryGetValue(mesh, out List<(Formats.Translokator.Object Row, Matrix4x4 Local)>? uses))
        {
            foreach ((Formats.Translokator.Object candidate, Matrix4x4 local) in uses)
            {
                if (ReferenceEquals(candidate, row)) return local;
            }
        }
        return Matrix4x4.Identity;
    }

    /// <summary>The prototype-mesh → copy-matrices map the mesh loader needs to mark meshes as instanced.</summary>
    public Dictionary<FrameObjectSingleMesh, Matrix4x4[]> BuildMatrixMap()
    {
        var map = new Dictionary<FrameObjectSingleMesh, Matrix4x4[]>(_byMesh.Count);
        foreach (FrameObjectSingleMesh mesh in _byMesh.Keys) map[mesh] = MatricesFor(mesh);
        return map;
    }

    /// <summary>
    /// Resolves every table row against the frame resource: a row names a prototype by hash, and the prototype's
    /// mesh children are what a copy actually draws. Rows whose prototype is missing or carries no mesh are
    /// dropped, exactly as the spawn path always did.
    /// </summary>
    public static CrashPlacements Build(FrameResource frames, TranslokatorDocumentAdapter document)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(document);

        var result = new CrashPlacements(document);
        var rows = new List<Formats.Translokator.Object>();

        foreach (ObjectGroup group in document.Table.ObjectGroups)
        {
            foreach (Formats.Translokator.Object row in group.Objects)
            {
                FrameObjectBase? prototype = frames.GetObjectByHash<FrameObjectBase>(row.Name.Hash);
                if (prototype == null || !prototype.HasMeshObject()) continue;

                var parts = new List<(FrameObjectSingleMesh Mesh, Matrix4x4 Local)>();
                foreach (FrameObjectBase child in prototype.Children) CollectParts(child, Matrix4x4.Identity, parts);
                if (parts.Count == 0) continue;

                rows.Add(row);
                var meshes = new List<FrameObjectSingleMesh>(parts.Count);
                foreach ((FrameObjectSingleMesh mesh, Matrix4x4 local) in parts)
                {
                    if (!result._byMesh.TryGetValue(mesh, out List<(Formats.Translokator.Object, Matrix4x4)>? uses))
                    {
                        result._byMesh[mesh] = uses = [];
                    }
                    uses.Add((row, local));
                    if (!meshes.Contains(mesh)) meshes.Add(mesh);
                }
                result._byRow[row] = meshes;
            }
        }

        result.Rows = rows;
        return result;
    }

    // Collects prototype meshes with their transform relative to the prototype root.
    private static void CollectParts(FrameObjectBase frame, Matrix4x4 parent,
        List<(FrameObjectSingleMesh Mesh, Matrix4x4 Local)> parts)
    {
        Matrix4x4 local = TransformMath.ComputeWorldTransform(frame.LocalTransform, parent);
        local.M44 = 1.0f;
        if (frame is FrameObjectSingleMesh sm && sm.Geometry != null) parts.Add((sm, local));
        foreach (FrameObjectBase child in frame.Children) CollectParts(child, local, parts);
    }
}
