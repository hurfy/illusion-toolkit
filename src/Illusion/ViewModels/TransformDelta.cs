using Illusion.Rendering.Gizmos;

namespace Illusion.ViewModels;

/// <summary>
/// What "how much did that change it by" means, per transform: a difference for a move or a rotation, and a
/// FACTOR for a scale — ×2 is twice the size it was, which is the only reading that survives a non-uniform
/// starting scale (a difference of +1 means nothing when one axis started at 0.1 and another at 10).
/// <para>
/// The pair is an inverse: <see cref="Apply"/> with what <see cref="Measure"/> returned puts the value back
/// where it already is. That is what lets the viewport overlay be edited — the number it shows can be retyped
/// as often as you like and always means the same change from the same starting point.
/// </para>
/// </summary>
internal static class TransformDelta
{
    /// <summary>A scale below this has no factor that leads anywhere: 0 × anything is still 0.</summary>
    private const float ScaleFloor = 1e-6f;

    /// <summary>The change from <paramref name="baseValue"/> to <paramref name="current"/>.</summary>
    public static float Measure(GizmoMode mode, float baseValue, float current) => mode switch
    {
        GizmoMode.Move or GizmoMode.Rotate => current - baseValue,
        GizmoMode.Scale => MathF.Abs(baseValue) > ScaleFloor ? current / baseValue : 1f,
        _ => 0f,
    };

    /// <summary>The value that is <paramref name="delta"/> away from <paramref name="baseValue"/>.</summary>
    public static float Apply(GizmoMode mode, float baseValue, float delta) => mode switch
    {
        GizmoMode.Move or GizmoMode.Rotate => baseValue + delta,
        GizmoMode.Scale => baseValue * delta,
        _ => baseValue,
    };

    /// <summary>The change that means "nothing happened" — 0 for a difference, 1 for a factor.</summary>
    public static float Neutral(GizmoMode mode) => mode == GizmoMode.Scale ? 1f : 0f;
}
