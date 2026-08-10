using System.ComponentModel;
using System.Numerics;
using System.Windows.Data;
using Illusion.Assets.Adapters;
using Illusion.Assets.Cars;
using Illusion.Domain;
using Illusion.Formats.Hashing;
using Illusion.Scene;
using Illusion.Settings;
using Illusion.Viewport;

namespace Illusion.ViewModels;

/// <summary>
/// The car as the thing a modder authored, for the panel's hierarchy: components by name and by nesting,
/// a window under its door, a bumper beside them — in the place the raw frame tree occupies on everything
/// that is not a car.
///
/// <para>
/// It is a VIEW over the <see cref="Car"/> aggregate and owns nothing of the car itself. The stitching, the
/// identities and the faults are the aggregate's; what is here is which rows exist, which one is selected,
/// and the resolve between a component and the frame the viewport draws — because the viewport draws frames
/// and this tree lists components, so one of the two always had to translate.
/// </para>
/// <para>
/// Read-only in this form. Nothing here writes to the car.
/// </para>
/// </summary>
public sealed class ComponentTreeViewModel : INotifyPropertyChanged
{
    private readonly D3DImageHost _viewport;
    private readonly Dictionary<long, ComponentRowViewModel> _rowsById = [];

    private ISceneDocument? _document;
    private bool _documentHasNoCar;
    private string? _archive;

    public ComponentTreeViewModel(D3DImageHost viewport) => _viewport = viewport;

    /// <summary>The staged car, or null when what is open is not one. Everything below is a view over it.</summary>
    public Car? Car { get; private set; }

    /// <summary>Whether the panel has a car to show components of — what the <c>Components | Raw</c> switch
    /// appears for, and what decides that every other archive still opens on the frame tree.</summary>
    public bool HasCar => Car != null;

    /// <summary>The components nothing hangs off: the parts at the top of the prefab's parent link, and the
    /// bare components, which no link names and which therefore stand alongside the rest.</summary>
    public IReadOnlyList<ComponentRowViewModel> Roots { get; private set; } = [];

    /// <summary>The row the selection is on, or null when the selection resolves to no component.</summary>
    public ComponentRowViewModel? Selected { get; private set; }

    /// <summary>Raised when <see cref="Selected"/> moved, so the tree can scroll it into view.</summary>
    public event Action<ComponentRowViewModel>? SelectionShown;

    // ── the switch ──

    private bool _isRaw;

    /// <summary>
    /// Whether the panel shows the frame tree instead of the components. Remembered per archive: peeking at
    /// the frames to check a name table or a resource envelope is a thing modders do constantly, and being
    /// put back on the components every time the car is reopened would make the switch a chore rather than an
    /// escape hatch.
    /// </summary>
    public bool IsRaw
    {
        get => _isRaw;
        set
        {
            if (_isRaw == value) return;
            _isRaw = value;
            Remember(value);
            Raise(nameof(IsRaw));
            Raise(nameof(IsComponents));
        }
    }

    /// <summary>The other half of the switch, so both segments can bind their own checked state.</summary>
    public bool IsComponents
    {
        get => !_isRaw;
        set { if (value) IsRaw = false; }
    }

    /// <summary>Which tree is actually on screen. A car the modder left on Raw shows the frames; everything
    /// that is not a car has no components to show and shows the frames whatever the switch last said.</summary>
    public bool ShowsComponents => HasCar && !_isRaw;

    // ── reading the car ──

    /// <summary>
    /// Re-stitches the staged car, or clears the tree when what is staged is not one.
    ///
    /// <para>
    /// Called whenever the scene changes, which is also whenever the car can have become a different car — a
    /// bridge push, an archive rolled back to a backup, another resource opened. Identities are carried
    /// across from the previous stitch of the SAME document, so a re-stitch that changes nothing does not
    /// renumber the car under the selection.
    /// </para>
    /// </summary>
    /// <param name="document">The one staged frame document, or null when the stage does not hold exactly
    /// one — an empty stage, or a district holding a dozen archives at once.</param>
    public void Refresh(ISceneDocument? document)
    {
        // A document already known to carry no car is not asked again: on a district the scene changes
        // constantly and the question would cost a manifest read every time, for an answer that cannot have
        // changed while the same document is staged.
        if (document == null)
        {
            if (Car == null && _document == null) return;
            Adopt(null, null, noCar: false);
            return;
        }
        if (_documentHasNoCar && ReferenceEquals(document, _document)) return;

        Car? read = null;
        bool failed = false;
        try
        {
            read = Assets.Cars.Car.ReadStaged(
                document, previous: ReferenceEquals(document, _document) ? Car : null);
        }
        catch (Exception)
        {
            // An archive whose car will not read is a panel that shows the frame tree, not a crash — the same
            // answer the Prefab tab gives to the same file. Caught WIDE on purpose: the read runs inside the
            // scene-changed handler, the format layer throws several unrelated exception types out of the
            // native reader, and there is no dispatcher handler above this to survive one.
            //
            // The faults of a car that reads only PARTLY are a different thing entirely: those are the
            // aggregate's, and they come back as rows.
            failed = true;
        }
        // A failure is not an answer. Memoing one would turn a working copy that was locked for a moment —
        // by a build, by a virus scanner — into a car that has no components for the rest of the session.
        Adopt(read, document, noCar: !failed && read == null);
    }

