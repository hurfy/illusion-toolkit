using System.Collections.ObjectModel;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Collisions;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gizmos;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// The Mafia map viewport: a <see cref="ViewportControl"/> that owns the Mafia scene tree and loads areas
/// (districts / interiors, whole-map streaming, city_crash). The reusable render pipeline, fly-camera, present
/// and shading modes all live in the base; the Mafia-specific work is split across collaborators —
/// <see cref="SceneTree"/> (tree + filters), <see cref="ViewportCatalogs"/> (map/zones/sky),
/// <see cref="DistrictStreamer"/> (load queue + background pipeline + streaming),
/// <see cref="SelectionController"/> (multi-select + outline + pivot),
/// <see cref="TransformEditController"/> (gizmo drags, undo/redo, delete) and
/// <see cref="ScenePersistence"/> (save/build tracking). This class wires them to the base control and
/// exposes the single facade the UI binds to, including the transform-gizmo host (<see cref="ITransformGizmoHost"/>).
/// </summary>
public sealed class D3DImageHost : ViewportControl, ITransformGizmoHost
{
    internal readonly SceneTree Tree;
    internal readonly ViewportCatalogs Catalogs;
    internal readonly DistrictStreamer Streamer;
    internal readonly SelectionController Selection;
    internal readonly TransformEditController Editing;
    internal readonly CollisionEditController CollisionEditing;
    internal readonly TranslokatorEditController CrashEditing;
    internal readonly PropertyEditController PropertyEditing;
    internal readonly ScenePersistence Persistence;
    internal readonly GeometryEditController GeometryEditing;
    internal readonly MaterialEditController MaterialEditing;
    internal readonly MaterialThumbnailRenderer MaterialThumbnails;
    internal readonly Bridge.BridgeSessionController BridgeSession;

    public D3DImageHost()
    {
        Tree = new SceneTree();
        Catalogs = new ViewportCatalogs(this);
        Streamer = new DistrictStreamer(this);
        Selection = new SelectionController(this);
        Editing = new TransformEditController(this);
        CollisionEditing = new CollisionEditController(this);
        CrashEditing = new TranslokatorEditController(this);
        PropertyEditing = new PropertyEditController(this);
        Persistence = new ScenePersistence(this);
        GeometryEditing = new GeometryEditController(this);
        MaterialEditing = new MaterialEditController(this);
        MaterialThumbnails = new MaterialThumbnailRenderer();
        BridgeSession = new Bridge.BridgeSessionController(this);
        BridgeSession.Notice += (message, isError) => BridgeNotice?.Invoke(message, isError);
    }

    // ── Facade: scene tree ──

    /// <summary>Scene tree roots: folder → SDS → frame hierarchy → mesh. Populated incrementally.</summary>
    public ObservableCollection<SceneNode> Roots => Tree.Roots;

    public int MeshCount => Tree.MeshCount;

    public event Action? SceneChanged;

    /// <summary>Render tab filters — all off by default: proxy scenes (whole neighbor/proxy districts),
    /// proxy meshes (embedded proxy_ nodes inside a district's main scene), snow scenes (prefix Z).</summary>
    public bool ShowProxyScenes
    {
        get => Tree.ShowProxyScenes;
        set { Tree.ShowProxyScenes = value; Tree.ApplySceneFilters(); }
    }
    public bool ShowProxyMeshes
    {
        get => Tree.ShowProxyMeshes;
        set { Tree.ShowProxyMeshes = value; Tree.ApplySceneFilters(); }
    }
    public bool ShowSnowScenes
    {
        get => Tree.ShowSnowScenes;
        set { Tree.ShowSnowScenes = value; Tree.ApplySceneFilters(); }
    }

    /// <summary>city_crash layer: spawn objects from the Translokator table (instances). Loads and
    /// unloads additively — toggling never resets the rest of the scene.</summary>
    public bool ShowCrash
    {
        get => Streamer.CrashEnabled;
        set
        {
            if (Streamer.CrashEnabled == value) return;
            Streamer.CrashEnabled = value;
            if (Renderer == null) return; // pre-load: LoadArea/EnterStreaming add the layer themselves
            if (value) Streamer.EnqueueCrashLayer();
            else Streamer.RemoveCrashLayer();
        }
    }

