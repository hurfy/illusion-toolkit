namespace Illusion.Formats.CityAreas;

/// <summary>AREA entry: volume name + up to two district/interior targets it keeps resident.</summary>
public sealed class CityAreaEntry
{
    public string Name { get; init; } = null!; // "AREA019_MIDTOWN_EASTSIDE" — AREA-volume name in the FrameResource
    public string? Target1 { get; init; }    // "midtown"  (or null)
    public string? Target2 { get; init; }    // "eastside" (or null — single-district core)
}
