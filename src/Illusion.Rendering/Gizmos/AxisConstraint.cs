using System.Numerics;

namespace Illusion.Rendering.Gizmos;

/// <summary>
/// The keyboard axis lock of a gizmo drag, Blender-style: <c>X</c>/<c>Y</c>/<c>Z</c> pins the drag to one world
/// axis, <c>Shift</c>+<c>X</c>/<c>Y</c>/<c>Z</c> pins it to the plane ACROSS that axis (the axis is excluded and
/// the other two stay free). A value type with no history of its own — <see cref="Toggle"/> is the whole state
/// machine, so the same key pressed twice releases the lock.
/// </summary>
public readonly record struct AxisConstraint(int Axis, bool IsPlane)
{
    private static readonly string[] AxisNames = { "X", "Y", "Z" };

    /// <summary>No lock: the drag follows whichever handle was grabbed.</summary>
    public static readonly AxisConstraint None = new(-1, false);

    /// <summary>Whether a lock is in effect at all.</summary>
    public bool IsSome => Axis >= 0;

    /// <summary>Whether world axis <paramref name="axis"/> is one the drag may still act on. Unlocked → all three.</summary>
    public bool Includes(int axis) => !IsSome || (IsPlane ? axis != Axis : axis == Axis);

    /// <summary>Applies one key press: a different axis/plane takes over, the same one again releases the lock
    /// (so <c>X</c> <c>X</c> is free again, and <c>X</c> then <c>Shift+X</c> swaps the axis lock for its plane).</summary>
    public static AxisConstraint Toggle(AxisConstraint current, int axis, bool plane) =>
        current.Axis == axis && current.IsPlane == plane ? None : new AxisConstraint(axis, plane);

    /// <summary>Overlay label — the axis for an axis lock ("X"), the two free axes for a plane lock ("YZ").</summary>
    public string Label => !IsSome ? "" : !IsPlane ? AxisNames[Axis] : Axis == 0 ? "YZ" : Axis == 1 ? "XZ" : "XY";

    /// <summary>Spreads one scale factor over the axes the lock allows; the excluded ones keep their size (1).</summary>
    public Vector3 ScaleFactors(float factor) => new(
        Includes(0) ? factor : 1f,
        Includes(1) ? factor : 1f,
        Includes(2) ? factor : 1f);
}
