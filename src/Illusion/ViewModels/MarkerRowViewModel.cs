using System.ComponentModel;
using Illusion.Assets.Cars;

namespace Illusion.ViewModels;

/// <summary>
/// One marker of a car under the component it hangs off — a seat, a climb box, a fuel tank, an exhaust
/// emitter, a wiper, a light.
///
/// <para>
/// Where it sits is the whole point. Ownership is the bone: a marker belongs to the component owning the bone
/// its frame hangs off, and to the body when no component owns that bone — which is where 524 of the 1081
/// shipped markers land. Before this they were a flat pile beside the tree and the numbers they carry lived
/// in a parallel list the modder had to count rows in.
/// </para>
/// </summary>
public sealed class MarkerRowViewModel : INotifyPropertyChanged, IComponentChildRow
{
    internal MarkerRowViewModel(CarMarker marker, ComponentRowViewModel component)
    {
        Marker = marker;
        Component = component;
    }

    /// <summary>The marker this row is.</summary>
    public CarMarker Marker { get; }

    /// <summary>The component row it hangs under.</summary>
    public ComponentRowViewModel Component { get; }

    /// <summary>What to call it: "Seat 2", "Headlight".</summary>
    public string Label => Marker.Label;

    /// <summary>The frame it names — the Dummy or the Point a modder drags to place it.</summary>
    public string Name => Marker.Name;

    /// <summary>Whether it carries anything of its own to edit. Four of the six roles do not: a fuel tank, an
    /// exhaust emitter, a wiper and a light are a frame name and nothing else.</summary>
    public bool HasFields => Marker.HasFields;

    /// <summary>Its own numbers, one per line, for the row's tooltip.</summary>
    public string Tip => Marker.HasFields
        ? string.Join("\n", Marker.Fields.Select(f => $"{f.Label}: {f.Text}"))
        : $"{Label} is placed by its frame \"{Name}\" and carries nothing else.";

    /// <summary>The frame the viewport selects when this row is clicked, so the next thing the modder does can
    /// be to drag it.</summary>
    public ulong FrameHash => Marker.Frame;

    /// <summary>A marker follows its component through the panel's search.</summary>
    public bool HasSearchMatch => Component.HasSearchMatch;

    private bool _isSelected;

    /// <summary>Whether this row is the one the menu will act on.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    public override string ToString() => $"{Label} ({Name})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>
/// The markers of one ROLE under a component — "Seats", "Climb boxes", "Lights".
///
/// <para>
/// Grouped rather than listed flat because of where they land: the body holds every marker whose bone no
/// component owns, which over the corpus is 524 of 1081, and a body with nineteen rows under it in no order
/// is the flat pile this ticket exists to take away, merely moved one level down.
/// </para>
/// </summary>
public sealed class MarkerGroupRowViewModel : INotifyPropertyChanged, IComponentChildRow
{
    internal MarkerGroupRowViewModel(CarMarkerRole role, ComponentRowViewModel component)
    {
        Role = role;
        Component = component;
    }

    private readonly List<MarkerRowViewModel> _markers = [];

    /// <summary>Which role this group holds.</summary>
    public CarMarkerRole Role { get; }

    /// <summary>The component row it hangs under.</summary>
    public ComponentRowViewModel Component { get; }

    /// <summary>The markers in it, in the prefab's own list order.</summary>
    public IReadOnlyList<MarkerRowViewModel> Markers => _markers;

    /// <summary>The group's heading — the role in words, with how many are in it.</summary>
    public string Label => $"{Car.RoleName(Role)} ({_markers.Count})";

    /// <summary>A group has no frame of its own; the markers in it do.</summary>
    public ulong FrameHash => 0;

    /// <summary>A group follows its component through the panel's search.</summary>
    public bool HasSearchMatch => Component.HasSearchMatch;

    private bool _isExpanded = true;

    /// <summary>Whether the group is open. A car is shallow and a group holds a handful of rows, so it opens
    /// showing them rather than making the modder unfold every one.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; Raise(nameof(IsExpanded)); } }
    }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    internal void Add(MarkerRowViewModel marker) => _markers.Add(marker);

    public override string ToString() => Label;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
