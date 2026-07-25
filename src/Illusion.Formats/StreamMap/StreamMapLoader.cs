namespace Illusion.Formats.StreamMap;

/// <summary>Loader — a single loadable asset, active for lines whose LineID is in the range [Start, End].</summary>
public sealed class StreamMapLoader
{
    public int Start;
    public int End;
    public StreamGroupType Type;
    public string Path = null!;   // path from the game root, e.g. "/sds/city/sicily01.sds"
    public string Entity = null!; // instance/spawn name, e.g. "City-1"
}
