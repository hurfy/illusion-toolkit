using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Domain;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gizmos;
using Illusion.Rendering.Passes;
using Illusion.Scene;
using Illusion.ViewModels;
using Illusion.Viewport;

namespace Illusion.Views;

public partial class MainWindow : Window
{
    private readonly ICollectionView _groupsView;
    private readonly SelectionViewModel _selection;
    private TransformGizmo? _transformGizmo;

    /// <summary>Undo (Ctrl+Z) — bound to the Edit menu and a window-wide hotkey.</summary>
    public static readonly RoutedUICommand UndoCmd = new("Undo", "Undo", typeof(MainWindow));

    /// <summary>Redo (Ctrl+Shift+Z) — bound to the Edit menu and a window-wide hotkey.</summary>
    public static readonly RoutedUICommand RedoCmd = new("Redo", "Redo", typeof(MainWindow));

    /// <summary>Delete selected objects (Del key / hierarchy context menu).</summary>
    public static readonly RoutedUICommand DeleteCmd = new("Delete", "Delete", typeof(MainWindow));

    /// <summary>Duplicate the selected collision placements (Ctrl+D / hierarchy context menu).</summary>
    public static readonly RoutedUICommand DuplicateCmd = new("Duplicate", "Duplicate", typeof(MainWindow));

    /// <summary>Save edits to disk (Ctrl+S) — bound to the File menu and a window-wide hotkey.</summary>
    public static readonly RoutedUICommand SaveCmd = new("Save", "Save", typeof(MainWindow));

    /// <summary>Import an external model (Ctrl+I) — File menu; opens the import dialog.</summary>
    public static readonly RoutedUICommand ImportCmd = new("Import", "Import", typeof(MainWindow));

    private const string BaseTitle = "Illusion Toolkit";

