namespace Illusion.Scene;

/// <summary>Shared search text for the scene tree.</summary>
public static class SceneSearch
{
    public static string Query = "";

    public static bool Matches(string name)
    {
        if (string.IsNullOrWhiteSpace(Query)) return true;
        return name != null && name.Contains(Query, StringComparison.OrdinalIgnoreCase);
    }
}
