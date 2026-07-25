namespace Illusion.Assets.World;

/// <summary>
/// One selector entry: a district or interior (summer .sds + optional winter <c>_z</c>) + list of neighbors
/// by the AREA graph (for context at the seams). Source — <c>cityareas.bin</c> from city_univers,
/// name resolution into real files <c>/sds/city/</c>.
/// </summary>
public sealed class MapArea
{
    public string BaseName { get; init; } = null!; // "midtown"
    public FileInfo Summer { get; init; } = null!;
    public FileInfo? Winter { get; init; }       // or null
    public bool IsInterior { get; init; }        // open-world district vs mission interior
    public List<string> Neighbors { get; } = new(); // BaseName of neighbors by the AREA graph

    public bool HasWinter => Winter != null;
    public FileInfo FileFor(bool winter) => winter && Winter != null ? Winter : Summer;
    // Icon by type (interior / district), without a season marker — the season is changed by a separate selector.
    public string Display => (IsInterior ? "⌂ " : "▦ ") + BaseName;
}