    /// <summary>
    /// Whether crash props are drawn only as far as the game draws them. The Translokator table gives each prop
    /// its own range — a bin 20 m, a billboard 300 m — so honouring it shows the clutter a player would actually
    /// see, and leaves most of the 57 000 copies out of the frame. Off draws every copy at any distance.
    /// On by default; drawing only, nothing is loaded or unloaded.
    /// </summary>
    public bool CrashGameDrawDistance
    {
        get => Rnd?.HonorInstanceDrawDistance ?? true;
        set { if (Rnd != null) Rnd.HonorInstanceDrawDistance = value; }
    }

    /// <summary>Collision layer: decode each resident district's Collisions (.col) and overlay the hulls as a
    /// translucent, wireframe-edged layer. Loads and unloads additively — toggling never resets the scene.</summary>
    public bool ShowCollision
    {
        get => Streamer.CollisionEnabled;
        set => Streamer.SetCollisionEnabled(value);
    }

    /// <summary>.nov overlay: draw each resident district's AI navigation graph and its AI-mesh boxes (one
    /// toggle). Decoded and uploaded at district load; this only gates drawing (no scene reload).</summary>
    public bool ShowNov
    {
        get => Rnd?.ShowNov ?? false;
        set { if (Rnd != null) Rnd.ShowNov = value; }
    }

    /// <summary>.nav overlay: draw each resident district's AI path objects (cover / vault-over / action
    /// markers) as boxes. Decoded and uploaded at district load; this only gates drawing (no scene reload).</summary>
    public bool ShowNavWorld
    {
        get => Rnd?.ShowNavWorld ?? false;
        set { if (Rnd != null) Rnd.ShowNavWorld = value; }
    }

    // ── Facade: catalogs ──

    /// <summary>Main catalog: map areas (districts + interiors from cityareas) for the selector.</summary>
    public IReadOnlyList<MapArea> Areas => Catalogs.Areas;

    public event Action? CatalogReady;

    // ── Facade: loading / streaming ──

    /// <inheritdoc cref="DistrictStreamer.LoadArea"/>
    public void LoadArea(MapArea? area, bool winter, bool wholeMap) => Streamer.LoadArea(area, winter, wholeMap);

    // ── Facade: selection ──

    /// <summary>The active selected node (last clicked) — drives the property panel; null when nothing is selected.</summary>
    public SceneNode? SelectedNode => Selection.Active;

    /// <summary>Every selected node (multi-select). The gizmo transforms all of them as a group.</summary>
    public IReadOnlyList<SceneNode> SelectedNodes => Selection.Selected;

    /// <summary>Raised after the selection changes (either source) so the UI can swap property tabs.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised after a transform edit (gizmo drag or numeric field) so the property fields refresh.</summary>
    public event Action? SelectionTransformChanged;

    /// <summary>Raised once per gizmo drag, the first time it actually moves the selection, with the tool used.
    /// The viewport overlay panel uses it to appear (only on a real gizmo edit) showing that tool's vector.</summary>
    public event Action<GizmoMode>? GizmoEdited;

    /// <summary>Nearest mesh under a screen pixel (viewport ray-pick), resolved to its scene-tree node; null on a miss.</summary>
    public SceneNode? Pick(Point screenPos)
    {
        GpuMesh? gm = PickMesh(screenPos, out _);
        return gm?.Owner as SceneNode;
    }

    /// <inheritdoc cref="SelectionController.Select"/>
    /// <remarks>While a Blender edit session is active the mode is modal: ghosted (non-edited)
    /// objects cannot be selected — leave the session (Tab/Esc) first.</remarks>
    public void Select(SceneNode? node)
    {
        if (node != null && BridgeEditedCount > 0 && !BridgeSession.IsEditedNode(node)) return;
        Selection.Select(node);
    }

    /// <inheritdoc cref="SelectionController.ToggleSelect"/>
    public void ToggleSelect(SceneNode node)
    {
        if (BridgeEditedCount > 0 && !BridgeSession.IsEditedNode(node)) return;
        Selection.ToggleSelect(node);
    }