    // Everything is settled BEFORE the first notification goes out. A listener that reacts to the switch
    // moving reads the rows, so raising IsRaw while the rows were still the previous car's would have it
    // resolve a selection against a tree that no longer exists.
    /// <param name="noCar">Whether the read actually ANSWERED that this archive holds no car — as opposed to
    /// having failed to read one. Only an answer is memoed.</param>
    private void Adopt(Car? car, ISceneDocument? document, bool noCar)
    {
        // Which branches the modder had folded away, so a re-stitch does not spring the whole tree open
        // again. Keyed on identity, which is what the aggregate carries across a re-stitch for exactly this
        // kind of reason.
        Dictionary<long, bool> folded = Folded();

        _document = document;
        _documentHasNoCar = noCar;
        Car = car;
        _rowsById.Clear();
        Roots = car == null ? [] : Build(car, folded);
        RootsView = CollectionViewSource.GetDefaultView(Roots);
        RootsView.Filter = o => o is ComponentRowViewModel row && row.HasSearchMatch;
        Selected = null;
        SelectedCollision = null;

        string? archive = document?.SourceArchive.Name;
        bool moved = !string.Equals(archive, _archive, StringComparison.OrdinalIgnoreCase);
        if (moved)
        {
            _archive = archive;
            // A new archive brings its own remembered position with it, without going through IsRaw — that
            // setter WRITES the preference, and adopting one would rewrite it as if the user had chosen it.
            _isRaw = archive != null && Recalled(archive);
        }

        Raise(nameof(Car));
        Raise(nameof(HasCar));
        Raise(nameof(Roots));
        Raise(nameof(RootsView));
        Raise(nameof(ShowsComponents));
        Raise(nameof(Selected));
        Raise(nameof(SelectedCollision));
        if (!moved) return;
        Raise(nameof(IsRaw));
        Raise(nameof(IsComponents));
    }

    // The rows, nested off the aggregate's own parent links. Roots come out in the order the aggregate
    // produced them — deform parts in the prefab's file order, then the bare components in rig order — so two
    // reads of the same car list it the same way round.
    private List<ComponentRowViewModel> Build(Car car, Dictionary<long, bool> folded)
    {
        var roots = new List<ComponentRowViewModel>();
        foreach (CarComponent component in car.Roots) roots.Add(Row(component, parent: null, folded));
        return roots;
    }

    private ComponentRowViewModel Row(
        CarComponent component, ComponentRowViewModel? parent, Dictionary<long, bool> folded)
    {
        var row = new ComponentRowViewModel(component, parent);
        if (folded.TryGetValue(component.Id.Value, out bool wasFolded) && wasFolded) row.IsExpanded = false;
        _rowsById[component.Id.Value] = row;
        foreach (CarCollision collision in component.Collisions)
        {
            row.AddCollision(new CollisionRowViewModel(collision, row));
        }
        foreach (CarComponent child in component.Children) row.AddChild(Row(child, row, folded));
        return row;
    }

    // The branches that were closed, by identity. A car opens fully expanded, so only the closed ones are
    // worth carrying — a row the previous stitch never had simply opens.
    private Dictionary<long, bool> Folded()
    {
        var folded = new Dictionary<long, bool>();
        foreach ((long id, ComponentRowViewModel row) in _rowsById)
        {
            if (!row.IsExpanded) folded[id] = true;
        }
        return folded;
    }

    /// <summary>The row an identity names, or null when this car has no such component.</summary>
    public ComponentRowViewModel? RowOf(ComponentId id) =>
        id.IsSet && _rowsById.TryGetValue(id.Value, out ComponentRowViewModel? found) ? found : null;

