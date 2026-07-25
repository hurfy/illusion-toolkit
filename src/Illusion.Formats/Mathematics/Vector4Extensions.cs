using System.Numerics;

namespace Illusion.Formats.Mathematics;

internal static class Vector4Extensions
{
    public static bool IsNaN(this Vector4 vector)
    {
        return float.IsNaN(vector.X) || float.IsNaN(vector.Y) || float.IsNaN(vector.Z) || float.IsNaN(vector.W);
    }
}
