using Illusion.Assets.Adapters;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;

namespace Illusion.Assets.Frames;

/// <summary>
/// The document-level half of an undoable frame-object deletion: takes a deleted subtree's frame objects out
/// of the loaded <see cref="FrameResource"/> (so Save/Build stop writing them) and puts them back on undo.
/// The scene-tree half (nodes, GPU meshes) stays with the caller's edit — this class never touches the tree.
/// <para>
/// Links INSIDE the detached set (a child's Parent pointer, its place in the parent's child list) are left
/// untouched, so a reattached subtree is exactly the one that was removed. Only the links that cross the
/// boundary — the holder's child-list slot, scene-folder membership — are captured and restored. Objects
/// elsewhere that anchor onto a detached frame keep their refs; the save-time index resolver already
/// degrades a ref to a missing object to -1 ("no parent"), and an undo makes the same refs resolve again.
/// </para>
/// </summary>
public sealed class DetachedFrames
{
    // Where a detached frame was held by something OUTSIDE the set: the holder's child list and the slot to
    // re-insert at on undo. A frame can appear in several containers (its hierarchy parent OR its anchor's
    // child list, plus scene folders), so links are captured per container, not per frame.
    private sealed record ExternalLink(List<FrameObjectBase> Holder, FrameObjectBase Frame, int Index);

    private readonly FrameResource _resource;
    private readonly HashSet<FrameObjectBase> _set;
    private readonly Dictionary<int, FrameObjectBase> _byRef;
    private int[] _order = Array.Empty<int>();          // FrameObjects key order before the first detach
    private int[] _geometryOrder = Array.Empty<int>();  // block dict orders — a save while detached prunes
    private int[] _materialOrder = Array.Empty<int>();  // the subtree's blocks, and re-registration alone
    private int[] _blendOrder = Array.Empty<int>();     // would append them at the end, breaking the
    private int[] _skeletonOrder = Array.Empty<int>();  // byte-faithfulness of an undone delete
    private int[] _hierarchyOrder = Array.Empty<int>();
    private readonly List<ExternalLink> _links = new();

    private DetachedFrames(FrameResource resource, HashSet<FrameObjectBase> set)
    {
        _resource = resource;
        _set = set;
        _byRef = set.ToDictionary(f => f.RefID, f => f);
    }

    /// <summary>Builds the detachment for the given subtree frames of <paramref name="document"/>.
    /// Null when the document is not a frame document or the list carries no vendor frames — the caller
    /// falls back to a tree-only removal rather than failing the delete.</summary>
    public static DetachedFrames? Capture(ISceneDocument document, IReadOnlyList<IFrameNode> frames)
    {
        if (document is not SceneDocumentAdapter adapter) return null;
        var set = new HashSet<FrameObjectBase>();
        foreach (IFrameNode f in frames)
            if (f is FrameNodeAdapter fna) set.Add(fna.Frame);
        return set.Count == 0 ? null : new DetachedFrames(adapter.Frame, set);
    }

    /// <summary>Removes the frames from the resource (initial apply and redo).</summary>
    public void Detach()
    {
        // Key orders are captured once, on the first detach — later redos see dictionaries that may already
        // contain newer entries, and the original order is what Reattach restores around.
        if (_order.Length == 0)
        {
            _order = _resource.FrameObjects.Keys.ToArray();
            _geometryOrder = _resource.FrameGeometries.Keys.ToArray();
            _materialOrder = _resource.FrameMaterials.Keys.ToArray();
            _blendOrder = _resource.FrameBlendInfos.Keys.ToArray();
            _skeletonOrder = _resource.FrameSkeletons.Keys.ToArray();
            _hierarchyOrder = _resource.FrameSkeletonHierachies.Keys.ToArray();
        }

        _links.Clear();
        foreach (FrameObjectBase frame in _set)
        {
            // The hierarchy parent owns the child slot; a parentless frame is held by its anchor instead.
            if (frame.Parent is { } parent)
            {
                CaptureLink(parent.Children, frame, insideSet: _set.Contains(parent));
            }
            else if (frame.Root is { } root)
            {
                CaptureLink(root.Children, frame, insideSet: _set.Contains(root));
            }
            // Scene folders hold their members in a separate runtime list, independent of the slots above.
            if (_resource.FrameScenes != null)
                foreach (FrameHeaderScene scene in _resource.FrameScenes.Values)
                    CaptureLink(scene.Children, frame, insideSet: false);
        }

        foreach (FrameObjectBase frame in _set)
            _resource.FrameObjects.Remove(frame.RefID);
    }

