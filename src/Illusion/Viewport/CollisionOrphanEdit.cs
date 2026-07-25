using Illusion.Assets.Adapters;
using Illusion.Assets.Collisions;
using Illusion.Formats.Collisions;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Removes the hulls no placement references any more, as one undoable step.
/// <para>
/// Deleting a placement never takes its hull with it, and resizing one leaves the original behind — the hull may
/// well be placed elsewhere, and an edit that guessed would be the one edit a modder cannot undo their way out
/// of. So unplaced hulls accumulate until this is run deliberately. Shipped data has none at all (measured
/// across all 141 files), so everything this removes is something the toolkit orphaned.
/// </para>
/// <para>
/// Undo restores the exact list: each hull is captured with the index it sat at, removed high-to-low so the
/// remaining indices stay valid, and re-inserted low-to-high. That is what makes an edit-then-undo produce a
/// byte-identical .col rather than a reordered one.
/// </para>
/// </summary>
internal sealed class CollisionOrphanEdit : INodeEdit
{
    private readonly ICollisionEditSink _sink;
    private readonly CollisionDocumentAdapter _doc;
    private readonly SceneNode _layer;
    private readonly (CollisionMesh Mesh, int Index)[] _removed;

    private CollisionOrphanEdit(ICollisionEditSink sink, CollisionDocumentAdapter doc, SceneNode layer,
        (CollisionMesh, int)[] removed)
    {
        _sink = sink;
        _doc = doc;
        _layer = layer;
        _removed = removed;
    }

    /// <summary>The hulls this edit would remove — the count the menu item shows.</summary>
    public int Count => _removed.Length;

    /// <summary>
    /// Builds the sweep for a collision layer, or null when nothing is unplaced. Nothing is applied: the caller
    /// redoes it and pushes it, like every other edit here.
    /// </summary>
    public static CollisionOrphanEdit? Build(ICollisionEditSink sink, CollisionDocumentAdapter doc, SceneNode layer)
    {
        var orphans = new List<(CollisionMesh, int)>();
        for (int i = 0; i < doc.Collision.Meshes.Count; i++)
        {
            CollisionMesh mesh = doc.Collision.Meshes[i];
            if (CollisionMeshMinter.IsOrphan(doc.Collision, mesh.Hash)) orphans.Add((mesh, i));
        }
        return orphans.Count == 0 ? null : new CollisionOrphanEdit(sink, doc, layer, orphans.ToArray());
    }

    public IEnumerable<SceneNode> Nodes { get { yield return _layer; } }

    public void Redo()
    {
        // Descending, so each removal cannot shift an index still to be removed.
        for (int i = _removed.Length - 1; i >= 0; i--)
        {
            _doc.Collision.Meshes.Remove(_removed[i].Mesh);
        }
        Finish();
    }

    public void Undo()
    {
        // Ascending, so each hull lands back in the slot it was captured from.
        foreach ((CollisionMesh mesh, int index) in _removed)
        {
            _doc.Collision.Meshes.Insert(Math.Min(index, _doc.Collision.Meshes.Count), mesh);
        }
        Finish();
    }

    private void Finish()
    {
        // Removal leaves the index resolving hulls the file no longer has — the stale-POSITIVE direction, which
        // the property panel's dangling-hash guard would otherwise trust.
        _doc.InvalidateMeshIndex();
        _doc.RenderDirty = true;
        _sink.Enlist(_layer);
        _sink.Refresh();
    }
}
