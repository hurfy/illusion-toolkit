using System.Globalization;
using System.Windows;
using Illusion.Diagnostics;
using Illusion.Mcp;
using Illusion.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Illusion;

public partial class App : Application
{
    /// <summary>
    /// The MCP server, shared by every window. It belongs to the application rather than to a window
    /// because there is no single main window: the launcher and the editor replace one another as the
    /// user moves between them, and the server has to outlive both. Null only in probe runs, which
    /// never reach the UI. Reached statically, in the same spirit as <see cref="UserSettings.Load"/> —
    /// this codebase has no DI container to hang it from.
    /// </summary>
    public static McpServerHost? McpServer { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Game files must be culture-independent: the legacy resource (de)compilers still format and
        // parse numbers through the current culture (upstream MafiaToolkit forced a fixed culture at
        // startup for the same reason — on e.g. a Russian locale, floats would otherwise extract as
        // "0,34" and fail to repack). UI culture is left alone; this only pins number/date formatting.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        // Headless probes (--probe-*): diagnose load chains without UI,
        // reports are written to %TEMP%\illusion_*.txt.
        if (ProbeRunner.TryRun(e.Args))
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // A collision cook that was killed or crashed leaves its scratch directory behind; nothing else ever
        // deletes them. Best-effort and off the critical path.
        Task.Run(Assets.Collisions.PhysXCooker.SweepStaleScratch);

        // Follow the OS light/dark setting. ThemeMode is still [Experimental] in WPF (WPF0001);
        // we opt in deliberately and keep the suppression scoped to this one call.
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001

        StartMcpServer();

        // Start from the launcher (path selection + unpacking), not straight into the editor.
        new LauncherWindow().Show();
    }

    /// <summary>
    /// Brings the MCP server up. Deliberately not awaited: binding a socket is quick, but the
    /// launcher must appear regardless, and it follows the server through
    /// <see cref="McpServerHost.StateChanged"/> anyway. Starting cannot throw.
    /// </summary>
    private void StartMcpServer()
    {
        var settings = UserSettings.Load();
        McpServer = new McpServerHost(new McpHostOptions
        {
            // settings.json is hand-edited, so the value has to be treated as untrusted. Zero would
            // mean "any free port", handing out a different address every launch; anything outside
            // the TCP range would fail the bind with a message about argument ranges. Neither is
            // worth showing the user — come up on the default port, which the launcher displays.
            Port = settings.McpPort is > 0 and <= 65535 ? settings.McpPort : McpHostOptions.DefaultPort,

            // Where tools get their hands on the application. Only the UI marshal for now; the state
            // future tools need (the open document, the selection) is registered alongside it.
            ConfigureServices = services =>
                services.AddSingleton<IUiThreadMarshal>(new WpfUiThreadMarshal(Dispatcher)),
        });

        _ = McpServer.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Waiting here is what releases the port before the process goes; the windows are already
        // gone, so the pause is invisible, and it measures in milliseconds. The outer cap is a
        // safety net only — never let a stuck shutdown strand the process with no window to close.
        _ = McpServer?.StopAsync(TimeSpan.FromSeconds(3)).Wait(TimeSpan.FromSeconds(5));
        base.OnExit(e);
    }
}
