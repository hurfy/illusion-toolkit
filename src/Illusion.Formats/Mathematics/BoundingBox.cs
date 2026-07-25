using System.Numerics;

namespace Illusion.Formats.Mathematics;

/// <summary>
/// Axis-aligned bounding box exactly as serialized in the game formats (Min vector, then Max vector).
/// Local replacement for the former Vortice.Mathematics.BoundingBox dependency.
/// </summary>
public struct BoundingBox
{
    public Vector3 Min;
    public Vector3 Max;

    public BoundingBox(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public override string ToString() => $"Min:{Min} Max:{Max}";
}