    public MainWindow()
    {
        InitializeComponent();

        SceneTree.ItemsSource = Viewport.Roots;
        _groupsView = CollectionViewSource.GetDefaultView(Viewport.Roots);
        _groupsView.Filter = o => o is SceneNode n && n.HasSearchMatch;

        // Contextual property tabs (Object / SDS / FrameResource / Scene) bind to this view-model.
        _selection = new SelectionViewModel(Viewport);
        PropertyTabs.DataContext = _selection;

        Viewport.SceneChanged += () => Dispatcher.Invoke(() =>
        {
            UpdateSceneStats();
            _groupsView.Refresh();
        });

        // Catalog ready → populate the area selector.
        Viewport.CatalogReady += () => Dispatcher.Invoke(PopulateAreas);

        // Live camera position output to the bottom panel (per-frame, already on the UI thread).
        Viewport.CameraMoved += UpdateCameraReadout;

        // Camera position editor (bottom bar): commit edits/pastes back to the camera.
        CamPosBox.ValueCommitted += (_, _) => CommitPosition();

        // Selection sync: tree ⇄ viewport ⇄ property tabs. Only act on a real node — a null NewValue can come
        // from the virtualized tree recycling the selected container on scroll, and must NOT deselect
        // (clearing selection is done by a viewport empty-click / area reload).
        // Plain tree click → single-select (Ctrl+click is intercepted below and multi-selects instead).
        SceneTree.SelectedItemChanged += (_, e) => { if (e.NewValue is SceneNode n) Viewport.Select(n); };
        SceneTree.PreviewMouseLeftButtonDown += SceneTree_PreviewMouseLeftButtonDown;
        Viewport.SelectionChanged += OnSelectionChanged;
        Viewport.SelectionTransformChanged += _selection.RefreshTransform;
        Viewport.SelectionPropertiesChanged += _selection.RefreshPropertyValues;

        // Materials: a tile click opens the (single, reused) material editor window; any material edit —
        // from either window, or an undo — rebuilds the tab's tiles in place.
        MaterialsPanel.OpenRequested += OpenMaterialEditor;
        Viewport.MaterialsChanged += _selection.RefreshMaterials;

        // Undo / redo: Edit-menu commands + window-wide Ctrl+Z / Ctrl+Shift+Z. The commands drive the viewport's
        // edit history; their enabled state follows CanUndo/CanRedo (re-queried when the history changes).
        CommandBindings.Add(new CommandBinding(UndoCmd, (_, _) => Viewport.Undo(), (_, e) => e.CanExecute = Viewport.History.CanUndo && !IsTextFieldFocused()));
        CommandBindings.Add(new CommandBinding(RedoCmd, (_, _) => Viewport.Redo(), (_, e) => e.CanExecute = Viewport.History.CanRedo && !IsTextFieldFocused()));
        InputBindings.Add(new KeyBinding(UndoCmd, new KeyGesture(Key.Z, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(RedoCmd, new KeyGesture(Key.Z, ModifierKeys.Control | ModifierKeys.Shift)));
        Viewport.History.Changed += CommandManager.InvalidateRequerySuggested;

        // Delete selected objects: Del key (gated off text fields so it still deletes characters there) + the
        // hierarchy context menu. Right-click also selects the row so the menu acts on it. Disabled during a
        // Blender edit session — deleting an object that is open in Blender would desync the bridge scene.
        CommandBindings.Add(new CommandBinding(DeleteCmd, (_, _) => Viewport.DeleteSelected(),
            (_, e) => e.CanExecute = Viewport.CanDeleteSelection() && !IsTextFieldFocused()
                && Viewport.BridgeEditedCount == 0));
        InputBindings.Add(new KeyBinding(DeleteCmd, new KeyGesture(Key.Delete)));
        SceneTree.PreviewMouseRightButtonDown += SceneTree_PreviewMouseRightButtonDown;

        // Duplicate selected collision placements: Ctrl+D + the hierarchy context menu (collision only).
        CommandBindings.Add(new CommandBinding(DuplicateCmd, (_, _) => Viewport.DuplicateSelected(),
            (_, e) => e.CanExecute = Viewport.CanDuplicateSelection() && !IsTextFieldFocused()
                && Viewport.BridgeEditedCount == 0));
        InputBindings.Add(new KeyBinding(DuplicateCmd, new KeyGesture(Key.D, ModifierKeys.Control)));

        // Save edits (Ctrl+S / File → Save): writes the edited FrameResource(s) back to their extracted folders.
        // Enabled only while there are unsaved edits; the title shows a '*' in that window.
        // Also enabled while a text field is focused so Ctrl+S can first COMMIT a just-typed transform value
        // (Vector3Box commits on LostFocus) — otherwise a still-focused edit would be dropped by the save.
        CommandBindings.Add(new CommandBinding(SaveCmd, (_, _) => SaveEdits(),
            (_, e) => e.CanExecute = Viewport.HasUnsavedEdits
                || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase));
        InputBindings.Add(new KeyBinding(SaveCmd, new KeyGesture(Key.S, ModifierKeys.Control)));

        // Import an external OBJ (Ctrl+I / File → Import…) into a loaded document; needs a scene to land in.
        CommandBindings.Add(new CommandBinding(ImportCmd, (_, _) => ShowImportDialog(),
            (_, e) => e.CanExecute = Viewport.Roots.Count > 0 && Viewport.BridgeEditedCount == 0));
        InputBindings.Add(new KeyBinding(ImportCmd, new KeyGesture(Key.I, ModifierKeys.Control)));
        Viewport.DirtyChanged += () => Dispatcher.Invoke(() => { UpdateTitle(); CommandManager.InvalidateRequerySuggested(); });

        // Editable transform overlay (bottom-left): the changed state's X/Y/Z, bound to the active selection.
        // Hidden until a real gizmo edit reveals it (GizmoEdited); a selection change hides it again.
        GizmoPanel.DataContext = _selection;
        Viewport.GizmoEdited += ShowGizmoPanelForMode;

        // Viewport overlays: transform gizmo (above the render surface, below the tool shelf) + navigation gizmo.
        if (Viewport.Parent is Grid viewportGrid)
        {
            _transformGizmo = new TransformGizmo();
            viewportGrid.Children.Insert(1, _transformGizmo);
            _transformGizmo.Attach(Viewport);

            var gizmo = new ViewportGizmo();
            viewportGrid.Children.Add(gizmo);
            gizmo.Attach(Viewport);
        }

        // The layers list is a look, not a decision: hovering the button is enough to open it.
        HoverPopup.Attach(LayersBtn, LayersPopup);

        // Viewport tool shelf → gizmo mode. ToolSelect is the default (select-only, no gizmo).
        ToolSelect.Checked += (_, _) => SetGizmoMode(GizmoMode.None);
        ToolMove.Checked += (_, _) => SetGizmoMode(GizmoMode.Move);
        ToolRotate.Checked += (_, _) => SetGizmoMode(GizmoMode.Rotate);
        ToolScale.Checked += (_, _) => SetGizmoMode(GizmoMode.Scale);

        // Multiplayer is only available when the M2Online launcher is present in the game folder.
        MultiplayerBtn.IsEnabled = File.Exists(M2OLauncherPath);

        UpdateBridgeUi(); // initial chrome state (no session, nothing selected → Blender button disabled)

        // Bridge edit-set changes drive the whole edit-mode chrome: title indicator, orange
        // viewport frame, the Blender tool button state, and disabling scene-reload controls
        // (switching district/season mid-session would pull the scene out from under Blender).
        Viewport.BridgeStateChanged += () => Dispatcher.BeginInvoke(UpdateBridgeUi);

        // Right-click on the render surface → a light context menu (restore-from-backup, scoped to the
        // clicked object's archive when it hit one). Built in code — the target depends on the hit.
        Viewport.ViewportContextMenuRequested += ShowViewportContextMenu;

        // Blender bridge notices arrive on protocol/background threads; NoticeBanner.Post marshals itself.
        // These used to be modal dialogs — the only notice channel the app had — which meant a dialog to
        // dismiss for every push outcome, and would have meant one per refused gizmo drag once collision
        // editing started refusing things. They are reports, not decisions, so they belong in the viewport.
        Viewport.BridgeNotice += (message, isError) => Notices.Post(message, isError);
        Viewport.TransientNotice += (message, isError) => Notices.Post(message, isError);
    }

    // Tab TOGGLES the Blender edit mode, mirroring Blender's own: no session + selection → open it
    // in Blender (everything else ghosts and becomes unselectable); session active → leave it (all
    // objects un-ghost, the bridge scene despawns in Blender, selection works again). Esc also
    // leaves. Intercepted in PreviewKeyDown because WPF refuses KeyGesture(Key.Tab) (unmodified
    // non-function keys throw NotSupportedException) and Tab would otherwise run focus traversal.
    // Falls through untouched while typing in a text field or with a modifier held.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None && !IsTextFieldFocused())
        {
            if (e.Key == Key.Tab)
            {
                if (Viewport.BridgeEditedCount > 0)
                {
                    Viewport.EndBridgeEditSession();
                    e.Handled = true;
                    return;
                }
                if (Viewport.SelectedNodes.Count > 0)
                {
                    Viewport.OpenInBlender();
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.Escape && Viewport.BridgeEditedCount > 0)
            {
                Viewport.EndBridgeEditSession();
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private void SetGizmoMode(GizmoMode mode)
    {
        if (Viewport == null) return;
        Viewport.GizmoMode = mode;
        _transformGizmo?.InvalidateVisual();
    }

    // A real gizmo edit occurred: reveal the overlay panel showing the vector for the tool that changed it —
    // Rotate -> Rotation, Scale -> Scale, otherwise Position. Only one Vector3Box shows at a time. Stays visible
    // (for hand-editing) until the selection changes.
    private void ShowGizmoPanelForMode(GizmoMode mode)
    {
        PanelRot.Visibility = mode == GizmoMode.Rotate ? Visibility.Visible : Visibility.Collapsed;
        PanelScale.Visibility = mode == GizmoMode.Scale ? Visibility.Visible : Visibility.Collapsed;
        PanelPos.Visibility = mode is GizmoMode.Rotate or GizmoMode.Scale ? Visibility.Collapsed : Visibility.Visible;
        GizmoPanel.Visibility = Visibility.Visible;
    }

    private SceneNode? _panelNode; // the active node the transform overlay currently tracks

    // Selection changed (tree click or viewport pick): feed the property tabs and surface the type's tab.
    private void OnSelectionChanged()
    {
        // Hide the transform overlay only when the ACTIVE object actually changes — undo/redo re-selects the same
        // object (and a background mesh-attach re-fires this with no change), and the panel should survive those.
        if (!ReferenceEquals(Viewport.SelectedNode, _panelNode))
        {
            GizmoPanel.Visibility = Visibility.Collapsed;
            _panelNode = Viewport.SelectedNode;
        }
        _selection.SetNode(Viewport.SelectedNode);
        ToolBlender.IsEnabled = Viewport.BridgeEditedCount > 0 || Viewport.SelectedNodes.Count > 0;

        // Surface the type's tab; when the selection has no contextual tab (folder / nothing selected) fall back
        // to the always-visible Render tab — otherwise the previously-selected tab, now Collapsed, would keep
        // showing stale content under a hidden header. Keep the current tab when it is still visible AND is one of
        // the two object tabs (Object / Type), so inspecting objects of the same type doesn't bounce the panel off
        // the tab the user is reading (e.g. staying on the per-type tab across successive Light selections).
        TabItem target =
            _selection.HasTransform ? ObjectTab :
            _selection.IsSds ? SdsTab :
            _selection.IsFrameResource ? FrameResourceTab :
            _selection.IsScene ? SceneTab :
            _selection.HasTypeProperties ? TypeTab : // type-only selections (e.g. a collision placement) surface their type tab
            RenderTab;
        bool keepCurrent = PropertyTabs.SelectedItem is TabItem cur && cur.Visibility == Visibility.Visible
            && (ReferenceEquals(cur, ObjectTab) || ReferenceEquals(cur, TypeTab) || ReferenceEquals(cur, MaterialsTab));
        if (!keepCurrent) target.IsSelected = true; // its Visibility binding has already made it visible

        // Selecting a mesh (viewport ray-pick or tree click) scrolls the hierarchy to that mesh's row.
        if (Viewport.SelectedNode is { Mesh: not null } meshNode) BringNodeIntoView(meshNode);
    }

    // Scrolls the scene tree to a node's row, realizing it through the virtualized panels — WPF's TreeView does
    // not auto-scroll to a programmatically-selected item. Ancestors are already expanded by Viewport.Select;
    // here we walk root→node, force each level's container to generate, and bring the final row into view.
    // Best-effort: any virtualization quirk leaves the ancestors expanded and the row selected, just unscrolled.
    private void BringNodeIntoView(SceneNode target)
    {
        var path = new List<SceneNode>();
        for (SceneNode? n = target; n != null; n = n.Parent) path.Insert(0, n);

        // Defer so the pending IsExpanded / IsSelected bindings and layout settle before we walk the containers.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                ItemsControl parent = SceneTree;
                for (int i = 0; i < path.Count; i++)
                {
                    if (RealizeContainer(parent, path[i]) is not TreeViewItem tvi) return;
                    if (i == path.Count - 1)
                    {
                        tvi.BringIntoView();
                    }
                    else
                    {
                        tvi.IsExpanded = true;
                        tvi.UpdateLayout();
                        parent = tvi;
                    }
                }
            }
            catch { /* virtualization/reflection quirk — the scroll is a best-effort nicety */ }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // The (possibly virtualized) TreeViewItem for one child item, forcing generation via the items-host panel.
    private static TreeViewItem? RealizeContainer(ItemsControl parent, object item)
    {
        parent.ApplyTemplate();
        parent.UpdateLayout();
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem c) return c;

        int index = parent.Items.IndexOf(item);
        if (index < 0) return null;
        if (FindItemsHost(parent) is VirtualizingPanel panel)
        {
            BringIndexIntoView(panel, index);
            parent.UpdateLayout();
        }
        return parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
    }

    // The panel that hosts an ItemsControl's rows (IsItemsHost), found in its visual subtree.
    private static Panel? FindItemsHost(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Panel p && p.IsItemsHost) return p;
            if (FindItemsHost(child) is Panel found) return found;
        }
        return null;
    }

    // VirtualizingPanel.BringIndexIntoView is protected — reflection is the only way to realize an out-of-view
    // row from outside the panel. Cached; null if the runtime ever renames it (then the scroll silently degrades).
    private static readonly System.Reflection.MethodInfo? BringIndexIntoViewMethod =
        typeof(VirtualizingPanel).GetMethod("BringIndexIntoView",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new[] { typeof(int) }, null);

    private static void BringIndexIntoView(VirtualizingPanel panel, int index)
        => BringIndexIntoViewMethod?.Invoke(panel, new object[] { index });

    // Tree click routing. Ctrl+click multi-selects a transformable object (toggles it); Ctrl+click on a plain
    // container is a no-op (never clobber an in-progress multi-selection). A plain click drives the single-select
    // DIRECTLY rather than via SelectedItemChanged — the TreeView's own SelectedItem can be stale after a viewport
    // pick / Ctrl-toggle, and re-clicking the row it still considers selected would fire no event (dead click).
    // Clicks on the eye toggle / expander are left to their own handlers.
    private void SceneTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(src) != null) return;
        if (FindAncestor<TreeViewItem>(src)?.DataContext is not SceneNode node) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (node.Source is IFrameNode) Viewport.ToggleSelect(node);
            e.Handled = true; // suppress the TreeView's default single-selection either way
        }
        else
        {
            // Idempotent (Select's same-single guard); not marked handled, so the TreeView still manages
            // focus / expansion and re-syncs its own SelectedItem for keyboard navigation.
            Viewport.Select(node);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    // Right-click selects the row (unless it's already part of the multi-selection), so the context menu's
    // Delete acts on it; the ContextMenu itself is attached in the tree's ItemContainerStyle.
    private void SceneTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (FindAncestor<TreeViewItem>(src)?.DataContext is not SceneNode node) return;
        if (!Viewport.SelectedNodes.Contains(node)) Viewport.Select(node);
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e) => Viewport.DeleteSelected();

