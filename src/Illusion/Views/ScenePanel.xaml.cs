using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Assets.Adapters;
using Illusion.Domain;
using Illusion.Scene;
using Illusion.Settings;
using Illusion.ViewModels;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The right-hand half of an editor window: what is loaded (stats), how to find something in it (search), what
/// it is made of (the scene tree) and everything about the selected object (the property tabs). It knows
/// nothing about WHERE the content came from — a city district or one archive off the library shelf — so the
/// map editor and the resource editor host the same control and hand it their own viewport through
/// <see cref="Attach"/>.
/// <para>
/// Two things it deliberately does NOT do, because they belong to a window rather than to a panel: opening the
/// material editor and rolling an archive back to a backup. Both are raised as requests
/// (<see cref="MaterialEditorRequested"/>, <see cref="RestoreBackupRequested"/>) for the host to answer.
/// </para>
/// </summary>
public partial class ScenePanel : UserControl
{
    /// <summary>Height of the host window's toolbar band, so the panel's stats strip lines up with it.</summary>
    public static readonly DependencyProperty HeaderHeightProperty = DependencyProperty.Register(
        nameof(HeaderHeight), typeof(double), typeof(ScenePanel), new PropertyMetadata(double.NaN));

    private D3DImageHost _viewport = null!;
    private SelectionViewModel _selection = null!;
    private ICollectionView _groupsView = null!;

    public ScenePanel() => InitializeComponent();

