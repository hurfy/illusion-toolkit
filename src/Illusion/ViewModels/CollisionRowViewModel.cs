using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Illusion.Assets.Cars;

namespace Illusion.ViewModels;

/// <summary>
/// One collision of a component, as a row under it: what it is, what form it takes and how big it is.
///
/// <para>
/// By ROLE and SHAPE, which is the whole point. The row never says type 5 or type 0, never says whose bone
/// space the matrix is in, and never says that one kind of volume states a full size while the other states
/// half of one — those are the file's accidents and the aggregate's business.
/// </para>
/// </summary>
public sealed class CollisionRowViewModel : INotifyPropertyChanged
{
    internal CollisionRowViewModel(CarCollision collision, ComponentRowViewModel component)
    {
        Collision = collision;
        Component = component;
    }

    /// <summary>The collision this row is.</summary>
    public CarCollision Collision { get; }

    /// <summary>The component row it hangs under.</summary>
    public ComponentRowViewModel Component { get; }

    /// <summary>What it is and what form it takes — <c>Body · box</c>, <c>Glass · box</c>.</summary>
    public string Label => Collision.Label;

    /// <summary>Its whole size in metres, which is the one number worth seeing without opening it.</summary>
    public string Size => Print(Collision.Size);

    /// <summary>Whether it cannot be edited — a cooked hull, or a component whose bone does not resolve.</summary>
    public bool IsReadOnly => Collision.IsReadOnly;

    /// <summary>Why, when it cannot. Shown on the row rather than only when an edit is refused: a modder
    /// should be able to see that a hull is a dead end before trying to resize one.</summary>
    public string? ReadOnlyReason => Collision.ReadOnlyReason;

    /// <summary>A collision's rows follow their component through the panel's search, so narrowing the tree
    /// to a door does not empty that door of everything it is made of.</summary>
    public bool HasSearchMatch => Component.HasSearchMatch;

    private bool _isSelected;

    /// <summary>Whether this row is the one the menu will act on.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    public override string ToString() => $"{Label} {Size}";

    private static string Print(Vector3 v) =>
        $"{v.X.ToString("0.##", CultureInfo.InvariantCulture)} × "
        + $"{v.Y.ToString("0.##", CultureInfo.InvariantCulture)} × "
        + $"{v.Z.ToString("0.##", CultureInfo.InvariantCulture)} m";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
