using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Windows.Data;
using Illusion.Assets.Adapters;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Domain.Properties;
using Illusion.Rendering.Gizmos;
using Illusion.Scene;
using Illusion.Viewport;
using PropertyDescriptor = Illusion.Domain.Properties.PropertyDescriptor;

namespace Illusion.ViewModels;

/// <summary>
/// View-model behind the contextual property tabs. Holds the selected <see cref="SceneNode"/>, exposes the
/// booleans the tabs bind their visibility to, and — for a transformable frame object — the editable local
/// Position / Rotation (Euler degrees) / Scale. Field edits recompose the local transform and commit it through
/// the viewport (which re-syncs the GPU meshes); a gizmo drag calls <see cref="RefreshTransform"/> so the
/// fields track it live.
/// </summary>
public sealed class SelectionViewModel : INotifyPropertyChanged
{
    private readonly D3DImageHost _viewport;
    private bool _applyingField; // suppresses the self-refresh while a field edit commits

    public SelectionViewModel(D3DImageHost viewport) => _viewport = viewport;

    private SceneNode? _node;
    public SceneNode? Node => _node;

    public void SetNode(SceneNode? node)
    {
        // Re-selecting the SAME node (a reparent's reselect, undo/redo, a background mesh-attach) must
        // refresh values IN PLACE, never rebuild the parent picker: the reparent runs synchronously inside
        // the ListBox's mouse-DOWN push, and swapping the ItemsSource there yanks the filtered list out
        // from under the still-pressed click — the mouse-UP then lands on whatever row the UNFILTERED
        // list puts at that position and silently reparents to an arbitrary node.
        bool sameNode = node != null && ReferenceEquals(node, _node);
        _node = node;
        ReadTransform();
        if (sameNode)
        {
            RefreshPropertyValues();
            SyncSelectedParent();
        }
        else
        {
            BuildPropertyGroups();
            BuildMaterials();
            BuildParentCandidates();
        }
        RaiseAll();
    }

    // ── Type flags (tab visibility) ──
    public bool HasTransform => _node?.Source is IFrameNode;
    public bool IsSds => _node?.Kind == "Sds";
    public bool IsFrameResource => _node?.Kind == "FrameResource";
    public bool IsScene => _node?.Kind == "Scene";
    public bool HasSelection => _node != null;

    // ── Header ──
    public string Title => _node?.Name ?? "—";
    public string ObjectType => _node == null ? "" : PrettyKind(_node.Kind);

    // ── Property groups (Object tab = common; per-type tab = type-specific) ──
    private IReadOnlyList<PropertyGroupViewModel> _commonGroups = Array.Empty<PropertyGroupViewModel>();
    private IReadOnlyList<PropertyGroupViewModel> _typeGroups = Array.Empty<PropertyGroupViewModel>();

    public IReadOnlyList<PropertyGroupViewModel> CommonGroups => _commonGroups;
    public IReadOnlyList<PropertyGroupViewModel> TypeGroups => _typeGroups;
    public bool HasTypeProperties => _typeGroups.Count > 0;

    /// <summary>Header/tooltip for the per-type property tab (e.g. "Light", "Single mesh").</summary>
    public string TypeTabTitle => ObjectType;

    private void BuildPropertyGroups()
    {
        var common = new List<PropertyGroupViewModel>();
        var type = new List<PropertyGroupViewModel>();
        if (_node is { Source: IPropertySource ps } node)
        {
            // Bind the commit to THIS node, not the live selection: a field that commits on lost-focus after the
            // selection already moved on must still edit the object the panel was built for.
            void Commit(PropertyDescriptor d, object? before, object? after) =>
                _viewport.CommitPropertyEdit(node, d, before, after);
            foreach (PropertyGroup g in ps.GetPropertyGroups())
                (g.IsTypeSpecific ? type : common).Add(new PropertyGroupViewModel(g, Commit));
        }
        _commonGroups = common;
        _typeGroups = type;
    }