    /// <summary>
    /// Narrows the tree to the rows matching the panel's search box, opening every branch that holds a match.
    /// The query itself is the frame tree's — one box drives whichever tree is on screen, so switching
    /// between them does not lose what was typed.
    /// </summary>
    public void ApplySearch()
    {
        bool searching = !string.IsNullOrWhiteSpace(SceneSearch.Query);
        foreach (ComponentRowViewModel root in Roots) Narrow(root, searching);
        RootsView?.Refresh();
    }

    private static void Narrow(ComponentRowViewModel row, bool searching)
    {
        foreach (ComponentRowViewModel child in row.Children) Narrow(child, searching);
        row.ChildrenView.Refresh();
        // A collision row has nothing under it and follows its component, so there is nothing to narrow
        // there — filtering one out would empty a matching door of the very thing it is made of.
        // While searching, the tree opens onto the matches. Clearing the query does NOT re-open everything —
        // the same thing the frame tree does, and the reason is the same: which branches are folded is the
        // modder's, and a search is not permission to unfold the lot.
        if (searching) row.IsExpanded = row.Children.Any(c => c.HasSearchMatch);
    }

    /// <summary>The roots as the tree binds them — narrowed by the same search.</summary>
    public ICollectionView? RootsView { get; private set; }

    // ── frame ⇄ component ──

    /// <summary>
    /// The component a scene node belongs to.
    ///
    /// <para>
    /// A bone IS a component, and so the nearest bone at or above the node is the answer for everything the
    /// rig carries — a bone glyph clicked in the viewport, a Dummy or a Point hung off one, a marker's frame.
    /// Anything else the car's document holds resolves to the BODY: a car is one skinned mesh, so a click on
    /// its geometry can only ever name the model as a whole, and the body is the component that owns it.
    /// </para>
    /// </summary>
    public CarComponent? ComponentOf(SceneNode? node)
    {
        if (Car is not { } car || node == null) return null;
        for (SceneNode? at = node; at != null; at = at.Parent)
        {
            if (at.Source is BoneNodeAdapter bone && bone.BoneName.Length > 0
                && car.ComponentOfBone(Fnv64.Hash(bone.BoneName)) is { } found)
            {
                return found;
            }
        }
        return BelongsToCar(node) ? car.Body : null;
    }

    /// <summary>
    /// The scene node a component is — the bone the viewport draws and the gizmo moves.
    ///
    /// <para>
    /// Null for a component whose bone does not resolve, which is the broken one the fault path exists for:
    /// there is nothing in the scene to select, and the row stays selected in the tree alone.
    /// </para>
    /// </summary>
    public SceneNode? NodeOf(CarComponent? component)
    {
        if (component == null || !component.BoneResolves) return null;
        foreach (SceneNode root in _viewport.Tree.Roots)
        {
            if (FindBone(root, component.BoneHash) is { } found) return found;
        }
        return null;
    }

    private static SceneNode? FindBone(SceneNode node, ulong boneHash)
    {
        if (node.Source is BoneNodeAdapter bone && bone.BoneName.Length > 0
            && Fnv64.Hash(bone.BoneName) == boneHash)
        {
            return node;
        }
        foreach (SceneNode child in node.Children)
        {
            if (FindBone(child, boneHash) is { } found) return found;
        }
        return null;
    }

    // Whether the node hangs under the document the car was stitched from. Without it, selecting something in
    // ANOTHER archive staged beside the car would land on the car's body — a row that has nothing to do with
    // what was clicked.
    private bool BelongsToCar(SceneNode node) =>
        _document != null && node.OwningDocumentNode()?.Source is ISceneDocument owner
        && ReferenceEquals(owner, _document);

    // ── selection ──

    /// <summary>
    /// Points the tree at whatever the viewport now has selected, resolving the frame to its component.
    ///
    /// <para>
    /// One direction only: it never selects anything in the viewport, because it is called FROM the
    /// viewport's own selection change and doing so would close the loop.
    /// </para>
    /// </summary>
    public void ShowSelection(SceneNode? node) =>
        Highlight(ComponentOf(node) is { } component ? RowOf(component.Id) : null);

