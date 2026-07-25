using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Collisions;

namespace Illusion.Assets.Adapters;

/// <summary>
/// Adapts one loaded <see cref="CollisionFile"/> (a district's streamed Collisions .col, plus its source archive)
/// into the Domain's <see cref="ISceneDocument"/> save unit — the collision analog of
/// <see cref="SceneDocumentAdapter"/>. Slotting into <c>ISceneDocument</c> is what makes collision edits ride the
/// exact same Save/Build/backup pipeline as frame edits: <c>ScenePersistence</c> enlists this document when a
/// child instance node is edited, and its <see cref="SaveWorkingCopy"/> rewrites the .col in the extracted folder
/// via <see cref="SdsCollisionSaver"/> (cooked blobs verbatim). Unlike a FrameResource this document owns no
/// name table, geometry pools, materials or skeletons, so those channels are zero / no-ops.
/// </summary>
public sealed class CollisionDocumentAdapter : ISceneDocument
{
    private readonly CollisionFile _collision;
    private readonly Dictionary<CollisionInstance, CollisionInstanceAdapter> _nodes = new();
    private Dictionary<ulong, CollisionMesh>? _meshByHash;

    public CollisionDocumentAdapter(CollisionFile collision, FileInfo sourceArchive)
    {
        _collision = collision;
        SourceArchive = sourceArchive;
    }

    public FileInfo SourceArchive { get; }

    /// <summary>The wrapped collision resource — the placement list that instance edits mutate and
    /// <see cref="SaveWorkingCopy"/> serializes.</summary>
    public CollisionFile Collision => _collision;

    /// <summary>Set when a placement's transform changed so the hull overlay is stale; the streamer consumes it
    /// once per frame to re-upload the instance matrices (live during a gizmo drag). Not persistence — that is
    /// tracked separately by ScenePersistence.</summary>
    public bool RenderDirty { get; set; }

    public int ObjectCount => _collision.Instances.Count;
    public int GeometryCount => _collision.Meshes.Count;
    public int MaterialCount => 0;
    public int SkeletonCount => 0;
    public int SceneCount => 0;

    /// <summary>Resolves the cooked collision mesh an instance references (by FNV64 hash), or null if absent.
    /// Lazily builds a hash→mesh index on first use.</summary>
    public CollisionMesh? MeshFor(ulong hash)
    {
        if (_meshByHash == null)
        {
            _meshByHash = new Dictionary<ulong, CollisionMesh>(_collision.Meshes.Count);
            foreach (CollisionMesh m in _collision.Meshes) _meshByHash[m.Hash] = m;
        }
        return _meshByHash.TryGetValue(hash, out CollisionMesh? mesh) ? mesh : null;
    }

    /// <summary>
    /// Drops the hash→mesh index so the next <see cref="MeshFor"/> rebuilds it. Every edit that adds or removes
    /// a mesh must call this, in BOTH directions: after an append the index is stale-negative (a freshly minted
    /// hull is invisible, which would make the bridge exporter refuse it and the property panel treat its hash as
    /// dangling), and after a removal it is stale-POSITIVE — <see cref="MeshFor"/> would keep resolving a hull the
    /// file no longer carries, so the dangling-hash guard in the property panel would wave through a hash that
    /// dangles on disk.
    /// </summary>
    public void InvalidateMeshIndex() => _meshByHash = null;

    /// <summary>Rewrites the district's .col in its extracted folder from the current instance list (the cooked
    /// PhysX blobs are re-emitted verbatim). Packing back to the .sds is a separate step
    /// (<c>ScenePersistence.BuildEdits</c> → <c>SdsWriter.PackSds</c>), shared with frame edits.</summary>
    public string SaveWorkingCopy() => SdsCollisionSaver.SaveWorkingCopy(_collision, SourceArchive);

    /// <summary>Collision carries no FrameNameTable — nothing to flag.</summary>
    public void MarkNameTableDirty() { }

    /// <summary>Collision instances are a flat placement list with no parent hierarchy — they never reparent.</summary>
    public bool Reparent(IFrameNode child, ISceneSource? newParent) => false;

    /// <summary>The session-only scale a placement is currently shown at (see
    /// <see cref="CollisionInstanceAdapter.PreviewScale"/>). Identity for a placement never scaled — including one
    /// with no adapter yet, since an unselected placement cannot have been dragged.</summary>
    public Vector3 ScaleOf(CollisionInstance instance) =>
        _nodes.TryGetValue(instance, out CollisionInstanceAdapter? node) ? node.PreviewScale : Vector3.One;

    /// <summary>The canonical <see cref="CollisionInstanceAdapter"/> for a placement of this document — one
    /// adapter per instance (cached by reference), so selection/edit key by identity like frame objects do.</summary>
    public CollisionInstanceAdapter Node(CollisionInstance instance)
    {
        if (!_nodes.TryGetValue(instance, out CollisionInstanceAdapter? node))
        {
            _nodes[instance] = node = new CollisionInstanceAdapter(instance, this);
        }
        return node;
    }
}
