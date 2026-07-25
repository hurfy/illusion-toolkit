namespace Illusion.Formats.CityAreas;

/// <summary>
/// Read-only parser for <c>cityareas.bin</c> (magic 1668571506, located in
/// <c>city_univers.sds\missions\CITY\</c>). This is the open-world streaming table: each entry
/// binds an AREA-volume to 1–2 districts/interiors — the engine keeps them loaded while the camera is
/// in the volume. Entries with two targets = junction zones (load both neighbors → seamlessness). Layout/logic
/// as in the toolkit <c>ResourceTypes.City.CityAreas</c>, but read-only.
/// </summary>
public sealed class CityAreasFile
{
    public const int Magic = 1668571506;

    public IReadOnlyList<CityAreaEntry> Areas { get; private set; } = Array.Empty<CityAreaEntry>();

    public static CityAreasFile Load(string path)
    {
        var f = new CityAreasFile();
        f.Parse(File.ReadAllBytes(path));
        return f;
    }

    private void Parse(byte[] b)
    {
        Areas = Native.Misc.NativeMiscFiles.ReadCityAreas(b);
    }

}
