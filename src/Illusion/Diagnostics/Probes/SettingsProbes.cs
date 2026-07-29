using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Rendering.Controls;
using Illusion.Settings;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The settings surface: the hotkey text format, the keymap's default/override rules and conflict scopes, the
/// settings window built headless, and the live re-apply into a real <see cref="MainWindow"/> (menus, camera,
/// gizmo). Needs no game data and no GPU — no window is ever shown.
/// <para>
/// It does write to the real settings.json, because that is where <see cref="HotkeyMap.Current"/> persists.
/// The user's rebindings are read first and put back in a finally, so a crashed run is the only way to lose
/// them — and everything the probe touches is a key, not data.
/// </para>
/// Output: %TEMP%\illusion_settings.txt
/// </summary>
internal static class SettingsProbes
{
    // A delegate rather than Action<...> so the detail argument can keep its default and the checks that have
    // nothing to add stay two arguments wide.
    private delegate void Assert(string name, bool ok, string detail = "");

    // Hand-written forms, the way a user editing settings.json would type them. Round-tripping the catalog's
    // own defaults would not catch a formatter and a parser that are wrong in the same direction.
    private static readonly (string Text, Key Key, ModifierKeys Modifiers)[] Forms =
    {
        ("Ctrl+Shift+Z", Key.Z, ModifierKeys.Control | ModifierKeys.Shift),
        ("Ctrl+S", Key.S, ModifierKeys.Control),
        ("Alt+F4", Key.F4, ModifierKeys.Alt),
        ("G", Key.G, ModifierKeys.None),
        ("Del", Key.Delete, ModifierKeys.None),
        ("Esc", Key.Escape, ModifierKeys.None),
        ("Enter", Key.Return, ModifierKeys.None),
        ("Space", Key.Space, ModifierKeys.None),
        ("Tab", Key.Tab, ModifierKeys.None),
        ("/", Key.OemQuestion, ModifierKeys.None),
        (",", Key.OemComma, ModifierKeys.None),
        ("Ctrl+=", Key.OemPlus, ModifierKeys.Control),
        ("Num /", Key.Divide, ModifierKeys.None),
        ("Num 5", Key.NumPad5, ModifierKeys.None),
        ("5", Key.D5, ModifierKeys.None),
        ("Shift", Key.None, ModifierKeys.Shift),
        ("", Key.None, ModifierKeys.None),
    };

    // Text that is not a combination at all. Each has to be refused rather than silently read as something
    // else — a mangled entry must leave the action on its default, not move it somewhere surprising.
    private static readonly string[] Garbage = { "Zzz", "42", "Ctrl+Z+X", "Num", "Ctrl+Nope", "Num 12" };

