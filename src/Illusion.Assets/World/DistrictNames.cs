using System.Text.RegularExpressions;

namespace Illusion.Assets.World;

/// <summary>
/// Canonicalizes a Mafia AREA/target name to a real <c>/sds/city</c> district base name. Shared by the
/// area selector (<see cref="MapCatalog"/>) and the streaming zones (<see cref="AreaZones"/>) so both
/// subsystems resolve names by the exact same rule and never silently drift apart.
/// </summary>
internal static class DistrictNames
{
    // Known mismatches between AREA name ↔ file name.
    private static readonly Dictionary<string, string> Alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kingston"] = "kingstone",
    };

    /// <summary>
    /// Resolves a logical area name to a base district name present in <paramref name="valid"/>, or null:
    /// (1) exact match, (2) alias, (3) drop a trailing number (<c>prazdna01</c> → <c>prazdna</c>).
    /// Case-sensitivity is decided by the collection's own comparer.
    /// </summary>
    public static string? Resolve(string? name, ICollection<string> valid)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (valid.Contains(name)) return name;
        if (Alias.TryGetValue(name, out string? a) && valid.Contains(a)) return a;
        string stripped = Regex.Replace(name, @"\d+$", ""); // prazdna01 → prazdna
        if (stripped != name && valid.Contains(stripped)) return stripped;
        return null;
    }
}
