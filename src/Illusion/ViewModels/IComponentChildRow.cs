namespace Illusion.ViewModels;

/// <summary>
/// A row that sits BENEATH a component in the component tree — one of its collisions, one of its markers, a
/// group of those, or one of the prefab rows that names its own bone.
///
/// <para>
/// They are one concept for exactly one reason: whatever the modder clicks settles which single row the menu
/// acts on, and two lit rows would make "Remove" a question about which. So the tree holds one selected child
/// at a time, whichever kind it is, and this is what that one field can be.
/// </para>
/// </summary>
public interface IComponentChildRow
{
    /// <summary>The component row this one hangs under.</summary>
    ComponentRowViewModel Component { get; }

    /// <summary>Whether this row is the one the menu will act on.</summary>
    bool IsSelected { get; set; }

    /// <summary>Whether it survives the panel's search — a child follows its component, so narrowing the tree
    /// to a door does not empty that door of everything it is made of.</summary>
    bool HasSearchMatch { get; }

    /// <summary>
    /// FNV64 of the frame the viewport takes hold of for this row, or 0 when it has none.
    ///
    /// <para>
    /// A marker's is its own Dummy or Point. A SOLID collision's is its mirror stub, which stands exactly
    /// where the volume does — handed over so the gizmo moves the collision rather than the part that carries
    /// it, while the stub itself is still never listed as a row. Glass, zones and the headings that are
    /// statements about a component have none at all, and fall back to the component's bone.
    /// </para>
    /// </summary>
    ulong FrameHash { get; }
}
