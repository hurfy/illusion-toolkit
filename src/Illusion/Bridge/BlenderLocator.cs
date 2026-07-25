using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Illusion.Bridge;

/// <summary>
/// Finds blender.exe on this machine, in trust order: the user's settings override, the .blend
/// file-association registry entry, the standard Blender Foundation install folders (highest
/// version), the Steam library (app 365670), and finally PATH. Returns null when nothing works —
/// the caller prompts for a manual path.
/// </summary>
internal static class BlenderLocator
{
    public static string? Locate(string? settingsOverride)
    {
        foreach (string? candidate in Candidates(settingsOverride))
        {
            string? exe = Normalize(candidate);
            if (exe != null) return exe;
        }
        return null;
    }

    private static IEnumerable<string?> Candidates(string? settingsOverride)
    {
        yield return settingsOverride;
        yield return FromBlendAssociation();
        yield return FromProgramFiles();
        yield return FromSteam();
        yield return FromPath();
    }

    // Accepts a file or its folder; resolves blender-launcher.exe to the sibling blender.exe (the
    // launcher just detaches the console — the real process handle comes from blender.exe).
    private static string? Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        string path = candidate;
        if (Directory.Exists(path)) path = Path.Combine(path, "blender.exe");
        if (Path.GetFileName(path).Equals("blender-launcher.exe", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Path.GetDirectoryName(path)!, "blender.exe");
        return File.Exists(path) ? path : null;
    }

    // HKCR\blendfile\shell\open\command (installer) or the per-user HKCU\Software\Classes variant.
    // The uninstaller has been known to leave stale keys, so existence is re-checked in Normalize.
    private static string? FromBlendAssociation()
    {
        foreach (RegistryKey root in new[] { Registry.ClassesRoot, Registry.CurrentUser })
        {
            try
            {
                string sub = root == Registry.CurrentUser
                    ? @"Software\Classes\blendfile\shell\open\command"
                    : @"blendfile\shell\open\command";
                using RegistryKey? key = root.OpenSubKey(sub);
                if (key?.GetValue(null) is string command)
                {
                    string? exe = ParseCommandExe(command);
                    if (exe != null) return exe;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
            {
                // Inaccessible hive — fall through to the next source.
            }
        }
        return null;
    }

    // First token of a shell command: either the quoted path or everything up to the first space.
    private static string? ParseCommandExe(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    // C:\Program Files\Blender Foundation\Blender <X.Y>\blender.exe — one folder per major.minor;
    // pick the highest by parsed version.
    private static string? FromProgramFiles()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        if (!Directory.Exists(root)) return null;

        return Directory.EnumerateDirectories(root, "Blender*")
            .Select(dir => (Dir: dir, Version: ParseVersion(Path.GetFileName(dir))))
            .Where(x => x.Version != null)
            .OrderByDescending(x => x.Version)
            .Select(x => Path.Combine(x.Dir, "blender.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static Version? ParseVersion(string folderName)
    {
        Match m = Regex.Match(folderName, @"(\d+(?:\.\d+)+)");
        return m.Success && Version.TryParse(m.Groups[1].Value, out Version? v) ? v : null;
    }

    // Steam: SteamPath from the registry, every library from libraryfolders.vdf, then the Blender
    // app folder (appid 365670 installs to steamapps\common\Blender).
    private static string? FromSteam()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key?.GetValue("SteamPath") is not string steamPath || string.IsNullOrEmpty(steamPath)) return null;

            var libraries = new List<string> { steamPath };
            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }

            return libraries
                .Select(lib => Path.Combine(lib, "steamapps", "common", "Blender", "blender.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static string? FromPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path == null) return null;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), "blender.exe"))
            .FirstOrDefault(File.Exists);
    }
}