    /// <summary>
    /// Selects a component from the tree: hands its bone to the viewport, so the component is highlighted in
    /// the scene and the property tabs describe it — and then takes the last word on which ROW is lit.
    ///
    /// <para>
    /// The last word matters twice, and both are cases where waiting for the viewport's own report would come
    /// back wrong. A bone TWO components claim resolves back to the first of them, so the highlight would
    /// jump off the row that was just clicked — on exactly the broken car this view exists to diagnose. And a
    /// bone the viewport already holds raises nothing at all, so re-clicking a row would be a dead click; the
    /// frame tree drives its own single-select directly for that same reason.
    /// </para>
    /// <para>
    /// A component whose bone does not resolve has no frame to hand over, and the viewport's selection is
    /// CLEARED rather than left where it was: the property tabs, the gizmo and Delete all act on it, and
    /// leaving them pointed at the last frame while the tree says this row is selected is how a modder
    /// deletes the wrong thing.
    /// </para>
    /// </summary>
    public void Select(ComponentRowViewModel? row)
    {
        if (row == null) return;
        SceneNode? node = NodeOf(row.Component);
        _viewport.Select(node);
        // A modal Blender edit session refuses to select anything outside it. Lighting the row anyway would
        // say the selection moved when it did not.
        if (node != null && !ReferenceEquals(_viewport.SelectedNode, node)) return;
        Highlight(row);
    }

    // Which row is lit, and the one place that is decided. Raises SelectionShown only when the selection
    // moved to a DIFFERENT component: a re-stitch rebuilds every row, and scrolling the tree back to the
    // same component after each unrelated edit is the panel moving the user where they did not ask to go.
    private void Highlight(ComponentRowViewModel? row)
    {
        // A collision row is never left lit beside a component: whatever moves the highlight settles which
        // ONE row the menu acts on, and two lit rows would make "Remove collision" a question about which.
        if (SelectedCollision != null)
        {
            SelectedCollision.IsSelected = false;
            SelectedCollision = null;
            Raise(nameof(SelectedCollision));
        }
        if (ReferenceEquals(row, Selected)) return;
        bool moved = Selected?.Id != row?.Id;

        if (Selected != null) Selected.IsSelected = false;
        Selected = row;
        if (row != null)
        {
            row.IsSelected = true;
            row.ExpandAncestors();
        }
        Raise(nameof(Selected));
        if (row != null && moved) SelectionShown?.Invoke(row);
    }

    // ── editing ──

    /// <summary>The collision row the menu acts on, or null when the selection is a component.</summary>
    public CollisionRowViewModel? SelectedCollision { get; private set; }

    /// <summary>
    /// Points the menu at a collision, and the viewport at the component that carries it.
    ///
    /// <para>
    /// The viewport draws frames and has nothing to select for a collision — a self-describing volume has no
    /// frame at all, and the mirror stub of a solid one is a copy the modder is deliberately never shown. So
    /// the component's own bone is what the gizmo, the property tabs and Delete stay pointed at.
    /// </para>
    /// </summary>
    public void Select(CollisionRowViewModel? row)
    {
        if (row == null) return;
        _viewport.Select(NodeOf(row.Component.Component));
        // …and the tree's highlight is THIS row, not the component's. Handing the bone over raises the
        // viewport's own selection change, which lights the component row on the way back through
        // ShowSelection, so the clearing has to come after it rather than before.
        Highlight(null);
        SelectedCollision = row;
        row.IsSelected = true;
        Raise(nameof(SelectedCollision));
    }

    /// <summary>Raised after an edit has been written and the car re-stitched, so the panel can rebuild the
    /// rows around a tree whose components may have gained or lost something.</summary>
    public event Action? CarEdited;

    /// <summary>
    /// Gives a component one more collision, by role and shape.
    ///
    /// <para>
    /// The aggregate is the only path from here to bytes: it derives the stored type, the space, the extents,
    /// the ItemDesc record and the mirror stub, and this hands the result to the three things that always
    /// follow an edit in this editor — the undo stack, the build list, and the modder.
    /// </para>
    /// </summary>
    public void AddCollision(
        ComponentRowViewModel? row, CarCollisionRole role, CarCollisionShape shape,
        Vector3 size, Vector3 position, out string? refusal)
    {
        refusal = null;
        if (row == null) { refusal = "no car is open"; return; }
        if (Reread(row.Id) is not (Car car, ComponentRowViewModel fresh))
        {
            refusal = "no car is open";
            return;
        }

        CarCollisionEdit? edit = car.AddCollision(fresh.Component, role, shape, size, position, out refusal);
        if (edit == null) return;
        Commit(car, edit, ref refusal);
        // The layer goes on once the collision is really there: one that exists and is invisible reads as
        // "nothing happened", and looking at where the box landed is the whole reason for typing a position
        // rather than guessing one. After the commit, so the overlay redraws from the file that was written.
        if (refusal == null) _viewport.ShowPartShapes = true;
    }

