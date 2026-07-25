namespace Illusion.Formats.StreamMap;

/// <summary>Line — a named streaming state (used in LUA). Belongs to a line-group (GroupID → GroupHeaders).</summary>
public sealed class StreamMapLine
{
    public string Name = null!;
    public int LineID;
    public int GroupID;
}
