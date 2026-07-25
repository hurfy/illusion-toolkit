using System.IO;
using System.Text.Json;

namespace Illusion;

/// <summary>
/// Application user settings (JSON in <c>%LOCALAPPDATA%\Illusion</c>).
/// Stores the game path chosen in the launcher; the same value is used by headless probes,
/// which have no way to ask for the path interactively.
/// </summary>
public sealed class UserSettings
{
    public string? GamePath { get; set; }

    /// <summary>When set, a successful Build no longer pops its "Built N archives" notice (the user ticked
    /// "Don't show this again"). Failures are always reported regardless.</summary>
    public bool SuppressBuildNotice { get; set; }

    /// <summary>Explicit blender.exe path (or its folder) for the Blender bridge. When unset the
    /// bridge auto-detects: .blend association → Program Files → Steam → PATH.</summary>
    public string? BlenderPath { get; set; }

    /// <summary>Blender bridge: also push edits automatically when leaving Edit Mode (the addon's
    /// N-panel toggle mirrors this; the panel button always works regardless).</summary>
    public bool BridgeAutoPush { get; set; } = true;

    /// <summary>Loopback port for the embedded MCP server, whose address the launcher displays.
    /// Only worth changing if something else on the machine already owns the default.</summary>
    public int McpPort { get; set; } = Mcp.McpHostOptions.DefaultPort;

    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Illusion", "settings.json");

    /// <summary>Reads settings; a corrupt/missing file yields clean settings rather than a crash.</summary>
    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsFile)) ?? new UserSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Settings are non-critical — start with empty ones.
        }
        return new UserSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            // Temp-then-move so a crash mid-write (or a concurrent headless probe reading the file) can never
            // observe a truncated settings.json and silently drop the saved game path.
            string tmp = SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SettingsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are non-critical — a failed write must not crash the application.
        }
    }
}