    internal static void RunSettingsProbe()
    {
        string outTxt = Path.Combine(Path.GetTempPath(), "illusion_settings.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        var saved = new Dictionary<string, string>(UserSettings.Current.Hotkeys, StringComparer.Ordinal);
        try
        {
            HotkeyMap map = HotkeyMap.Current;
            map.ResetAll();

            CheckCatalog(Check);
            CheckTextFormat(Check);
            CheckMap(Check, map);
            CheckConflicts(Check, map);
            CheckDetachedLoad(Check);
            CheckWindow(Check, map);
            CheckLiveApply(Check, map);
            sb.AppendLine(RenderSections());

            sb.Insert(0, $"SETTINGS PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            RestoreUserKeymap(saved);
            File.WriteAllText(outTxt, sb.ToString());
        }
    }

    // ── The shipped table ──

    private static void CheckCatalog(Assert check)
    {
        HotkeyId[] ids = Enum.GetValues<HotkeyId>();
        check("every action id has exactly one catalog entry",
            HotkeyCatalog.Actions.Count == ids.Length
            && ids.All(id => HotkeyCatalog.Actions.Count(a => a.Id == id) == 1),
            $"{HotkeyCatalog.Actions.Count} entries for {ids.Length} ids");

        check("the shipped keymap has no conflicts of its own", !HotkeyMap.Current.HasConflicts());

        check("modifier-only actions ship a modifier and no key",
            HotkeyCatalog.Actions.Where(a => a.ModifierOnly)
                .All(a => a.Default.Key == Key.None && a.Default.Modifiers != ModifierKeys.None));
        check("every other action ships a real key",
            HotkeyCatalog.Actions.Where(a => !a.ModifierOnly).All(a => a.Default.Key != Key.None));
        check("every action is labelled and explained",
            HotkeyCatalog.Actions.All(a => a.Label.Length > 0 && a.Description.Length > 0));

        // The settings window lists one heading per group and the rows under it in catalog order; a group
        // split in two would silently lose the second half under a heading that is already closed.
        var order = HotkeyCatalog.Actions.Select(a => a.Group).ToList();
        check("each group is one contiguous run",
            order.Distinct().Count() == order.Where((g, i) => i == 0 || order[i - 1] != g).Count(),
            string.Join(" · ", order.Distinct()));

        // Two independent tables name the same defaults: the catalog (what the settings window shows) and the
        // rendering layer's own fallbacks (what an unconfigured viewport uses). They must agree.
        CameraKeyMap camera = CameraKeyMap.Default;
        check("the camera's built-in defaults match the catalog",
            camera.Forward == HotkeyCatalog.Default(HotkeyId.CameraForward).Key
            && camera.Back == HotkeyCatalog.Default(HotkeyId.CameraBack).Key
            && camera.Left == HotkeyCatalog.Default(HotkeyId.CameraLeft).Key
            && camera.Right == HotkeyCatalog.Default(HotkeyId.CameraRight).Key
            && camera.Fast == HotkeyCatalog.Default(HotkeyId.CameraFast).Modifiers
            && camera.Slow == HotkeyCatalog.Default(HotkeyId.CameraSlow).Modifiers,
            camera.ToString());

        GizmoKeyMap gizmo = GizmoKeyMap.Default;
        check("the gizmo's built-in defaults match the catalog",
            gizmo.Move == HotkeyCatalog.Default(HotkeyId.GizmoMove).Key
            && gizmo.Rotate == HotkeyCatalog.Default(HotkeyId.GizmoRotate).Key
            && gizmo.Scale == HotkeyCatalog.Default(HotkeyId.GizmoScale).Key
            && gizmo.AxisX == HotkeyCatalog.Default(HotkeyId.AxisX).Key
            && gizmo.AxisY == HotkeyCatalog.Default(HotkeyId.AxisY).Key
            && gizmo.AxisZ == HotkeyCatalog.Default(HotkeyId.AxisZ).Key
            && gizmo.Commit == HotkeyCatalog.Default(HotkeyId.ModalCommit).Key
            && gizmo.CommitAlt == HotkeyCatalog.Default(HotkeyId.ModalCommitAlt).Key
            && gizmo.Cancel == HotkeyCatalog.Default(HotkeyId.ModalCancel).Key,
            gizmo.ToString());

        check("the gizmo maps its axis keys to X=0, Y=1, Z=2",
            gizmo.AxisOf(gizmo.AxisX) == 0 && gizmo.AxisOf(gizmo.AxisY) == 1 && gizmo.AxisOf(gizmo.AxisZ) == 2
            && gizmo.AxisOf(Key.Q) < 0 && gizmo.AxisOf(Key.None) < 0);
    }

    // ── The text form that reaches settings.json ──

    private static void CheckTextFormat(Assert check)
    {
        foreach (HotkeyAction action in HotkeyCatalog.Actions)
        {
            string text = action.Default.ToString();
            bool ok = Hotkey.TryParse(text, out Hotkey parsed) && parsed == action.Default;
            check($"default for {action.Id} survives the text form", ok, $"\"{text}\" → {parsed}");
        }

        foreach ((string text, Key key, ModifierKeys modifiers) in Forms)
        {
            var expected = new Hotkey(key, modifiers);
            bool parsed = Hotkey.TryParse(text, out Hotkey actual);
            check($"\"{text}\" reads as {expected}", parsed && actual == expected, actual.ToString());
            check($"\"{text}\" is also what {expected} writes", expected.ToString() == text, expected.ToString());
        }

        foreach (string text in Garbage)
        {
            check($"\"{text}\" is refused", !Hotkey.TryParse(text, out _));
        }

        check("an unbound combination reports itself as unbound",
            !Hotkey.None.IsBound && new Hotkey(Key.None, ModifierKeys.Shift).IsBound);

        // The modifiers must match exactly, or a binding on a bare key would fire for every combination
        // built on it and steal Ctrl+S from Save.
        var bare = new Hotkey(Key.S, ModifierKeys.None);
        check("a bare key does not match the same key with a modifier",
            bare.Matches(Key.S, ModifierKeys.None)
            && !bare.Matches(Key.S, ModifierKeys.Control)
            && !bare.Matches(Key.D, ModifierKeys.None));
        check("an unbound action matches nothing",
            !Hotkey.None.Matches(Key.None, ModifierKeys.None) && !Hotkey.None.Matches(Key.S, ModifierKeys.None));
    }

    // ── Defaults, overrides, persistence ──

    private static void CheckMap(Assert check, HotkeyMap map)
    {
        map.ResetAll();
        check("a pristine map reports itself pristine",
            map.IsPristine && HotkeyCatalog.Actions.All(a => map.IsDefault(a.Id)));

        int changes = 0;
        void Count() => changes++;
        map.Changed += Count;

        var rebound = new Hotkey(Key.K, ModifierKeys.Control);
        map.Set(HotkeyId.Save, rebound);
        check("a rebinding takes", map[HotkeyId.Save] == rebound && !map.IsDefault(HotkeyId.Save));
        check("a rebinding announces itself", changes == 1, changes.ToString());
        check("only the rebinding is stored",
            map.Overrides.Count == 1
            && UserSettings.Current.Hotkeys.Count == 1
            && UserSettings.Current.Hotkeys.TryGetValue("Save", out string? text) && text == "Ctrl+K",
            string.Join(", ", UserSettings.Current.Hotkeys.Select(p => $"{p.Key}={p.Value}")));

        map.Set(HotkeyId.Save, rebound);
        check("re-setting the same combination changes nothing", changes == 1, changes.ToString());

        // Putting the shipped key back by hand has to drop the override, not pin today's default forever:
        // that is what lets a changed default reach everyone who never rebound the action.
        map.Set(HotkeyId.Save, HotkeyCatalog.Default(HotkeyId.Save));
        check("setting the shipped key back drops the override",
            map.IsPristine && UserSettings.Current.Hotkeys.Count == 0);

        map.Set(HotkeyId.Duplicate, Hotkey.None);
        check("an action can be unbound",
            !map[HotkeyId.Duplicate].IsBound
            && !map.Matches(HotkeyId.Duplicate, Key.D, ModifierKeys.Control)
            && UserSettings.Current.Hotkeys["Duplicate"] == "");
        map.Reset(HotkeyId.Duplicate);
        check("reset puts one action back",
            map[HotkeyId.Duplicate] == HotkeyCatalog.Default(HotkeyId.Duplicate) && map.IsPristine);

        map.Set(HotkeyId.Undo, new Hotkey(Key.F1, ModifierKeys.None));
        map.Set(HotkeyId.Redo, new Hotkey(Key.F2, ModifierKeys.None));
        map.ResetAll();
        check("restore-defaults puts everything back", map.IsPristine && map[HotkeyId.Undo].Key == Key.Z);

        int before = changes;
        map.ResetAll();
        check("restoring an already-pristine map is silent", changes == before, changes.ToString());

        map.Changed -= Count;
    }

    // ── Conflicts, and the scopes that make them not conflicts ──

    private static void CheckConflicts(Assert check, HotkeyMap map)
    {
        map.ResetAll();

        // The one that would look like a bug and is not: three actions ship on S.
        check("Scale and the camera's Back share S without conflicting",
            map[HotkeyId.GizmoScale].Key == Key.S && map[HotkeyId.CameraBack].Key == Key.S
            && map.ConflictsWith(HotkeyId.GizmoScale).Count == 0
            && map.ConflictsWith(HotkeyId.CameraBack).Count == 0);
        check("Save's Ctrl+S does not conflict with the camera's Back either",
            map.ConflictsWith(HotkeyId.Save).Count == 0);
        check("Esc leaves Edit Mode and cancels a transform without conflicting",
            map[HotkeyId.BridgeLeave] == map[HotkeyId.ModalCancel]
            && map.ConflictsWith(HotkeyId.BridgeLeave).Count == 0);

        map.Set(HotkeyId.Import, map[HotkeyId.Save]);
        check("two editor actions on one combination conflict, both ways",
            map.ConflictsWith(HotkeyId.Save).Any(a => a.Id == HotkeyId.Import)
            && map.ConflictsWith(HotkeyId.Import).Any(a => a.Id == HotkeyId.Save)
            && map.HasConflicts());
        map.Reset(HotkeyId.Import);
        check("resolving it clears the report", !map.HasConflicts());

        map.Set(HotkeyId.Save, Hotkey.None);
        map.Set(HotkeyId.Import, Hotkey.None);
        check("two unbound actions are not a conflict", !map.HasConflicts());
        map.ResetAll();
    }

    // ── Reading a settings file that was not written by this build ──

    private static void CheckDetachedLoad(Assert check)
    {
        var file = new UserSettings();
        file.Hotkeys["Save"] = "Ctrl+K";
        file.Hotkeys["NotAnAction"] = "Ctrl+Q";     // written by a newer build
        file.Hotkeys["Undo"] = "!!!";               // mangled by a hand edit
        file.Hotkeys["Redo"] = "Ctrl+Shift+Z";      // spelled out, but equal to the default

        int settingsBefore = UserSettings.Current.Hotkeys.Count;
        HotkeyMap detached = HotkeyMap.Detached(file);

        check("a stored rebinding loads", detached[HotkeyId.Save] == new Hotkey(Key.K, ModifierKeys.Control));
        check("an unknown action name is ignored", detached.Overrides.Count == 1,
            string.Join(", ", detached.Overrides.Keys));
        check("a mangled gesture leaves the action on its default",
            detached[HotkeyId.Undo] == HotkeyCatalog.Default(HotkeyId.Undo) && detached.IsDefault(HotkeyId.Undo));
        check("an entry equal to the default is not kept as an override", detached.IsDefault(HotkeyId.Redo));

        detached.Set(HotkeyId.Delete, new Hotkey(Key.F9, ModifierKeys.None));
        check("a detached map never writes to the user's settings",
            UserSettings.Current.Hotkeys.Count == settingsBefore
            && HotkeyMap.Current.IsDefault(HotkeyId.Delete));
    }

    // ── The window ──

    private static void CheckWindow(Assert check, HotkeyMap map)
    {
        map.ResetAll();
        var window = new SettingsWindow();

        check("the rail has a section per enum value",
            window.Sections.Items.Count == Enum.GetValues<SettingsSection>().Length,
            $"{window.Sections.Items.Count} tabs");
        window.SelectSection(SettingsSection.McpServer);
        check("a named section can be selected",
            window.Sections.SelectedIndex == (int)SettingsSection.McpServer);

        check("the keymap lists every catalog action",
            window.KeymapRowCount == HotkeyCatalog.Actions.Count,
            $"{window.KeymapRowCount} rows");
        check("a modifier-only action gets no key field, the rest do",
            window.BoxFor(HotkeyId.CameraFast) == null && window.BoxFor(HotkeyId.Save) != null);
        check("a row shows the key the action is on",
            window.BoxFor(HotkeyId.Save)!.Value == map[HotkeyId.Save]);
        check("reset is offered only for a rebound action", !window.CanResetRow(HotkeyId.Save));

        // What a user does: click the field, press the key.
        var rebound = new Hotkey(Key.K, ModifierKeys.Control);
        window.BoxFor(HotkeyId.Save)!.Commit(rebound);
        check("recording a key rebinds the action",
            map[HotkeyId.Save] == rebound && window.BoxFor(HotkeyId.Save)!.Value == rebound);
        check("reset is offered once the action is rebound", window.CanResetRow(HotkeyId.Save));

        window.BoxFor(HotkeyId.Import)!.Commit(rebound);
        check("a clash is reported on both rows",
            window.ConflictTextFor(HotkeyId.Save).Contains("Import", StringComparison.Ordinal)
            && window.ConflictTextFor(HotkeyId.Import).Contains("Save", StringComparison.Ordinal),
            window.ConflictTextFor(HotkeyId.Save));
        check("keys shared across scopes are not reported",
            window.ConflictTextFor(HotkeyId.GizmoScale) == ""
            && window.ConflictTextFor(HotkeyId.CameraBack) == "");

        window.KeymapSearch.Text = "undo";
        check("the filter matches on the action name",
            window.IsRowVisible(HotkeyId.Undo) && !window.IsRowVisible(HotkeyId.Save));
        window.KeymapSearch.Text = "Ctrl+K";
        check("the filter matches on the key too",
            window.IsRowVisible(HotkeyId.Save) && !window.IsRowVisible(HotkeyId.Delete));
        window.KeymapSearch.Text = "";
        check("clearing the filter brings every row back",
            HotkeyCatalog.Actions.All(a => window.IsRowVisible(a.Id)));

        map.ResetAll();
        check("restore-defaults reaches the open window",
            window.BoxFor(HotkeyId.Save)!.Value == HotkeyCatalog.Default(HotkeyId.Save)
            && !window.CanResetRow(HotkeyId.Save));

    }

    // ── The editor window follows a rebinding while it is open ──

    private static void CheckLiveApply(Assert check, HotkeyMap map)
    {
        map.ResetAll();
        var main = new MainWindow();

        check("the editor starts on the shipped keys",
            main.Viewport.CameraKeys.Forward == Key.W
            && main.SaveMenuItem.InputGestureText == "Ctrl+S"
            && main.TreeDeleteItem.InputGestureText == "Del",
            $"save=\"{main.SaveMenuItem.InputGestureText}\", del=\"{main.TreeDeleteItem.InputGestureText}\"");
        check("the settings entry advertises its own key",
            main.SettingsMenuItem.InputGestureText == "Ctrl+,",
            main.SettingsMenuItem.InputGestureText);

        map.Set(HotkeyId.Save, new Hotkey(Key.K, ModifierKeys.Control));
        check("the menu follows a rebinding with no restart",
            main.SaveMenuItem.InputGestureText == "Ctrl+K", main.SaveMenuItem.InputGestureText);

        map.Set(HotkeyId.CameraForward, new Hotkey(Key.Up, ModifierKeys.None));
        map.Set(HotkeyId.CameraSlow, new Hotkey(Key.None, ModifierKeys.Alt));
        check("the camera follows a rebinding",
            main.Viewport.CameraKeys.Forward == Key.Up
            && main.Viewport.CameraKeys.Slow == ModifierKeys.Alt
            && main.Viewport.CameraKeys.IsMoveKey(Key.Up)
            && !main.Viewport.CameraKeys.IsMoveKey(Key.W),
            main.Viewport.CameraKeys.ToString());

        TransformGizmo? gizmo = main.TransformGizmoOverlay;
        check("the editor has its gizmo overlay", gizmo != null);
        if (gizmo != null)
        {
            map.Set(HotkeyId.AxisY, new Hotkey(Key.N, ModifierKeys.None));
            map.Set(HotkeyId.GizmoRotate, new Hotkey(Key.T, ModifierKeys.None));
            check("the gizmo follows a rebinding, slot for slot",
                gizmo.Keys.AxisY == Key.N && gizmo.Keys.AxisOf(Key.N) == 1
                && gizmo.Keys.Rotate == Key.T
                && gizmo.Keys.Move == Key.G && gizmo.Keys.Cancel == Key.Escape,
                gizmo.Keys.ToString());
        }

        map.ResetAll();
        check("the editor comes back to the shipped keys",
            main.Viewport.CameraKeys.Forward == Key.W && main.SaveMenuItem.InputGestureText == "Ctrl+S");

    }

    /// <summary>
    /// Draws each section to a PNG so the layout can be looked at instead of only asserted — a row whose key
    /// field has slid off the edge passes every check above.
    /// <para>
    /// Read it as a wireframe, not as a screenshot: the chrome comes out in Fluent's default (light) colours,
    /// so this window's own white-on-dark text is invisible in it. A window that is never shown never gets an
    /// HWND, and that is when the theme dictionaries attach — setting ThemeMode here changes nothing (tried).
    /// Light and dark share their metrics, so the geometry is the shipped one. Best-effort — a failed render
    /// leaves the asserts alone.
    /// </para>
    /// </summary>
    private static string RenderSections()
    {
        var written = new List<string>();
        try
        {
            var window = new SettingsWindow();
            var content = (FrameworkElement)window.Content;
            const double width = 840, height = 620;

            foreach (SettingsSection section in Enum.GetValues<SettingsSection>())
            {
                window.SelectSection(section);
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();

                var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                string path = Path.Combine(Path.GetTempPath(),
                    $"illusion_settings_{section.ToString().ToLowerInvariant()}.png");
                using (FileStream file = File.Create(path)) encoder.Save(file);
                written.Add(path);
            }
        }
        catch (Exception ex)
        {
            return "\nrender skipped — " + ex.Message;
        }
        return "\nrendered " + string.Join("\n         ", written);
    }

    private static void RestoreUserKeymap(Dictionary<string, string> saved)
    {
        HotkeyMap.Current.ResetAll();
        foreach ((string name, string gesture) in saved)
        {
            if (Enum.TryParse(name, out HotkeyId id) && Enum.IsDefined(id)
                && Hotkey.TryParse(gesture, out Hotkey hotkey))
            {
                HotkeyMap.Current.Set(id, hotkey);
            }
        }
    }
}
