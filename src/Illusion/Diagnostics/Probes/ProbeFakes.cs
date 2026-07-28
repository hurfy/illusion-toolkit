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

// A stand-in selection + camera so the transform gizmo can lay out and paint without a viewport behind it.
// The view-projection is left as identity on purpose: a pivot given in normalized device coordinates then
// lands at a predictable pixel, which is what the overflow check needs.
internal sealed class FakeTransformGizmoHost : ITransformGizmoHost
{
    public Matrix4x4 GizmoViewProjection { get; set; } = Matrix4x4.Identity;
    public Vector3 GizmoCameraPosition { get; set; } = new(0f, 0f, -10f);
    public GizmoMode GizmoMode { get; set; } = GizmoMode.Move;
    public bool HasGizmoTarget { get; set; } = true;
    public bool CanTransformSelection { get; set; } = true;
    public Vector3 GizmoPivot { get; set; }
    public event Action? CameraMoved { add { } remove { } }

    /// <summary>Drag lifecycle calls in the order they arrived ("begin", "end", "cancel") — a modal transform's
    /// whole contract is which of these it ends with.</summary>
    public List<string> Calls { get; } = new();

    /// <summary>The last world delta handed over, so a probe can see what the drag actually asked for.</summary>
    public Matrix4x4 LastDelta { get; private set; } = Matrix4x4.Identity;

    public void GizmoBeginDrag() { Calls.Add("begin"); LastDelta = Matrix4x4.Identity; }
    public void GizmoApplyWorldDelta(Matrix4x4 totalWorldDelta) => LastDelta = totalWorldDelta;
    public void GizmoEndDrag() => Calls.Add("end");
    public void GizmoCancelDrag() => Calls.Add("cancel");
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
