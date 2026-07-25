using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Mcp;
using Microsoft.Win32;

namespace Illusion.Views;

/// <summary>
/// Startup window: pick the game path, unpack all resources into <c>&lt;game&gt;\resources</c> (mandatory),
/// then enter the Map Editor (the 3D window). Resource Editor is a stub for now.
/// </summary>
public partial class LauncherWindow : Window
{
    // Status-dot palette, reusing the colours already established elsewhere in the app: the ready
    // card's green check, the warning card's amber, the notice banner's error red.
    private static readonly Brush McpRunningBrush = Frozen("#60C060");
    private static readonly Brush McpStartingBrush = Frozen("#C77700");
    private static readonly Brush McpFailedBrush = Frozen("#E0736B");
    private static readonly Brush McpStoppedBrush = Frozen("#808080");

    private readonly McpServerHost? _mcp = App.McpServer;
    private DispatcherTimer? _copiedTimer;
    private bool _busy;

    public LauncherWindow()
    {
        InitializeComponent();

        // Subscribed here rather than in Loaded: the server starts before this window does, so its
        // first state changes can land before Loaded ever runs.
        if (_mcp is not null)
        {
            _mcp.StateChanged += OnMcpStateChanged;
            ShowMcpState(_mcp.State);
        }

        // The launcher is built anew each time the editor hands control back, so a handler left on
        // the application-lifetime server would pin every window that ever existed.
        Closed += (_, _) =>
        {
            if (_mcp is not null) _mcp.StateChanged -= OnMcpStateChanged;
            _copiedTimer?.Stop();
        };

        Loaded += (_, _) =>
        {
            // Prefill with the last saved path (remembered after the game is opened successfully).
            string? saved = UserSettings.Load().GamePath;
            if (!string.IsNullOrEmpty(saved) && string.IsNullOrEmpty(PathBox.Text)) PathBox.Text = saved;
            RefreshState();
        };
    }

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    // Raised on a thread-pool thread — hop to the dispatcher before touching the controls.
    private void OnMcpStateChanged(McpServerState state) =>
        Dispatcher.BeginInvoke(() => ShowMcpState(state));

    private void ShowMcpState(McpServerState state)
    {
        _copiedTimer?.Stop();

        switch (state.Status)
        {
            case McpServerStatus.Running:
                McpDot.Fill = McpRunningBrush;
                McpAddressText.Text = state.Address;
                McpStateText.Text = "MCP running";
                McpAddressPanel.Cursor = Cursors.Hand;
                McpAddressPanel.ToolTip = "Click to copy the MCP server address";
                break;

            case McpServerStatus.Starting:
                McpDot.Fill = McpStartingBrush;
                // Nothing to put beside "starting…" yet, and repeating the word would just be noise.
                McpAddressText.Text = "";
                McpStateText.Text = "MCP starting…";
                McpAddressPanel.Cursor = null;
                McpAddressPanel.ToolTip = null;
                break;

            case McpServerStatus.Failed:
                McpDot.Fill = McpFailedBrush;
                McpAddressText.Text = state.Error;
                McpStateText.Text = "MCP failed";
                McpAddressPanel.Cursor = null;
                McpAddressPanel.ToolTip = state.Error;
                break;

            default:
                McpDot.Fill = McpStoppedBrush;
                McpAddressText.Text = "";
                McpStateText.Text = "MCP stopped";
                McpAddressPanel.Cursor = null;
                McpAddressPanel.ToolTip = null;
                break;
        }
    }

