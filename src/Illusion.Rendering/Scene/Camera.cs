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

    private static readonly Vector3 WorldUp = new(0f, 0f, 1f);

    /// <summary>Max pitch magnitude (radians). Kept just below π/2 so <see cref="Forward"/> never aligns with
    /// <see cref="WorldUp"/> — otherwise <see cref="Right"/> = cross(Forward, WorldUp) degenerates to NaN.</summary>
    public const float MaxPitch = 1.55f;

    /// <summary>Spherical yaw/pitch → unit forward vector (Mafia convention: Z up).</summary>
    public static Vector3 ForwardFrom(float yaw, float pitch)
    {
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
        return Vector3.Normalize(new Vector3(cp * cy, cp * sy, sp));
    }

    public Vector3 Forward => ForwardFrom(Yaw, Pitch);

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, WorldUp));

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, WorldUp);

    public Matrix4x4 Projection =>
        Matrix4x4.CreatePerspectiveFieldOfView(Fov, AspectRatio, Near, Far);

    public Matrix4x4 ViewProjection => View * Projection;

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
