namespace Illusion.Formats.Frames;

/// <summary>A triple of 16-bit values (used by FrameObjectModel's physics-split bookkeeping).</summary>
public class Short3
{
    public ushort S1 { get; set; }
    public ushort S2 { get; set; }
    public ushort S3 { get; set; }

    /// <summary>Empty triple for the native-boundary mapper.</summary>
    internal Short3()
    {
    }

    public Short3(Short3 other)
    {
        S1 = other.S1;
        S2 = other.S2;
        S3 = other.S3;
    }

    public override string ToString()
    {
        return $"{S1} {S2} {S3}";
    }
}