    // Copying beats retyping a URL by hand, and the address is what every client needs.
    private void McpAddress_Click(object sender, MouseButtonEventArgs e)
    {
        McpServerState? state = _mcp?.State;
        if (state is not { Status: McpServerStatus.Running, Address: { } address }) return;

        try
        {
            Clipboard.SetText(address);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process can hold the clipboard open; nothing to do but skip the feedback.
            return;
        }

        McpAddressText.Text = "copied to clipboard";
        _copiedTimer ??= new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _copiedTimer.Interval = TimeSpan.FromSeconds(1.2);
        _copiedTimer.Tick -= RestoreAddress;
        _copiedTimer.Tick += RestoreAddress;
        // Start() on a timer that is already running does nothing — it would keep the deadline from
        // the previous click, cutting this one's confirmation short. Stopping first rewinds it.
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void RestoreAddress(object? sender, EventArgs e)
    {
        _copiedTimer?.Stop();
        if (_mcp is not null) ShowMcpState(_mcp.State);
    }

    // Drives the three mutually-exclusive status panels (warn / ready / unpack). The status bar is
    // not touched here — it belongs to the MCP server and follows its own events.
    private void RefreshState()
    {
        if (_busy) return;

        string? root = MafiaEnvironment.ResolveGameRoot(PathBox.Text?.Trim());
        bool validPath = root != null && Directory.Exists(root);
        bool unpacked = validPath && File.Exists(Path.Combine(root!, "resources", ".unpacked"));

        BrowseBtn.IsEnabled = true;
        UnpackBtn.IsEnabled = validPath && !unpacked;
        MapEditorBtn.IsEnabled = unpacked;

        // Idle: the live-unpack panel is always hidden here.
        UnpackPanel.Visibility = Visibility.Collapsed;

        if (unpacked)
        {
            // Clean, friendly ready state — no progress bar, no "Resources unpacked:" line.
            WarnPanel.Visibility = Visibility.Collapsed;
            ReadyPanel.Visibility = Visibility.Visible;
            ReadyPathText.Text = Path.Combine(root!, "resources");
        }
        else
        {
            ReadyPanel.Visibility = Visibility.Collapsed;
            WarnPanel.Visibility = Visibility.Visible;
            WarnText.Text = validPath
                ? "The game is not unpacked. Click the unpack button before opening the editors."
                : "Enter a valid Mafia II install path (the pc folder or the game root).";
        }
    }

    // Shows the warn panel with an ad-hoc message (WarnText lives inside WarnPanel now, so error
    // paths can't just toggle its Visibility — they route through here).
    private void ShowWarn(string message)
    {
        ReadyPanel.Visibility = Visibility.Collapsed;
        UnpackPanel.Visibility = Visibility.Collapsed;
        WarnPanel.Visibility = Visibility.Visible;
        WarnText.Text = message;
    }

    private void Path_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshState();

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select the Mafia II 'pc' folder or install root",
        };
        if (!string.IsNullOrWhiteSpace(PathBox.Text) && Directory.Exists(PathBox.Text))
        {
            dlg.InitialDirectory = PathBox.Text;
        }
        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FolderName; // TextChanged → RefreshState
        }
    }

    // Initializes the environment with the chosen path (idempotent) and persists it — the next
    // launch and the headless probes reuse it. Needed before unpack and Map Editor.
    private bool EnsureEnv(out string? error)
    {
        string path = PathBox.Text.Trim();
        if (!MafiaEnvironment.TryInitialize(path, out error)) return false;

        // The environment is initialized once per session: if it is already bound to a DIFFERENT path,
        // the entered path is effectively unused — don't save it, otherwise the next launch
        // and the headless probes would get a path this session never opened.
        string? enteredRoot = MafiaEnvironment.ResolveGameRoot(path);
        if (!string.Equals(enteredRoot, MafiaEnvironment.GameRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        UserSettings settings = UserSettings.Load();
        if (settings.GamePath != path)
        {
            settings.GamePath = path;
            settings.Save();
        }
        return true;
    }

    // Shows the dedicated UnpackPanel (progress + status) for the duration of the run; RefreshState()
    // in finally hides it and flips to the ready banner on success. This is the ONLY place the
    // progress bar is ever shown — the already-unpacked state never displays it.
    private async void Unpack_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureEnv(out string? err))
        {
            ShowWarn("Could not open the game: " + err);
            return;
        }

        _busy = true;
        UnpackBtn.IsEnabled = false;
        BrowseBtn.IsEnabled = false;
        PathBox.IsEnabled = false;

        // Reveal the live-unpack panel exclusively.
        WarnPanel.Visibility = Visibility.Collapsed;
        ReadyPanel.Visibility = Visibility.Collapsed;
        UnpackPanel.Visibility = Visibility.Visible;
        UnpackProgress.Maximum = 1;
        UnpackProgress.Value = 0;
        UnpackStatus.Text = "Preparing…";

        var progress = new Progress<(int done, int total, string name)>(p =>
        {
            UnpackProgress.Maximum = Math.Max(1, p.total);
            UnpackProgress.Value = p.done;
            UnpackStatus.Text = $"{p.done}/{p.total} · {p.name}";
        });

        string? unpackError = null;
        try
        {
            await Task.Run(() => ResourceUnpacker.UnpackAll(progress, CancellationToken.None));
            UnpackStatus.Text = "Unpacking complete.";
        }
        catch (Exception ex)
        {
            unpackError = ex.Message;
        }
        finally
        {
            _busy = false;
            BrowseBtn.IsEnabled = true;
            PathBox.IsEnabled = true;
            RefreshState(); // hides UnpackPanel, shows the ready banner on success

            // RefreshState hides UnpackPanel — show the error on top, otherwise it would vanish instantly.
            if (unpackError != null) ShowWarn("Unpack failed: " + unpackError);
        }
    }

    private void MapEditor_Click(object sender, RoutedEventArgs e)
    {
        // Initialize with the chosen path BEFORE opening the viewport (it calls TryInitialize again
        // and reuses the ready environment).
        if (!EnsureEnv(out string? err))
        {
            ShowWarn("Could not open the game: " + err);
            return;
        }

        var editor = new MainWindow();
        Application.Current.MainWindow = editor;
        editor.Show();
        Close();
    }
}