    /// <summary>Re-reads every property row's value in place (after an undo/redo or a second-editor edit),
    /// keeping the panel's expander/scroll state. Also refreshes the header, so a rename shows immediately.</summary>
    public void RefreshPropertyValues()
    {
        Raise(nameof(Title));
        Raise(nameof(ObjectType));
        foreach (PropertyGroupViewModel g in _commonGroups) g.Refresh();
        foreach (PropertyGroupViewModel g in _typeGroups) g.Refresh();
    }

    // ── Materials (mesh only) ──
    private IReadOnlyList<MaterialViewModel> _materials = Array.Empty<MaterialViewModel>();
    public IReadOnlyList<MaterialViewModel> Materials => _materials;
    public bool HasMaterials => _materials.Count > 0;

    private void BuildMaterials()
    {
        if (_node?.Source is not IMaterialListSource src)
        {
            _materials = Array.Empty<MaterialViewModel>();
            return;
        }
        IReadOnlyList<MaterialInfo> infos = src.GetMaterials();
        var list = new List<MaterialViewModel>(infos.Count);
        for (int i = 0; i < infos.Count; i++)
            list.Add(new MaterialViewModel(infos[i], i, _viewport.RenderMaterialThumbnail(infos[i])));
        _materials = list;
    }

    /// <summary>Rebuilds the material tiles in place (after a material edit or its undo/redo) — the
    /// thumbnails and slot bindings may have changed while the selected node stayed the same.</summary>
    public void RefreshMaterials()
    {
        BuildMaterials();
        Raise(nameof(Materials));
        Raise(nameof(HasMaterials));
    }

    // ── Hierarchy (parent picker) ──
    private IReadOnlyList<ParentOption> _parentCandidates = Array.Empty<ParentOption>();
    private ICollectionView? _parentCandidatesView;
    private ParentOption? _selectedParent;
    private string _parentSearchText = "";
    // Re-entry guard while a reparent/rebuild runs. A COUNTER, not a bool: the reparent's reselect
    // re-enters SetNode inside the SelectedParent setter, and a nested guard cleared by the inner
    // finally would re-open the outer one to WPF's binding echoes mid-flight.
    private int _applyingParent;
    private bool _parentPickerSynced;  // false until the picker displays the node's real parent — see SelectedParent

    /// <summary>Filtered view of the parent candidates the picker list binds to (see <see cref="ParentSearchText"/>).</summary>
    public ICollectionView? ParentCandidatesView => _parentCandidatesView;
    public bool CanReparent => _node?.Source is IFrameNode && _parentCandidates.Count > 0;

    /// <summary>Search text that filters the parent candidate list (case-insensitive substring of the label).</summary>
    public string ParentSearchText
    {
        get => _parentSearchText;
        set { if (_parentSearchText != value) { _parentSearchText = value; _parentCandidatesView?.Refresh(); } }
    }

    private bool FilterParent(object o) =>
        _parentSearchText.Length == 0 ||
        (o is ParentOption p && p.Display.Contains(_parentSearchText, StringComparison.OrdinalIgnoreCase));

    /// <summary>The chosen parent for the selected object; setting it reparents (an undoable edit).</summary>
    public ParentOption? SelectedParent
    {
        get => _selectedParent;
        set
        {
            // Ignore a deselect (the filter hiding the current item) and re-entry while a reparent rebuilds.
            if (value == null || _applyingParent > 0 || _node == null) return;
            if (ReferenceEquals(value.Node, _node.Parent))
            {
                _selectedParent = value;
                _parentPickerSynced = true; // the picker now agrees with reality — later pushes are the user's
                return;
            }

            // Until the picker has been shown sitting on the node's ACTUAL parent, any value arriving here came
            // from WPF, not from the user: swapping the ItemsSource makes the ComboBox publish the collection
            // view's current item, and that push can arrive a dispatcher tick after the rebuild finished. Acting
            // on it silently reparents the object, which persists to the FrameResource and crashes the game.
            // Selecting an object in the tree must never modify the scene.
            if (!_parentPickerSynced) return;

            _applyingParent++;
            try
            {
                _selectedParent = value;
                _viewport.Reparent(_node, value.Node); // reselects → SetNode → resyncs the panel in place
            }
            finally { _applyingParent--; }
        }
    }

