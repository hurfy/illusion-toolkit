using System.Diagnostics;
using System.IO;
using System.Windows;
using Illusion.Assets;

namespace Illusion.Views;

/// <summary>
/// Starting the game (and the multiplayer client) from an editor window. Both editors offer it, and both
/// have to look in exactly the same places — the executable is not in a fixed spot across editions, so a
/// second copy of the search order would eventually disagree with the first.
/// </summary>
internal static class GameLauncher
{
    /// <summary>M2Online launcher, expected at <c>&lt;game root&gt;\m2o\client\M2OLauncher.exe</c>.</summary>
    public static string M2OLauncherPath =>
        Path.Combine(MafiaEnvironment.GameRoot ?? "", "m2o", "client", "M2OLauncher.exe");

    /// <summary>Whether the multiplayer client is installed beside the game — the button is enabled by it.</summary>
    public static bool HasMultiplayer => File.Exists(M2OLauncherPath);

    /// <summary>
    /// Launches Mafia II. The executable isn't in a fixed spot across editions, so the usual names are probed
    /// in <c>pc\</c> first (the Steam layout), then in the game root.
    /// </summary>
    public static void Play(Window owner)
    {
        string[] candidates =
        {
            Path.Combine(MafiaEnvironment.PcFolder ?? "", "mafia2.exe"),
            Path.Combine(MafiaEnvironment.PcFolder ?? "", "launcher.exe"),
            Path.Combine(MafiaEnvironment.GameRoot ?? "", "mafia2.exe"),
            Path.Combine(MafiaEnvironment.GameRoot ?? "", "launcher.exe"),
        };

        string? exe = candidates.FirstOrDefault(File.Exists);
        if (exe == null)
        {
            AppDialog.Show(owner, new DialogOptions
            {
                Title = "Play",
                Icon = DialogIcon.Warning,
                Text = "Mafia II executable not found (looked for mafia2.exe / launcher.exe).",
            });
            return;
        }
        Launch(owner, exe);
    }

    public static void Multiplayer(Window owner) => Launch(owner, M2OLauncherPath);

    private static void Launch(Window owner, string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            });
        }
        catch (Exception ex)
        {
            AppDialog.Show(owner, new DialogOptions
            {
                Title = "Launch",
                Icon = DialogIcon.Error,
                Heading = "Failed to launch",
                Text = ex.Message,
            });
        }
    }
}