    private void DuplicateMenuItem_Click(object sender, RoutedEventArgs e) => Viewport.DuplicateSelected();

    private void RemoveUnusedHulls_Click(object sender, RoutedEventArgs e) => Viewport.RemoveUnusedHulls();

    private void EditMenu_SubmenuOpened(object sender, RoutedEventArgs e) => RefreshUnusedHullsItems();

    private void SceneTreeContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        RefreshUnusedHullsItems();
        RefreshTreeRestoreItem();
    }

    // Both "Remove unused hulls" items show the live count and disable at zero: sweeping is never automatic
    // (an orphaned hull may be wanted back), so the menu is where a modder finds out there is anything to sweep.
    private void RefreshUnusedHullsItems()
    {
        int n = Viewport.UnusedHullCount();
        foreach (MenuItem item in new[] { RemoveUnusedHullsItem, TreeRemoveUnusedHullsItem })
        {
            item.Header = n > 0 ? $"Remove unused hulls ({n})" : "Remove unused hulls";
            item.IsEnabled = n > 0;
        }
    }

    // Keep scene undo/redo (Ctrl+Z / Ctrl+Shift+Z) from firing while a text field is focused — a TextBox has its
    // own text-undo, and the mirror gesture Ctrl+Shift+Z would otherwise leak into scene redo mid-edit.
    private static bool IsTextFieldFocused() =>
        Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;

    // ── Center action bar: Play · Multiplayer · Build ──

    /// <summary>M2Online launcher, expected at <c>&lt;game root&gt;\m2o\client\M2OLauncher.exe</c>.</summary>
    private static string M2OLauncherPath =>
        Path.Combine(MafiaEnvironment.GameRoot ?? "", "m2o", "client", "M2OLauncher.exe");

    // Launch Mafia II. The executable isn't in a fixed spot across editions, so probe the usual names
    // in pc\ first (Steam layout), then the game root.
    private void Play_Click(object sender, RoutedEventArgs e)
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
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Play",
                Icon = DialogIcon.Warning,
                Text = "Mafia II executable not found (looked for mafia2.exe / launcher.exe).",
            });
            return;
        }
        LaunchExe(exe);
    }

    private void Multiplayer_Click(object sender, RoutedEventArgs e) => LaunchExe(M2OLauncherPath);

    // Save (Ctrl+S / File → Save): write the edited FrameResource(s) back to their extracted folders. Quiet on
    // success — the title's '*' clearing is the feedback, as in any editor; only failures raise a dialog.
    private void SaveEdits()
    {
        CommitFocusedField();
        if (!Viewport.HasUnsavedEdits) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Viewport.SaveEdits();
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

    // The material editor window — one non-modal instance, re-focused (not re-created) on every tile click
    // so its library list, search text and camera survive between materials.
    private MaterialEditorWindow? _materialEditor;

    private void OpenMaterialEditor(MaterialViewModel vm)
    {
        if (_materialEditor is not { IsLoaded: true })
        {
            _materialEditor = new MaterialEditorWindow(Viewport) { Owner = this };
            _materialEditor.Show();
        }
        _materialEditor.ShowMaterial(vm.Hash, Viewport.SelectedNode, vm.SlotIndex);
        _materialEditor.Activate();
    }

    // Import (Ctrl+I / File → Import…): bring a glTF file in — meshes and COL_-prefixed collision hulls.
    private void ShowImportDialog()
    {
        if (Viewport.FrameDocumentNodes().Count == 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Import",
                Icon = DialogIcon.Info,
                Text = "Load an area first — an import needs a loaded document to land in.",
            });
            return;
        }
        new ImportWindow(Viewport) { Owner = this }.ShowDialog();
    }

    // Build (center toolbar button / File → Build SDS): pack the edited archive(s) back into the game's .sds.
    // No pre-build confirmation — edits are already saved to the extracted folders and every build keeps a
    // versioned backup, so it runs straight away and reports the outcome afterwards (ShowBuildResult). Each build
    // versions the previous archive contents into a timestamped copy under a "backups" folder beside it.
    private void Build_Click(object sender, RoutedEventArgs e)
    {
        CommitFocusedField();

        if (Viewport.PendingBuildArchives().Count == 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Build",
                Icon = DialogIcon.Info,
                Text = "No edits to build — move or edit an object first.",
            });
            return;
        }

        D3DImageHost.BuildReport report;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            report = Viewport.BuildEdits(createBackup: true); // backups are always kept (versioned in a "backups" folder)
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Build",
                Icon = DialogIcon.Error,
                Heading = "Build failed",
                Text = ex.Message,
            });
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        ShowBuildResult(report);
    }

    // Reports a finished build. A fully-successful build is a "Built N archives" notice the user can silence for
    // good with "Don't show this again" (persisted to settings) — once silenced, successful builds are quiet.
    // A whole/partial failure is always shown; those need attention regardless of the preference.
    private void ShowBuildResult(D3DImageHost.BuildReport report)
    {
        bool anyFailed = report.Failed.Count > 0;
        if (!anyFailed && UserSettings.Load().SuppressBuildNotice) return; // user silenced successful-build notices

        var msg = new StringBuilder();
        // On a partial build the heading states the failure, so spell out what DID build in the body.
        if (report.Packed.Count > 0 && anyFailed)
            msg.AppendLine(report.Packed.Count == 1 ? "Built 1 archive." : $"Built {report.Packed.Count} archives.");

        if (report.Packed.Count > 0)
        {
            var backupDirs = report.Packed.Where(r => r.Backup != null)
                                          .Select(r => Path.GetDirectoryName(r.Backup!)!)
                                          .Distinct(StringComparer.OrdinalIgnoreCase)
                                          .ToList();
            if (backupDirs.Count > 0)
            {
                if (msg.Length > 0) msg.AppendLine();
                msg.AppendLine(backupDirs.Count == 1 ? "Backup saved to:" : "Backups saved to:");
                foreach (string d in backupDirs) msg.AppendLine(d);
            }
        }

        if (anyFailed)
        {
            if (msg.Length > 0) msg.AppendLine();
            msg.AppendLine(report.Failed.Count == 1
                ? "1 archive failed to build:"
                : $"{report.Failed.Count} archives failed to build:");
            foreach (D3DImageHost.BuildFailure f in report.Failed)
                msg.AppendLine($"•  {DescribeArchive(new FileInfo(f.Archive))} — {f.Error}");
            msg.AppendLine();
            msg.AppendLine("They are still marked as edited — fix the cause (e.g. close the game) and Build again.");
        }

        DialogIcon icon = !anyFailed ? DialogIcon.Success
                        : report.Packed.Count == 0 ? DialogIcon.Error
                        : DialogIcon.Warning;
        string heading = !anyFailed
            ? (report.Packed.Count == 1 ? "Built 1 archive" : $"Built {report.Packed.Count} archives")
            : report.Packed.Count == 0 ? "Build failed" : "Built with errors";

        DialogOutcome outcome = AppDialog.Show(this, new DialogOptions
        {
            Title = "Build",
            Icon = icon,
            Heading = heading,
            Text = msg.ToString().TrimEnd(),
            // Only a clean success offers "don't show again"; failures must always surface.
            CheckboxText = anyFailed ? null : "Don't show this again",
        });

        if (!anyFailed && outcome.Checked)
        {
            UserSettings settings = UserSettings.Load();
            settings.SuppressBuildNotice = true;
            settings.Save();
        }
    }

    // A short, readable name for an archive in the build list and the restore dialog: its path relative to the
    // game's pc\ folder when it lives under it (e.g. "sds\city\eastside.sds"), else the bare file name.
    internal static string DescribeArchive(FileInfo sds)
    {
        string? pc = MafiaEnvironment.PcFolder;
        if (!string.IsNullOrEmpty(pc))
        {
            string rel = Path.GetRelativePath(pc, sds.FullName);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) return rel;
        }
        return sds.Name;
    }

    // ── Restore from backup (File → Restore Backup… / tree context menu / viewport right-click) ──

    // The archive a scene-tree node belongs to: its own document, the nearest ancestor document, or — for
    // the "Sds" wrapper row, whose documents are CHILDREN (FrameResource / Collisions) — the first child
    // document. Null for folder rows and empty space.
    private static FileInfo? ArchiveOf(SceneNode? node)
    {
        if (node == null) return null;
        if (node.OwningDocumentNode()?.Source is ISceneDocument owner) return owner.SourceArchive;
        foreach (SceneNode child in node.Children)
            if (child.Source is ISceneDocument doc) return doc.SourceArchive;
        return null;
    }

    // The tree menu's restore item follows the right-clicked row (right-click selects it, see
    // SceneTree_PreviewMouseRightButtonDown): the header names the target archive so the rollback target
    // is readable before the dialog opens; rows that resolve to no archive (folders) disable it.
    private void RefreshTreeRestoreItem()
    {
        FileInfo? sds = ArchiveOf(Viewport.SelectedNode);
        TreeRestoreBackupItem.Header = sds != null ? $"Restore Backup… ({sds.Name})" : "Restore Backup…";
        TreeRestoreBackupItem.IsEnabled = sds != null && Viewport.BridgeEditedCount == 0;
    }

    private void TreeRestoreBackup_Click(object sender, RoutedEventArgs e) =>
        ShowRestoreDialog(ArchiveOf(Viewport.SelectedNode));

    private void RestoreBackup_Click(object sender, RoutedEventArgs e) => ShowRestoreDialog(null);

    // Viewport right-click: on an object — select it (the tree convention) and scope the restore to its
    // archive; on empty space — the generic restore picker.
    private void ShowViewportContextMenu(SceneNode? hit, Point pos)
    {
        if (hit != null && !Viewport.SelectedNodes.Contains(hit)) Viewport.Select(hit);
        FileInfo? sds = ArchiveOf(hit);

        var restore = new MenuItem
        {
            Header = sds != null ? $"Restore Backup… ({sds.Name})" : "Restore Backup…",
            IsEnabled = Viewport.BridgeEditedCount == 0,
        };
        restore.Click += (_, _) => ShowRestoreDialog(sds);

        var menu = new ContextMenu { PlacementTarget = Viewport };
        menu.Items.Add(restore);
        menu.IsOpen = true;
    }

    private void ShowRestoreDialog(FileInfo? preselect)
    {
        if (Viewport.BridgeEditedCount > 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Restore Backup",
                Icon = DialogIcon.Info,
                Text = "Leave the Blender edit session first (Tab) — a restore reloads the scene under it.",
            });
            return;
        }
        if (Viewport.FrameDocumentNodes().Count == 0)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Restore Backup",
                Icon = DialogIcon.Info,
                Text = "Load an area first — restore targets an SDS archive loaded in the scene.",
            });
            return;
        }

        var win = new RestoreBackupWindow(Viewport, preselect) { Owner = this };
        if (win.ShowDialog() != true) return;
        if (win.SelectedArchive is not { } sds || win.SelectedBackup is not { } backup) return;
        PerformRestore(sds, backup);
    }

    // The destructive step, sequenced so a failure can never leave the viewport silently diverged from
    // disk: stop the scene (and wait out the background loader) → drop the extracted mirror → swap the
    // game .sds → reload. Mirror-first on purpose: if the swap then fails (game running), the reload
    // re-extracts the CURRENT archive — consistent state, honest error. Swapping first and failing the
    // delete would reload STALE extracted files over the restored archive — silent divergence.
    private void PerformRestore(FileInfo sds, FileInfo backup)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            Viewport.PrepareForArchiveRestore();
            SdsWriter.DeleteExtracted(MafiaEnvironment.ExtractedDir(sds));
            SdsWriter.RestoreArchive(sds, backup);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Restore Backup",
                Icon = DialogIcon.Error,
                Heading = "Restore failed",
                Text = ex.Message
                     + "\n\nNothing was replaced beyond the extracted files; the scene reloads from what is "
                     + "on disk now. If the game is running, close it and try again.",
            });
            ReloadArea();
            return;
        }
        finally { Mouse.OverrideCursor = null; }

        ReloadArea();
        Notices.Post($"Restored {sds.Name} from {backup.Name}.", false);
    }

    // Title shows a trailing '*' while there are edits not yet written to disk (Save clears it) and
    // the Blender edit-session indicator while objects are open in Blender.
    private void UpdateTitle()
    {
        int editing = Viewport.BridgeEditedCount;
        string bridge = editing > 0 ? $" — Blender: editing {editing} object(s)" : "";
        Title = BaseTitle + bridge + (Viewport.HasUnsavedEdits ? " *" : "");
    }

    // The Blender edit-mode chrome, all in one place: orange viewport frame, tool-button state, and
    // the scene-reload controls (area/season/whole-map/crash) that must not fire mid-session.
    private void UpdateBridgeUi()
    {
        bool editing = Viewport.BridgeEditedCount > 0;
        BridgeFrame.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ToolBlender.IsChecked = editing;
        ToolBlender.IsEnabled = editing || Viewport.SelectedNodes.Count > 0;
        AreaCombo.IsEnabled = !editing && WholeMapCheck.IsChecked != true;
        WholeMapCheck.IsEnabled = !editing;
        WinterToggle.IsEnabled = !editing;
        CrashToggle.IsEnabled = !editing;
        CollisionToggle.IsEnabled = !editing;
        CommandManager.InvalidateRequerySuggested(); // Del enablement follows the session
        UpdateTitle();
    }

    // The tool-shelf Blender button — the mouse analog of Tab. The toggle VISUAL follows the real
    // session state (BridgeStateChanged → UpdateBridgeUi), never the raw click.
    private void BlenderTool_Click(object sender, RoutedEventArgs e)
    {
        ToolBlender.IsChecked = Viewport.BridgeEditedCount > 0; // undo WPF's automatic flip
        if (Viewport.BridgeEditedCount > 0) Viewport.EndBridgeEditSession();
        else if (Viewport.SelectedNodes.Count > 0) Viewport.OpenInBlender();
    }

    // A focused transform field (Vector3Box) commits its typed value only on LostFocus / Enter. Before persisting,
    // move focus off it (to the always-focusable viewport) so Ctrl+S / Build capture the just-typed value rather
    // than the stale model — the commit (and any resulting RecordTransform) runs synchronously here.
    private void CommitFocusedField()
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
            Viewport.Focus();
    }

    private void LaunchExe(string exe)
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
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Launch",
                Icon = DialogIcon.Error,
                Heading = "Failed to launch",
                Text = ex.Message,
            });
        }
    }

    // Exit and the window close button return to the launcher instead of closing the app.
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        var launcher = new LauncherWindow();
        Application.Current.MainWindow = launcher;
        launcher.Show();
    }

    // ── Bottom panel: camera position (per axis) + speed ──

    private void UpdateCameraReadout()
    {
        Vector3 p = Viewport.CameraPosition;
        // The Vector3Box skips any field the user is currently editing, so this can push every frame.
        CamPosBox.X = p.X;
        CamPosBox.Y = p.Y;
        CamPosBox.Z = p.Z;
        if (!SpeedBox.IsKeyboardFocused)
        {
            string s = Viewport.MoveSpeed.ToString("F0", CultureInfo.InvariantCulture);
            if (SpeedBox.Text != s) SpeedBox.Text = s;
        }

        // Draw calls + culled-in instances keep render cost visible while tuning (cells, filters).
        long inst = Viewport.DrawnInstances;
        string fps = inst > 0
            ? $"{Viewport.Fps:F0} FPS · {Viewport.DrawCalls} draws · {FormatCompact(inst)} inst"
            : $"{Viewport.Fps:F0} FPS · {Viewport.DrawCalls} draws";
        if (FpsText.Text != fps) FpsText.Text = fps;
    }

    // Called from CamPosBox.ValueCommitted when the user edits/pastes a camera coordinate.
    private void CommitPosition()
    {
        Viewport.CameraPosition = new Vector3((float)CamPosBox.X, (float)CamPosBox.Y, (float)CamPosBox.Z);
    }

    private void CommitSpeed()
    {
        if (TryFloat(SpeedBox.Text, out float s) && s > 0) Viewport.MoveSpeed = s;
    }

    private static bool TryFloat(string t, out float v) =>
        float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private void Speed_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitSpeed(); Viewport.Focus(); }
    }
    private void Speed_LostFocus(object sender, RoutedEventArgs e) => CommitSpeed();

    private void PopulateAreas()
    {
        AreaCombo.ItemsSource = Viewport.Areas;
        if (Viewport.Areas.Count > 0)
        {
            AreaCombo.SelectedIndex = 0; // first district → loads immediately via SelectionChanged
        }
    }

    private void AreaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ReloadArea();

    private void Winter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Viewport == null) return;
        // Snow scenes hold winter-only geometry (their own scene4XX folder): auto-show them in winter and
        // hide them in summer. Assigning IsChecked drives Viewport.ShowSnowScenes via SceneFilter_Changed;
        // the switch stays interactive, so the user can still override it afterwards.
        SnowScenesToggle.IsChecked = WinterToggle.IsChecked == true;
        ReloadArea();
    }
    private void WholeMap_Changed(object sender, RoutedEventArgs e)
    {
        // With "Whole map" the area selector doesn't affect the set — dim it out.
        if (AreaCombo != null) AreaCombo.IsEnabled = WholeMapCheck?.IsChecked != true;
        ReloadArea();
    }

    private void Zones_Changed(object sender, RoutedEventArgs e)
    {
        if (Viewport != null) Viewport.ShowZones = ZonesToggle.IsChecked == true;
    }

    // Shading mode (Blender-style): the checked radio drives the viewport render mode.
    // Fires during InitializeComponent (IsChecked="True" on MaterialModeBtn) — guard like the others.
    // Checked fires on the mode that was just picked, and each button carries its RenderMode in Tag — so the
    // handler reads the sender instead of the group. That also keeps the buttons nameless, which they have to
    // be: they live inside CompactStrip, and a UserControl's namescope will not take this window's names.
    private void RenderMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Viewport == null) return;
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out RenderMode mode))
            Viewport.RenderMode = mode;
    }

    // city_crash is an additive layer: the ShowCrash setter loads/unloads it without a scene reload.
    private void Crash_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Viewport == null) return;
        Viewport.ShowCrash = CrashToggle.IsChecked == true;
    }

    // Collision is an additive per-district layer: the ShowCollision setter loads/unloads it without a scene reload.
    private void Collision_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Viewport == null) return;
        Viewport.ShowCollision = CollisionToggle.IsChecked == true;
    }

    // AI navigation overlay — both halves at once: the .nov graph + its AI-mesh boxes, and the .nav path objects
    // (cover / vault-over / action markers). They answer the same question and were never read apart, so the
    // layers list offers them as one switch. Both are uploaded at load; this only gates drawing.
    private void AiNav_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || Viewport == null) return;
        bool show = AiNavToggle.IsChecked == true;
        Viewport.ShowNov = show;
        Viewport.ShowNavWorld = show;
    }

    // The layers popup closes on an outside click; untoggle the button, or reopening it would take two presses.
    private void LayersPopup_Closed(object sender, EventArgs e) => LayersBtn.IsChecked = false;

    private void ReloadArea()
    {
        // Toggle/combobox handlers can fire during InitializeComponent,
        // when Viewport and other controls aren't created yet — skip.
        if (!IsInitialized || Viewport == null) return;

        bool winter = WinterToggle.IsChecked == true;
        bool wholeMap = WholeMapCheck.IsChecked == true;
        Viewport.LoadArea(AreaCombo.SelectedItem as MapArea, winter, wholeMap);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility =
            string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        SceneSearch.Query = SearchBox.Text ?? "";
        bool searching = !string.IsNullOrWhiteSpace(SceneSearch.Query);

        foreach (SceneNode root in Viewport.Roots) RefreshNode(root, searching);
        _groupsView?.Refresh();
    }

    // Recursively re-filter the branch and expand nodes with matches while searching.
    private static void RefreshNode(SceneNode node, bool searching)
    {
        foreach (SceneNode c in node.Children) RefreshNode(c, searching);
        node.ChildrenView.Refresh();
        if (searching) node.IsExpanded = node.Children.Any(c => c.HasSearchMatch);
    }

    // Scene panel header: loaded SDS files · meshes · polygons. Files = SDS nodes across all folders.
    private void UpdateSceneStats()
    {
        int files = 0;
        foreach (SceneNode f in Viewport.Roots) files += f.Children.Count;
        StatFiles.Text = files.ToString("N0", CultureInfo.InvariantCulture);
        StatMeshes.Text = Viewport.MeshCount.ToString("N0", CultureInfo.InvariantCulture);
        StatPolys.Text = FormatCompact(Viewport.TriangleCount);
    }

    // Compact number for the narrow stats cell: 1.2M / 45.6K / 8,900.
    private static string FormatCompact(long n) =>
        n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : n >= 10_000 ? (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        : n.ToString("N0", CultureInfo.InvariantCulture);

    private void SceneFilter_Changed(object sender, RoutedEventArgs e)
    {
        // Guard against a toggle firing before the whole tab is built (as in ReloadArea). The switches
        // start off, so nothing fires during InitializeComponent — but keep the guard defensively.
        if (!IsInitialized || Viewport == null) return;
        Viewport.ShowProxyScenes = ProxyScenesToggle.IsChecked == true;
        Viewport.ShowProxyMeshes = ProxyMeshesToggle.IsChecked == true;
        Viewport.ShowSnowScenes = SnowScenesToggle.IsChecked == true;
    }
}
