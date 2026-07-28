using System.Numerics;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Object selection of the viewport: the multi-selection list, the active node, the silhouette-outline
/// refresh and the cached gizmo pivot (selection centroid). Raises the host's selection events.
/// </summary>
internal sealed class SelectionController
{
    private readonly D3DImageHost _host;

    public SelectionController(D3DImageHost host) => _host = host;

    // Multi-selection: Ctrl+click accumulates nodes; a plain click replaces it. Selected holds every selected
    // node; Active is the ACTIVE one (last clicked) that drives the property panel / numeric fields.
    private readonly List<SceneNode> _selected = new();

    /// <summary>Every selected node (multi-select). The gizmo transforms all of them as a group.</summary>
    public IReadOnlyList<SceneNode> Selected => _selected;

    /// <summary>The active selected node (last clicked) — drives the property panel; null when nothing is selected.</summary>
    public SceneNode? Active { get; private set; }

    // Cached gizmo pivot (centroid of the selection) — recomputed on select/transform, not per render frame,
    // so the gizmo overlay never walks a large selection's mesh leaves every frame.
    public Vector3 GizmoPivot { get; private set; }

    public bool Contains(SceneNode node) => _selected.Contains(node);

    /// <summary>Single-selects a node (tree or viewport), replacing any multi-selection. Null clears it.</summary>
    public void Select(SceneNode? node)
    {
        if (node == null) { if (_selected.Count > 0) SetSelection(Array.Empty<SceneNode>(), null); return; }
        if (_selected.Count == 1 && ReferenceEquals(_selected[0], node)) return; // already the sole selection
        SetSelection(new[] { node }, node);
    }

    /// <summary>Ctrl+click: toggles a node in the multi-selection. The toggled node becomes active (or, when
    /// removed, the last remaining node does).</summary>
    public void ToggleSelect(SceneNode node)
    {
        var next = new List<SceneNode>(_selected);
        SceneNode? active;
        if (next.Remove(node)) active = next.Count > 0 ? next[^1] : null;
        else { next.Add(node); active = node; }
        SetSelection(next, active);
    }

    // Replaces the whole selection: refreshes each node's IsSelected flag (drives the tree row highlight),
    // records the active node, expands its ancestors, refreshes the outline/pivot, and notifies the UI.
    public void SetSelection(IReadOnlyList<SceneNode> nodes, SceneNode? active)
    {
        foreach (SceneNode n in _selected) n.IsSelected = false;
        _selected.Clear();
        foreach (SceneNode n in nodes)
            if (!_selected.Contains(n)) { _selected.Add(n); n.IsSelected = true; }
        Active = active;
        active?.ExpandAncestors();
        UpdateSelectionHighlight();
        _host.RaiseSelectionChanged();
    }

    // Refreshes the selection silhouette outline (every selected mesh) AND caches the gizmo pivot (centroid of
    // the selection). Called on select + after each transform edit — never per frame.
    public void UpdateSelectionHighlight()
    {
        if (_host.Rnd == null) return;

        // Outline every selected node that directly carries a (non-instanced) GPU mesh. Containers (folder / SDS /
        // FrameResource / scene) carry no mesh; instanced clouds (city_crash) have no single silhouette — both skip.
        var meshes = new List<GpuMesh>(_selected.Count);
        foreach (SceneNode n in _selected)
            if (n.Mesh is { Instanced: false } m) meshes.Add(m);
        _host.Rnd.SetSelectionMeshes(meshes);

        // Instanced collision hulls have no per-node mesh, so they highlight through a dedicated renderer path.
        _host.Streamer.UpdateCollisionSelection(_selected);

        // Crash props are instanced too: one selected copy is outlined by re-drawing its prototype at that copy's
        // matrix (an instanced mesh has no World of its own to outline).
        _host.Rnd.SetSelectionPlacements(_host.Streamer.CrashSelectionOutlines(_selected));

        GizmoPivot = ComputeGroupPivot();
    }

    /// <summary>Combined world bounds of the selection — what "look at this" has to fit in frame. A node with no
    /// measurable geometry (a frame with no mesh) contributes its origin, so a selection of those still yields a
    /// point to fly to. False when nothing is selected.</summary>
    public bool TryGetSelectionBounds(out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        foreach (SceneNode n in _selected)
        {
            if (n.TryGetWorldBounds(out Vector3 lo, out Vector3 hi))
            {
                min = Vector3.Min(min, lo);
                max = Vector3.Max(max, hi);
            }
            else if (n.Source is IFrameNode fn)
            {
                Vector3 p = fn.WorldTransform.Translation;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        return min.X <= max.X;
    }

    /// <summary>True when at least one selected node is a transformable frame object.</summary>
    public bool AnyTransformable()
    {
        foreach (SceneNode n in _selected) if (n.Source is IFrameNode) return true;
        return false;
    }

    // Centroid the gizmo sits at: the average of each selected object's bounds centre (or frame origin).
    private Vector3 ComputeGroupPivot()
    {
        var sum = Vector3.Zero;
        int count = 0;
        foreach (SceneNode n in _selected)
        {
            if (n.TryGetWorldBounds(out Vector3 min, out Vector3 max)) { sum += (min + max) * 0.5f; count++; }
            else if (n.Source is IFrameNode fn) { sum += fn.WorldTransform.Translation; count++; }
        }
        return count > 0 ? sum / count : Vector3.Zero;
    }
}
