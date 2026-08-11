using System.ComponentModel;
using System.Windows.Data;
using Illusion.Assets.Cars;
using Illusion.Scene;

namespace Illusion.ViewModels;

/// <summary>
/// One row of the component tree: a real thing of the car — a door, a bumper, a licence plate — with the
/// components that hang off it underneath.
///
/// <para>
/// The row is named after the component's own bone, which is the name the modder sees in Blender, and says
/// its part kind beside it rather than leaving that to be inferred: a cover names <c>doorBL</c> on seven
/// shipped cars, so the name is not the kind.
/// </para>
/// <para>
/// Read-only. Nothing here writes to the car — the aggregate is the only path from an intent to bytes, and
/// editing from this tree starts a ticket later.
/// </para>
/// </summary>
public sealed class ComponentRowViewModel : INotifyPropertyChanged
{
    private readonly List<ComponentRowViewModel> _children = [];
    private readonly List<CollisionRowViewModel> _collisions = [];
    private readonly List<ComponentDataRowViewModel> _data = [];
    private readonly List<MarkerGroupRowViewModel> _markers = [];
    private readonly List<ComponentHandleRowViewModel> _handles = [];
    private ComponentDamageRowViewModel? _damage;

    /// <summary>
    /// What the tree actually binds, in the order a modder reads the component: what it IS first — how it
    /// behaves when hit and how it crumples — then what it is made of: its collisions, the prefab rows that
    /// name its own bone, and the markers that hang off it grouped by role. The components hanging under it come
    /// last.
    ///
    /// <para>
    /// A child component comes last because it is a thing of its own that happens to hang here: a window under
    /// its door should not come between the door and the door's own glass.
    /// </para>
    /// </summary>
    private readonly List<object> _rows = [];

    /// <summary>
    /// Where each kind of row sits in that reading order. The rows are INSERTED by rank rather than appended,
    /// so the order holds however the tree happens to fill them in — six kinds of child row is more arithmetic
    /// than is worth keeping in the caller's head, and getting it wrong is a tree that reads differently
    /// depending on what a car happens to carry.
    /// </summary>
    private static int Rank(object row) => row switch
    {
        ComponentDamageRowViewModel => 0,
        ComponentHandleRowViewModel => 1,
        CollisionRowViewModel => 2,
        ComponentDataRowViewModel => 3,
        MarkerGroupRowViewModel => 4,
        _ => 5,
    };

    private void Insert(object row)
    {
        int rank = Rank(row);
        int at = _rows.FindIndex(existing => Rank(existing) > rank);
        _rows.Insert(at < 0 ? _rows.Count : at, row);
    }

    internal ComponentRowViewModel(
        CarComponent component, ComponentRowViewModel? parent, IReadOnlyList<CarFault> faults)
    {
        Component = component;
        Parent = parent;
        Faults = faults;
    }

    /// <summary>The component this row is. Its identity is what everything else keys on.</summary>
    public CarComponent Component { get; }

    /// <summary>The row this one hangs off — a window's door, a patch's bonnet.</summary>
    public ComponentRowViewModel? Parent { get; }

    /// <summary>What hangs off this component, in the prefab's own order.</summary>
    public IReadOnlyList<ComponentRowViewModel> Children => _children;

    /// <summary>What this component is solid with, by role and shape.</summary>
    public IReadOnlyList<CollisionRowViewModel> Collisions => _collisions;

    /// <summary>The prefab rows that name this component's own bone — its door points, its window, its axle.</summary>
    public IReadOnlyList<ComponentDataRowViewModel> Data => _data;

    /// <summary>The markers that hang off this component's bone, grouped by role.</summary>
    public IReadOnlyList<MarkerGroupRowViewModel> MarkerGroups => _markers;

    /// <summary>What this component does when it is hit, or null on a bare one — which has no deform part for
    /// any of it to be on.</summary>
    public ComponentDamageRowViewModel? Damage => _damage;

    /// <summary>
    /// The rows about how this component crumples: one per deform handle — or, on a component that has none, the
    /// single row that says so, which is how 1093 of the 1698 shipped parts are written.
    ///
    /// <para>
    /// Called <c>HandleRows</c> and not <c>Handles</c> on purpose. It is NOT
    /// <see cref="CarComponent.Crumples"/> restated: a component that crumples around nothing has one row here
    /// and no handle, so a count of these is never a count of handles. Ask <see cref="IComponentChildRow"/>'s
    /// own <c>HasFields</c> to tell the two apart.
    /// </para>
    /// </summary>
    public IReadOnlyList<ComponentHandleRowViewModel> HandleRows => _handles;

    private ICollectionView? _childrenView;