    // ── Facade: editing ──

    /// <summary>Undo/redo stack for object transforms (gizmo drags + numeric-field commits). Cleared on scene reset.</summary>
    public EditHistory History => Editing.History;

    /// <summary>Reverts the last transform edit (gizmo drag or numeric field).</summary>
    public void Undo() => Editing.History.Undo();

    /// <summary>Re-applies the last undone transform edit.</summary>
    public void Redo() => Editing.History.Redo();

    /// <summary>Whether the selection has anything deletable — a frame object or a collision placement.</summary>
    public bool CanDeleteSelection() =>
        Editing.CanDeleteSelection() || CollisionEditing.HasCollisionSelection() || CrashEditing.HasCrashSelection();

    /// <summary>Deletes the selection: collision placements from their .col, frame objects from their
    /// FrameResource — both undoable and both persisted by Save/Build.</summary>
    public void DeleteSelected()
    {
        CollisionEditing.DeleteSelected(); // collision instances (drops them from the selection)
        CrashEditing.DeleteSelected();     // city_crash placements, in both seasons when linked
        Editing.DeleteSelected();          // frame objects (its DeletableRoots excludes collision)
    }

    /// <summary>Whether the selection has anything duplicable — a static mesh or a collision placement.</summary>
    public bool CanDuplicateSelection() =>
        CollisionEditing.HasCollisionSelection() || CrashEditing.HasCrashSelection() || Editing.CanDuplicateSelection();

    /// <summary>Duplicates the selection: collision placements get copies in their .col, static meshes get
    /// deep, independent copies in their FrameResource — both undoable and persisted.</summary>
    public void DuplicateSelected()
    {
        CollisionEditing.DuplicateSelected(); // collision placements (re-selects the copies)
        CrashEditing.DuplicateSelected();     // city_crash placements (re-selects the copies)
        Editing.DuplicateSelected();          // frame objects (skips collision sources)
    }

    /// <summary>Whether a city_crash archive is loaded, so props can be placed into it.</summary>
    public bool CanPlaceCrashObject => Streamer.CrashLayer != null;

    /// <summary>Whether the loaded crash archive has a seasonal counterpart — without one, "place in all
    /// seasons" has nowhere to place into.</summary>
    public bool HasCrashSeasonTwin => Streamer.CrashLayer?.Document.Twin != null;

    /// <summary>The props the loaded crash archive can place: name, how many copies already stand in the world,
    /// the distance they draw at, and the table row itself (opaque to the UI).</summary>
    public IReadOnlyList<(string Name, int Count, float Distance, object Row)> CrashObjectChoices()
    {
        var choices = new List<(string, int, float, object)>();
        foreach (Formats.Translokator.Object row in CrashEditing.AvailableObjects)
        {
            choices.Add((row.Name.String, row.Instances.Count, row.GridMax, row));
        }
        return choices;
    }

    /// <summary>Places a new copy of a crash prop at a world position. <paramref name="row"/> comes from
    /// <see cref="CrashObjectChoices"/>. Returns false when the archive has no free placement id left.</summary>
    public bool PlaceCrashObject(object row, Vector3 position, bool bothSeasons) =>
        row is Formats.Translokator.Object table
        && CrashEditing.PlaceObject(table, position, bothSeasons) != null;

    /// <summary>
    /// The world point under a screen pixel: where the viewport ray first meets scene geometry (a mesh or a crash
    /// prop), or a point 10 m in front of the camera when it meets nothing — so a click on the sky still places
    /// something the user can see and drag.
    /// </summary>
    public Vector3 PickWorldPoint(Point screenPos)
    {
        (Vector3 origin, Vector3 dir) = BuildViewportRay(screenPos);
        PickMesh(screenPos, out float meshT);
        Streamer.PickCrash(origin, dir, out float crashT);
        float t = MathF.Min(meshT, crashT);
        return origin + dir * (float.IsFinite(t) && t > 0f ? t : 10f);
    }

