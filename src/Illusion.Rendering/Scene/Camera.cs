using System.Numerics;

namespace Illusion.Rendering.Scene;

/// <summary>
/// Free (fly) camera. Mafia uses Z up, so "up" = (0,0,1).
/// </summary>
public sealed class Camera
{
    public Vector3 Position = new(0f, -50f, 20f);
    public float Yaw;    // rotation around Z
    public float Pitch;  // tilt
    public float MoveSpeed = 100f; // base movement speed (units/s); Shift accelerates
    public float Fov = MathF.PI / 3f;
    public float Near = 0.5f;
    public float Far = 30000f;
    public float AspectRatio = 1f;

    /// <summary>
    /// Draw the scene in parallel projection instead of a perspective one — no vanishing point, so an object
    /// keeps its size wherever it stands and two edges that line up on screen line up in the world. It is what
    /// makes an axis-aligned view worth having: the viewport becomes a drawing to measure against rather than a
    /// photograph. The viewport turns it on when the navigation gizmo snaps to an axis.
    /// </summary>
    public bool Orthographic;

    /// <summary>
    /// How much world the parallel view spans top to bottom — its "zoom", and the only thing that changes one,
    /// since moving a camera with no vanishing point toward something does not make it any bigger. Ignored
    /// while <see cref="Orthographic"/> is false. The width follows from <see cref="AspectRatio"/>, exactly as
    /// the perspective field of view is vertical too.
    /// </summary>
    public float OrthoHeight = 50f;

    private static readonly Vector3 WorldUp = new(0f, 0f, 1f);

    /// <summary>
    /// Max pitch magnitude (radians): straight down, exactly. It used to stop 1.2° short, because the basis was
    /// derived by crossing <see cref="Forward"/> with <see cref="WorldUp"/> and that collapses to nothing where
    /// the two line up — but the whole point of a top view is that it IS a top view, and a degree of lean is
    /// plainly visible in one (a ten-metre wall shows a fifth of a metre of it, and an object slid along the
    /// ground appears to change height). <see cref="Right"/> and <see cref="View"/> are built without that cross
    /// product now, so the pole is an ordinary direction and the limit can be the real one.
    /// </summary>
    public const float MaxPitch = MathF.PI / 2f;

    /// <summary>Spherical yaw/pitch → unit forward vector (Mafia convention: Z up).</summary>
    public static Vector3 ForwardFrom(float yaw, float pitch)
    {
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
        return Vector3.Normalize(new Vector3(cp * cy, cp * sy, sp));
    }

    public Vector3 Forward => ForwardFrom(Yaw, Pitch);

    /// <summary>
    /// Camera right. Written out rather than as normalize(cross(Forward, WorldUp)): for a yaw/pitch camera the
    /// two are the same vector — the pitch cancels out of that cross product entirely, leaving the yaw — and
    /// this form is still a unit vector where the cross product is the zero one, looking straight down.
    /// </summary>
    public Vector3 Right => new(MathF.Sin(Yaw), -MathF.Cos(Yaw), 0f);

    /// <summary>
    /// World → view, assembled from the camera's own basis. Matrix4x4.CreateLookAt returns exactly this for
    /// every direction it can handle; it just cannot handle the one pointing along <see cref="WorldUp"/>, since
    /// it recovers the right vector with the same cross product <see cref="Right"/> avoids.
    /// </summary>
    public Matrix4x4 View
    {
        get
        {
            Vector3 f = Forward, r = Right, u = Vector3.Cross(r, f);
            return new Matrix4x4(
                r.X, u.X, -f.X, 0f,
                r.Y, u.Y, -f.Y, 0f,
                r.Z, u.Z, -f.Z, 0f,
                -Vector3.Dot(r, Position), -Vector3.Dot(u, Position), Vector3.Dot(f, Position), 1f);
        }
    }

    public Matrix4x4 Projection => Orthographic
        ? Matrix4x4.CreateOrthographic(OrthoHeight * AspectRatio, OrthoHeight, Near, Far)
        : Matrix4x4.CreatePerspectiveFieldOfView(Fov, AspectRatio, Near, Far);

    public Matrix4x4 ViewProjection => View * Projection;

    /// <summary>
    /// Where a shader should treat the eye as being when it asks "which way am I looking at this surface from" —
    /// the specular highlight and the sky ray. A parallel projection has no eye: its rays never converge, so
    /// every point is looked at along the same direction. Reporting a point far back along the view axis says
    /// exactly that in the language those shaders already speak, without a second code path: at
    /// <see cref="ParallelEyeSpans"/> view heights the rays to everything on screen are parallel to within a
    /// sixteenth of a degree. A perspective camera reports where it actually stands.
    /// <para>NOT the camera's position — anything that means "where the viewer is" (streaming, draw distance,
    /// the coordinate readout, the collision overlay's depth nudge) must keep using <see cref="Position"/>.</para>
    /// </summary>
    public Vector3 ShadingEye => Orthographic ? Position - Forward * (OrthoHeight * ParallelEyeSpans) : Position;

    private const float ParallelEyeSpans = 512f;

    /// <summary>Offset in camera axes: X=right, Y=forward, Z=up (world).</summary>
    public void Move(float right, float forward, float up)
    {
        Position += Right * right + Forward * forward + WorldUp * up;
    }

    public void AddLook(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -MaxPitch, MaxPitch);
    }

    public void LookAt(Vector3 eye, Vector3 target)
    {
        Position = eye;
        Vector3 d = target - eye;
        if (d.LengthSquared() < 1e-12f) return; // degenerate (eye == target) — keep the current orientation
        Vector3 f = Vector3.Normalize(d);
        // Clamp to MaxPitch like every other pitch writer — ±π/2 exactly degenerates Right to NaN.
        Pitch = Math.Clamp(MathF.Asin(Math.Clamp(f.Z, -1f, 1f)), -MaxPitch, MaxPitch);
        Yaw = MathF.Atan2(f.Y, f.X);
    }
}
