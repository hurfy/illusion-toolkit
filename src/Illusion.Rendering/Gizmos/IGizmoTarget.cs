using System.Numerics;

namespace Illusion.Rendering.Gizmos;

/// <summary>
/// Camera surface the navigation gizmo drives: it reads the view matrix to place the axis balls,
/// snaps to a preset axis on click, and orbits on drag. Implemented by the application's viewport host;
/// a lightweight fake is used for headless rendering tests.
/// </summary>
public interface IGizmoTarget
{
    /// <summary>Current camera view matrix (world → view).</summary>
    Matrix4x4 CameraView { get; }

    /// <summary>Snap the camera to look down a world axis (front/back/top/bottom/left/right).</summary>
    void SnapCameraToAxis(Vector3 axis);

    /// <summary>Orbit the camera around its focus pivot by the given yaw/pitch deltas (radians).</summary>
    void OrbitCamera(float deltaYaw, float deltaPitch);

    /// <summary>Raised when the camera changes so the gizmo can repaint.</summary>
    event Action? CameraMoved;
}