    /// <inheritdoc cref="CollisionEditController.UnusedHullCount"/>
    public int UnusedHullCount() => CollisionEditing.UnusedHullCount();

    /// <inheritdoc cref="CollisionEditController.RemoveUnusedHulls"/>
    public void RemoveUnusedHulls() => CollisionEditing.RemoveUnusedHulls();

    /// <inheritdoc cref="TransformEditController.Reparent"/>
    public void Reparent(SceneNode node, SceneNode newParent) => Editing.Reparent(node, newParent);

    /// <inheritdoc cref="TransformEditController.RecordTransform"/>
    public void RecordTransform(SceneNode node, Matrix4x4 before, Matrix4x4 after) =>
        Editing.RecordTransform(node, before, after);

    /// <inheritdoc cref="TransformEditController.CommitNodeTransform"/>
    public void CommitNodeTransform(SceneNode node) => Editing.CommitNodeTransform(node);

    /// <inheritdoc cref="PropertyEditController.Commit"/>
    public void CommitPropertyEdit(SceneNode node, PropertyDescriptor descriptor, object? before, object? after) =>
        PropertyEditing.Commit(node, descriptor, before, after);

    /// <summary>Raised after a property edit (or its undo/redo) so a second on-screen editor of the same object
    /// refreshes its values in place.</summary>
    public event Action? SelectionPropertiesChanged;

    internal void RaiseSelectionPropertiesChanged() => SelectionPropertiesChanged?.Invoke();

    // ── Facade: material editing (the Materials tab tiles + the material editor window) ──

    /// <summary>The loaded MTL libraries port — browsing for the material editor window.</summary>
    public Domain.Materials.IMaterialCatalog MaterialCatalog => MaterialEditing.Catalog;

    /// <summary>Raised after any material edit (texture rebind, create/delete, slot reassignment) or its
    /// undo/redo, so the Materials tab tiles and the editor window refresh. UI thread.</summary>
    public event Action? MaterialsChanged;

    internal void RaiseMaterialsChanged() => MaterialsChanged?.Invoke();

    /// <inheritdoc cref="MaterialEditController.SetTexture"/>
    public bool SetMaterialTexture(ulong hash, string slotId, string textureName) =>
        MaterialEditing.SetTexture(hash, slotId, textureName);

    /// <inheritdoc cref="MaterialEditController.AddTextureSlot"/>
    public bool AddMaterialTextureSlot(ulong hash, string slotId) => MaterialEditing.AddTextureSlot(hash, slotId);

    /// <inheritdoc cref="MaterialEditController.RemoveTextureSlot"/>
    public bool RemoveMaterialTextureSlot(ulong hash, string slotId) =>
        MaterialEditing.RemoveTextureSlot(hash, slotId);

    /// <inheritdoc cref="MaterialEditController.SetParameter"/>
    public bool SetMaterialParameter(ulong hash, string paramId, IReadOnlyList<float> values) =>
        MaterialEditing.SetParameter(hash, paramId, values);

    /// <inheritdoc cref="MaterialEditController.AddParameter"/>
    public bool AddMaterialParameter(ulong hash, string paramId, IReadOnlyList<float> values) =>
        MaterialEditing.AddParameter(hash, paramId, values);

    /// <inheritdoc cref="MaterialEditController.CreateMaterial"/>
    public ulong? CreateMaterial(string library, string name) => MaterialEditing.CreateMaterial(library, name);

    /// <inheritdoc cref="MaterialEditController.RenameMaterial"/>
    public ulong? RenameMaterial(ulong hash, string newName) => MaterialEditing.RenameMaterial(hash, newName);

    /// <inheritdoc cref="MaterialEditController.DeleteMaterial"/>
    public bool DeleteMaterial(ulong hash) => MaterialEditing.DeleteMaterial(hash);

    /// <inheritdoc cref="MaterialEditController.AssignSlotMaterial"/>
    public bool AssignSlotMaterial(SceneNode node, int slotIndex, ulong newHash) =>
        MaterialEditing.AssignSlotMaterial(node, slotIndex, newHash);

