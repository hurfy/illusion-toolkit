using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Translokator;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Undoable placing / deleting / duplicating of city_crash props. These mutate the loaded Translokator table
/// directly, so they persist to the .tra on save. Edits share the viewport's
/// <see cref="TransformEditController.History"/> so Ctrl+Z unwinds them alongside transform edits, and each keeps
/// the tree layer's child nodes index-aligned with the row's placement list (the ray-picker maps by index). The
/// instanced props repaint via the owning document's <c>RenderDirty</c> flag, which the streamer picks up on the
/// next frame.
///
/// Every command honours the placement's season switch: a linked placement is added, moved and deleted in both
/// the summer and the winter archive at once, so a prop only has to be positioned once.
/// </summary>
internal sealed class TranslokatorEditController
{
    private readonly D3DImageHost _host;

    public TranslokatorEditController(D3DImageHost host) => _host = host;

    private EditHistory History => _host.Editing.History;

    /// <summary>Whether the selection contains at least one crash placement (delete/duplicate target).</summary>
    public bool HasCrashSelection() => _host.Selection.Selected.Any(n => n.Source is TranslokatorInstanceAdapter);

    /// <summary>The rows of the loaded crash table that resolve to real geometry — what the "place an object"
    /// picker offers. Empty when city_crash is not in the scene.</summary>
    public IReadOnlyList<Formats.Translokator.Object> AvailableObjects =>
        _host.Streamer.CrashLayer?.Rows ?? [];

    /// <summary>Deletes every selected crash placement as one undoable edit.</summary>
    public void DeleteSelected()
    {
        var items = new List<CrashListEdit.Item>();
        foreach (SceneNode node in _host.Selection.Selected)
        {
            if (node.Source is not TranslokatorInstanceAdapter adapter) continue;
            if (node.Parent is not { } rowNode) continue;
            items.Add(new CrashListEdit.Item(
                adapter.Document, rowNode, adapter.Owner, rowNode.Children.IndexOf(node),
                adapter.Instance, node, adapter.SeasonLinked));
        }
        if (items.Count == 0) return;

        var edit = new CrashListEdit(_host, items, added: false);
        edit.Redo();
        History.Push(edit);
    }

    /// <summary>Duplicates every selected crash placement in place (the copy lands on top of its original, ready
    /// to be dragged off). Each copy gets its own placement id, and lands in both seasons when the original was
    /// linked. Selects the copies, so the next gizmo drag moves them and not the originals.</summary>
    public void DuplicateSelected()
    {
        var items = new List<CrashListEdit.Item>();
        foreach (SceneNode node in _host.Selection.Selected)
        {
            if (node.Source is not TranslokatorInstanceAdapter adapter) continue;
            if (node.Parent is not { } rowNode) continue;

            Instance copy = TranslokatorDocumentAdapter.Clone(adapter.Instance);
            if (!adapter.Document.TryAllocateId(out ushort id)) continue; // table full — see TryAllocateId
            copy.ID = id;

            TranslokatorInstanceAdapter copyAdapter = adapter.Document.Node(copy, adapter.Owner);
            copyAdapter.SeasonLinked = adapter.SeasonLinked;
            var copyNode = new SceneNode($"copy #{copy.ID}", "CrashInstance", false) { Source = copyAdapter };
            items.Add(new CrashListEdit.Item(
                adapter.Document, rowNode, adapter.Owner, rowNode.Children.Count, copy, copyNode,
                adapter.SeasonLinked));
        }
        if (items.Count == 0) return;

        var edit = new CrashListEdit(_host, items, added: true);
        edit.Redo();
        History.Push(edit);
        _host.Selection.SetSelection(items.Select(i => i.Node).ToList(), items[^1].Node);
    }