    /// <summary>Resizes and moves a collision, in its component's own space.</summary>
    public void SetCollision(
        CollisionRowViewModel? row, Vector3 size, Vector3 position, out string? refusal)
    {
        refusal = null;
        if (row == null) { refusal = "no car is open"; return; }
        if (Collision(row) is not (Car car, CarCollision collision))
        {
            refusal = "that collision is no longer there";
            return;
        }

        // The turn the collision already has is kept: the modder typed a position, not a rotation, and
        // throwing away a shipped volume's orientation because its position moved would be a second edit
        // nobody asked for.
        Matrix4x4 placement = collision.Placement;
        placement.Translation = position;

        CarCollisionEdit? edit = car.SetCollision(collision, size, placement, out refusal);
        if (edit == null) return;
        Commit(car, edit, ref refusal);
    }

    /// <summary>Takes a collision off its component, and its ItemDesc record and mirror stub with it when
    /// nothing else names them.</summary>
    public void RemoveCollision(CollisionRowViewModel? row, out string? refusal)
    {
        refusal = null;
        if (row == null) { refusal = "no car is open"; return; }
        if (Collision(row) is not (Car car, CarCollision collision))
        {
            refusal = "that collision is no longer there";
            return;
        }

        CarCollisionEdit? edit = car.RemoveCollision(collision, out refusal);
        if (edit == null) return;
        Commit(car, edit, ref refusal);
    }

    /// <summary>
    /// Re-reads the car from the working copy before an edit is made on it, and finds the row again in what
    /// comes back.
    ///
    /// <para>
    /// This is not belt and braces. The aggregate writes the WHOLE prefab from the copy it holds, and that
    /// copy is only refreshed when the SCENE changes — while four other modules write the same file directly
    /// and raise nothing: the Prefab tab repointing a seat, the collision overlay changing a volume's type,
    /// a part being added. Editing on top of a stale read would put those changes back the way they were, and
    /// the modder would be told the collision was added.
    /// </para>
    /// </summary>
    private (Car Car, ComponentRowViewModel Row)? Reread(ComponentId id)
    {
        Restitch();
        if (Car is not { } car || RowOf(id) is not { } row) return null;
        return (car, row);
    }

    /// <summary>The same, for a row that names a collision: the component is found again by identity and the
    /// collision by its place in that component's list.</summary>
    private (Car Car, CarCollision Collision)? Collision(CollisionRowViewModel row)
    {
        int at = row.Component.Collisions.ToList().FindIndex(c => ReferenceEquals(c, row));
        if (Reread(row.Component.Id) is not (Car car, ComponentRowViewModel fresh)) return null;
        CarCollision? found = fresh.Component.Collisions.ElementAtOrDefault(at);
        return found == null ? null : (car, found);
    }

    /// <summary>
    /// Writes one intent through, and tells everything that has to hear about it.
    ///
    /// <para>
    /// A save that is REFUSED puts the car back first. The aggregate refuses whole — it verifies the prefab by
    /// writing it and reading it back before a byte reaches a file — so a refusal here means nothing was
    /// written, and leaving the edit in memory would let the next save carry it in unnoticed.
    /// </para>
    /// </summary>
    private void Commit(Car car, CarCollisionEdit edit, ref string? refusal)
    {
        CarSave saved = car.Save();
        if (!saved.Ok)
        {
            refusal = string.Join("; ", saved.Lost);
            car.Restore(edit.Before);
            return;
        }

        _viewport.History.Push(new CarCollisionEditAction(edit, () => Car, Restitch,
            why => _viewport.RaiseNotice("that could not be taken back: " + why, isError: true)));
        if (_document != null) _viewport.MarkArchiveModified(_document.SourceArchive);
        Restitch();
        _viewport.RaiseNotice(edit.What + ". Build to write it into the archive.");
    }

    /// <summary>
    /// Re-stitches the car and rebuilds the rows over it — what every edit ends with, and what an undo of one
    /// ends with too.
    ///
    /// <para>
    /// The collision overlay is redrawn from the file rather than from the cache, because the file is what
    /// just changed and the overlay is where a modder sees whether the box landed where they meant it to.
    /// </para>
    /// </summary>
    private void Restitch()
    {
        SelectedCollision = null;
        Refresh(_document);
        _viewport.RefreshCarCollisionOverlay();
        CarEdited?.Invoke();
    }

    // ── remembering the switch ──

    private static bool Recalled(string archive) =>
        UserSettings.Current.RawSceneTree.Contains(archive.ToLowerInvariant(), StringComparer.Ordinal);

    private void Remember(bool raw)
    {
        if (_archive is not { Length: > 0 } archive) return;
        string key = archive.ToLowerInvariant();
        UserSettings.Update(settings =>
        {
            settings.RawSceneTree.RemoveAll(a => string.Equals(a, key, StringComparison.Ordinal));
            if (raw) settings.RawSceneTree.Add(key);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