    /// <summary>Whether <paramref name="node"/> is still part of the loaded scene tree — actions pinned
    /// to a node across scene reloads (the material editor's assign target) validate with this.</summary>
    public bool IsNodeInScene(SceneNode node) => Tree.IsInScene(node);

    /// <inheritdoc cref="MaterialEditController.CountLoadedUses"/>
    public int CountLoadedMaterialUses(ulong hash) => MaterialEditing.CountLoadedUses(hash);

    /// <summary>The map viewport's registered texture folders — the search scope thumbnails and the
    /// editor's preview sphere resolve their .dds names against.</summary>
    public IReadOnlyList<string> TextureFolders => Rnd?.Textures.Folders ?? Array.Empty<string>();

    /// <summary>A cached sphere thumbnail of one material (null when the GPU stack is unavailable).</summary>
    public System.Windows.Media.ImageSource? RenderMaterialThumbnail(Domain.Materials.MaterialInfo info) =>
        MaterialThumbnails.Render(info, TextureFolders);

    // ── Facade: import (File → Import…) ──

    /// <summary>Loaded frame documents (SDS scenes) an import can land in. A hull payload goes to the
    /// .col layer under the same document.</summary>
    public IReadOnlyList<SceneNode> FrameDocumentNodes()
    {
        var result = new List<SceneNode>();
        void Walk(SceneNode node)
        {
            if (node.Source is ISceneDocument and not CollisionDocumentAdapter) result.Add(node);
            foreach (SceneNode c in node.Children) Walk(c);
        }
        foreach (SceneNode root in Tree.Roots) Walk(root);
        return result;
    }

    /// <summary>Whether the archive behind <paramref name="documentNode"/> has a loaded .col layer —
    /// the dialog disables collision rows for a target that cannot take them.</summary>
    public bool HasCollisionLayer(SceneNode documentNode) => FindCollisionLayer(documentNode) != null;

    /// <summary>A sensible landing point for an imported object: a few metres in front of the camera.</summary>
    public Vector3 ImportDropPoint() =>
        Renderer is { } r ? r.Camera.Position + r.Camera.Forward * 15f : Vector3.Zero;

    /// <summary>Outcome of one import: objects landed, hulls landed, and per-item skip reasons.</summary>
    public sealed record ImportReport(int MeshesApplied, int HullsApplied, IReadOnlyList<string> Skipped);

