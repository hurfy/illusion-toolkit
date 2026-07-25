namespace Illusion.Formats.Geometry;

/// <summary>
/// Pair of IEEE-754 half floats — the packed texcoord channel of the game's vertex layouts.
/// Local replacement for the former Vortice.Mathematics.PackedVector.Half2 dependency.
/// </summary>
public struct Half2
{
    public Half X;
    public Half Y;

    public Half2(Half x, Half y)
    {
        X = x;
        Y = y;
    }

    public Half2(float x, float y)
    {
        X = (Half)x;
        Y = (Half)y;
    }

    public override string ToString() => $"{X}:{Y}";
}
