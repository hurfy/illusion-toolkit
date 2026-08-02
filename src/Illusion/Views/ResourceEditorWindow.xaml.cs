using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Illusion.Assets;
using Illusion.Assets.Library;
using Illusion.Rendering.Passes;
using Illusion.Settings;
using Illusion.ViewModels;

namespace Illusion.Views;

/// <summary>
/// The resource editor: the game's archives on the left as a browsable library, one of them at a time on the
/// stage in the middle, and the ordinary scene panel on the right. It is a window of its own rather than a
/// mode of the map editor, so working on a car never disturbs the district you had loaded — the two keep
/// separate scenes, cameras, selections and undo stacks, and the map editor's own edits survive untouched.
/// <para>
/// Everything it edits with is the same machinery the map editor uses: the same viewport control, the same
/// <see cref="ScenePanel"/>, the same Save/Build path through the extracted folder and a versioned backup.
/// What it does NOT have is the city — no district selector, no seasons, no streaming — because a resource is
/// not placed anywhere.
/// </para>
/// </summary>
public partial class ResourceEditorWindow : Window
{
    private LibraryCatalog? _catalog;
    private LibraryEntry? _staged;
    private MaterialEditorWindow? _materialEditor;
    private double _browserHeight = 300;   // two rows of tiles — see the BrowserRow definition

    public ResourceEditorWindow()
    {
        InitializeComponent();
        WindowFit.ToWorkArea(this);

        Scene.Attach(Stage);
        Scene.HideCityFilters();          // no proxy districts and no winter twin off the map
        Scene.MaterialEditorRequested += OpenMaterialEditor;
        Scene.RestoreBackupRequested += ShowRestoreDialog;

        Stage.CameraMoved += UpdateCameraReadout;
        Stage.SceneChanged += () => Dispatcher.Invoke(UpdateStageChrome);
        Stage.DirtyChanged += () => Dispatcher.Invoke(() =>
        {
            UpdateTitle();
            CommandManager.InvalidateRequerySuggested();
        });
        Stage.TransientNotice += (message, isError) => Notices.Post(message, isError);
        Stage.BridgeNotice += (message, isError) => Notices.Post(message, isError);

        // The same tools the map editor has, over the same kind of viewport: select / move / rotate / scale,
        // walk mode, and the Blender bridge. The bridge refuses skinned geometry (a car body is exactly that
        // and says so), but the rest of the library — props, city objects — it takes.
        ToolShelf.Attach(Stage);
        ToolShelf.BlenderRequested += ToggleBridgeSession;

        // Names the helper glyph under the cursor — the glyphs themselves carry no text.
        GlyphLabel.Attach(Stage);

        // The layers list is a look, not a decision: hovering the button is enough to open it — same as the
        // map editor, because where you switch what the viewport draws must not depend on the window.
        HoverPopup.Attach(LayersBtn, LayersPopup);
        Stage.BridgeStateChanged += () => Dispatcher.BeginInvoke(UpdateBridgeUi);
        Stage.SelectionChanged += UpdateBridgeUi;
        UpdateBridgeUi();

        Browser.EntryActivated += StageEntry;
        Browser.CollapsedChanged += UpdateBrowserRow;

        CommandBindings.Add(new CommandBinding(EditorCommands.Undo, (_, _) => Stage.Undo(),
            (_, e) => e.CanExecute = Stage.History.CanUndo && !IsTextFieldFocused()));
        CommandBindings.Add(new CommandBinding(EditorCommands.Redo, (_, _) => Stage.Redo(),
            (_, e) => e.CanExecute = Stage.History.CanRedo && !IsTextFieldFocused()));
        Stage.History.Changed += CommandManager.InvalidateRequerySuggested;

        CommandBindings.Add(new CommandBinding(EditorCommands.Delete, (_, _) => Stage.DeleteSelected(),
            (_, e) => e.CanExecute = Stage.CanDeleteSelection() && !IsTextFieldFocused()));
        CommandBindings.Add(new CommandBinding(EditorCommands.Duplicate, (_, _) => Stage.DuplicateSelected(),
            (_, e) => e.CanExecute = Stage.CanDuplicateSelection() && !IsTextFieldFocused()));

        // Save is also available while a text field is focused, so the key can first COMMIT a just-typed
        // value (the fields commit on LostFocus) instead of dropping it — same rule as the map editor.
        CommandBindings.Add(new CommandBinding(EditorCommands.Save, (_, _) => SaveEdits(),
            (_, e) => e.CanExecute = Stage.HasUnsavedEdits
                || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase));
        CommandBindings.Add(new CommandBinding(EditorCommands.Settings, (_, _) =>
            new SettingsWindow { Owner = this }.ShowDialog()));

        ApplyHotkeys();
        HotkeyMap.Current.Changed += ApplyHotkeys;
        Closed += (_, _) => HotkeyMap.Current.Changed -= ApplyHotkeys;

        // Multiplayer is only available when the M2Online launcher is present in the game folder.
        MultiplayerBtn.IsEnabled = GameLauncher.HasMultiplayer;

        UpdateStageChrome();
        Loaded += (_, _) => BuildCatalog();
    }