    /// <summary>
    /// Lands a whole imported file — render meshes into the document, hulls into its .col — as ONE
    /// undoable edit, through the same creation pipelines a Blender push uses. Hull cooking happens here
    /// (a subprocess, can take seconds); the caller shows a wait cursor.
    /// </summary>
    public ImportReport ImportBatch(
        SceneNode documentNode,
        IReadOnlyList<MeshObjectPayload> meshes,
        IReadOnlyList<CollisionObjectPayload> hulls)
    {
        var skipped = new List<string>();
        if (documentNode.Source is not ISceneDocument doc || !Tree.IsInScene(documentNode))
        {
            skipped.Add("the target document is no longer loaded");
            return new ImportReport(0, 0, skipped);
        }

        // Hulls first (cooking): each becomes a mint + placement edit pair for the batch below.
        var collisionEdits = new List<IEditAction>();
        int hullsBuilt = 0;
        SceneNode? layer = hulls.Count > 0 ? FindCollisionLayer(documentNode) : null;
        if (hulls.Count > 0 && layer?.Source is not CollisionDocumentAdapter)
        {
            skipped.Add("this archive has no loaded collision (.col) layer — hulls were not imported");
        }
        else if (layer?.Source is CollisionDocumentAdapter colDoc)
        {
            foreach (CollisionObjectPayload payload in hulls)
            {
                CollisionPushAcceptor.Result accepted = CollisionPushAcceptor.TryAccept(colDoc, payload);
                if (accepted.Minted is not { } minted)
                {
                    skipped.Add($"{payload.Name}: {accepted.Refusal ?? "the hull could not be cooked"}");
                    continue;
                }
                if (!TransformMath.TryDecompose(payload.World, out _, out Quaternion rotation, out Vector3 position))
                {
                    skipped.Add($"{payload.Name}: the placement transform could not be read");
                    continue;
                }
                var placement = new CollisionInstance
                {
                    Position = position,
                    Rotation = TransformMath.CollisionEulerFromQuaternion(rotation),
                    Hash = minted.Hash,
                    // A fresh placement owns no visible object; 128 is the stock-data default group (the
                    // same choices the Blender new-hull path makes).
                    Unk4 = -1,
                    Group = 128,
                };
                IReadOnlyList<IEditAction>? edits =
                    CollisionEditing.BuildCreateHull(colDoc, layer, minted.Added, placement, payload.Name);
                if (edits == null)
                {
                    skipped.Add($"{payload.Name}: the collision layer left the scene");
                    continue;
                }
                collisionEdits.AddRange(edits);
                hullsBuilt++;
            }
        }

        var creations = new List<GeometryEditController.CreationItem>(meshes.Count);
        foreach (MeshObjectPayload payload in meshes)
            creations.Add(new GeometryEditController.CreationItem(payload.Id, payload, doc, documentNode));

        List<GeometryEditController.CreationOutcome> outcomes = GeometryEditing.ApplyPushBatch(
            Array.Empty<GeometryEditController.GeometryItem>(),
            Array.Empty<GeometryEditController.TransformItem>(),
            creations, delete: null, collisionEdits);

        int meshesApplied = 0;
        var created = new List<SceneNode>();
        foreach (GeometryEditController.CreationOutcome outcome in outcomes)
        {
            if (outcome.Node != null)
            {
                meshesApplied++;
                created.Add(outcome.Node);
            }
            else
            {
                MeshObjectPayload? source = meshes.FirstOrDefault(m => m.Id == outcome.Id);
                skipped.Add($"{source?.Name ?? outcome.Id}: {outcome.SkipReason ?? "creation failed"}");
            }
        }
        if (created.Count > 0)
        {
            Selection.SetSelection(created, created[^1]);
            created[^1].ExpandAncestors();
        }
        return new ImportReport(meshesApplied, hullsBuilt, skipped);
    }

    // The .col layer loaded under the same document subtree (collision streams in beside its archive).
    private SceneNode? FindCollisionLayer(SceneNode documentNode)
    {
        SceneNode? found = null;
        void Walk(SceneNode node)
        {
            if (found != null) return;
            if (node.Source is CollisionDocumentAdapter) { found = node; return; }
            foreach (SceneNode c in node.Children) Walk(c);
        }
        // The layer may hang beside the frame document under the shared SDS wrapper — search from the
        // document's parent when it has one.
        Walk(documentNode.Parent ?? documentNode);
        return found;
    }

    // ── Facade: Blender bridge ──

    /// <summary>Exports the current selection (children included) into a live Blender session —
    /// the Tab action. Launches Blender when none is connected.</summary>
    public void OpenInBlender() => BridgeSession.OpenInBlender();

    /// <summary>Bridge notices for the UI: (message, isError). Raised on background threads.</summary>
    public event Action<string, bool>? BridgeNotice;

    /// <summary>
    /// A short-lived message for the viewport's notice surface: (message, isError). This is how an edit says it
    /// refused to do something — a hull that cannot be resized, a cook that failed — without a modal dialog
    /// interrupting the drag that caused it. May be raised from any thread.
    /// </summary>
    public event Action<string, bool>? TransientNotice;

    internal void RaiseNotice(string message, bool isError = false) => TransientNotice?.Invoke(message, isError);

    /// <summary>Raised when the bridge edit set changes (objects opened/closed in Blender) so the
    /// title indicator can refresh. May fire on background threads.</summary>
    public event Action? BridgeStateChanged;

    internal void RaiseBridgeStateChanged() => BridgeStateChanged?.Invoke();

    /// <summary>How many objects are currently open in Blender (0 = no active edit session).</summary>
    public int BridgeEditedCount => BridgeSession.ExportedCount;

    /// <summary>Ends the Blender edit session (un-ghosts the scene); the Blender side stays open.</summary>
    public void EndBridgeEditSession() => BridgeSession.EndEditSession();

    // ── Facade: persistence (save / build) ──

