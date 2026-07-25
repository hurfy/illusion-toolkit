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
    GizmoMode GizmoMode { get; }
    /// <summary>A transformable frame object is selected and a manipulation tool is active.</summary>
    bool HasGizmoTarget { get; }
    /// <summary>World-space pivot the gizmo sits at (selection bounds centre).</summary>
    Vector3 GizmoPivot { get; }
    /// <summary>Raised each frame the camera changes so the overlay repaints.</summary>
    event Action? CameraMoved;

    void GizmoBeginDrag();
    void GizmoApplyWorldDelta(Matrix4x4 totalWorldDelta);
    void GizmoEndDrag();
}