    /// <summary>
    /// Places a new copy of a table row at a world position — the "add an object" command. The row is one the
    /// archive already carries (its prototype mesh, draw distances and actor type come with it), so this only
    /// adds a placement. Returns the new node, or null when the archive has no free placement id left.
    /// </summary>
    public SceneNode? PlaceObject(Formats.Translokator.Object row, Vector3 position, bool bothSeasons)
    {
        ArgumentNullException.ThrowIfNull(row);
        CrashPlacements? placements = _host.Streamer.CrashLayer;
        if (placements == null) return null;
        SceneNode? rowNode = _host.Streamer.CrashRowNode(row);
        if (rowNode == null) return null;
        if (!placements.Document.TryAllocateId(out ushort id)) return null;

        var instance = new Instance
        {
            Position = position,
            Rotation = Vector3.Zero,
            Scale = 1.0f,
            ID = id,
        };

        TranslokatorInstanceAdapter adapter = placements.Document.Node(instance, row);
        adapter.SeasonLinked = bothSeasons && placements.Document.Twin != null;
        var node = new SceneNode($"copy #{id}", "CrashInstance", false) { Source = adapter };

        var edit = new CrashListEdit(_host,
            [new CrashListEdit.Item(placements.Document, rowNode, row, rowNode.Children.Count, instance, node,
                adapter.SeasonLinked)],
            added: true);
        edit.Redo();
        History.Push(edit);
        _host.Selection.SetSelection([node], node);
        return node;
    }

    // One undoable add-or-remove of a set of placements. For an add edit Redo inserts + Undo removes; for a delete
    // edit Redo removes + Undo restores at the captured index. Both keep the row's placement list and the tree
    // layer's Children index-aligned (the picker maps a hit to a copy by position), mark the document dirty
    // (persist + repaint) and mirror into the other season for linked placements.
    private sealed class CrashListEdit : INodeEdit
    {
        public readonly record struct Item(
            TranslokatorDocumentAdapter Doc, SceneNode RowNode, Formats.Translokator.Object Row, int Index,
            Instance Placement, SceneNode Node, bool Mirror);

        private readonly D3DImageHost _host;
        private readonly Item[] _items;
        private readonly bool _added;

        public CrashListEdit(D3DImageHost host, IReadOnlyList<Item> items, bool added)
        {
            _host = host;
            _items = items.ToArray();
            _added = added;
        }

        public IEnumerable<SceneNode> Nodes { get { foreach (Item i in _items) yield return i.Node; } }

        public void Redo() { if (_added) Add(); else Remove(); }
        public void Undo() { if (_added) Remove(); else Add(); }

        private void Add()
        {
            // Ascending, so several restores land back in their original slots (both lists grow in lockstep).
            foreach (Item it in _items.OrderBy(i => i.Index))
            {
                int index = Math.Clamp(it.Index, 0, it.Row.Instances.Count);
                it.Doc.InsertPlacement(it.Row, it.Placement, index, it.Mirror);
                it.RowNode.InsertChild(index, it.Node);
            }
            Finish();
        }

        private void Remove()
        {
            foreach (Item it in _items)
            {
                it.Doc.RemovePlacement(it.Row, it.Placement, it.Mirror);
                it.RowNode.Children.Remove(it.Node);
            }
            DropFromSelection();
            Finish();
        }

        private void DropFromSelection()
        {
            if (!_host.Selection.Selected.Any(n => _items.Any(i => ReferenceEquals(i.Node, n)))) return;
            var keep = _host.Selection.Selected.Where(n => _items.All(i => !ReferenceEquals(i.Node, n))).ToList();
            _host.Selection.SetSelection(keep, keep.Count > 0 ? keep[^1] : null);
        }

        private void Finish()
        {
            foreach (Item it in _items)
            {
                it.Doc.MarkRowDirty(it.Row);                      // re-upload just this row's copies next frame
                it.RowNode.Name = $"{it.Row.Name.String} — {it.Row.Instances.Count}";
                _host.Persistence.MarkFrameModified(it.RowNode);  // enlist the .tra for save/build
            }
            _host.RaiseSceneChanged();
        }
    }
}
