using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Rendering.Gpu;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Actor editing of the viewport: deleting selected actors as one undoable edit. Actors do not go through the
/// frame path — they have no frame object of their own, and their geometry hangs in the FrameResource branch
/// as a prototype the actor merely places. Deleting one drops its record from the .act pack and hides that
/// geometry; the prototype frame itself stays, which is also what the game does with a prototype nothing
/// spawns.
/// </summary>
internal sealed class ActorEditController
{
    private readonly D3DImageHost _host;

    public ActorEditController(D3DImageHost host) => _host = host;

    /// <summary>Whether the selection contains an actor this controller can delete.</summary>
    public bool HasActorSelection() => SelectedActors().Count > 0;

    /// <summary>Deletes the selected actors as ONE undoable edit.</summary>
    public void DeleteSelected()
    {
        IReadOnlyList<SceneNode> nodes = SelectedActors();
        if (nodes.Count == 0) return;

        var items = new List<DeletedActor>(nodes.Count);
        foreach (SceneNode node in nodes)
        {
            if (node.Source is not ActorNodeAdapter adapter) continue;
            if (node.OwningDocumentNode()?.Source is not ActorDocumentAdapter document) continue;
            ActorPlacements placements = document.Placements;
            if (placements.PackOf(adapter.Actor) is not { } pack) continue;

            items.Add(new DeletedActor
            {
                Node = node,
                Adapter = adapter,
                Document = document,
                Pack = pack,
                Parent = node.Parent,
                TreeIndex = node.Parent?.Children.IndexOf(node) ?? -1,
                PlacementIndex = IndexIn(placements.All, adapter.Actor),
                HadGlyph = placements.HasGlyph(adapter.Actor),
            });
        }
        if (items.Count == 0) return;

        var edit = new DeleteActorsEdit(this, items);
        edit.Redo();
        _host.History.Push(edit);
    }

    // ── Duplicate (undoable) ──

    /// <summary>Whether the selection contains an actor that can be copied.</summary>
    public bool HasDuplicableSelection() => SelectedActors().Count > 0;

    /// <summary>Copies the selected actors as ONE undoable edit. An actor that places a scene object is
    /// skipped with a notice — that copy needs its own clone of the object first.</summary>
    public void DuplicateSelected()
    {
        IReadOnlyList<SceneNode> nodes = SelectedActors();
        if (nodes.Count == 0) return;

        var items = new List<CopiedActor>(nodes.Count);
        string? lastReason = null;
        int skipped = 0;

        foreach (SceneNode node in nodes)
        {
            if (node.Source is not ActorNodeAdapter adapter) continue;
            if (node.OwningDocumentNode()?.Source is not ActorDocumentAdapter document) continue;
            if (document.Placements.PackOf(adapter.Actor) is not { } pack) continue;

            ActorEntry? copy = pack.Duplicate(adapter.Actor, out string? reason);
            if (copy == null)
            {
                skipped++;
                lastReason = reason;
                continue;
            }

            ActorNodeAdapter copyAdapter = document.ActorNode(copy);
            var copyNode = new SceneNode(copyAdapter.Name, "Actor", false) { Source = copyAdapter };

            items.Add(new CopiedActor
            {
                Source = adapter.Actor,
                Copy = copy,
                Node = copyNode,
                Parent = node.Parent,
                TreeIndex = (node.Parent?.Children.IndexOf(node) ?? -1) + 1,
                Document = document,
                Pack = pack,
            });
        }

        if (items.Count > 0)
        {
            var edit = new DuplicateActorsEdit(this, items);
            edit.Redo();
            _host.History.Push(edit);
        }
        if (skipped > 0)
        {
            _host.RaiseNotice($"{skipped} actor(s) not copied — {lastReason}", isError: false);
        }
    }

    private sealed class CopiedActor
    {
        public required ActorEntry Source;
        public required ActorEntry Copy;
        public required SceneNode Node;
        public required SceneNode? Parent;
        public required int TreeIndex;
        public required ActorDocumentAdapter Document;
        public required ActorsFile Pack;
        public bool Applied;
    }

    private sealed class DuplicateActorsEdit : INodeEdit
    {
        private readonly ActorEditController _owner;
        private readonly List<CopiedActor> _items;

        public DuplicateActorsEdit(ActorEditController owner, List<CopiedActor> items)
        {
            _owner = owner;
            _items = items;
        }

        public IEnumerable<SceneNode> Nodes
        {
            get { foreach (CopiedActor item in _items) yield return item.Node; }
        }

