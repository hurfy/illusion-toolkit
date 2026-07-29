using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Assets;
using Illusion.Bridge;
using Illusion.Mcp;
using Illusion.Settings;
using Microsoft.Win32;

namespace Illusion.Views;

/// <summary>
/// The application's settings window: the game folder, the keymap, the Blender bridge and the MCP server, in
/// one place. Everything it shows lives in <see cref="UserSettings"/>, and every edit is applied and saved as
/// it is made — there is no draft to reconcile with the live keymap the editor is already reading. Text fields
/// commit when they lose focus (and when the window closes), so a half-typed path is never persisted.
/// <para>
/// The keymap list is built in code rather than templated: the rows come from <see cref="HotkeyCatalog"/>, and
/// a table that grows by one entry there should not also need a XAML edit.
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly Brush OkBrush = Frozen("#60C060");
    private static readonly Brush WarnBrush = Frozen("#D9903A");
    private static readonly Brush ConflictBrush = Frozen("#E8A33D");
    private static readonly Brush DimBrush = Frozen("#80FFFFFF");

    /// <summary>What a modifier-only action (the camera speed keys) can be put on.</summary>
    private static readonly ModifierChoice[] ModifierChoices =
    {
        new("None", ModifierKeys.None),
        new("Shift", ModifierKeys.Shift),
        new("Ctrl", ModifierKeys.Control),
        new("Alt", ModifierKeys.Alt),
    };

    private readonly List<KeymapGroup> _groups = new();
    private readonly Dictionary<HotkeyId, KeymapRow> _rows = new();
    private string? _autoDetectedBlender;
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();
        WindowFit.ToWorkArea(this);

        // The locator walks the .blend association, Program Files, Steam and PATH — cheap, but not something
        // to redo on every keystroke, so the answer is taken once and reused by the hint.
        _autoDetectedBlender = BlenderLocator.Locate(null);

        BuildKeymap();
        LoadValues();

        // Paths and the port commit on focus loss rather than per keystroke: persisting "C:\Prog" on the way
        // to "C:\Program Files" would hand the probes a path that never existed.
        GamePathBox.LostFocus += (_, _) => CommitGamePath();
        BlenderPathBox.LostFocus += (_, _) => CommitBlenderPath();
        McpPortBox.LostFocus += (_, _) => CommitMcpPort();
        GamePathBox.KeyDown += CommitOnEnter;
        BlenderPathBox.KeyDown += CommitOnEnter;
        McpPortBox.KeyDown += CommitOnEnter;

        // The map is not only edited here (Restore defaults goes through it, and so will anything else that
        // ever rebinds a key), and it outlives this window — follow it, and come back off on the way out.
        HotkeyMap.Current.Changed += RefreshKeymap;
        Closed += (_, _) => HotkeyMap.Current.Changed -= RefreshKeymap;
    }

    /// <summary>Opens the window on a named section — the launcher sends first-time users straight at the
    /// game folder, which is the only thing that blocks them.</summary>
    public void SelectSection(SettingsSection section) => Sections.SelectedIndex = (int)section;

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    // A close (or Alt+F4) with focus still in a path box must not lose what was typed there.
    protected override void OnClosing(CancelEventArgs e)
    {
        CommitGamePath();
        CommitBlenderPath();
        CommitMcpPort();
        base.OnClosing(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitGamePath();
        CommitBlenderPath();
        CommitMcpPort();
        e.Handled = true;
    }

    private void LoadValues()
    {
        _loading = true;
        UserSettings settings = UserSettings.Current;
        GamePathBox.Text = settings.GamePath ?? "";
        BuildNoticeCheck.IsChecked = !settings.SuppressBuildNotice;
        BlenderPathBox.Text = settings.BlenderPath ?? "";
        AutoPushCheck.IsChecked = settings.BridgeAutoPush;
        McpPortBox.Text = settings.McpPort.ToString(CultureInfo.InvariantCulture);
        _loading = false;

        RefreshGamePathStatus();
        RefreshBlenderStatus();
        RefreshMcpStatus();
        RefreshKeymap();
    }

    // ── General ──

    private void GamePath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) RefreshGamePathStatus();
    }

    private void CommitGamePath()
    {
        string path = GamePathBox.Text.Trim();
        if (UserSettings.Current.GamePath == path) return;
        UserSettings.Update(s => s.GamePath = path);
    }

    private void RefreshGamePathStatus()
    {
        string path = GamePathBox.Text.Trim();
        if (path.Length == 0)
        {
            Say(GamePathStatus, "Not set — the editors stay closed until it is.", WarnBrush);
            return;
        }

        string? root = MafiaEnvironment.ResolveGameRoot(path);
        if (root == null || !Directory.Exists(root))
        {
            Say(GamePathStatus, "No Mafia II install there.", WarnBrush);
            return;
        }

        string resources = Path.Combine(root, "resources");
        if (File.Exists(Path.Combine(resources, ".unpacked")))
        {
            Say(GamePathStatus, "Ready — resources unpacked in " + resources, OkBrush);
        }
        else
        {
            Say(GamePathStatus, "Found, but not unpacked yet — unpack it from the launcher.", WarnBrush);
        }
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the Mafia II 'pc' folder or install root" };
        string current = GamePathBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) != true) return;
        GamePathBox.Text = dlg.FolderName;
        CommitGamePath();
    }

    private void BuildNotice_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool show = BuildNoticeCheck.IsChecked == true;
        UserSettings.Update(s => s.SuppressBuildNotice = !show);
    }

    // ── Blender bridge ──

    private void BlenderPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) RefreshBlenderStatus();
    }

    private void CommitBlenderPath()
    {
        string path = BlenderPathBox.Text.Trim();
        string? value = path.Length == 0 ? null : path;
        if (UserSettings.Current.BlenderPath == value) return;
        UserSettings.Update(s => s.BlenderPath = value);

        // An explicit path that resolves is worth confirming with the real locator, not the cheap existence
        // check the typing hint uses.
        _autoDetectedBlender = BlenderLocator.Locate(null);
        RefreshBlenderStatus();
    }

    private void RefreshBlenderStatus()
    {
        string path = BlenderPathBox.Text.Trim();
        if (path.Length == 0)
        {
            if (_autoDetectedBlender is { } found) Say(BlenderPathStatus, "Found by itself: " + found, OkBrush);
            else Say(BlenderPathStatus, "No Blender found on this machine — the bridge will not open.", WarnBrush);
            return;
        }

        bool exists = File.Exists(path) || Directory.Exists(path);
        Say(BlenderPathStatus,
            exists ? "Using this instead of searching." : "Nothing at that path.",
            exists ? OkBrush : WarnBrush);
    }

    private void BrowseBlender_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select blender.exe",
            Filter = "Blender|blender.exe|Programs|*.exe|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        BlenderPathBox.Text = dlg.FileName;
        CommitBlenderPath();
    }

    private void AutoPush_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = AutoPushCheck.IsChecked == true;
        UserSettings.Update(s => s.BridgeAutoPush = on);
    }

    // ── MCP server ──

    private void McpPort_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) RefreshMcpStatus();
    }

    private void CommitMcpPort()
    {
        if (!TryReadPort(out int port)) return;   // a typo is left in the box, not written out
        if (UserSettings.Current.McpPort == port) return;
        UserSettings.Update(s => s.McpPort = port);
    }

    private bool TryReadPort(out int port) =>
        int.TryParse(McpPortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
        && port is > 0 and <= 65535;

    private void RefreshMcpStatus()
    {
        if (TryReadPort(out int port))
        {
            Say(McpPortStatus,
                port == McpHostOptions.DefaultPort
                    ? "The default."
                    : "The server binds at startup, so this is taken up the next time the toolkit runs.",
                DimBrush);
        }
        else
        {
            Say(McpPortStatus, "Has to be a number from 1 to 65535.", WarnBrush);
        }

        McpServerState? state = App.McpServer?.State;
        McpLiveState.Text = state == null
            ? "not started in this session"
            : state.Status switch
            {
                McpServerStatus.Running => "running · " + state.Address,
                McpServerStatus.Starting => "starting…",
                McpServerStatus.Failed => "failed · " + state.Error,
                _ => "stopped",
            };
    }

    private static void Say(TextBlock target, string text, Brush brush)
    {
        target.Text = text;
        target.Foreground = brush;
    }

    // ── Keymap ──

    private void BuildKeymap()
    {
        var caption = (Style)FindResource("Caption");
        var hint = (Style)FindResource("Hint");
        var iconButton = (Style)FindResource("RowIconButton");

        foreach (string group in HotkeyCatalog.Groups)
        {
            var header = new TextBlock
            {
                Text = group.ToUpperInvariant(),
                Style = caption,
                Margin = new Thickness(1, 16, 0, 7),
            };
            KeymapList.Children.Add(header);

            var rows = new List<KeymapRow>();
            foreach (HotkeyAction action in HotkeyCatalog.Actions)
            {
                if (action.Group != group) continue;
                KeymapRow row = BuildRow(action, hint, iconButton);
                KeymapList.Children.Add(row.Container);
                rows.Add(row);
                _rows[action.Id] = row;
            }
            _groups.Add(new KeymapGroup(header, rows));
        }
    }

    private KeymapRow BuildRow(HotkeyAction action, Style hint, Style iconButton)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = action.Label,
            Foreground = Brushes.White,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(1, 0, 12, 0),
        };
        grid.Children.Add(label);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(controls, 1);
        grid.Children.Add(controls);

        HotkeyBox? box = null;
        DropDownField? modifier = null;
        if (action.ModifierOnly)
        {
            // A held modifier is not something to record by pressing it — there is no keypress to catch, and
            // the whole choice is four values wide.
            modifier = new DropDownField
            {
                Width = 148,
                ItemsSource = ModifierChoices,
                VerticalAlignment = VerticalAlignment.Center,
            };
            modifier.SelectionChanged += (_, _) => OnModifierPicked(action.Id, modifier);
            controls.Children.Add(modifier);
        }
        else
        {
            box = new HotkeyBox { Width = 148, VerticalAlignment = VerticalAlignment.Center };
            box.Committed += hotkey => Rebind(action.Id, hotkey);
            controls.Children.Add(box);

            var clear = new Button
            {
                Content = "✕",
                Style = iconButton,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Unbind — the action keeps working from the menu, just not from a key",
            };
            clear.Click += (_, _) => Rebind(action.Id, Hotkey.None);
            controls.Children.Add(clear);
        }

        var reset = new Button
        {
            Content = "↺",
            Style = iconButton,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Back to the key this action shipped with",
        };
        reset.Click += (_, _) =>
        {
            HotkeyMap.Current.Reset(action.Id);
            RefreshKeymap();
        };
        controls.Children.Add(reset);

        var description = new TextBlock
        {
            Text = action.Description,
            Style = hint,
            Margin = new Thickness(1, 2, 12, 0),
        };
        Grid.SetRow(description, 1);
        grid.Children.Add(description);

        var conflict = new TextBlock
        {
            Foreground = ConflictBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(1, 4, 12, 0),
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(conflict, 2);
        Grid.SetColumnSpan(conflict, 2);
        grid.Children.Add(conflict);

        return new KeymapRow(action, grid, box, modifier, conflict, reset);
    }

    private void OnModifierPicked(HotkeyId id, DropDownField field)
    {
        if (_loading || field.SelectedItem is not ModifierChoice choice) return;
        Rebind(id, new Hotkey(Key.None, choice.Value));
    }

    private void Rebind(HotkeyId id, Hotkey hotkey)
    {
        HotkeyMap.Current.Set(id, hotkey);
        RefreshKeymap();
    }

    /// <summary>Re-reads the whole list from the map. One rebinding can change another row (it may have just
    /// gained or lost a conflict), so nothing is refreshed row-locally.</summary>
    private void RefreshKeymap()
    {
        HotkeyMap map = HotkeyMap.Current;
        bool wasLoading = _loading;
        _loading = true;   // moving a dropdown to its value must not read back as a user pick

        foreach (KeymapRow row in _rows.Values)
        {
            Hotkey hotkey = map[row.Action.Id];
            if (row.Box != null) row.Box.Value = hotkey;
            if (row.Modifier != null)
            {
                row.Modifier.SelectedItem =
                    Array.Find(ModifierChoices, c => c.Value == hotkey.Modifiers) ?? ModifierChoices[0];
            }
            row.Reset.IsEnabled = !map.IsDefault(row.Action.Id);

            IReadOnlyList<HotkeyAction> clashes = map.ConflictsWith(row.Action.Id);
            row.Conflict.Visibility = clashes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (clashes.Count > 0)
            {
                // Deliberately does not claim which one wins: the order keys are offered around in the editor
                // is an implementation detail, and a conflict is something to fix rather than to rely on.
                row.Conflict.Text = "Shares this key with " + string.Join(", ", clashes.Select(c => c.Label))
                    + " — both are listening at the same time, so only one of them will fire.";
            }
        }

        ResetAllKeysBtn.IsEnabled = !map.IsPristine;
        _loading = wasLoading;
    }

    private void ResetAllKeys_Click(object sender, RoutedEventArgs e)
    {
        HotkeyMap.Current.ResetAll();
        RefreshKeymap();
    }

    private void KeymapSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        string needle = KeymapSearch.Text.Trim();
        HotkeyMap map = HotkeyMap.Current;

        foreach (KeymapGroup group in _groups)
        {
            int shown = 0;
            foreach (KeymapRow row in group.Rows)
            {
                bool hit = needle.Length == 0
                    || row.Action.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || row.Action.Group.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || map[row.Action.Id].ToString().Contains(needle, StringComparison.OrdinalIgnoreCase);
                row.Container.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                if (hit) shown++;
            }
            // A heading with nothing under it reads as an empty section rather than as a filtered-out one.
            group.Header.Visibility = shown == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ── What the probe looks at ──

    internal HotkeyBox? BoxFor(HotkeyId id) => _rows.TryGetValue(id, out KeymapRow? row) ? row.Box : null;

    internal bool IsRowVisible(HotkeyId id) =>
        _rows.TryGetValue(id, out KeymapRow? row) && row.Container.Visibility == Visibility.Visible;

    internal string ConflictTextFor(HotkeyId id) =>
        _rows.TryGetValue(id, out KeymapRow? row) && row.Conflict.Visibility == Visibility.Visible
            ? row.Conflict.Text
            : "";

    internal bool CanResetRow(HotkeyId id) => _rows.TryGetValue(id, out KeymapRow? row) && row.Reset.IsEnabled;

    internal int KeymapRowCount => _rows.Count;

    private sealed record ModifierChoice(string Label, ModifierKeys Value)
    {
        public override string ToString() => Label;
    }

    private sealed record KeymapRow(
        HotkeyAction Action,
        FrameworkElement Container,
        HotkeyBox? Box,
        DropDownField? Modifier,
        TextBlock Conflict,
        Button Reset);

    private sealed record KeymapGroup(TextBlock Header, List<KeymapRow> Rows);
}

/// <summary>The settings window's sections, in the order the rail lists them.</summary>
public enum SettingsSection
{
    General,
    Keymap,
    BlenderBridge,
    McpServer,
}