    // Records the frame's slot in a holder list and removes it — but only for holders outside the detached
    // set: an inside link is part of the subtree being carried away whole.
    private void CaptureLink(List<FrameObjectBase> holder, FrameObjectBase frame, bool insideSet)
    {
        if (insideSet) return;
        int index = holder.IndexOf(frame);
        if (index < 0) return;
        _links.Add(new ExternalLink(holder, frame, index));
        holder.RemoveAt(index);
    }

    /// <summary>Puts the frames back (undo): the object dictionary in its original order, the blocks a
    /// save-time sanitize may have pruned while they were detached, and the boundary child-list slots.</summary>
    public void Reattach()
    {
        // Rebuild FrameObjects in the captured order — objects created after the delete keep their place at
        // the end. Order matters beyond cosmetics: the frame name table records objects by index, so the
        // caller re-marks it dirty either way, but restoring the order keeps an undone delete byte-faithful.
        Dictionary<int, object> current = _resource.FrameObjects;
        var restored = new Dictionary<int, object>(current.Count + _set.Count);
        foreach (int key in _order)
        {
            if (current.TryGetValue(key, out object? live)) restored.Add(key, live);
            else if (_byRef.TryGetValue(key, out FrameObjectBase? mine)) restored.Add(key, mine);
        }
        foreach (KeyValuePair<int, object> pair in current)
            if (!restored.ContainsKey(pair.Key)) restored.Add(pair.Key, pair.Value);
        _resource.FrameObjects = restored;

        foreach (FrameObjectBase frame in _set) ReregisterBlocks(frame);
        _resource.FrameGeometries = InOriginalOrder(_resource.FrameGeometries, _geometryOrder);
        _resource.FrameMaterials = InOriginalOrder(_resource.FrameMaterials, _materialOrder);
        _resource.FrameBlendInfos = InOriginalOrder(_resource.FrameBlendInfos, _blendOrder);
        _resource.FrameSkeletons = InOriginalOrder(_resource.FrameSkeletons, _skeletonOrder);
        _resource.FrameSkeletonHierachies = InOriginalOrder(_resource.FrameSkeletonHierachies, _hierarchyOrder);

        // Ascending original index so multiple slots of one holder land where they were captured.
        foreach (ExternalLink link in _links.OrderBy(l => l.Index))
            link.Holder.Insert(Math.Min(link.Index, link.Holder.Count), link.Frame);
        _links.Clear();
    }

    // Rebuilds a block dictionary in its pre-delete key order (entries born later keep their place at the
    // end) — a re-registered block would otherwise sit at the end and shift every save-time block index.
    private static Dictionary<int, T> InOriginalOrder<T>(Dictionary<int, T> current, int[] order)
    {
        var rebuilt = new Dictionary<int, T>(current.Count);
        foreach (int key in order)
            if (current.TryGetValue(key, out T? value)) rebuilt.Add(key, value);
        foreach (KeyValuePair<int, T> pair in current)
            if (!rebuilt.ContainsKey(pair.Key)) rebuilt.Add(pair.Key, pair.Value);
        return rebuilt;
    }

    // A save while the frames were detached ran SanitizeFrameData, which prunes blocks nothing references;
    // re-register whatever the frame still points at (shared blocks may already be back via another object).
    private void ReregisterBlocks(FrameObjectBase frame)
    {
        if (frame is FrameObjectSingleMesh mesh)
        {
            if (mesh.Geometry is { } geometry && !_resource.FrameGeometries.ContainsKey(geometry.RefID))
                _resource.FrameGeometries.Add(geometry.RefID, geometry);
            if (mesh.Material is { } material && !_resource.FrameMaterials.ContainsKey(material.RefID))
                _resource.FrameMaterials.Add(material.RefID, material);
        }
        if (frame is FrameObjectModel model)
        {
            if (model.BlendInfo is { } blend && !_resource.FrameBlendInfos.ContainsKey(blend.RefID))
                _resource.FrameBlendInfos.Add(blend.RefID, blend);
            if (model.Skeleton is { } skeleton && !_resource.FrameSkeletons.ContainsKey(skeleton.RefID))
                _resource.FrameSkeletons.Add(skeleton.RefID, skeleton);
            if (model.SkeletonHierarchy is { } hierarchy
                && !_resource.FrameSkeletonHierachies.ContainsKey(hierarchy.RefID))
            {
                _resource.FrameSkeletonHierachies.Add(hierarchy.RefID, hierarchy);
            }
        }
    }
}