        public void Redo()
        {
            var selection = new List<SceneNode>(_items.Count);
            foreach (CopiedActor item in _items)
            {
                // Redo after an undo has to put the record back into the pack as well as the row into the tree.
                if (!item.Applied && !item.Pack.Actors.Contains(item.Copy))
                {
                    item.Pack.Duplicate(item.Source, out _); // fresh copy is not reused: the row must be rebuilt
                }
                item.Document.Placements.AddCopy(item.Copy, item.Source, item.Pack);
                if (item.Parent != null)
                {
                    int at = Math.Clamp(item.TreeIndex, 0, item.Parent.Children.Count);
                    item.Parent.Children.Insert(at, item.Node);
                    _owner._host.Persistence.MarkFrameModified(item.Parent);
                }
                item.Applied = true;
                selection.Add(item.Node);
            }
            _owner.AfterChange(selection);
        }

        public void Undo()
        {
            foreach (CopiedActor item in _items)
            {
                item.Pack.RemoveCopy(item.Copy);
                item.Document.Placements.Detach(item.Copy);
                item.Parent?.Children.Remove(item.Node);
                if (item.Parent != null) _owner._host.Persistence.MarkFrameModified(item.Parent);
                item.Applied = false;
            }
            _owner.AfterChange(Array.Empty<SceneNode>());
        }
    }

    private static int IndexIn(IReadOnlyList<ActorEntry> list, ActorEntry actor)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], actor)) return i;
        return list.Count;
    }

    private List<SceneNode> SelectedActors()
    {
        var list = new List<SceneNode>();
        foreach (SceneNode n in _host.Selection.Selected)
            if (n.Source is ActorNodeAdapter) list.Add(n);
        return list;
    }

    // One deleted actor and everything needed to put it back.
    private sealed class DeletedActor
    {
        public required SceneNode Node;
        public required ActorNodeAdapter Adapter;
        public required ActorDocumentAdapter Document;
        public required ActorsFile Pack;
        public required SceneNode? Parent;
        public required int TreeIndex;
        public required int PlacementIndex;
        public required bool HadGlyph;

        public ActorRemoval? Removal;          // what the pack gave back, for the restore
        public FrameObjectBase? PlacedFrame;   // the prototype it used to place (its geometry gets hidden)
    }

    private sealed class DeleteActorsEdit : INodeEdit
    {
        private readonly ActorEditController _owner;
        private readonly List<DeletedActor> _items;

        public DeleteActorsEdit(ActorEditController owner, List<DeletedActor> items)
        {
            _owner = owner;
            _items = items;
        }

        public IEnumerable<SceneNode> Nodes
        {
            get { foreach (DeletedActor item in _items) yield return item.Node; }
        }

        public void Redo()
        {
            foreach (DeletedActor item in _items)
            {
                item.Removal = item.Pack.Remove(item.Adapter.Actor);
                item.PlacedFrame = item.Document.Placements.Detach(item.Adapter.Actor);
                _owner.SetSubtreeVisible(item.PlacedFrame, false);
                item.Parent?.Children.Remove(item.Node);
                _owner._host.Persistence.MarkFrameModified(item.Parent ?? item.Node);
            }
            _owner.AfterChange();
        }

        public void Undo()
        {
            foreach (DeletedActor item in _items)
            {
                if (item.Removal == null) continue;
                item.Pack.Restore(item.Removal);
                item.Document.Placements.Attach(item.Adapter.Actor, item.PlacedFrame, item.PlacementIndex, item.HadGlyph);
                _owner.SetSubtreeVisible(item.PlacedFrame, true);
                if (item.Parent != null)
                {
                    int at = Math.Clamp(item.TreeIndex, 0, item.Parent.Children.Count);
                    item.Parent.Children.Insert(at, item.Node);
                    _owner._host.Persistence.MarkFrameModified(item.Parent);
                }
                item.Removal = null;
            }
            _owner.AfterChange();
        }
    }

    // Shows or hides the geometry an actor placed. The frame objects stay in the FrameResource either way —
    // only the actor decides whether the game ever spawns them.
    private void SetSubtreeVisible(FrameObjectBase? frame, bool visible)
    {
        if (frame == null) return;
        var meshes = new List<GpuMesh>();
        _host.Streamer.CollectPlacedMeshes(frame, meshes);
        foreach (GpuMesh mesh in meshes) mesh.Visible = visible;
    }

    // Common tail of both directions: the glyph buffers, the selection and the panels are all stale now.
    private void AfterChange() => AfterChange(Array.Empty<SceneNode>());

    private void AfterChange(IReadOnlyList<SceneNode> selection)
    {
        _host.Streamer.RefreshActorMarkers();
        _host.Selection.SetSelection(selection, selection.Count > 0 ? selection[^1] : null);
        _host.RaiseSceneChanged();
    }
}