    /// <summary>The rows under this one as the tree binds them — its collisions and its child components,
    /// narrowed to what the panel's search box matches. Built on first read: a car is shallow, but only the
    /// open branches are ever bound.</summary>
    public ICollectionView ChildrenView
    {
        get
        {
            if (_childrenView == null)
            {
                _childrenView = CollectionViewSource.GetDefaultView(_rows);
                _childrenView.Filter = o => o switch
                {
                    ComponentRowViewModel row => row.HasSearchMatch,
                    IComponentChildRow child => child.HasSearchMatch,
                    _ => false,
                };
            }
            return _childrenView;
        }
    }

    /// <summary>Whether this row or anything under it matches the panel's search. The same shared query the
    /// frame tree filters on, so switching between the two trees does not lose what was typed.</summary>
    public bool HasSearchMatch =>
        SceneSearch.Matches(Name) || _children.Any(c => c.HasSearchMatch);

    /// <summary>Who this is, for as long as the archive is open.</summary>
    public ComponentId Id => Component.Id;

    /// <summary>The bone's name — or the bare hash when nothing in the car resolves to it.</summary>
    public string Name => Component.Name;

    /// <summary>The part kind in words: <c>door</c>, <c>cover</c>, <c>bare</c>.</summary>
    public string Kind => Component.Kind;

    /// <summary>Whether no deform part claims this bone. A licence plate, a light, a wiper — shootable parts
    /// of the car that had nowhere to be edited before the component view.</summary>
    public bool IsBare => Component.IsBare;

    /// <summary>Whether this is the car's BODY — the one part every shipped car has exactly one of, and where
    /// every marker whose bone no component owns hangs. It is the one component whose deform part cannot be
    /// taken away.</summary>
    public bool IsBody => Component.PartType == BodyPartType;

    /// <summary>The engine's own part kind for the body.</summary>
    private const uint BodyPartType = 1;

    /// <summary>Whether the bone the part names is actually in this car. False is the signature of a rename
    /// made in Blender, and the row is shown broken rather than dropped.</summary>
    public bool IsBroken => !Component.BoneResolves;

    /// <summary>
    /// What the toolkit could not stitch about THIS component — shown on the row, not only in the car's
    /// list, because a fault a modder has to go looking for is one they find by noticing the damage first.
    /// </summary>
    public IReadOnlyList<CarFault> Faults { get; }

    /// <summary>Whether anything about this component failed to stitch.</summary>
    public bool HasFault => Faults.Count > 0;

    /// <summary>Whether one of them is a failure no shipped car raises — the difference between a component
    /// something was done to and one the game simply ships that way. Only the first earns a warning colour.
    /// </summary>
    public bool HasBreak => Faults.Any(f => !f.ShipsThisWay);

    /// <summary>Every one of them, one per line, for the row's tooltip.</summary>
    public string FaultTip => string.Join("\n", Faults.Select(f => $"{f.Title}: {f.What}"));

    /// <summary>How many deform handles this component crumples around, said on the row because a component
    /// with none does not crumple at all and that is worth seeing without opening it.</summary>
    public int HandleCount => Component.Handles.Count;

    private bool _isExpanded = true;

    /// <summary>Whether the branch is open. Cars are shallow — a window under its door — so a component tree
    /// opens showing everything rather than making the modder unfold it.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; Raise(nameof(IsExpanded)); } }
    }

    private bool _isSelected;

    /// <summary>Whether this row is the selection. Driven from the viewport in both directions — the tree
    /// lists components and the viewport draws frames, so one of them always had to resolve to the other.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    internal void AddChild(ComponentRowViewModel child)
    {
        _children.Add(child);
        Insert(child);
    }

    internal void AddCollision(CollisionRowViewModel collision)
    {
        _collisions.Add(collision);
        Insert(collision);
    }

    internal void AddData(ComponentDataRowViewModel row)
    {
        _data.Add(row);
        Insert(row);
    }

    internal void AddMarkerGroup(MarkerGroupRowViewModel group)
    {
        _markers.Add(group);
        Insert(group);
    }

    internal void AddDamage(ComponentDamageRowViewModel row)
    {
        _damage = row;
        Insert(row);
    }

    internal void AddHandle(ComponentHandleRowViewModel row)
    {
        _handles.Add(row);
        Insert(row);
    }

    /// <summary>Opens every branch above this row, so a row selected from the viewport is one the tree can
    /// actually show.</summary>
    public void ExpandAncestors()
    {
        for (ComponentRowViewModel? at = Parent; at != null; at = at.Parent) at.IsExpanded = true;
    }

    /// <summary>This row and every row beneath it, top down.</summary>
    public IEnumerable<ComponentRowViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (ComponentRowViewModel child in _children)
        {
            foreach (ComponentRowViewModel found in child.SelfAndDescendants()) yield return found;
        }
    }

    public override string ToString() => $"{Name} ({Kind})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
