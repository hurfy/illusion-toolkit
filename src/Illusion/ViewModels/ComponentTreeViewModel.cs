using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Windows.Data;
using Illusion.Assets.Adapters;
using Illusion.Assets.Cars;
using Illusion.Domain;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
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

    // ── the level of detail ──

    private int _lod;

    /// <summary>
    /// Which level of detail the tree lists — the whole car at once, never one component.
    ///
    /// <para>
    /// The far level is a SHELL rather than a second copy of the car: 4882 of the 5046 bones that carry
    /// geometry near carry none far, and the tree there is three rows on the average car. That emptiness is
    /// the answer the switch exists to give — what survives past fifty metres — and not a fault.
    /// </para>
    /// <para>
    /// Moving it re-stitches the car at the new level. The car itself is the same car at either level — the
    /// components, the lookups and the faults do not move — so the SELECTION survives on its own: the
    /// viewport never lets go of the bone, and the panel re-resolves it onto the new rows the way it does
    /// after any other rebuild. A component this level does not draw simply has no row to light.
    /// </para>
    /// <para>
    /// NOT remembered per archive, unlike <see cref="IsRaw"/>: a far level is a thing a modder looks at
    /// rather than works in, and reopening a car onto its shell would read as half the car having gone
    /// missing.
    /// </para>
    /// </summary>
    public int Lod
    {
        get => _lod;
        set
        {
            // The near level is always reachable, whatever the read last said: a car that failed to stitch
            // reports no levels at all, and a switch that could not be put back would leave the tree on a
            // level nothing can be seen at for the rest of the session.
            if (_lod == value || value < 0 || (value > 0 && value >= Lods)) return;
            _lod = value;
            // Through the ordinary read, so a switch takes exactly the path a scene change does — and with
            // the current car as the previous stitch, which is what carries the identities across.
            Refresh(_document);
            Raise(nameof(Lod));
        }
    }

    /// <summary>How many levels this car carries: 2 on 82 of the 85 shipped cars, 1 on the other 3.</summary>
    public int Lods => Car?.Lods ?? 0;

    /// <summary>Whether there is a second level to switch to at all. False on a car carrying one, where the
    /// switch is shown disabled rather than hidden — a level that does not exist is not offered.</summary>
    public bool CanSwitchLod => Lods > 1;

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
        // While a push is landing, the car is stitched ONCE and at the END of it, by the push itself. The
        // apply raises a scene change from inside its own middle — a mesh swapped, a frame deleted — and
        // stitching a car whose frames are half rewritten costs a full rebuild for a tree nobody sees and a
        // diagnosis nobody could act on. See PushLanding.
        //
        // …unless a DIFFERENT document has arrived, which is the stage moving out from under the push: the
        // modder can open another archive while the apply runs, and holding the old one would leave the tree
        // describing a car that is no longer on screen. That one is followed at once, and what the push had
        // to say about the previous car goes with it.
        if (_landing)
        {
            if (ReferenceEquals(document, _document)) return;
            _landing = false;
            _selectedBeforePush = default;
        }

        // A car that is not the one the switch was moved on opens on its NEAR level. The level of detail is
        // a look at what survives past fifty metres rather than a setting, and the next archive may not even
        // carry a far level to carry it into — 3 of the 85 shipped cars ship one.
        int wasLod = _lod;
        if (!string.Equals(
                document?.SourceArchive.Name, _archive, StringComparison.OrdinalIgnoreCase))
        {
            _lod = 0;
        }

        // A document already known to carry no car is not asked again: on a district the scene changes
        // constantly and the question would cost a manifest read every time, for an answer that cannot have
        // changed while the same document is staged.
        if (document == null)
        {
            if (Car == null && _document == null) return;
            Adopt(null, null, noCar: false);
            if (_lod != wasLod) Raise(nameof(Lod));
            return;
        }
        if (_documentHasNoCar && ReferenceEquals(document, _document)) return;

        bool failed = false;
        Car? read = Read(document, ref failed);
        // The car may have lost the level out from under the switch — a bridge push can leave one where it
        // had two. Reading again at the near level costs a stitch on the rare push that does it, and the
        // alternative is a tree of a level that is not there, which draws as a car with nothing in it.
        if (read != null && _lod > 0 && _lod >= read.Lods)
        {
            _lod = 0;
            read = Read(document, ref failed);
        }
        // A failure is not an answer. Memoing one would turn a working copy that was locked for a moment —
        // by a build, by a virus scanner — into a car that has no components for the rest of the session.
        Adopt(read, document, noCar: !failed && read == null);
        // Only when the level really moved under the read — the switch's own setter raises its own change,
        // and a scene change on an unmoved switch must not send the panel round the rebuild again.
        if (_lod != wasLod) Raise(nameof(Lod));
    }

    // ── a push from Blender ──

    /// <summary>Whether a push is being applied right now, and the tree is therefore waiting for the end of
    /// it rather than following the scene changes it raises on its way through.</summary>
    private bool _landing;

    /// <summary>What the modder had selected when the push began — which component, and what it was called
    /// then, so that one the push took away can be named in the past tense.</summary>
    private (ComponentId Id, string Name) _selectedBeforePush;

    /// <summary>
    /// A push from Blender is about to change the frame graph this car is stitched from.
    ///
    /// <para>
    /// What the modder had selected is remembered HERE rather than read back afterwards. The re-stitch that
    /// follows resolves the viewport's own selection onto the new rows, so by the time the push has landed
    /// the tree can no longer say what it was looking at before — which is exactly the thing a component the
    /// push took away has to be reported against.
    /// </para>
    /// <para>
    /// A row BENEATH a component — a collision, a marker — is remembered as its component. Those rows are
    /// rebuilt from the car and the push may well have taken one away, so the component is the nearest thing
    /// that is still true; the selection comes back one level up rather than not at all.
    /// </para>
    /// </summary>
    public void PushLanding()
    {
        ComponentRowViewModel? row = Selected ?? SelectedChild?.Component;
        _selectedBeforePush = row == null ? (ComponentId.None, "") : (row.Id, row.Name);
        _landing = true;
    }

    /// <summary>
    /// …and it has landed: the resolver runs again over the car as it now stands.
    ///
    /// <para>
    /// It has to. Identity is rebuilt on every open and must equally be rebuilt on every push, because a push
    /// can change the very bones the stitching keys on — a renamed bone, a deleted one, a new one — and a tree
    /// left as it was would be a picture of a car the file no longer holds.
    /// </para>
    /// <para>
    /// The SELECTION is put back by identity rather than by bone: a component whose bone the push renamed is
    /// the same component, and asking the scene for it would find nothing under the name it used to have. One
    /// the push took away cannot be put back, and is said out loud instead — a selection that quietly stops
    /// existing is how a modder goes on typing numbers into a component that is not there.
    /// </para>
    /// </summary>
    /// <param name="movedBones">The bones the push wrote a new rest transform into, by name. Empty for a push
    /// that was about geometry alone, which is most of them.</param>
    public void PushLanded(IReadOnlyList<string>? movedBones = null)
    {
        _landing = false;
        Restitch();

        var said = new List<string>();
        // What the push moved, in the modder's own terms. A bone IS a component, and the bridge names bones —
        // so the two are joined HERE, through the aggregate's own lookup, and never by a second mapping the
        // bridge keeps of its own. That is what "the same component" means when the push says one thing and
        // the tree shows another.
        if (Car is { } car && movedBones is { Count: > 0 })
        {
            var seen = new HashSet<long>();
            var moved = new List<string>();
            foreach (string bone in movedBones)
            {
                if (car.ComponentOfBone(Fnv64.Hash(bone)) is not { } component) continue;
                if (seen.Add(component.Id.Value)) moved.Add(component.Name);
            }
            if (moved.Count > 0)
            {
                said.Add($"{moved.Count.ToString(CultureInfo.InvariantCulture)} component(s) moved: "
                    + string.Join(", ", moved.Take(6))
                    + (moved.Count > 6 ? ", …" : ""));
            }
        }

        (ComponentId id, string name) = _selectedBeforePush;
        _selectedBeforePush = default;
        if (id.IsSet)
        {
            if (RowOf(id) is { } row)
            {
                // Through the ordinary selection, so the viewport and the property tabs follow it — and then
                // the row is lit whatever the viewport made of that. A push lands precisely while a Blender
                // session is open, and a session refuses to select anything outside the set it holds: waiting
                // for the viewport's report back would leave the tree with nothing selected after every push,
                // which is the silence this whole method exists to prevent.
                Select(row);
                Highlight(row);
            }
            else
            {
                said.Add($"\"{name}\" is no longer a component of this car — the push took it away, so "
                    + "nothing is selected where it was");
            }
        }
        if (said.Count > 0) _viewport.RaiseNotice(string.Join("\n", said));
    }

    private Car? Read(ISceneDocument document, ref bool failed)
    {
        try
        {
            return Assets.Cars.Car.ReadStaged(
                document, _lod, previous: ReferenceEquals(document, _document) ? Car : null);
        }
        catch (Exception)
        {
            // An archive whose car will not read is a panel that shows the frame tree, not a crash. Caught
            // WIDE on purpose: the read runs inside the
            // scene-changed handler, the format layer throws several unrelated exception types out of the
            // native reader, and there is no dispatcher handler above this to survive one.
            //
            // The faults of a car that reads only PARTLY are a different thing entirely: those are the
            // aggregate's, and they come back as rows.
            failed = true;
            return null;
        }
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
        // After the rows, because a fault leads to the row it is about and the rows have to exist first.
        Faults = car == null
            ? []
            : [.. car.Faults.Select(f => new FaultRowViewModel(f, RowOf(f.Component)))];
        RootsView = CollectionViewSource.GetDefaultView(Roots);
        RootsView.Filter = o => o is ComponentRowViewModel row && row.HasSearchMatch;
        Selected = null;
        SelectedChild = null;

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
        // How many levels there are to switch between belongs to the car that has just arrived, and so does
        // whether the switch is offered at all — a car carrying one level shows it disabled.
        Raise(nameof(Lods));
        Raise(nameof(CanSwitchLod));
        Raise(nameof(Selected));
        RaiseChild();
        Raise(nameof(Faults));
        Raise(nameof(HasFaults));
        Raise(nameof(FaultSummary));
        Raise(nameof(FaultsOpen));
        if (!moved) return;
        Raise(nameof(IsRaw));
        Raise(nameof(IsComponents));
    }

    /// <summary>
    /// The rows, nested off the aggregate's own parent links. Roots come out in the order the aggregate
    /// produced them — deform parts in the prefab's file order, then the bare components in rig order — so
    /// two reads of the same car list it the same way round.
    ///
    /// <para>
    /// This is the ONE place the level of detail decides anything: the aggregate describes the whole car at
    /// either level, and the tree lists the components that are drawn at the one on screen
    /// (<see cref="Car.DrawnHere"/>). A component whose parent is not drawn here stands at the top rather
    /// than disappearing under it — the far level keeps a door and drops the body panel it hangs off often
    /// enough that hiding the survivors would empty a tree that has something in it.
    /// </para>
    /// </summary>
    private List<ComponentRowViewModel> Build(Car car, Dictionary<long, bool> folded)
    {
        var roots = new List<ComponentRowViewModel>();
        foreach (CarComponent component in car.Roots) Walk(car, component, parent: null, roots, folded);
        return roots;
    }

    // Down the aggregate's own tree, listing what this level draws. A component the level does not draw is
    // skipped and its children are offered to the nearest one above it that IS drawn — to the top when there
    // is none — so a door that survives past fifty metres is in the tree even where the panel it hangs off is
    // not. At the near level nothing is skipped and this is the walk that was here before.
    private void Walk(
        Car car, CarComponent component, ComponentRowViewModel? parent, List<ComponentRowViewModel> roots,
        Dictionary<long, bool> folded)
    {
        ComponentRowViewModel? under = parent;
        if (car.DrawnHere(component))
        {
            under = Row(car, component, parent, folded);
            if (parent == null) roots.Add(under); else parent.AddChild(under);
        }
        foreach (CarComponent child in component.Children) Walk(car, child, under, roots, folded);
    }

    private ComponentRowViewModel Row(
        Car car, CarComponent component, ComponentRowViewModel? parent, Dictionary<long, bool> folded)
    {
        var row = new ComponentRowViewModel(component, parent, car.FaultsOf(component.Id));
        if (folded.TryGetValue(component.Id.Value, out bool wasFolded) && wasFolded) row.IsExpanded = false;
        _rowsById[component.Id.Value] = row;
        // What the component IS before what it is made of: how it behaves when hit, then how it crumples. A
        // bare component has neither — there is no deform part for them to be on — and one with a part but no
        // handle gets the row that says it does not crumple, which is the commoner answer of the two.
        if (component.DamageFields.Count > 0) row.AddDamage(new ComponentDamageRowViewModel(row));
        if (component.Crumples)
        {
            foreach (CarHandle handle in component.Handles)
            {
                row.AddHandle(new ComponentHandleRowViewModel(handle, row));
            }
        }
        else if (!component.IsBare)
        {
            row.AddHandle(new ComponentHandleRowViewModel(handle: null, row));
        }
        foreach (CarCollision collision in component.Collisions)
        {
            row.AddCollision(new CollisionRowViewModel(collision, row));
        }
        foreach (CarComponentRow data in component.Rows)
        {
            row.AddData(new ComponentDataRowViewModel(data, row));
        }
        // Grouped by role rather than listed flat: the body ends up holding every marker whose bone no
        // component owns — 524 of the 1081 shipped ones — and nineteen rows under it in no order is the flat
        // pile this view exists to take away, merely moved one level down. In the order the roles are
        // declared, so two reads of the same car list them the same way round.
        foreach (IGrouping<CarMarkerRole, CarMarker> byRole in component.Markers.GroupBy(m => m.Role)
                     .OrderBy(g => g.Key))
        {
            var group = new MarkerGroupRowViewModel(byRole.Key, row);
            foreach (CarMarker marker in byRole) group.Add(new MarkerRowViewModel(marker, row));
            row.AddMarkerGroup(group);
        }
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

    // ── the diagnosis ──

    /// <summary>
    /// Everything the aggregate could not stitch about the staged car, as a list.
    ///
    /// <para>
    /// The editor opens every car, including the ones it cannot fully understand — those are the cars the tool
    /// is most needed for — so what it could not make sense of has to be READABLE the moment the car opens
    /// rather than discovered one component at a time. The rows that have a component of their own also carry
    /// their fault (<see cref="ComponentRowViewModel.HasFault"/>); this is where the ones that do not are
    /// seen at all.
    /// </para>
    /// <para>
    /// Deliberately NOT narrowed by the panel's search. A diagnosis filtered by what the modder happens to be
    /// looking for is one that hides the fault they have not thought to look for yet.
    /// </para>
    /// </summary>
    public IReadOnlyList<FaultRowViewModel> Faults { get; private set; } = [];

    /// <summary>Whether this car has anything wrong with it — what the strip above the tree appears for.</summary>
    public bool HasFaults => Faults.Count > 0;

    /// <summary>Whether any of them is a failure no shipped car raises. What decides whether the strip reads
    /// as damage or as a note.</summary>
    public bool HasBreak => Faults.Any(f => f.IsBreak);

    /// <summary>
    /// How many, in words, for the strip's own line — and in WHICH words, which is the part that matters.
    ///
    /// <para>
    /// 41 faults over 8 of the 85 shipped cars are things cars are simply written like: a half-track whose
    /// axles name its track hinges, 24 door parts with no door row. Telling a modder that a stock archive
    /// they have not touched "did not fully stitch" is how a diagnosis stops being read — so a car carrying
    /// only those says what it is, and the accusing line is kept for the faults no shipped car raises.
    /// </para>
    /// </summary>
    public string FaultSummary
    {
        get
        {
            string count = Faults.Count.ToString(CultureInfo.InvariantCulture);
            if (Faults.Any(f => !f.Fault.ShipsThisWay))
            {
                return Faults.Count == 1
                    ? "1 fault — this car did not fully stitch"
                    : $"{count} faults — this car did not fully stitch";
            }
            return Faults.Count == 1
                ? "1 note — this car's lists do not line up"
                : $"{count} notes — this car's lists do not line up";
        }
    }

    private bool _faultsOpen;

    /// <summary>
    /// Whether the list itself is open under the strip. Shut by default and remembered for as long as the
    /// session lasts: the count is the diagnosis being AVAILABLE, and a list that opens itself on every car
    /// takes the tree's room to say something the modder has already read.
    /// </summary>
    public bool FaultsOpen
    {
        get => _faultsOpen && HasFaults;
        set { if (_faultsOpen != value) { _faultsOpen = value; Raise(nameof(FaultsOpen)); } }
    }

    /// <summary>Selects the component a fault is about, so reading the diagnosis and looking at what it is
    /// about are one gesture. A fault with no component of its own selects nothing.</summary>
    public void Select(FaultRowViewModel? fault) => Select(fault?.Component);

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

    /// <summary>
    /// The scene node a marker IS — the Dummy or the Point the modder drags to place it.
    ///
    /// <para>
    /// Matched by the FNV64 of the frame's name, because that is the only thing the prefab row holds: the
    /// assembly layer names frames by hash and nothing else, so a marker and its frame are joined here the
    /// same way the game joins them.
    /// </para>
    /// </summary>
    public SceneNode? NodeOfFrame(ulong frameHash)
    {
        if (frameHash == 0) return null;
        foreach (SceneNode root in _viewport.Tree.Roots)
        {
            if (FindFrame(root, frameHash) is { } found) return found;
        }
        return null;
    }

    private static SceneNode? FindFrame(SceneNode node, ulong frameHash)
    {
        if (node.Source is FrameNodeAdapter frame && frame.Frame.Name?.String is { Length: > 0 } name
            && Fnv64.Hash(name) == frameHash)
        {
            return node;
        }
        foreach (SceneNode child in node.Children)
        {
            if (FindFrame(child, frameHash) is { } found) return found;
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
        // A child row is never left lit beside a component: whatever moves the highlight settles which ONE
        // row the menu acts on, and two lit rows would make "Remove" a question about which.
        if (SelectedChild != null)
        {
            SelectedChild.IsSelected = false;
            SelectedChild = null;
            RaiseChild();
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

    /// <summary>
    /// The row BENEATH a component that the menu acts on — a collision, a marker, a marker group or one of
    /// the component's own rows — or null when the selection is the component itself. One field, because
    /// whatever the modder clicked settles which single row a menu item acts on.
    /// </summary>
    public IComponentChildRow? SelectedChild { get; private set; }

    /// <summary>The selected child when it is a collision.</summary>
    public CollisionRowViewModel? SelectedCollision => SelectedChild as CollisionRowViewModel;

    /// <summary>The selected child when it is a marker.</summary>
    public MarkerRowViewModel? SelectedMarker => SelectedChild as MarkerRowViewModel;

    /// <summary>The selected child when it is one of the component's own prefab rows.</summary>
    public ComponentDataRowViewModel? SelectedDataRow => SelectedChild as ComponentDataRowViewModel;

    /// <summary>The selected child when it is the component's damage parameters.</summary>
    public ComponentDamageRowViewModel? SelectedDamage => SelectedChild as ComponentDamageRowViewModel;

    /// <summary>The selected child when it is one of the component's deform handles — or the row that says it
    /// has none, which carries nothing to type.</summary>
    public ComponentHandleRowViewModel? SelectedHandle => SelectedChild as ComponentHandleRowViewModel;

    /// <summary>
    /// Points the menu at one of a component's child rows, and the viewport at the thing that row IS.
    ///
    /// <para>
    /// A MARKER is a frame — a Dummy or a Point — so selecting its row hands that frame over, and the next
    /// thing the modder does can be to drag it. A SOLID collision is handed its mirror stub for the same
    /// reason: the stub stands exactly where the volume does, so the gizmo takes hold of the collision instead
    /// of the part that carries it. The stub is still not shown as a row of its own — it is a handle, not a
    /// thing of the car.
    /// </para>
    /// <para>
    /// Everything else here — glass and zones, which name no record and have no frame anywhere in the corpus,
    /// and the headings that are statements about a component rather than things of their own — falls back to
    /// the component's bone, so the property tabs still describe what was clicked. That bone is the WHOLE
    /// part, and dragging it moves the part rather than the row: a collision with no handle says so out loud
    /// rather than letting a modder discover it by moving a door with the box they meant to nudge.
    /// </para>
    /// </summary>
    public void Select(IComponentChildRow? row)
    {
        if (row == null) return;
        _viewport.Select(NodeOfFrame(row.FrameHash) ?? NodeOf(row.Component.Component));
        // …and the tree's highlight is THIS row, not the component's. Handing the frame over raises the
        // viewport's own selection change, which lights the component row on the way back through
        // ShowSelection, so the clearing has to come after it rather than before.
        Highlight(null);
        SelectedChild = row;
        row.IsSelected = true;
        RaiseChild();
        if (row is CollisionRowViewModel { HasHandle: false } noHandle)
        {
            _viewport.RaiseNotice(
                $"{noHandle.Label} has no handle to drag — every shipped one of its kind is placed by its "
                + "numbers. Use \"Size and position…\" on its row; the gizmo would move the whole part.");
        }
    }

    private void RaiseChild()
    {
        Raise(nameof(SelectedChild));
        Raise(nameof(SelectedCollision));
        Raise(nameof(SelectedMarker));
        Raise(nameof(SelectedDataRow));
        Raise(nameof(SelectedDamage));
        Raise(nameof(SelectedHandle));
    }

    /// <summary>Raised after an edit has been written and the car re-stitched, so the panel can rebuild the
    /// rows around a tree whose components may have gained or lost something.</summary>
    public event Action? CarEdited;

    /// <summary>
    /// Holds the car still for the length of ONE component-level intent, so that an edit and a push landing
    /// from Blender are never applied at the same time.
    ///
    /// <para>
    /// They would otherwise overlap for real. A push computes each mesh's application on the bridge's own
    /// thread, reading the frame graph an edit here adds a marker's Dummy to, takes a mirror stub out of and
    /// hands to <c>Car.Save</c> to serialize — two threads, one graph, and a save that lands in the middle
    /// writes an archive neither of them meant.
    /// </para>
    /// <para>
    /// An edit that cannot take the gate is REFUSED rather than made to wait. A push holds it across the
    /// dispatcher calls that apply it, so a UI thread waiting here would be waiting for a push that is
    /// waiting for the UI thread; and a frozen window is a worse answer than "try again in a moment".
    /// </para>
    /// </summary>
    private readonly struct CarEditHold : IDisposable
    {
        /// <summary>What an intent is refused with while a push has the car.</summary>
        internal const string Landing =
            "a push from Blender is landing on this car — make that change again in a moment";

        private readonly D3DImageHost? _viewport;

        private CarEditHold(D3DImageHost? viewport) => _viewport = viewport;

        /// <summary>Whether this intent may go ahead.</summary>
        internal bool Held => _viewport != null;

        internal static CarEditHold Take(D3DImageHost viewport) =>
            new(viewport.BridgeSession.TryHoldForEdit() ? viewport : null);

        public void Dispose() => _viewport?.BridgeSession.ReleaseAfterEdit();
    }

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
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (row == null) { refusal = "no car is open"; return; }
        if (Reread(row.Id) is not (Car car, ComponentRowViewModel fresh))
        {
            refusal = "no car is open";
            return;
        }

        CarEdit? edit = car.AddCollision(fresh.Component, role, shape, size, position, out refusal);
        if (edit == null) return;
        // A solid's mirror stub joined the frame graph, and the graph is what the viewport draws FROM — but a
        // frame the graph holds and the scene tree does not is one the gizmo cannot take hold of. Without this
        // the new collision could not be dragged until the archive was reopened, and selecting its row fell
        // back to the component's bone, which drags the whole part.
        ShowFrames(edit.After);
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
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
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

        CarEdit? edit = car.SetCollision(collision, size, placement, out refusal);
        if (edit == null) return;
        Commit(car, edit, ref refusal);
    }

    /// <summary>Takes a collision off its component, and its ItemDesc record and mirror stub with it when
    /// nothing else names them.</summary>
    public void RemoveCollision(CollisionRowViewModel? row, out string? refusal)
    {
        refusal = null;
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (row == null) { refusal = "no car is open"; return; }
        if (Collision(row) is not (Car car, CarCollision collision))
        {
            refusal = "that collision is no longer there";
            return;
        }

        CarEdit? edit = car.RemoveCollision(collision, out refusal);
        if (edit == null) return;
        // …and a stub that has left the graph has to leave the scene tree, or its row stays behind as a handle
        // that holds nothing.
        ShowFrames(edit.After);
        Commit(car, edit, ref refusal);
    }

    // ── the deform part itself ──

    /// <summary>
    /// The components a new part could hang off — every one that has a deform part of its own, since there is
    /// nothing for a part to hang off a bare component.
    ///
    /// <para>
    /// In the tree's own order, so the list a modder picks from reads the way the tree they picked from does.
    /// </para>
    /// </summary>
    public IReadOnlyList<ComponentRowViewModel> Parentable()
    {
        var rows = new List<ComponentRowViewModel>();
        foreach (ComponentRowViewModel root in Roots) Collect(root, rows);
        return rows;
    }

    private static void Collect(ComponentRowViewModel row, List<ComponentRowViewModel> into)
    {
        if (!row.IsBare) into.Add(row);
        foreach (ComponentRowViewModel child in row.Children) Collect(child, into);
    }

    /// <summary>The row the body is on — where a component with no obvious owner belongs, and what the
    /// parent list opens on.</summary>
    public ComponentRowViewModel? BodyRow => Car?.Body is { } body ? RowOf(body.Id) : null;

    /// <summary>
    /// Gives a bare component a deform part of its own, making it damageable.
    ///
    /// <para>
    /// The aggregate derives everything but the kind and the parent — the flag word, the crumple thresholds,
    /// the tuning block, all three copies of the parent link, and the half of the struct nobody has read.
    /// </para>
    /// </summary>
    public void GrantDeformPart(
        ComponentRowViewModel? row, CarPartTemplate kind, ComponentRowViewModel? parent,
        out string? refusal)
    {
        refusal = null;
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (row == null || parent == null) { refusal = "no car is open"; return; }
        // Both rows are found again in the SAME re-read: two rereads would leave the parent pointing into a
        // stitch the component is no longer part of.
        if (Reread(row.Id) is not (Car car, ComponentRowViewModel fresh)
            || RowOf(parent.Id) is not { } freshParent)
        {
            refusal = "that component is no longer there";
            return;
        }

        CarEdit? edit = car.GrantDeformPart(fresh.Component, kind, freshParent.Component, out refusal);
        if (edit == null) return;
        Commit(car, edit, ref refusal);
    }

    /// <summary>
    /// Adds a component to the car: a bone, the kind of part it carries, and which component it hangs off.
    ///
    /// <para>
    /// Offered whatever bone is named, and refused by the aggregate when that bone would have to be MINTED —
    /// which is every bone Blender has not made yet. The refusal is the aggregate's and is shown where the
    /// modder typed the name; nothing here decides it, so the day the rig writer lands this method does not
    /// change.
    /// </para>
    /// </summary>
    public void AddComponent(
        string bone, CarPartTemplate kind, ComponentRowViewModel? parent, out string? refusal)
    {
        refusal = null;
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (parent == null) { refusal = "no car is open"; return; }
        if (Reread(parent.Id) is not (Car car, ComponentRowViewModel freshParent))
        {
            refusal = "that component is no longer there";
            return;
        }

        CarEdit? edit = car.AddComponent(bone, kind, freshParent.Component, out refusal);
        if (edit == null) return;
        Commit(car, edit, ref refusal);
    }

    /// <summary>
    /// Takes a component off the car altogether — which means taking its bone away, and is refused with the
    /// reason.
    ///
    /// <para>
    /// Offered rather than greyed, and it takes the same path every other intent takes: the aggregate is
    /// asked, and it answers. A refusal that never travels the path is one nothing can measure — and this one
    /// has to be measurable, since what it promises is that the archive is left byte for byte as it was.
    /// </para>
    /// </summary>
    public void RemoveComponent(ComponentRowViewModel? row, out string? refusal)
    {
        refusal = null;
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (row == null) { refusal = "no car is open"; return; }
        if (Reread(row.Id) is not (Car car, ComponentRowViewModel fresh))
        {
            refusal = "that component is no longer there";
            return;
        }

        CarEdit? edit = car.RemoveComponent(fresh.Component, out refusal);
        if (edit == null) return;
        Commit(car, edit, ref refusal);
    }

    /// <summary>Takes a component's deform part away, demoting it back to a bare component — the bone, the
    /// geometry and the hit boxes stay exactly as they were.</summary>
    public void RemoveDeformPart(ComponentRowViewModel? row, out string? refusal)
    {
        refusal = null;
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (row == null) { refusal = "no car is open"; return; }
        if (Reread(row.Id) is not (Car car, ComponentRowViewModel fresh))
        {
            refusal = "that component is no longer there";
            return;
        }

        CarEdit? edit = car.RemoveDeformPart(fresh.Component, out refusal);
        if (edit == null) return;
        // A solid collision the part carried took its mirror stub out of the frame graph with it, and a stub
        // the graph no longer holds has to leave the scene tree or its row stays behind holding nothing.
        ShowFrames(edit.After);
        Commit(car, edit, ref refusal);
    }

    // ── markers ──

    /// <summary>Writes the numbers a marker's own row carries, and moves the marker itself when one of them
    /// says where it is.</summary>
    public void SetMarker(MarkerRowViewModel? row, IReadOnlyList<CarField> fields, out string? refusal)
    {
        refusal = null;
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (row == null) { refusal = "no car is open"; return; }
        if (Marker(row) is not (Car car, CarMarker marker))
        {
            refusal = "that marker is no longer there";
            return;
        }

        CarEdit? edit = car.SetMarker(marker, fields, out refusal);
        if (edit == null) return;
        Commit(car, edit, ref refusal);
    }

    /// <summary>Writes the numbers one of a component's own-bone rows carries — a door's handle and lock, a
    /// window's depth, an axle's masses.</summary>
    public void SetDataRow(
        ComponentDataRowViewModel? row, IReadOnlyList<CarField> fields, out string? refusal)
    {
        refusal = null;
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (row == null) { refusal = "no car is open"; return; }
        if (DataRow(row) is not (Car car, CarComponentRow fresh))
        {
            refusal = "that row is no longer there";
            return;
        }

        CarEdit? edit = car.SetRow(fresh, fields, out refusal);
        if (edit == null) return;
        Commit(car, edit, ref refusal);
    }

    // ── damage ──

    /// <summary>
    /// Writes a component's own damage parameters — its mass and centre of mass, its resistance, its speed
    /// window, its energy start and drop, its effect group and its flags.
    /// </summary>
    public void SetDamage(
        ComponentDamageRowViewModel? row, IReadOnlyList<CarField> fields, out string? refusal)
    {
        refusal = null;
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (row == null) { refusal = "no car is open"; return; }
        if (Reread(row.Component.Id) is not (Car car, ComponentRowViewModel fresh))
        {
            refusal = "that component is no longer there";
            return;
        }

        CarEdit? edit = car.SetDamage(fresh.Component, fields, out refusal);
        if (edit == null) return;
        Commit(car, edit, ref refusal);
    }

    /// <summary>Writes one deform handle's crumple parameters — how far it travels, how hard it resists, and
    /// over what radius the panel follows it.</summary>
    public void SetHandle(
        ComponentHandleRowViewModel? row, IReadOnlyList<CarField> fields, out string? refusal)
    {
        refusal = null;
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (row?.Handle == null) { refusal = "no car is open"; return; }
        if (Handle(row) is not (Car car, CarComponent component, CarHandle handle))
        {
            refusal = "that deform handle is no longer there";
            return;
        }

        CarEdit? edit = car.SetHandle(component, handle, fields, out refusal);
        if (edit == null) return;
        Commit(car, edit, ref refusal);
    }

    /// <summary>Gives a component one more marker: the helper frame is minted on its bone and the prefab row
    /// that names it written beside it.</summary>
    public void AddMarker(ComponentRowViewModel? row, CarMarkerRole role, out string? refusal)
    {
        refusal = null;
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (row == null) { refusal = "no car is open"; return; }
        if (Reread(row.Id) is not (Car car, ComponentRowViewModel fresh))
        {
            refusal = "no car is open";
            return;
        }

        CarEdit? edit = car.AddMarker(fresh.Component, role, out refusal);
        if (edit == null) return;
        // The frame joined the graph the viewport is drawing, so it has to join the tree the viewport lists —
        // a frame the graph holds and the tree does not is one the modder can neither see nor drag, and
        // dragging it is the whole of placing a marker.
        ShowFrames(edit.After);
        Commit(car, edit, ref refusal);
    }

    /// <summary>Takes a marker off its component, and the helper frame it named with it when nothing else in
    /// the assembly still names that frame.</summary>
    public void RemoveMarker(MarkerRowViewModel? row, out string? refusal)
    {
        refusal = null;
        using CarEditHold hold = CarEditHold.Take(_viewport);
        if (!hold.Held) { refusal = CarEditHold.Landing; return; }
        if (row == null) { refusal = "no car is open"; return; }
        if (Marker(row) is not (Car car, CarMarker marker))
        {
            refusal = "that marker is no longer there";
            return;
        }

        CarEdit? edit = car.RemoveMarker(marker, out refusal);
        if (edit == null) return;
        // …and a frame that has left the graph has to leave the tree, or its row stays behind acting on
        // nothing.
        ShowFrames(edit.After);
        Commit(car, edit, ref refusal);
    }

    /// <summary>
    /// Puts the frames a state holds into the scene tree, and takes the ones it says are gone out of it.
    ///
    /// <para>
    /// Through the viewport's own part controller, so that a marker added here and one added from the bone
    /// menu get the SAME two rows — the copy under the bone that says which part it belongs to, and the one
    /// in the hierarchy that says where it sits in the graph. Run on an undo and a redo as well, because a
    /// marker's frame comes and goes with them.
    /// </para>
    /// </summary>
    private void ShowFrames(CarState state)
    {
        if (_document is not SceneDocumentAdapter document) return;
        foreach (CarFrameRef frame in state.Frames)
        {
            _viewport.CarPartEditing.SyncHelperRows(
                document, frame.Frame, frame.Joint, frame.Present);
        }
    }

    /// <summary>
    /// Re-reads the car from the working copy before an edit is made on it, and finds the row again in what
    /// comes back.
    ///
    /// <para>
    /// This is not belt and braces. The aggregate writes the WHOLE prefab from the copy it holds, and that
    /// copy is only refreshed when the SCENE changes — while a scene save, a bridge push or a rollback moves
    /// the file underneath it without saying so. Editing on top of a stale read would put those changes back
    /// the way they were, and the modder would be told the collision was added.
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

    /// <summary>The same, for a marker: found again by the ROLE and the place in its own list, which is how
    /// the prefab addresses it and therefore the only pair a re-read can be trusted to reproduce.</summary>
    private (Car Car, CarMarker Marker)? Marker(MarkerRowViewModel row)
    {
        CarMarkerRole role = row.Marker.Role;
        int index = row.Marker.Index;
        if (Reread(row.Component.Id) is not (Car car, ComponentRowViewModel fresh)) return null;
        CarMarker? found = fresh.Component.Markers.FirstOrDefault(
            m => m.Role == role && m.Index == index);
        return found == null ? null : (car, found);
    }

    /// <summary>And for a deform handle, found again by its place in its component's own handle list — which is
    /// how the prefab addresses it, and therefore the only pair a re-read can be trusted to reproduce.</summary>
    private (Car Car, CarComponent Component, CarHandle Handle)? Handle(ComponentHandleRowViewModel row)
    {
        if (row.Handle is not { } was) return null;
        if (Reread(row.Component.Id) is not (Car car, ComponentRowViewModel fresh)) return null;
        CarHandle? found = fresh.Component.Handles.FirstOrDefault(h => h.Index == was.Index);
        return found == null ? null : (car, fresh.Component, found);
    }

    /// <summary>And for one of a component's own-bone rows, found again by its kind and its place in that
    /// list.</summary>
    private (Car Car, CarComponentRow Row)? DataRow(ComponentDataRowViewModel row)
    {
        string kind = row.Row.Kind;
        int index = row.Row.Index;
        if (Reread(row.Component.Id) is not (Car car, ComponentRowViewModel fresh)) return null;
        CarComponentRow? found = fresh.Component.Rows.FirstOrDefault(
            r => string.Equals(r.Kind, kind, StringComparison.Ordinal) && r.Index == index);
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
    /// <para>
    /// And the SCENE TREE comes back with it, which is not a nicety. Three of the intents above put a frame
    /// into the tree before the save is attempted, because a frame the graph holds and the tree does not is
    /// one the gizmo cannot take hold of — so a refusal that only restored the aggregate would leave a row
    /// for a stub the rollback has just dropped, or take away the row of a collision that is still in the car
    /// and let the modder Build it believing it gone. A save IS refused in practice: four other modules write
    /// the same prefab directly, and the aggregate refuses outright when one of them got there first.
    /// </para>
    /// </summary>
    private void Commit(Car car, CarEdit edit, ref string? refusal)
    {
        CarSave saved = car.Save();
        if (!saved.Ok)
        {
            refusal = string.Join("; ", saved.Lost);
            car.Restore(edit.Before);
            ShowFrames(edit.Before);
            Restitch();
            return;
        }

        _viewport.History.Push(new CarEditAction(edit, () => Car, Restored,
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
    /// <summary>What an undo or a redo ends with: the scene tree put back around the frames that state holds,
    /// and then the car re-stitched over them.</summary>
    private void Restored(CarState state)
    {
        ShowFrames(state);
        Restitch();
    }

    private void Restitch()
    {
        SelectedChild = null;
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