    /// <summary>
    /// The collision overlay, on the Render tab rather than behind the Layers button. It draws the whole
    /// car's collision, not the selected part's: "where is this car solid" is not a question whose answer
    /// should depend on what happens to be clicked.
    /// </summary>
    private void PartShapes_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _viewport == null) return;
        _viewport.ShowPartShapes = PartShapesToggle.IsChecked == true;
    }

    // The Prefab tab's two buttons. Plain Click handlers reading the row off the DataContext, the same shape
    // the scene tree's context menu uses — a command would have to carry the row anyway.
    private void PrefabAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PrefabGroupRowsViewModel group }) group.Add();
    }

    private void PrefabRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PrefabRowViewModel row }) return;
        // A part is a handful of numbers the user cannot see from here, and dropping one is not a keystroke
        // away from being undone in the game — ask, and say what it is.
        if (AppDialog.Show(Window.GetWindow(this), new DialogOptions
            {
                Title = "Remove part",
                Heading = row.RemoveTip + "?",
                Text = "It stops being part of the car the next time the archive is built. Ctrl+Z puts it "
                     + "back exactly as it was.",
                Icon = DialogIcon.Question,
                Buttons = DialogButtons.YesCancel,
                ConfirmText = "Remove",
            }).Confirmed)
        {
            row.Remove();
        }
    }

    // What the tree is bound to — the roots themselves, or the flattened stage view. Kept so the empty state
    // can ask whether there is anything to show without caring which of the two it is.
    private ObservableCollection<SceneNode>? _shown;

    /// <summary>
    /// What the panel says when there is no hierarchy to show. The host sets it to name WHICH nothing this
    /// is — a texture on the stage has no scene to list, and saying so beats an empty box that reads as a
    /// panel which failed.
    /// <para><paramref name="always"/> says it even when a scene IS still loaded: a texture replaces the
    /// scene on the stage without unloading it, and a tree listing something you are no longer looking at
    /// is worse than a line saying what you are.</para>
    /// </summary>
    public void ShowNothing(string title, string hint, bool always = false)
    {
        EmptyTitle.Text = title;
        EmptyHint.Text = hint;
        _forceEmpty = always;
        UpdateEmptyState();
    }

    private bool _forceEmpty;

    private void UpdateEmptyState()
    {
        bool empty = _forceEmpty || _shown is not { Count: > 0 };
        EmptyScene.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SceneTree.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Brings the Prefab tab up. The content browser calls it when its PREFAB tile is opened: the tile is
    /// the obvious way in, and the tab describes the whole archive rather than a selection, so nothing else
    /// would have brought it forward.
    /// </summary>
    public void ShowPrefab()
    {
        if (PrefabTab.Visibility == Visibility.Visible) PropertyTabs.SelectedItem = PrefabTab;
    }

    /// <summary>The same for the Tuning tab, which is what an EntityDataStorage tile opens onto.</summary>
    public void ShowTuning()
    {
        if (TuningTab.Visibility == Visibility.Visible) PropertyTabs.SelectedItem = TuningTab;
    }

    /// <summary>A material tile was clicked — the host opens (or re-focuses) the material editor on it.</summary>
    public event Action<MaterialViewModel>? MaterialEditorRequested;

    /// <summary>The tree menu's rollback item was picked, scoped to the clicked row's archive (null = let the
    /// host ask which one).</summary>
    public event Action<FileInfo?>? RestoreBackupRequested;

    /// <summary>Raised after the selection changed and the panel has re-pointed itself at it — the host uses
    /// it for the chrome that lives outside this panel (the transform overlay, the Blender button).</summary>
    public event Action? SelectionShown;

    public double HeaderHeight
    {
        get => (double)GetValue(HeaderHeightProperty);
        set => SetValue(HeaderHeightProperty, value);
    }

    /// <summary>The view-model behind the property tabs. The host binds its own transform overlay to it.</summary>
    public SelectionViewModel Selection => _selection;

    /// <summary>
    /// Points the panel at the viewport whose content it reports on. Called once, by the host window's
    /// constructor — the panel is useless before it and every handler below assumes it has happened.
    /// </summary>
    public void Attach(D3DImageHost viewport)
    {
        _viewport = viewport;

        // A district is a folder of archives and that nesting is the truth of it; one archive on a stage is
        // not, and the folder / SDS / FrameResource spine would be three rows of ceremony before the first
        // thing you can click. Same nodes either way — only where the view starts differs.
        ObservableCollection<SceneNode> shown = viewport.IsMapViewport ? viewport.Roots : viewport.StageRoots;
        SceneTree.ItemsSource = shown;
        _groupsView = CollectionViewSource.GetDefaultView(shown);
        _groupsView.Filter = o => o is SceneNode n && n.HasSearchMatch;
        _shown = shown;

        _selection = new SelectionViewModel(viewport);
        PropertyTabs.DataContext = _selection;

        // A prefab pick writes the working copy the moment it is made, so the three things that follow are
        // the host's: it goes on the undo stack, the archive joins the build list, and the user is told.
        _selection.PrefabEdited += (archive, message, edit) =>
        {
            viewport.History.Push(edit);
            viewport.MarkArchiveModified(archive);
            viewport.RaiseNotice(message + " Build to write it into the archive.", isError: false);
        };

        // A tuning edit is the same deal: the number is already in the working copy, so the host records it,
        // adds the archive to the build list and says so.
        _selection.TuningEdited += (archive, message, edit) =>
        {
            viewport.History.Push(edit);
            viewport.MarkArchiveModified(archive);
            viewport.RaiseNotice(message + " Build to write it into the archive.", isError: false);
        };

        viewport.SceneChanged += () => Dispatcher.Invoke(() =>
        {
            UpdateSceneStats();
            _groupsView.Refresh();
            UpdateEmptyState();
            // The Prefab tab describes the ARCHIVE, not the selection, so it has to follow what is staged —
            // otherwise it only appears once something has been clicked, and a car that has just opened
            // shows nothing at all.
            _selection.RefreshPrefab();
            _selection.RefreshTuning();
        });
        UpdateEmptyState();

        // Selection sync: tree ⇄ viewport ⇄ property tabs. Only act on a real node — a null NewValue can come
        // from the virtualized tree recycling the selected container on scroll, and must NOT deselect
        // (clearing selection is done by a viewport empty-click / area reload).
        // Plain tree click → single-select (Ctrl+click is intercepted below and multi-selects instead).
        SceneTree.SelectedItemChanged += (_, e) => { if (e.NewValue is SceneNode n) viewport.Select(n); };
        SceneTree.PreviewMouseLeftButtonDown += SceneTree_PreviewMouseLeftButtonDown;
        SceneTree.PreviewMouseRightButtonDown += SceneTree_PreviewMouseRightButtonDown;
        // Crash rows fill in when opened — their placements are not materialised until someone looks.
        SceneTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(SceneTree_ItemExpanded));

        viewport.SelectionChanged += OnSelectionChanged;
        viewport.SelectionTransformChanged += _selection.RefreshTransform;
        viewport.SelectionPropertiesChanged += _selection.RefreshPropertyValues;

        // Materials: a tile click asks the host to open the material editor; any material edit — from either
        // window, or an undo — rebuilds the tab's tiles in place.
        MaterialsPanel.OpenRequested += vm => MaterialEditorRequested?.Invoke(vm);
        viewport.MaterialsChanged += _selection.RefreshMaterials;

        ApplyHotkeys();
        HotkeyMap.Current.Changed += ApplyHotkeys;
        Unloaded += (_, _) => HotkeyMap.Current.Changed -= ApplyHotkeys;
    }

    /// <summary>
    /// Hides the filters that only mean something in a city district. A resource on a stage has no
    /// neighbouring proxy districts and no winter twin, and a switch that does nothing is worse than no
    /// switch. Actor glyphs stay — an archive's actors are exactly as interesting off the map as on it.
    /// </summary>
    public void HideCityFilters()
    {
        ProxyScenesRow.Visibility = Visibility.Collapsed;
        ProxyMeshesRow.Visibility = Visibility.Collapsed;
        SnowScenesRow.Visibility = Visibility.Collapsed;
    }

    // The tree menu's shortcuts are display text only; the keys themselves come from the keymap, dispatched by
    // the host window. Re-read whenever a binding changes, so a rebinding lands without a restart.
    private void ApplyHotkeys()
    {
        HotkeyMap map = HotkeyMap.Current;
        TreeDuplicateItem.InputGestureText = map[HotkeyId.Duplicate].ToString();
        TreeDeleteItem.InputGestureText = map[HotkeyId.Delete].ToString();
    }

    // ── Scene tree ──

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
            if (node.Source is IFrameNode) _viewport.ToggleSelect(node);
            e.Handled = true; // suppress the TreeView's default single-selection either way
        }
        else
        {
            // Idempotent (Select's same-single guard); not marked handled, so the TreeView still manages
            // focus / expansion and re-syncs its own SelectedItem for keyboard navigation.
            _viewport.Select(node);
        }
    }

    // Right-click selects the row under the cursor first, so the context menu's Duplicate / Restore /
    // Delete acts on it; the ContextMenu itself is attached in the tree's ItemContainerStyle.
    private void SceneTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (FindAncestor<TreeViewItem>(src)?.DataContext is not SceneNode node) return;
        if (!_viewport.SelectedNodes.Contains(node)) _viewport.Select(node);
    }

    // A crash row was opened in the tree: build the nodes for its placements now.
    private void SceneTree_ItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: SceneNode { Kind: "CrashObject" } row })
        {
            _viewport.ExpandCrashRow(row);
        }
    }

    private void SceneTreeContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        // The item shows the live count and disables at zero: sweeping is never automatic (an orphaned hull may
        // be wanted back), so the menu is where a modder finds out there is anything to sweep.
        int n = _viewport.UnusedHullCount();
        TreeRemoveUnusedHullsItem.Header = n > 0 ? $"Remove unused hulls ({n})" : "Remove unused hulls";
        TreeRemoveUnusedHullsItem.IsEnabled = n > 0;

        // The rollback item follows the right-clicked row (right-click selects it, above): the header names the
        // target archive so the rollback target is readable before the dialog opens; rows that resolve to no
        // archive (folders) disable it.
        FileInfo? sds = ArchiveOf(_viewport.SelectedNode);
        TreeRestoreBackupItem.Header = sds != null ? $"Restore Backup… ({sds.Name})" : "Restore Backup…";
        TreeRestoreBackupItem.IsEnabled = sds != null && _viewport.BridgeEditedCount == 0;

        // A collision box hangs off a PART, so the item only lights up on a bone and says which one.
        string? bone = _viewport.SelectedBoneName;
        TreeAddCollisionBoxItem.Header = bone != null ? $"Add Collision Shape… ({bone})" : "Add Collision Shape…";
        TreeAddCollisionBoxItem.IsEnabled = _viewport.CanAddCollisionBox;
    }

    private void AddCollisionBox_Click(object sender, RoutedEventArgs e)
    {
        if (_viewport.SelectedBoneName is not { } bone) return;
        var dialog = new CollisionBoxWindow(_viewport.CollisionPartChoices, bone)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || dialog.Size is not { } size || dialog.Part is not { } part) return;

        // Two different things behind one dialog: a placed physics shape, or a plain box that is what its
        // type says. They are not variants — a window is type 0 on all 527 shipped ones and a body type 5 on
        // all 407 — so they take different paths from here.
        if (dialog.VolumeType is { } volumeType)
        {
            _viewport.AddCollisionZone(volumeType, size, part.Bone);
        }
        else if (dialog.Kind is { } kind)
        {
            _viewport.AddCollisionShape(kind, size, part.Bone, dialog.Surface);
        }
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e) => _viewport.DeleteSelected();

    private void DuplicateMenuItem_Click(object sender, RoutedEventArgs e) => _viewport.DuplicateSelected();

    private void RemoveUnusedHulls_Click(object sender, RoutedEventArgs e) => _viewport.RemoveUnusedHulls();

    private void TreeRestoreBackup_Click(object sender, RoutedEventArgs e) =>
        RestoreBackupRequested?.Invoke(ArchiveOf(_viewport.SelectedNode));

    /// <summary>
    /// The archive a scene-tree node belongs to: its own document, the nearest ancestor document, or — for the
    /// "Sds" wrapper row, whose documents are CHILDREN (FrameResource / Collisions) — the first child document.
    /// Null for folder rows and empty space.
    /// </summary>
    internal static FileInfo? ArchiveOf(SceneNode? node)
    {
        if (node == null) return null;
        if (node.OwningDocumentNode()?.Source is ISceneDocument owner) return owner.SourceArchive;
        foreach (SceneNode child in node.Children)
            if (child.Source is ISceneDocument doc) return doc.SourceArchive;
        return null;
    }

    // ── Selection → property tabs ──

    // Selection changed (tree click or viewport pick): feed the property tabs and surface the type's tab.
    private void OnSelectionChanged()
    {
        _selection.SetNode(_viewport.SelectedNode);

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

        // Selecting a mesh or an actor (viewport ray-pick or tree click) scrolls the hierarchy to its row —
        // an actor node carries no mesh, and without this a viewport pick left the tree where it was.
        if (_viewport.SelectedNode is { } picked && (picked.Mesh != null || picked.Source is ActorNodeAdapter))
        {
            BringNodeIntoView(picked);
        }

        SelectionShown?.Invoke();
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

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
        {
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return d as T;
    }

    // ── Search · stats · render filters ──

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility =
            string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        SceneSearch.Query = SearchBox.Text ?? "";
        bool searching = !string.IsNullOrWhiteSpace(SceneSearch.Query);

        foreach (SceneNode root in _viewport.Roots) RefreshNode(root, searching);
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
        foreach (SceneNode f in _viewport.Roots) files += f.Children.Count;
        StatFiles.Text = files.ToString("N0", CultureInfo.InvariantCulture);
        StatMeshes.Text = _viewport.MeshCount.ToString("N0", CultureInfo.InvariantCulture);
        StatPolys.Text = FormatCompact(_viewport.TriangleCount);
    }

    // Compact number for the narrow stats cell: 1.2M / 45.6K / 8,900.
    internal static string FormatCompact(long n) =>
        n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : n >= 10_000 ? (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        : n.ToString("N0", CultureInfo.InvariantCulture);

    private void SceneFilter_Changed(object sender, RoutedEventArgs e)
    {
        // The switches start off, so nothing fires during InitializeComponent — but a toggle can still fire
        // before Attach() has handed the panel its viewport.
        if (_viewport == null) return;
        _viewport.ShowProxyScenes = ProxyScenesToggle.IsChecked == true;
        _viewport.ShowProxyMeshes = ProxyMeshesToggle.IsChecked == true;
        _viewport.ShowSnowScenes = SnowScenesToggle.IsChecked == true;
        // Actor glyphs, skeletons and helpers are pure overlay — no scene reload, unlike the three filters above.
        _viewport.ShowActors = ActorsToggle.IsChecked == true;
        _viewport.ShowSkeleton = SkeletonToggle.IsChecked == true;
        _viewport.ShowHelpers = HelpersToggle.IsChecked == true;
        _viewport.ShowHitBoxes = HitBoxToggle.IsChecked == true;
        UpdateHelpersSubtitle();
        UpdateHitBoxSubtitle();
    }

    // Says how many pieces the layer found provably unshootable. The colour alone cannot say it: a red sphere
    // inside a cloud of a hundred and eighty blue ones is not something anyone spots by looking, and zero is
    // exactly as worth stating as three.
    private void UpdateHitBoxSubtitle()
    {
        if (_viewport == null) return;
        int escaped = _viewport.UnshootablePieceCount;
        HitBoxSubtitle.Text = HitBoxToggle.IsChecked != true
            ? "What a bullet has to be inside to count"
            : escaped == 0
                ? "What a bullet has to be inside — nothing found outside"
                : $"{escaped} piece{(escaped == 1 ? "" : "s")} outside its own box — cannot be shot";
    }

    // Says how many helper nodes were left out as placeholders, so "seventeen points I cannot see" is
    // answered where the switch is rather than nowhere.
    private void UpdateHelpersSubtitle()
    {
        if (_viewport == null) return;
        int hidden = _viewport.HiddenHelperCount;
        HelpersSubtitle.Text = HelpersToggle.IsChecked == true && hidden > 0
            ? $"Dummies, points, volumes — {hidden} unnamed placeholder{(hidden == 1 ? "" : "s")} left out"
            : "Dummies, points, volumes — no geometry of their own";
    }

    /// <summary>Drives the snow filter from the host's winter selector: winter geometry lives in its own
    /// scene folder, and showing it in summer is never what anyone wants. The switch stays interactive.</summary>
    public void SetSnowFilter(bool on) => SnowScenesToggle.IsChecked = on;
}
