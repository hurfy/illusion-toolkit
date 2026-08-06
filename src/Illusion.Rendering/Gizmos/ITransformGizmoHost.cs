using System.Numerics;

namespace Illusion.Rendering.Gizmos;

/// <summary>
/// Surface the transform gizmo drives. The host owns the camera, the selection and the transform edit; the
/// gizmo only maps mouse drags into world-space delta matrices and hands them back. Implemented by the
/// application's viewport host; headless probes drive it through a fake.
/// </summary>
public interface ITransformGizmoHost
{
    Matrix4x4 GizmoViewProjection { get; }
    Vector3 GizmoCameraPosition { get; }
    /// <summary>
    /// The single direction every ray of a PARALLEL projection runs along — null while the projection is a
    /// perspective one, where there is no such thing: the way the camera looks at a point is the line from the
    /// eye to it, and that differs across the screen. Only the gizmo's screen-facing tools (plane move, free
    /// rotate) ask, and only they can tell the difference; everything else works off the view-projection.
    /// </summary>
    Vector3? GizmoParallelDir { get; }
    GizmoMode GizmoMode { get; }
    /// <summary>A transformable frame object is selected and a manipulation tool is active.</summary>
    bool HasGizmoTarget { get; }
    /// <summary>A transformable frame object is selected, whatever tool is active — a modal transform is
    /// started from the keyboard and does not go through the tool shelf.</summary>
    bool CanTransformSelection { get; }
    /// <summary>World-space pivot the gizmo sits at (selection bounds centre).</summary>
    Vector3 GizmoPivot { get; }
    /// <summary>Raised each frame the camera changes so the overlay repaints.</summary>
    event Action? CameraMoved;

    /// <summary>
    /// A drag is starting. <paramref name="mode"/> is what the grabbed handle actually DOES, which is not the
    /// same as <see cref="GizmoMode"/>: a modal transform is started from the keyboard and never touches the
    /// tool shelf, so a keyboard scale while the shelf says Move arrives here as Scale.
    /// </summary>
    void GizmoBeginDrag(GizmoMode mode);
    void GizmoApplyWorldDelta(Matrix4x4 totalWorldDelta);
    void GizmoEndDrag();
    /// <summary>Abandons the drag in progress: the objects go back to where they started and nothing is
    /// recorded. The caller has already applied an identity delta, so this only drops the drag's state.</summary>
    void GizmoCancelDrag();
}
