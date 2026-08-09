using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Domain;
using Illusion.Scene;
using Illusion.Settings;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The hierarchy half of the scene panel: what the loaded content is made of, one row per node, with the
/// menu that acts on the clicked row and the placard that takes the tree's place when there is nothing to
/// list.
/// <para>
/// It drives the viewport's selection and reads its tree; it does not know what a property tab is. Rolling
/// an archive back to a backup belongs to a window rather than to a tree, so the menu item raises
/// <see cref="RestoreBackupRequested"/> for the host to answer.
/// </para>
/// </summary>
public partial class SceneTreeView : UserControl
{
    private D3DImageHost _viewport = null!;

    // Null until Attach: the tree is a control of its own now, so Refresh / ApplySearch can be reached
    // before the panel has handed it a viewport, and a filter that does not exist yet is not an error.
    private ICollectionView? _groupsView;

    // What the tree is bound to — the roots themselves, or the flattened stage view. Kept so the empty state
    // can ask whether there is anything to show without caring which of the two it is.
    private ObservableCollection<SceneNode>? _shown;
    private bool _forceEmpty;

    public SceneTreeView() => InitializeComponent();

    /// <summary>The menu's rollback item was picked, scoped to the clicked row's archive (null = let the
    /// host ask which one).</summary>
    public event Action<FileInfo?>? RestoreBackupRequested;

    /// <summary>
    /// Points the tree at the viewport whose content it lists. Called once, by the scene panel's own
    /// <c>Attach</c> — every handler below assumes it has happened.
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
        UpdateEmptyState();

        // Selection sync: tree ⇄ viewport. Only act on a real node — a null NewValue can come from the
        // virtualized tree recycling the selected container on scroll, and must NOT deselect (clearing
        // selection is done by a viewport empty-click / area reload).
        // Plain tree click → single-select (Ctrl+click is intercepted below and multi-selects instead).
        SceneTree.SelectedItemChanged += (_, e) => { if (e.NewValue is SceneNode n) viewport.Select(n); };
        SceneTree.PreviewMouseLeftButtonDown += SceneTree_PreviewMouseLeftButtonDown;
        SceneTree.PreviewMouseRightButtonDown += SceneTree_PreviewMouseRightButtonDown;
        // Crash rows fill in when opened — their placements are not materialised until someone looks.
        SceneTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(SceneTree_ItemExpanded));

        ApplyHotkeys();
        HotkeyMap.Current.Changed += ApplyHotkeys;
        Unloaded += (_, _) => HotkeyMap.Current.Changed -= ApplyHotkeys;
    }

    /// <summary>The staged content changed: re-run the search filter over the new roots and say whether
    /// there is anything left to list.</summary>
    public void Refresh()
    {
        _groupsView?.Refresh();
        UpdateEmptyState();
    }

    /// <summary>
    /// What the tree says when there is no hierarchy to show. The host sets it to name WHICH nothing this
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

    private void UpdateEmptyState()
    {
        bool empty = _forceEmpty || _shown is not { Count: > 0 };
        EmptyScene.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SceneTree.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Narrows the tree to the rows matching <paramref name="query"/>, opening every branch that
    /// holds a match. An empty query puts every row back.</summary>
    public void ApplySearch(string query)
    {
        SceneSearch.Query = query;
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

    // The menu's shortcuts are display text only; the keys themselves come from the keymap, dispatched by
    // the host window. Re-read whenever a binding changes, so a rebinding lands without a restart.
    private void ApplyHotkeys()
    {
        HotkeyMap map = HotkeyMap.Current;
        TreeDuplicateItem.InputGestureText = map[HotkeyId.Duplicate].ToString();
        TreeDeleteItem.InputGestureText = map[HotkeyId.Delete].ToString();
    }

    // ── Clicks ──

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

    // ── Scrolling a row into view ──

    /// <summary>
    /// Scrolls the tree to a node's row, realizing it through the virtualized panels — WPF's TreeView does not
    /// auto-scroll to a programmatically-selected item. Ancestors are already expanded by Viewport.Select; here
    /// we walk root→node, force each level's container to generate, and bring the final row into view.
    /// Best-effort: any virtualization quirk leaves the ancestors expanded and the row selected, just unscrolled.
    /// </summary>
    public void BringNodeIntoView(SceneNode target)
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
}
