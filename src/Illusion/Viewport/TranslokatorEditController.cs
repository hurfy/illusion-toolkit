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
            // The index is the placement's slot in the TABLE, not in the tree: restoring it there is what keeps
            // an undone delete byte-identical, while the tree only has to hold the node again.
            items.Add(new CrashListEdit.Item(
                adapter.Document, adapter.Owner, adapter.Owner.Instances.IndexOf(adapter.Instance),
                adapter.Instance, adapter.SeasonLinked));
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

            Instance copy = TranslokatorDocumentAdapter.Clone(adapter.Instance);
            if (!adapter.Document.TryAllocateId(out ushort id)) continue; // table full — see TryAllocateId
            copy.ID = id;

            adapter.Document.Node(copy, adapter.Owner).SeasonLinked = adapter.SeasonLinked;
            items.Add(new CrashListEdit.Item(
                adapter.Document, adapter.Owner, adapter.Owner.Instances.Count, copy, adapter.SeasonLinked));
        }
        if (items.Count == 0) return;

        var edit = new CrashListEdit(_host, items, added: true);
        edit.Redo();
        History.Push(edit);

        var nodes = edit.Nodes.ToList();
        if (nodes.Count > 0) _host.Selection.SetSelection(nodes, nodes[^1]);
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

        var edit = new CrashListEdit(_host,
            [new CrashListEdit.Item(placements.Document, row, row.Instances.Count, instance, adapter.SeasonLinked)],
            added: true);
        edit.Redo();
        History.Push(edit);

        SceneNode? node = edit.Nodes.FirstOrDefault();
        if (node != null) _host.Selection.SetSelection([node], node);
        return node;
    }

    // One undoable add-or-remove of a set of placements. For an add edit Redo inserts + Undo removes; for a delete
    // edit Redo removes + Undo restores at the captured index. Both keep the row's placement list and the tree
    // layer's Children index-aligned (the picker maps a hit to a copy by position), mark the document dirty
    // (persist + repaint) and mirror into the other season for linked placements.
    private sealed class CrashListEdit : INodeEdit
    {
        /// <summary>Index is the placement's slot in the TABLE — restoring it there is what makes an undone
        /// delete byte-identical. The tree node is not part of the edit: it is materialised on demand, and only
        /// the placements that someone actually looked at have one.</summary>
        public readonly record struct Item(
            TranslokatorDocumentAdapter Doc, Formats.Translokator.Object Row, int Index,
            Instance Placement, bool Mirror);

        private readonly D3DImageHost _host;
        private readonly Item[] _items;
        private readonly bool _added;

        public CrashListEdit(D3DImageHost host, IReadOnlyList<Item> items, bool added)
        {
            _host = host;
            _items = items.ToArray();
            _added = added;
        }

        public IEnumerable<SceneNode> Nodes
        {
            get
            {
                foreach (Item i in _items)
                {
                    if (_host.Streamer.CrashNodeFor(i.Placement, i.Row) is { } node) yield return node;
                }
            }
        }

        public void Redo() { if (_added) Add(); else Remove(); }
        public void Undo() { if (_added) Remove(); else Add(); }

        private void Add()
        {
            // Ascending, so several restores land back in their original table slots.
            foreach (Item it in _items.OrderBy(i => i.Index))
            {
                it.Doc.InsertPlacement(it.Row, it.Placement, Math.Clamp(it.Index, 0, it.Row.Instances.Count),
                    it.Mirror);
                _host.Streamer.CrashNodeFor(it.Placement, it.Row); // give it a node so it can be selected
            }
            Finish();
        }

        private void Remove()
        {
            var dropped = new List<SceneNode>();
            foreach (Item it in _items)
            {
                if (_host.Streamer.CrashNodeFor(it.Placement, it.Row) is { } node)
                {
                    node.Parent?.Children.Remove(node);
                    dropped.Add(node);
                }
                it.Doc.RemovePlacement(it.Row, it.Placement, it.Mirror);
                _host.Streamer.ForgetCrashNode(it.Placement);
            }
            DropFromSelection(dropped);
            Finish();
        }

        private void DropFromSelection(List<SceneNode> dropped)
        {
            if (!_host.Selection.Selected.Any(dropped.Contains)) return;
            var keep = _host.Selection.Selected.Where(n => !dropped.Contains(n)).ToList();
            _host.Selection.SetSelection(keep, keep.Count > 0 ? keep[^1] : null);
        }

        private void Finish()
        {
            foreach (Item it in _items)
            {
                it.Doc.MarkRowDirty(it.Row);                          // re-upload just this row's copies next frame
                if (_host.Streamer.CrashRowNode(it.Row) is not { } rowNode) continue;
                rowNode.Name = $"{it.Row.Name.String} — {it.Row.Instances.Count}";
                _host.Persistence.MarkFrameModified(rowNode);         // enlist the .tra for save/build
            }
            _host.RaiseSceneChanged();
        }
    }
}
