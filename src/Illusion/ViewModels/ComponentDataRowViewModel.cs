using System.ComponentModel;
using Illusion.Assets.Cars;

namespace Illusion.ViewModels;

/// <summary>
/// One of the prefab rows that names a component's OWN bone, shown under it: the door points that say where
/// the handle and the lock are, the window record that says how deep the pane sits and whether it rolls down,
/// the axle that carries the brake drum and the masses.
///
/// <para>
/// It is not a marker — a marker hangs a helper frame off a bone, while this names the bone itself — but it
/// sits in the same place for the same reason: a modder should never have to know which of the four parallel
/// lists a number lives in, let alone count rows to find it.
/// </para>
/// </summary>
public sealed class ComponentDataRowViewModel : INotifyPropertyChanged, IComponentChildRow
{
    internal ComponentDataRowViewModel(CarComponentRow row, ComponentRowViewModel component)
    {
        Row = row;
        Component = component;
    }

    /// <summary>The row this one is.</summary>
    public CarComponentRow Row { get; }

    /// <summary>The component row it hangs under.</summary>
    public ComponentRowViewModel Component { get; }

    /// <summary>What to call it: "Door points", "Window", "Axle 2".</summary>
    public string Label => Row.Label;

    /// <summary>The one number worth seeing without opening it — the first the row carries.</summary>
    public string Summary => Row.Fields.Count == 0
        ? ""
        : $"{Row.Fields[0].Label.ToLowerInvariant()} {Row.Fields[0].Text}";

    /// <summary>Every one of them, one per line, for the row's tooltip.</summary>
    public string Tip => string.Join("\n", Row.Fields.Select(f => $"{f.Label}: {f.Text}"));

    /// <summary>A row of the component's own bone is not a frame of its own.</summary>
    public ulong FrameHash => 0;

    /// <summary>It follows its component through the panel's search.</summary>
    public bool HasSearchMatch => Component.HasSearchMatch;

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    public override string ToString() => Label;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