    private void BuildParentCandidates()
    {
        // Hold the re-entry guard for the WHOLE rebuild. Swapping the ItemsSource makes the ComboBox push the
        // collection view's current item back through SelectedParent, and that push is indistinguishable from a
        // user choice — without this guard it silently reparents the object, which persists to the FrameResource
        // and can crash the game. Selecting an object must never mutate the scene.
        _applyingParent++;
        try
        {
            var list = new List<ParentOption>();
            // Only frame-resource documents support reparenting; a collision placement has no hierarchy (its
            // document is a CollisionDocumentAdapter whose Reparent is a no-op), so it gets no parent picker.
            if (_node?.Source is IFrameNode && _node.OwningDocumentNode() is { Source: SceneDocumentAdapter } docNode)
            {
                list.Add(new ParentOption("(root)", docNode));
                CollectCandidates(docNode, _node, list, 0);
            }
            _parentCandidates = list;
            _parentSearchText = "";
            _parentCandidatesView = new ListCollectionView(list) { Filter = FilterParent };
            _selectedParent = null;
            foreach (ParentOption o in list)
                if (ReferenceEquals(o.Node, _node?.Parent)) { _selectedParent = o; break; }

            // The picker may only act once it is showing the node's real parent. When the parent is not among the
            // candidates the combo has nothing truthful to display, so a push from it would be pure noise.
            _parentPickerSynced = _selectedParent != null;
        }
        finally { _applyingParent--; }
    }

    // Re-points the picker at the node's CURRENT parent without touching the candidate list, the view or
    // the search text — the in-place half of a same-node refresh. The list may go cosmetically stale (the
    // node's own row still shows its old spot); it rebuilds on the next real selection change. Falls back
    // to a full rebuild only when the parent is not among the candidates at all (cannot display the truth).
    private void SyncSelectedParent()
    {
        _applyingParent++;
        try
        {
            ParentOption? current = null;
            foreach (ParentOption o in _parentCandidates)
                if (ReferenceEquals(o.Node, _node?.Parent)) { current = o; break; }
            if (current == null)
            {
                BuildParentCandidates();
                return;
            }
            _selectedParent = current;
            _parentPickerSynced = true;
        }
        finally { _applyingParent--; }
    }

    // Flattens the document subtree into parent options (scene folders + frame objects), skipping the node itself
    // and its whole subtree so a cycle can't be chosen. Indented by depth; scene folders marked with a caret.
    private static void CollectCandidates(SceneNode node, SceneNode exclude, List<ParentOption> list, int depth)
    {
        foreach (SceneNode c in node.Children)
        {
            if (ReferenceEquals(c, exclude)) continue;
            if (c.Source is IFrameScene || c.Source is IFrameNode)
            {
                string indent = new string(' ', depth * 3);
                string mark = c.Source is IFrameScene ? "▸ " : "";
                list.Add(new ParentOption(indent + mark + c.Name, c));
                CollectCandidates(c, exclude, list, depth + 1);
            }
        }
    }

    // ── Metadata (computed on selection change) ──
    public string MeshCountText => CountMeshes(out _).ToString("N0", CultureInfo.InvariantCulture);
    public string TriangleCountText { get { CountMeshes(out long tris); return tris.ToString("N0", CultureInfo.InvariantCulture); } }
    public string ChildCountText => (_node?.Children.Count ?? 0).ToString("N0", CultureInfo.InvariantCulture);
    public string SceneCategory => _node?.Category ?? "";