    /// <summary>Whether there are edits not yet written to disk — frame documents or MTL libraries
    /// (the title shows a '*' while true).</summary>
    public bool HasUnsavedEdits => Persistence.HasUnsavedEdits || MaterialEditing.HasUnsavedMaterials;

    /// <summary>Whether any archive has edits to repack (saved or not) — gates the Build action.</summary>
    public bool HasBuildableEdits => Persistence.HasBuildableEdits;

    /// <summary>Raised when the unsaved/edited state may have changed, so the title '*' and menus can refresh.</summary>
    public event Action? DirtyChanged;

    /// <summary>Writes the edited frame documents AND every dirty MTL library (each .mtl gets a timestamped
    /// backup + atomic replace, like an .sds build). An MTL failure is reported through the notice surface —
    /// not thrown — so the frame save always completes.</summary>
    public int SaveEdits()
    {
        string? materialError = MaterialEditing.SaveDirtyMaterials(out int savedLibraries);
        int saved = Persistence.SaveEdits();
        if (materialError != null) RaiseNotice("Materials not saved: " + materialError, isError: true);
        return saved + savedLibraries;
    }

    /// <inheritdoc cref="ScenePersistence.PendingBuildArchives"/>
    public IReadOnlyList<FileInfo> PendingBuildArchives() => Persistence.PendingBuildArchives();

    /// <summary>One archive that failed to pack during a build (kept buildable so the user can retry).</summary>
    public readonly record struct BuildFailure(string Archive, string Error);

    /// <summary>Outcome of <see cref="BuildEdits"/>: the archives packed (each with its backup) and any that failed.
    /// A build is resilient — one archive failing does not abort the others (already-packed archives are real,
    /// on-disk changes), and a failed archive stays in the edited set so a later Build retries just it.</summary>
    public readonly record struct BuildReport(
        IReadOnlyList<SdsWriter.PackResult> Packed,
        IReadOnlyList<BuildFailure> Failed);

    /// <inheritdoc cref="ScenePersistence.BuildEdits"/>
    public BuildReport BuildEdits(bool createBackup = true) => Persistence.BuildEdits(createBackup);

    /// <inheritdoc cref="DistrictStreamer.ResetForExternalChange"/>
    public void PrepareForArchiveRestore() => Streamer.ResetForExternalChange();

    // ── ViewportControl hooks ──

    // Environment (sky) + map catalogs, once the renderer exists. Content arrives via LoadArea, not here.
    protected override void OnSceneInitialized() => Catalogs.InitAsync();

    // Per-frame scene advancement (before the base moves the camera) — the streamer's pipeline.
    protected override void OnFrameUpdate(float dt) => Streamer.Tick(dt);

