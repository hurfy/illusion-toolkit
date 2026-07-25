using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Collisions;
using Illusion.Formats.Collisions;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// What a collision edit needs from the app around it. Kept to these two calls so the edits can be driven
/// headlessly by the probes — the seam that matters (which bytes change, and in what order undo puts them back)
/// is then the real code under test, not a copy of it written into a probe.
/// </summary>
internal interface ICollisionEditSink
{
    /// <summary>Enlists the .col behind this layer for save/build.</summary>
    void Enlist(SceneNode layer);

    /// <summary>The edit changed something the property fields are showing.</summary>
    void Refresh();
}

/// <summary>
/// Turns a previewed hull resize into something the file can carry: adds the derived hull to the .col and
/// repoints the placement at it, as one undoable step.
/// <para>
/// A <c>CollisionInstance</c> has no scale field, so "this hull, bigger" can only be said as "a different hull".
/// The gizmo parks the factor on <see cref="CollisionInstanceAdapter.PreviewScale"/> during the drag — session
/// state the file never sees — and this edit is what makes it real at drag-end. The preview is reset to identity
/// on the way, because the minted hull now carries the size itself; leaving it would apply the scale twice.
/// </para>
/// <para>
/// The undo order is load-bearing: the placement is repointed back FIRST and only then is the minted hull
/// collected. Reversed, the .col would briefly hold a placement naming a hull that is gone — and nothing would
/// catch it, because the document's hash→mesh index still resolves removed hulls until it is invalidated.
/// </para>
/// </summary>
internal sealed class CollisionMintEdit : INodeEdit
{
    private readonly ICollisionEditSink _sink;
    private readonly CollisionDocumentAdapter _doc;
    private readonly SceneNode _layer;
    private readonly SceneNode _node;
    private readonly CollisionInstanceAdapter _adapter;
    private readonly ulong _oldHash;
    private readonly ulong _newHash;
    private readonly CollisionMesh? _added;
    private readonly Vector3 _oldPreviewScale;

    public CollisionMintEdit(ICollisionEditSink sink, CollisionDocumentAdapter doc, SceneNode layer, SceneNode node,
        CollisionInstanceAdapter adapter, ulong oldHash, ulong newHash, CollisionMesh? added, Vector3 oldPreviewScale)
    {
        _sink = sink;
        _doc = doc;
        _layer = layer;
        _node = node;
        _adapter = adapter;
        _oldHash = oldHash;
        _newHash = newHash;
        _added = added;
        _oldPreviewScale = oldPreviewScale;
    }

    public IEnumerable<SceneNode> Nodes { get { yield return _node; } }

    public void Redo()
    {
        if (_added != null) InsertByHash(_doc.Collision.Meshes, _added);
        _adapter.Instance.Hash = _newHash;
        _adapter.PreviewScale = Vector3.One;
        Finish();
    }

    public void Undo()
    {
        _adapter.Instance.Hash = _oldHash;
        _adapter.PreviewScale = _oldPreviewScale;
        // Only the edit that ADDED the hull collects it, and only once nothing points at it: a group drag that
        // scales two placements of the same hull mints once and repoints twice, so the second placement's undo
        // must leave the mesh alone for the first one to take back out.
        if (_added != null && CollisionMeshMinter.IsOrphan(_doc.Collision, _newHash))
        {
            CollisionMeshMinter.RemoveMesh(_doc.Collision, _newHash);
        }
        Finish();
    }

    private void Finish()
    {
        _doc.InvalidateMeshIndex();   // the mesh set changed — in BOTH directions
        _doc.RenderDirty = true;      // a bare hash repoint repaints nothing on its own
        _sink.Enlist(_layer);
        _sink.Refresh();
    }

    /// <summary>
    /// Inserts a hull keeping the mesh list hash-ascending — the order every shipped .col is in (measured over
    /// all 141 files; the old toolkit wrote a sorted dictionary). Whether the game requires it is unknown, so
    /// this is cheap insurance rather than a proven constraint. Removal is by hash and disturbs nothing else,
    /// so an edit-then-undo restores the original list exactly either way.
    /// </summary>
    private static void InsertByHash(List<CollisionMesh> meshes, CollisionMesh mesh)
    {
        int i = 0;
        while (i < meshes.Count && meshes[i].Hash <= mesh.Hash) i++;
        meshes.Insert(i, mesh);
    }
}
