using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Prefabs;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Prefab;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Giving a car a part it did not ship with.
///
/// <para>
/// A car's parts are rows in its PREFAB, and every row names a frame. Until now the toolkit could only write
/// the row, so "add a climb box" produced a row pointing at some other frame — a part that exists and does
/// nothing. This mints the frame as well, so the car really gains the part.
/// </para>
/// <para>
/// It only does so for the kinds whose frame is a HELPER. Measured over the 85 shipped car prefabs
/// (<c>--probe-car-items</c>), a seat, a climb box and a fuel tank name a <c>Dummy</c> every time and an
/// exhaust emitter a <c>Point</c> every time, while doors, windows, axles and wipers name a bone of the rig
/// every time — and growing a rig is a different job. Those are refused with that reason.
/// </para>
/// </summary>
internal sealed class CarPartController
{
    private readonly D3DImageHost _host;

    internal CarPartController(D3DImageHost host) => _host = host;

    /// <summary>The bone the selection points at, or null when it points at anything else.</summary>
    internal BoneNodeAdapter? SelectedBone => _host.SelectedNode?.Source as BoneNodeAdapter;

    /// <summary>Whether a part could be minted right now — a bone has to be selected to hang it on.</summary>
    internal bool CanAddPart => SelectedBone != null;

    /// <summary>
    /// Adds a part of <paramref name="kind"/> to the selected bone: the helper frame, the prefab row that
    /// names it, a tree row under the bone, and one undo entry covering all of it. Reports through the host's
    /// notice line; nothing is written when it refuses.
    /// </summary>
    /// <returns>True when the car gained the part.</returns>
    internal bool AddPart(CarItemKind kind)
    {
        if (SelectedBone is not { } selected)
        {
            _host.RaiseNotice(
                $"select the bone to hang it on first — {CarPartBuilder.Words(kind)} belongs to a part", true);
            return false;
        }

        SceneNode? boneNode = _host.SelectedNode;
        FrameObjectModel model = selected.Model;
        string extracted = MafiaEnvironment.ExtractedDir(selected.Document.SourceArchive);
        int joint = selected.Index;

        AddedCarPart? added = CarPartBuilder.Add(model, joint, kind, extracted, out string? refusal);
        if (added == null)
        {
            _host.RaiseNotice("nothing added: " + (refusal ?? "unknown reason"), true);
            return false;
        }

        // A frame shows up TWICE in the tree — under the bone it hangs on, and in its own place in the
        // hierarchy, which for a car's helpers is a grouping node at the origin. The loader builds both on a
        // reload, so both are built here: a part that appears in only one of the two reads as "it did not
        // add", which is exactly how it looked the first time.
        SceneNode? row = null;
        if (boneNode != null)
        {
            row = new SceneNode($"{added.Name}  ({Kind(added.Frame)})", "Attachment", false)
            {
                Source = selected.Document.Node(added.Frame),
            };
            boneNode.AddChild(row);
            boneNode.IsExpanded = true;
        }

        // The hierarchy row, beside the donor's — the donor is a part of the same kind, so its neighbours are
        // where this one belongs. Found by SOURCE rather than by name: two frames may share a name, and the
        // node identity is what the panel and the gizmo agree on.
        //
        // The donor's HIERARCHY row, not its attachment one. A helper frame shows up twice, and the first
        // match walking from the roots is the copy under the bone — so taking "the first" put the new row
        // beside the bone's attachments instead of inside the grouping node, and the grouping node then
        // listed every climb box but the new one. That is the shape of the report.
        SceneNode? sibling = FindHierarchyRow(selected.Document.Node(added.Donor));
        SceneNode? hierarchyRow = null;
        if (sibling?.Parent is { } holder)
        {
            hierarchyRow = new SceneNode(added.Name, Kind(added.Frame), false)
            {
                Source = selected.Document.Node(added.Frame),
            };
            holder.AddChild(hierarchyRow);
            holder.IsExpanded = true;
        }

        var edit = new CarPartEdit(model, added, () =>
        {
            bool present = model.Resource.FrameObjects.ContainsKey(added.Frame.RefID);
            Sync(boneNode, row, present);
            Sync(hierarchyRow?.Parent, hierarchyRow, present);
        });

        _host.History.Push(edit);
        _host.Persistence.MarkFrameModified(boneNode ?? _host.SelectedNode!);
        if (row != null) _host.Select(row);

        _host.RaiseNotice(
            $"added {CarPartBuilder.Words(kind)} on \"{selected.BoneName}\" as \"{added.Name}\" — "
            + "drag it into place, then Save and Build. Ctrl+Z takes it back.");
        return true;
    }

    /// <summary>Puts a row back under its parent, or takes it away — an undo/redo of one half of the tree.</summary>
    private static void Sync(SceneNode? parent, SceneNode? row, bool present)
    {
        if (parent == null || row == null) return;
        if (present)
        {
            if (!parent.Children.Contains(row)) parent.AddChild(row);
        }
        else
        {
            parent.Children.Remove(row);
        }
    }

    /// <summary>
    /// The row showing this object in the HIERARCHY, skipping the copy that hangs under a bone.
    ///
    /// <para>
    /// A helper frame has two rows and they mean different things: the one under the bone says which part it
    /// belongs to, the one in the hierarchy says where it sits in the graph. Only the second has the right
    /// neighbours for a new frame, and it is never the first one a walk from the roots finds — the skeleton
    /// branch comes first.
    /// </para>
    /// </summary>
    private SceneNode? FindHierarchyRow(object source)
    {
        foreach (SceneNode root in _host.Tree.Roots)
        {
            if (Find(root, source, "Attachment") is { } found) return found;
        }
        return null;
    }

    private static SceneNode? Find(SceneNode node, object source, string skipKind)
    {
        if (ReferenceEquals(node.Source, source)
            && !string.Equals(node.Kind, skipKind, StringComparison.Ordinal))
        {
            return node;
        }
        foreach (SceneNode child in node.Children)
        {
            if (Find(child, source, skipKind) is { } found) return found;
        }
        return null;
    }

    private static string Kind(FrameObjectBase frame) => frame switch
    {
        FrameObjectDummy => "Dummy",
        FrameObjectPoint => "Point",
        _ => "Frame",
    };
}