    // Left-click on the render surface selects the mesh under the cursor. Ctrl+click toggles it in the
    // multi-selection (a miss is ignored); a plain click replaces the selection (or clears it on a miss).
    protected override void OnViewportLeftClick(Point pos)
    {
        SceneNode? hit = PickNode(pos);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (hit != null) ToggleSelect(hit);
        }
        else Select(hit);
    }

    // Right-click on the render surface: report what the click hit (mesh or collision hull, or nothing) and
    // where — MainWindow builds the context menu from it (menus are a Views concern, not the viewport's).
    protected override void OnViewportRightClick(Point pos) =>
        ViewportContextMenuRequested?.Invoke(PickNode(pos), pos);

    /// <summary>Raised by a right-click on the render surface with the node under the cursor (null on a miss)
    /// and the click position, so the window can show a context menu for it.</summary>
    public event Action<SceneNode?, Point>? ViewportContextMenuRequested;

    // Nearest node under the cursor across BOTH the frame-mesh pick and the collision-hull pick (the latter only
    // when its overlay is shown — hidden collision isn't pickable). A collision hull wins a tie so, with the
    // overlay up, a hull coincident with its visual mesh is the one selected.
    private SceneNode? PickNode(Point pos)
    {
        GpuMesh? gm = PickMesh(pos, out float meshT);
        (Vector3 origin, Vector3 dir) = BuildViewportRay(pos);
        SceneNode? col = Streamer.PickCollision(origin, dir, out float colT);
        // A crash prop is drawn instanced, so the mesh pick can only ever return its whole cloud (and in fact
        // skips it). Picking one copy is its own pass; it wins over the cloud's prototype at the same spot,
        // which is what makes clicking a street lamp select that lamp.
        SceneNode? crash = Streamer.PickCrash(origin, dir, out float crashT);

        if (col != null && (gm == null || colT <= meshT) && (crash == null || colT <= crashT)) return col;
        if (crash != null && (gm == null || crashT <= meshT)) return crash;
        return gm?.Owner as SceneNode;
    }

    public override void Dispose()
    {
        BridgeSession.Dispose(); // says bye to Blender; the Blender process itself stays up
        MaterialThumbnails.Dispose(); // its own GPU stack, independent of the (possibly deferred) main one
        History.Clear(); // release any still-applied delete's detached-but-held meshes (base.Dispose only frees attached ones)
        if (Streamer.ShutdownDeferred(TearDown)) return; // GPU teardown continues after the stuck loader ends
        base.Dispose(); // Renderer.Dispose releases the attached meshes
    }

    // ── Transform gizmo host (ITransformGizmoHost) ──

    public Matrix4x4 GizmoViewProjection => Renderer?.Camera.ViewProjection ?? Matrix4x4.Identity;
    public Vector3 GizmoCameraPosition => Renderer?.Camera.Position ?? Vector3.Zero;

    /// <summary>Active manipulation tool (driven by the viewport tool shelf). None = select-only.</summary>
    public GizmoMode GizmoMode { get; set; } = GizmoMode.None;

    /// <summary>True when at least one transformable frame object is selected and a manipulation tool is active.</summary>
    public bool HasGizmoTarget => GizmoMode != GizmoMode.None && Selection.AnyTransformable();

    /// <summary>World pivot the gizmo sits at (cached group centroid — see SelectionController).</summary>
    public Vector3 GizmoPivot => Selection.GizmoPivot;

    /// <inheritdoc cref="TransformEditController.GizmoBeginDrag"/>
    public void GizmoBeginDrag() => Editing.GizmoBeginDrag();

    /// <inheritdoc cref="TransformEditController.GizmoApplyWorldDelta"/>
    public void GizmoApplyWorldDelta(Matrix4x4 totalWorldDelta) => Editing.GizmoApplyWorldDelta(totalWorldDelta);

    /// <inheritdoc cref="TransformEditController.GizmoEndDrag"/>
    public void GizmoEndDrag() => Editing.GizmoEndDrag();

    // ── Collaborator plumbing ──

    /// <summary>The base's protected renderer, surfaced to the collaborators.</summary>
    internal SceneRenderer? Rnd => Renderer;

    // Position the camera so the whole district fits in frame (by world-AABB of ready meshes).
    internal void FrameCameraOver(List<GpuMesh> meshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (GpuMesh m in meshes)
        {
            min = Vector3.Min(min, m.BoundsMin);
            max = Vector3.Max(max, m.BoundsMax);
        }
        if (min.X > max.X) return; // empty

        Vector3 center = (min + max) * 0.5f;
        float radius = (max - min).Length() * 0.5f;
        Vector3 eye = center + new Vector3(0f, -radius * 1.3f, radius * 0.8f);
        Renderer!.Camera.LookAt(eye, center);
        Renderer.Camera.Far = radius * 8f + 5000f;
        _orbitDistance = MathF.Max(radius * 1.5f, 5f); // pivot ≈ scene center for gizmo snap / orbit
    }

    internal void RaiseSceneChanged() => SceneChanged?.Invoke();
    internal void RaiseCatalogReady() => CatalogReady?.Invoke();
    internal void RaiseSelectionChanged() => SelectionChanged?.Invoke();
    internal void RaiseSelectionTransformChanged() => SelectionTransformChanged?.Invoke();
    internal void RaiseGizmoEdited(GizmoMode mode) => GizmoEdited?.Invoke(mode);
    internal void RaiseDirtyChanged() => DirtyChanged?.Invoke();
}