    public string FrObjects => Doc()?.ObjectCount.ToString("N0", CultureInfo.InvariantCulture) ?? "0";
    public string FrGeometries => Doc()?.GeometryCount.ToString("N0", CultureInfo.InvariantCulture) ?? "0";
    public string FrMaterials => Doc()?.MaterialCount.ToString("N0", CultureInfo.InvariantCulture) ?? "0";
    public string FrSkeletons => Doc()?.SkeletonCount.ToString("N0", CultureInfo.InvariantCulture) ?? "0";
    public string FrScenes => Doc()?.SceneCount.ToString("N0", CultureInfo.InvariantCulture) ?? "0";

    private ISceneDocument? Doc() => _node?.Source as ISceneDocument;

    private int CountMeshes(out long triangles)
    {
        int meshes = 0;
        triangles = 0;
        if (_node != null)
            foreach (SceneNode leaf in _node.DescendantMeshLeaves())
                if (leaf.Mesh != null) { meshes++; triangles += leaf.Mesh.TriangleCount; }
        return meshes;
    }

    // ── Transform (local) ──
    private Vector3 _pos, _rotDeg, _scale = Vector3.One;

    public float PosX { get => _pos.X; set => SetPos(0, value); }
    public float PosY { get => _pos.Y; set => SetPos(1, value); }
    public float PosZ { get => _pos.Z; set => SetPos(2, value); }
    public float RotX { get => _rotDeg.X; set => SetRot(0, value); }
    public float RotY { get => _rotDeg.Y; set => SetRot(1, value); }
    public float RotZ { get => _rotDeg.Z; set => SetRot(2, value); }
    public float ScaleX { get => _scale.X; set => SetScale(0, value); }
    public float ScaleY { get => _scale.Y; set => SetScale(1, value); }
    public float ScaleZ { get => _scale.Z; set => SetScale(2, value); }

    private void SetPos(int axis, float v) { _pos = With(_pos, axis, v); ApplyTransform(); }
    private void SetRot(int axis, float v) { _rotDeg = With(_rotDeg, axis, v); ApplyTransform(); }
    private void SetScale(int axis, float v) { _scale = With(_scale, axis, v); ApplyTransform(); }

    private static Vector3 With(Vector3 v, int axis, float value) =>
        axis == 0 ? new Vector3(value, v.Y, v.Z) : axis == 1 ? new Vector3(v.X, value, v.Z) : new Vector3(v.X, v.Y, value);

    // Rebuilds the frame's local transform from the fields, commits it (re-syncs its GPU meshes) and records
    // it as one undoable edit.
    private void ApplyTransform()
    {
        if (_node?.Source is not IFrameNode fn) return;
        _applyingField = true;
        try
        {
            Matrix4x4 before = fn.LocalTransform;
            fn.LocalTransform = TransformMath.Compose(TransformOps.EulerDegToQuat(_rotDeg), _scale, _pos);
            _viewport.CommitNodeTransform(_node);
            _viewport.RecordTransform(_node, before, fn.LocalTransform);
        }
        finally { _applyingField = false; }

        // Notify the fields from the cached values (no lossy re-read) so a second editor bound to the same
        // SelectionViewModel — the Object tab and the viewport overlay panel are both on screen — stays in sync.
        RaiseTransformFields();
    }

    private void RaiseTransformFields()
    {
        Raise(nameof(PosX)); Raise(nameof(PosY)); Raise(nameof(PosZ));
        Raise(nameof(RotX)); Raise(nameof(RotY)); Raise(nameof(RotZ));
        Raise(nameof(ScaleX)); Raise(nameof(ScaleY)); Raise(nameof(ScaleZ));
        Raise(nameof(DeltaX)); Raise(nameof(DeltaY)); Raise(nameof(DeltaZ));
    }

    // ── The change the last gizmo transform made (the viewport overlay) ──

    // The overlay answers "how much did that just change it by", which only a fixed starting point can
    // answer — the object's own current values cannot, and reading them back is what made the overlay show
    // an absolute position after a resize.
    private GizmoMode _deltaMode = GizmoMode.None;
    private Vector3 _basePos, _baseRotDeg, _baseScale = Vector3.One;

