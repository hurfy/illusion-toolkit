using System.Numerics;
using Illusion.Domain;
using Illusion.Rendering.Gizmos;

namespace Illusion.Diagnostics.Probes;

// A stand-in camera target so the gizmo can render without a real D3D viewport.
internal sealed class FakeGizmoTarget : IGizmoTarget
{
    public Matrix4x4 CameraView { get; set; } = Matrix4x4.Identity;
    public Vector3 LastSnap { get; private set; }
    public (float Yaw, float Pitch) LastOrbit { get; private set; }
    public void SnapCameraToAxis(Vector3 axis) => LastSnap = axis;
    public void OrbitCamera(float deltaYaw, float deltaPitch) => LastOrbit = (deltaYaw, deltaPitch);
    public event Action? CameraMoved { add { } remove { } }
}

// Records undo/redo calls so the stack's ordering can be asserted.
internal sealed class FakeEdit : IEditAction
{
    private readonly List<string> _log;
    public string Name { get; }
    public bool Discarded { get; private set; }
    public FakeEdit(List<string> log, string name) { _log = log; Name = name; }
    public void Undo() => _log.Add("undo:" + Name);
    public void Redo() => _log.Add("redo:" + Name);
    public void Discard() => Discarded = true;
}
