using Illusion.Domain;
using Illusion.Formats.Compression;
using Illusion.Formats.ResourceFormats;

namespace Illusion.Assets;

/// <summary>
/// Resolves the Mafia II install layout (pc / root / resources / city folders) from a path passed by the
/// caller, and points the Oodle native binary at the game folder (needed only for Mafia II DE oodle
/// blocks). The format layer no longer has any global state to initialize —
/// <see cref="Formats.GameProfile"/> and options are passed explicitly at each call.
/// </summary>
public static class MafiaEnvironment
{
    private static bool _initialized;

    public static bool IsInitialized => _initialized;

    /// <summary>The game's <c>pc</c> folder (holds the exe and <c>sds\</c>). Valid after a successful TryInitialize.</summary>
    public static string PcFolder { get; private set; } = null!;

    /// <summary>Root of the game install (sibling of <c>pc\</c>, where <c>edit\</c> and our <c>resources\</c> live).</summary>
    public static string GameRoot { get; private set; } = null!;

    /// <summary>Folder of unpacked resources <c>&lt;root&gt;\resources</c> — mirrors the game's structure.</summary>
    public static string? ResourcesFolder => GameRoot is null ? null : Path.Combine(GameRoot, "resources");

    public static string CityFolder => Path.Combine(PcFolder, "sds", "city");

    /// <summary>The shared <c>city_univers</c> SDS (AREA volumes + <c>missions\CITY\cityareas.bin</c>).</summary>
    public static string CityUniversSds => Path.Combine(PcFolder, "sds", "city_univers", "city_univers.sds");

    /// <summary>Resolves <c>cityareas.bin</c> (AREA targets + district adjacency) out of <c>city_univers</c>, or null.</summary>
    public static string? TryGetCityAreasBin(Func<FileInfo, string> ensureExtracted)
    {
        var cuSds = new FileInfo(CityUniversSds);
        if (!cuSds.Exists) return null;
        string extracted = ensureExtracted(cuSds);
        string bin = Path.Combine(extracted, "missions", "CITY", "cityareas.bin");
        return File.Exists(bin) ? bin : null;
    }

    /// <summary>
    /// The game's base StreamMap: <c>&lt;install&gt;\edit\tables\StreamMapa.bin</c> (in the game ROOT,
    /// sibling of <c>pc\</c> — like <c>edit\materials</c>). Loader paths inside are relative to <see cref="PcFolder"/>.
    /// </summary>
    public static string StreamMapPath { get; private set; } = null!;

    /// <summary>Game root from an arbitrary path (pc or root) — pure path logic, no initialization.</summary>
    public static string? ResolveGameRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string pc = path;
        if (!string.Equals(Path.GetFileName(pc), "pc", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Path.Combine(pc, "pc")))
        {
            pc = Path.Combine(pc, "pc");
        }
        return string.Equals(Path.GetFileName(pc), "pc", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(pc)?.FullName ?? pc
            : pc;
    }

    /// <summary>
    /// Where this game-SDS is unpacked to: <c>&lt;root&gt;\resources\&lt;path-from-root&gt;\&lt;name&gt;.sds\</c>.
    /// Mirrors the SDS location relative to the game root (e.g. pc\sds\city\midtown.sds).
    /// </summary>
    public static string ExtractedDir(FileInfo sds)
    {
        string rel = Path.GetRelativePath(GameRoot, sds.Directory!.FullName);
        return Path.Combine(ResourcesFolder!, rel, sds.Name);
    }

    /// <param name="pcPath">Explicit path to the game's <c>pc</c> (or root) folder. The caller resolves
    /// where the path comes from (launcher input, saved settings, ...) — this layer never reads settings.</param>
    public static bool TryInitialize(string? pcPath, out string? error)
    {
        error = null;
        if (_initialized)
        {
            return true;
        }

        string? pc = pcPath;
        if (string.IsNullOrEmpty(pc))
        {
            error = "Game path is not set — pick the Mafia II folder in the launcher first.";
            return false;
        }

        // Allow specifying either pc or the game root: if there is a pc\ inside — take it.
        if (!string.Equals(Path.GetFileName(pc), "pc", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Path.Combine(pc, "pc")))
        {
            pc = Path.Combine(pc, "pc");
        }
        if (!Directory.Exists(pc))
        {
            error = "Game folder not found: " + pc;
            return false;
        }
        PcFolder = pc;

        // Game root (edit\, resources\) — one level above pc\. pc is already normalized above, so
        // ResolveGameRoot is idempotent here and yields the identical root without duplicating the rule.
        GameRoot = ResolveGameRoot(pc) ?? pc;
        StreamMapPath = Path.Combine(GameRoot, "edit", "tables", "StreamMapa.bin");

        // Point the native oodle shim at the game's own oo2core DLL. Only Mafia II DE archives carry oodle blocks;
        // a classic install has none, so a missing DLL is harmless until an oodle block is decompressed.
        OodleNative.TryResolveFrom(pc);

        // Prefer the installation's own physics-surface names over the ones we ship. Purely cosmetic — indices,
        // and therefore collision colours, come from the same table either way — so a modded or missing
        // tables.sds silently leaves the shipped copy in place.
        TryAdoptGameMaterialNames();

        _initialized = true;
        return true;
    }

    private static void TryAdoptGameMaterialNames()
    {
        try
        {
            IReadOnlyDictionary<int, string>? tokens = MaterialsPhysicsTable.TryReadFromGame(GameRoot);
            if (tokens is not null) CollisionMaterialCatalog.ApplyGameTokens(tokens);
        }
        catch (Exception)
        {
            // An unreadable table must never stop the toolkit from opening — the shipped copy is authoritative
            // enough for every stock install, and --probe-collision-materials is what proves that.
        }
    }
}