    /// <summary>Which transform the overlay is reporting; <see cref="GizmoMode.None"/> when it has nothing to say.</summary>
    public GizmoMode DeltaMode => _deltaMode;

    /// <summary>
    /// Starts reporting changes against where the object stood before the transform. Called when a gizmo drag
    /// first moves something, with the pre-drag state — never with the live one, or the change would measure
    /// itself and always read as nothing.
    /// </summary>
    public void BeginDelta(GizmoMode mode, Vector3 position, Vector3 rotationDeg, Vector3 scale)
    {
        _deltaMode = mode;
        _basePos = position;
        _baseRotDeg = rotationDeg;
        _baseScale = scale;
        RaiseTransformFields();
    }

    /// <summary>Stops reporting (a different object is a different story).</summary>
    public void ClearDelta()
    {
        if (_deltaMode == GizmoMode.None) return;
        _deltaMode = GizmoMode.None;
        RaiseTransformFields();
    }

    /// <summary>
    /// How much the last transform changed each axis by — see <see cref="TransformDelta"/> for what that means
    /// per transform. Assigning re-applies against the same starting point, so typing 2 into a scale always
    /// means "twice the original", however many times it is typed.
    /// </summary>
    public float DeltaX { get => Delta(0); set => SetDelta(0, value); }
    public float DeltaY { get => Delta(1); set => SetDelta(1, value); }
    public float DeltaZ { get => Delta(2); set => SetDelta(2, value); }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    // Which pair of vectors the change is measured between — the live one and the one captured before the drag.
    private (Vector3 Base, Vector3 Current) DeltaPair => _deltaMode switch
    {
        GizmoMode.Move => (_basePos, _pos),
        GizmoMode.Rotate => (_baseRotDeg, _rotDeg),
        GizmoMode.Scale => (_baseScale, _scale),
        _ => (Vector3.Zero, Vector3.Zero),
    };

    private float Delta(int axis)
    {
        (Vector3 baseline, Vector3 current) = DeltaPair;
        return TransformDelta.Measure(_deltaMode, Axis(baseline, axis), Axis(current, axis));
    }

    private void SetDelta(int axis, float value)
    {
        if (_deltaMode == GizmoMode.None) return;
        (Vector3 baseline, _) = DeltaPair;
        float applied = TransformDelta.Apply(_deltaMode, Axis(baseline, axis), value);
        switch (_deltaMode)
        {
            case GizmoMode.Move: _pos = With(_pos, axis, applied); break;
            case GizmoMode.Rotate: _rotDeg = With(_rotDeg, axis, applied); break;
            default: _scale = With(_scale, axis, applied); break;
        }
        ApplyTransform();
    }

    /// <summary>Re-reads the transform fields from the frame (after a gizmo drag). Ignored while a field edit commits.</summary>
    public void RefreshTransform()
    {
        if (_applyingField) return;
        ReadTransform();
        RaiseTransformFields();
    }

    private void ReadTransform()
    {
        if (_node?.Source is IFrameNode fn &&
            TransformMath.TryDecompose(fn.LocalTransform, out Vector3 scale, out Quaternion rot, out Vector3 pos))
        {
            _pos = pos;
            _scale = scale;
            _rotDeg = TransformOps.QuatToEulerDeg(rot);
        }
        else
        {
            _pos = Vector3.Zero;
            _scale = Vector3.One;
            _rotDeg = Vector3.Zero;
        }
    }

    private static string PrettyKind(string kind) => kind switch
    {
        "Sds" => "SDS archive",
        "FrameResource" => "Frame resource",
        "Scene" => "Scene folder",
        "Folder" => "Folder",
        "Mesh" => "Single mesh",
        "Collision" => "Collision layer",
        "CollisionInstance" => "Collision placement",
        _ => kind,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

/// <summary>One entry of the parent picker: a display label (indented by depth) and the tree node it targets.</summary>
public sealed record ParentOption(string Display, SceneNode Node);
