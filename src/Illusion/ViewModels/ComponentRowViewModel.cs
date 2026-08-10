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

    internal ComponentRowViewModel(CarComponent component, ComponentRowViewModel? parent)
    {
        Component = component;
        Parent = parent;
    }

    /// <summary>The component this row is. Its identity is what everything else keys on.</summary>
    public CarComponent Component { get; }

    /// <summary>The row this one hangs off — a window's door, a patch's bonnet.</summary>
    public ComponentRowViewModel? Parent { get; }

    /// <summary>What hangs off this component, in the prefab's own order.</summary>
    public IReadOnlyList<ComponentRowViewModel> Children => _children;

    private ICollectionView? _childrenView;

    /// <summary>The children as the tree binds them — the same list, narrowed to what the panel's search box
    /// matches. Built on first read: a car is shallow, but only the open branches are ever bound.</summary>
    public ICollectionView ChildrenView
    {
        get
        {
            if (_childrenView == null)
            {
                _childrenView = CollectionViewSource.GetDefaultView(_children);
                _childrenView.Filter = o => o is ComponentRowViewModel row && row.HasSearchMatch;
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

    /// <summary>Whether the bone the part names is actually in this car. False is the signature of a rename
    /// made in Blender, and the row is shown broken rather than dropped.</summary>
    public bool IsBroken => !Component.BoneResolves;

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

    internal void AddChild(ComponentRowViewModel child) => _children.Add(child);

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