    /// <summary>The archive currently on the stage, or null before anything has been opened.</summary>
    public LibraryEntry? StagedEntry => _staged;

    /// <summary>
    /// Opens the window on a particular archive — what the map editor's "Open in library" jump will use once
    /// it exists. Safe to call before the catalog has finished building: the request is remembered and
    /// honoured when it lands.
    /// </summary>
    public void Reveal(FileInfo archive)
    {
        if (_catalog?.Find(archive) is { } entry)
        {
            Browser.Reveal(entry);
            StageEntry(entry);
            return;
        }
        _pendingReveal = archive;
    }

    private FileInfo? _pendingReveal;

    // ── Library ──

    // A directory walk of the game's sds tree — fast, but not instant on a cold disk, and the window is opened
    // by a click. Built once per window, off the UI thread.
    private async void BuildCatalog()
    {
        if (_catalog != null || !MafiaEnvironment.IsInitialized) return;
        string sdsFolder = Path.Combine(MafiaEnvironment.PcFolder, "sds");
        try
        {
            LibraryCatalog catalog = await Task.Run(() => LibraryCatalog.Build(sdsFolder));
            _catalog = catalog;
            Browser.SetCatalog(catalog);
            if (_pendingReveal is { } pending)
            {
                _pendingReveal = null;
                Reveal(pending);
            }
        }
        catch (Exception ex)
        {
            Notices.Post("Could not index the game's archives — " + ex.Message, true);
        }
    }

    /// <summary>
    /// Puts one archive on the stage. The load replaces the scene, so anything edited but not yet written is
    /// saved into its extracted folder first — that is exactly what Ctrl+S does, and a frame that has left
    /// memory can no longer be saved from it. What is saved but not yet packed stays on the build list: that
    /// list is about folders on disk, and staging something else does not make them any less unpacked.
    /// </summary>
    private void StageEntry(LibraryEntry entry)
    {
        CommitFocusedField();

        // One extracted working copy per archive, shared by every window that opens it. Two editors on the
        // same one are two pictures of the same folder, and whichever saves last wins silently. Say so before
        // the loss rather than after — but do not refuse: looking at a district in both windows is a fair
        // thing to want, as long as it is not done by accident.
        if (Assets.Sds.OpenArchives.IsHeldByAnyoneElse(entry.File, Stage))
        {
            DialogOutcome outcome = AppDialog.Show(this, new DialogOptions
            {
                Title = "Open on the stage",
                Icon = DialogIcon.Warning,
                Heading = $"{entry.Name} is already open in another editor window",
                Text = "Both windows would work on the same extracted copy of it, from their own picture of "
                     + "what it holds — and the one that saves last would overwrite the other's changes "
                     + "without a word.\n\nOpen it here anyway?",
                Buttons = DialogButtons.YesCancel,
                ConfirmText = "Open anyway",
                CancelText = "Cancel",
            });
            if (!outcome.Confirmed) return;
        }

        if (Stage.HasUnsavedEdits) SaveEdits();

        _staged = entry;
        Stage.Start();      // first thing to draw: bring the render pipeline up (see the XAML)
        Stage.LoadStage(entry.File, entry.Name);
        UpdateStageChrome();
    }

    private void UpdateStageChrome()
    {
        StagedText.Text = _staged?.Name ?? "nothing loaded";

        // With nothing loaded the stage column is a page, not a viewport: the render surface is not even
        // running, so the tools and gizmos that act on it go with it rather than floating over an empty
        // background. The hover label is left alone — it drives its own visibility, and there are no glyphs
        // here to name.
        bool staged = Stage.Roots.Count > 0;
        EmptyStage.Visibility = staged ? Visibility.Collapsed : Visibility.Visible;
        ToolShelf.SetShown(staged);
        UpdateTitle();
    }

    private void UpdateTitle() =>
        Title = "Resource Editor" + (_staged != null ? " — " + _staged.Name : "")
              + (Stage.HasUnsavedEdits ? " *" : "");

