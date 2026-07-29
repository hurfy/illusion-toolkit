using System.Windows.Input;

namespace Illusion.Rendering.Controls;

/// <summary>
/// Which keys fly the camera. <see cref="ViewportControl"/> ships with WASD and the usual speed modifiers
/// and names no key anywhere else, so an application that lets the user rebind them hands in a different
/// map instead of the rendering layer having to know that settings exist.
/// </summary>
public sealed record CameraKeyMap(
    Key Forward,
    Key Back,
    Key Left,
    Key Right,
    ModifierKeys Fast,
    ModifierKeys Slow)
{
    /// <summary>WASD with Shift to cover ground and Ctrl to creep.</summary>
    public static readonly CameraKeyMap Default =
        new(Key.W, Key.S, Key.A, Key.D, ModifierKeys.Shift, ModifierKeys.Control);

    /// <summary>Is this one of the four movement keys? An unbound slot never matches.</summary>
    public bool IsMoveKey(Key key) =>
        key != Key.None && (key == Forward || key == Back || key == Left || key == Right);
}

/// <summary>
/// Which keys a running <see cref="TransformGizmo"/> transform answers to — the modal keys and Blender's
/// axis locks. Same contract as <see cref="CameraKeyMap"/>: the gizmo ships with the defaults and takes a
/// replacement from whoever hosts it.
/// </summary>
public sealed record GizmoKeyMap(
    Key Move,
    Key Rotate,
    Key Scale,
    Key AxisX,
    Key AxisY,
    Key AxisZ,
    Key Commit,
    Key CommitAlt,
    Key Cancel)
{
    /// <summary>Blender's own: G/R/S, X/Y/Z, Enter or Space to keep it, Esc to drop it.</summary>
    public static readonly GizmoKeyMap Default = new(
        Key.G, Key.R, Key.S,
        Key.X, Key.Y, Key.Z,
        Key.Return, Key.Space, Key.Escape);

    /// <summary>The world axis a key locks the drag to, or -1. An unbound slot never matches.</summary>
    public int AxisOf(Key key)
    {
        if (key == Key.None) return -1;
        if (key == AxisX) return 0;
        if (key == AxisY) return 1;
        if (key == AxisZ) return 2;
        return -1;
    }
}
