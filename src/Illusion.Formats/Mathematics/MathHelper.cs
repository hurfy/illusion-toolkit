namespace Illusion.Formats.Mathematics;

/// <summary>
/// Degree/radian/clamp helpers formerly provided by Vortice.Mathematics.MathHelper.
/// Formulas are kept 1:1 so serialized transform math does not drift.
/// </summary>
internal static class MathHelper
{
    public static float ToRadians(float degrees) => degrees * (MathF.PI / 180f);

    public static float ToDegrees(float radians) => radians * (180f / MathF.PI);

    public static float Clamp(float value, float min, float max) =>
        value < min ? min : value > max ? max : value;
}
