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
    /// FNV64 of the frame this row IS, or 0 when it is not a frame at all.
    ///
    /// <para>
    /// A marker has one — its Dummy or its Point — and selecting the row hands that frame to the viewport, so
    /// that the next thing the modder does can be to drag it. A collision has none: a self-describing volume
    /// is not a frame, and the mirror stub of a solid one is a copy the modder is deliberately never shown.
    /// </para>
    /// </summary>
    ulong FrameHash { get; }
}
