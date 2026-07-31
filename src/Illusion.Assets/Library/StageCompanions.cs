namespace Illusion.Assets.Library;

/// <summary>
/// Archives that another archive cannot be LOOKED at without. Mafia II does not make every resource
/// self-contained: a car keeps its own body, its own paint and its own sounds, and takes everything generic
/// — the chrome and glass shaders, the headlight glows, the wheels and hubcaps — from a shared library the
/// game always has loaded beside it. Open the car on its own and it comes out white and wheel-less, because
/// two thirds of the textures its materials name are simply not in the file.
/// <para>
/// Nothing INSIDE a car names that library: no frame, material or buffer reference in the archive points
/// outward at all (the frame format has no cross-archive addressing). The pairing is knowledge about the
/// game, which is why it is written down here rather than discovered.
/// </para>
/// </summary>
public static class StageCompanions
{
    // The shared car library, in the order the game would have it: cars_universal carries the wheels,
    // hubcaps, lights and the generic Univ* / wheel_univers_* textures; cars_universal2 a second set.
    private static readonly string[] CarLibrary = { "cars_universal.sds", "cars_universal2.sds" };

    /// <summary>
    /// The archives to load alongside <paramref name="sds"/> so it can be shown as the game shows it.
    /// Empty for anything with no known companion — which is most of the game.
    /// </summary>
    public static IReadOnlyList<FileInfo> For(FileInfo sds)
    {
        DirectoryInfo? folder = sds.Directory;
        if (folder == null || !folder.Name.Equals("cars", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<FileInfo>();

        // The libraries are not their own companions, and neither are the archives in cars\ that are no
        // vehicle at all (a texture pack has no materials to satisfy).
        if (CarLibrary.Contains(sds.Name, StringComparer.OrdinalIgnoreCase)) return Array.Empty<FileInfo>();

        var found = new List<FileInfo>();
        foreach (string name in CarLibrary)
        {
            var companion = new FileInfo(Path.Combine(folder.FullName, name));
            if (companion.Exists) found.Add(companion);
        }
        return found;
    }
}