    // The browser may never take more than half of what the stage column has to split — the render surface is
    // the point of the window, and on the smallest screen the column is barely 500px tall.
    private void StageColumn_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BrowserRow == null) return; // fires during InitializeComponent, before the row definition exists
        BrowserRow.MaxHeight = BrowserCap(e.NewSize.Height);
        if (BrowserRow.Height.IsAbsolute && BrowserRow.Height.Value > BrowserRow.MaxHeight)
            BrowserRow.Height = new GridLength(BrowserRow.MaxHeight);
    }

    private double BrowserCap(double columnHeight) =>
        Math.Max(120, (columnHeight - ToolbarRow.ActualHeight - BrowserSplitter.Height) * 0.5);

    private void UpdateBrowserRow()
    {
        // Catch a dragged height before the row falls back to Auto, so folding and reopening the browser does
        // not undo the resize.
        if (BrowserRow.Height.IsAbsolute && BrowserRow.Height.Value > 40) _browserHeight = BrowserRow.Height.Value;
        bool open = !Browser.IsCollapsed;
        double cap = BrowserCap(StageColumn.ActualHeight);
        BrowserRow.MaxHeight = cap;
        BrowserRow.Height = open ? new GridLength(Math.Min(_browserHeight, cap)) : GridLength.Auto;
        BrowserSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Keyboard ──

    /// <summary>
    /// The keys this window acts on. There is no modal transform here (the stage has no gizmo overlay yet), so
    /// unlike the map editor it is only the menu commands — read from the keymap, never from a KeyGesture.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool typing = IsTextFieldFocused();
        if ((!typing && (ToolShelf.HandleKey(key, Keyboard.Modifiers, e.IsRepeat) || HandleBridgeKey(key)))
            || EditorCommands.Handle(key, Keyboard.Modifiers, this))
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // Blender edit mode, mirroring Blender's own Tab: no session + selection → open the selection there;
    // session active → leave it. Same rule as the map editor, because it is the same bridge.
    private bool HandleBridgeKey(Key key)
    {
        HotkeyMap map = HotkeyMap.Current;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (map.Matches(HotkeyId.BridgeToggle, key, modifiers))
        {
            if (Stage.BridgeEditedCount > 0) { Stage.EndBridgeEditSession(); return true; }
            if (Stage.SelectedNodes.Count > 0) { Stage.OpenInBlender(); return true; }
            return false;   // nothing selected and no session: Tab still means focus traversal
        }
        if (map.Matches(HotkeyId.BridgeLeave, key, modifiers) && Stage.BridgeEditedCount > 0)
        {
            Stage.EndBridgeEditSession();
            return true;
        }
        return false;
    }

    // The tool-shelf Blender button — the mouse analog of Tab. The toggle VISUAL follows the real session
    // state, never the raw click.
    private void ToggleBridgeSession()
    {
        ToolShelf.RevertBlenderToggle(Stage.BridgeEditedCount > 0);
        if (Stage.BridgeEditedCount > 0) Stage.EndBridgeEditSession();
        else if (Stage.SelectedNodes.Count > 0) Stage.OpenInBlender();
    }

    private void UpdateBridgeUi()
    {
        bool editing = Stage.BridgeEditedCount > 0;
        BridgeFrame.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ToolShelf.SetBridgeState(editing, Stage.SelectedNodes.Count > 0);
        CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyHotkeys()
    {
        HotkeyMap map = HotkeyMap.Current;
        SaveMenuItem.InputGestureText = map[HotkeyId.Save].ToString();
        SettingsMenuItem.InputGestureText = map[HotkeyId.OpenSettings].ToString();
        UndoMenuItem.InputGestureText = map[HotkeyId.Undo].ToString();
        RedoMenuItem.InputGestureText = map[HotkeyId.Redo].ToString();

        Stage.CameraKeys = new Rendering.Controls.CameraKeyMap(
            map[HotkeyId.CameraForward].Key, map[HotkeyId.CameraBack].Key,
            map[HotkeyId.CameraLeft].Key, map[HotkeyId.CameraRight].Key,
            map[HotkeyId.CameraFast].Modifiers, map[HotkeyId.CameraSlow].Modifiers);
    }

    private static bool IsTextFieldFocused() =>
        Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;

    // A focused field commits on LostFocus; anything that reads the scene has to move focus off it first, or
    // it reads the value from before the last keystroke.
    private void CommitFocusedField()
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase box)
        {
            box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    // ── Save · Build · Restore ──

    private void SaveEdits()
    {
        CommitFocusedField();
        if (!Stage.HasUnsavedEdits) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Stage.SaveEdits();
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Save",
                Icon = DialogIcon.Error,
                Heading = "Failed to save",
                Text = ex.Message,
            });
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private void LayersPopup_Closed(object sender, EventArgs e) => LayersBtn.IsChecked = false;

    private void PartShapes_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Stage == null) return;
        Stage.ShowPartShapes = PartShapesToggle.IsChecked == true;
    }

    private void Build_Click(object sender, RoutedEventArgs e)
    {
        CommitFocusedField();
        if (Stage.PendingBuildArchives().Count == 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Build",
                Icon = DialogIcon.Info,
                Text = "No edits to build — change something on the stage first.",
            });
            return;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Viewport.D3DImageHost.BuildReport report = Stage.BuildEdits(createBackup: true);
            Mouse.OverrideCursor = null;
            ShowBuildResult(report);
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Build",
                Icon = DialogIcon.Error,
                Heading = "Build failed",
                Text = ex.Message,
            });
        }
        finally { Mouse.OverrideCursor = null; }
    }

    // Short and to the point: the map editor's version offers a "don't show again" for successful builds,
    // which only earns its keep when you build district after district.
    private void ShowBuildResult(Viewport.D3DImageHost.BuildReport report)
    {
        if (report.Failed.Count == 0)
        {
            string? backup = report.Packed.Select(r => r.Backup).FirstOrDefault(b => b != null);
            Notices.Post(report.Packed.Count == 1 ? "Built 1 archive." : $"Built {report.Packed.Count} archives."
                         + (backup != null ? "  Backup: " + Path.GetDirectoryName(backup) : ""), false);
            return;
        }

        AppDialog.Show(this, new DialogOptions
        {
            Title = "Build",
            Icon = report.Packed.Count == 0 ? DialogIcon.Error : DialogIcon.Warning,
            Heading = report.Packed.Count == 0 ? "Build failed" : "Built with errors",
            Text = string.Join("\n", report.Failed.Select(f =>
                       $"•  {MainWindow.DescribeArchive(new FileInfo(f.Archive))} — {f.Error}"))
                   + "\n\nThey are still marked as edited — fix the cause (e.g. close the game) and Build again.",
        });
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e) => ShowRestoreDialog(null);

    private void ShowRestoreDialog(FileInfo? preselect)
    {
        if (Stage.FrameDocumentNodes().Count == 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Restore Backup",
                Icon = DialogIcon.Info,
                Text = "Put an archive on the stage first — restore targets an archive loaded in the scene.",
            });
            return;
        }

        var win = new RestoreBackupWindow(Stage, preselect) { Owner = this };
        if (win.ShowDialog() != true) return;
        if (win.SelectedArchive is not { } sds || win.SelectedBackup is not { } backup) return;

        // Mirror-first, like the map editor: if the archive swap then fails (game running), the reload
        // re-extracts the CURRENT archive — consistent state and an honest error, instead of stale extracted
        // files silently sitting on top of a restored archive.
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Stage.PrepareForArchiveRestore();
            Assets.Sds.SdsWriter.DeleteExtracted(MafiaEnvironment.ExtractedDir(sds));
            Assets.Sds.SdsWriter.RestoreArchive(sds, backup);
            Stage.ForgetPendingBuild(sds); // the working copy it would have packed has just been deleted
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Restore Backup",
                Icon = DialogIcon.Error,
                Heading = "Restore failed",
                Text = ex.Message + "\n\nNothing was replaced beyond the extracted files.",
            });
            ReloadStage();
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        ReloadStage();
        Notices.Post($"Restored {sds.Name} from {backup.Name}.", false);
    }

    private void ReloadStage()
    {
        if (_staged is { } entry) Stage.LoadStage(entry.File, entry.Name);
    }

    // ── Chrome ──

    private void OpenMaterialEditor(MaterialViewModel vm)
    {
        if (_materialEditor is not { IsLoaded: true })
        {
            _materialEditor = new MaterialEditorWindow(Stage) { Owner = this };
            _materialEditor.Show();
        }
        _materialEditor.ShowMaterial(vm.Hash, Stage.SelectedNode, vm.SlotIndex);
        _materialEditor.Activate();
    }

    private void Play_Click(object sender, RoutedEventArgs e) => GameLauncher.Play(this);

    private void Multiplayer_Click(object sender, RoutedEventArgs e) => GameLauncher.Multiplayer(this);

    private void RenderMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Stage == null) return;
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out RenderMode mode))
            Stage.RenderMode = mode;
    }

    private void UpdateCameraReadout()
    {
        Vector3 p = Stage.CameraPosition;
        CamPosBox.X = p.X;
        CamPosBox.Y = p.Y;
        CamPosBox.Z = p.Z;
        FpsText.Text = $"{Stage.Fps:F0} FPS · {Stage.DrawCalls} draws";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _materialEditor?.Close();
        Stage.Dispose();  // its own GPU stack — the map editor's keeps running
        base.OnClosed(e);
    }
}
