using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.ViewModels;

namespace Illusion.Views;

/// <summary>
/// The component half of the scene panel's hierarchy: the car as the thing a modder authored — a door, a
/// bumper, a licence plate — in the place the frame tree occupies on everything that is not a car.
///
/// <para>
/// It is a view over <see cref="ComponentTreeViewModel"/> and knows nothing about the car itself: a click
/// asks the view-model to select the row, and the view-model resolves that to the bone the viewport draws.
/// The <c>Components | Raw</c> switch that puts the frame tree back is the panel's, not this control's.
/// </para>
/// <para>
/// Read-only. Nothing here edits a car.
/// </para>
/// </summary>
public partial class ComponentTreeView : UserControl
{
    private ComponentTreeViewModel? _components;

    public ComponentTreeView() => InitializeComponent();

    /// <summary>
    /// Points the tree at the car it lists. Called once, by the scene panel's own <c>Attach</c> — every
    /// handler below assumes it has happened.
    /// </summary>
    public void Attach(ComponentTreeViewModel components)
    {
        ArgumentNullException.ThrowIfNull(components);
        _components = components;
        components.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ComponentTreeViewModel.RootsView)) Rebind();
        };
        // A row selected from the viewport has to be scrollable to, not merely highlighted somewhere below
        // the fold — the same courtesy the frame tree does a viewport pick.
        components.SelectionShown += BringRowIntoView;
        Rebind();

        ComponentTree.PreviewMouseLeftButtonDown += ComponentTree_PreviewMouseLeftButtonDown;
        ComponentTree.PreviewMouseRightButtonDown += ComponentTree_PreviewMouseRightButtonDown;
        // Arrow keys and type-ahead move the TreeView's own selection; without this they would move it
        // silently — the highlight is the row view-model's, not the TreeViewItem's. Only a real row acts: a
        // null NewValue comes from the virtualized tree recycling a container on scroll, and must not
        // deselect. Idempotent against the click above, which routes through the same call.
        ComponentTree.SelectedItemChanged += (_, e) =>
        {
            if (e.NewValue is ComponentRowViewModel row) components.Select(row);
        };
    }

    /// <summary>Re-runs the search filter over the rows and says whether there is anything left to list.</summary>
    public void Refresh()
    {
        _components?.ApplySearch();
        UpdateEmptyState();
    }

    private void Rebind()
    {
        ComponentTree.ItemsSource = _components?.RootsView;
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool empty = ComponentTree.Items.Count == 0;
        EmptyComponents.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ComponentTree.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    // A click selects the component under it — through the view-model, which hands the bone to the viewport
    // and lets the selection come back the way a viewport pick does. Not marked handled, so the TreeView
    // still manages focus, expansion and its own keyboard navigation.
    private void ComponentTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) != null) return;
        if (FindAncestor<TreeViewItem>(source)?.DataContext is not ComponentRowViewModel row) return;
        _components?.Select(row);
    }

    // Right-click selects the row under the cursor first, so the menu acts on it.
    private void ComponentTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<TreeViewItem>(source)?.DataContext is not ComponentRowViewModel row) return;
        if (!ReferenceEquals(_components?.Selected, row)) _components?.Select(row);
    }

    /// <summary>
    /// The component tree edits nothing yet, so the one thing its menu can offer is the way BACK: everything
    /// that acts on a row — adding a collision shape, sweeping unused hulls, rolling the archive back —
    /// lives on the frame tree's own menu, and a car opening on its components would otherwise hide the only
    /// path to them behind a switch nobody has been told about.
    /// </summary>
    public event Action? ShowInRawRequested;

    private void ShowInRaw_Click(object sender, RoutedEventArgs e) => ShowInRawRequested?.Invoke();

    /// <summary>
    /// Scrolls a row into view, realizing it through the virtualized panels — WPF's TreeView does not
    /// auto-scroll to a row selected in code. Best-effort: a virtualization quirk leaves the row selected and
    /// its branch open, just unscrolled.
    /// </summary>
    private void BringRowIntoView(ComponentRowViewModel target)
    {
        var path = new List<ComponentRowViewModel>();
        for (ComponentRowViewModel? at = target; at != null; at = at.Parent) path.Insert(0, at);

        // Deferred so the pending IsExpanded / IsSelected bindings and the layout settle first.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                ItemsControl parent = ComponentTree;
                for (int i = 0; i < path.Count; i++)
                {
                    if (Realize(parent, path[i]) is not TreeViewItem item) return;
                    if (i == path.Count - 1)
                    {
                        item.BringIntoView();
                    }
                    else
                    {
                        item.IsExpanded = true;
                        item.UpdateLayout();
                        parent = item;
                    }
                }
            }
            catch { /* virtualization quirk — the scroll is a best-effort nicety */ }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // The (possibly virtualized) TreeViewItem for one child row, forcing generation via the items-host panel.
    private static TreeViewItem? Realize(ItemsControl parent, object item)
    {
        parent.ApplyTemplate();
        parent.UpdateLayout();
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem found) return found;

        int index = parent.Items.IndexOf(item);
        if (index < 0) return null;
        if (FindItemsHost(parent) is VirtualizingPanel panel)
        {
            BringIndexIntoView(panel, index);
            parent.UpdateLayout();
        }
        return parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
    }

    private static Panel? FindItemsHost(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is Panel panel && panel.IsItemsHost) return panel;
            if (FindItemsHost(child) is Panel found) return found;
        }
        return null;
    }

    // VirtualizingPanel.BringIndexIntoView is protected — reflection is the only way to realize an
    // out-of-view row from outside the panel. Cached; null if the runtime ever renames it (the scroll then
    // silently degrades, which is what the frame tree's own copy of this does).
    private static readonly System.Reflection.MethodInfo? BringIndexIntoViewMethod =
        typeof(VirtualizingPanel).GetMethod("BringIndexIntoView",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [typeof(int)], null);

    private static void BringIndexIntoView(VirtualizingPanel panel, int index) =>
        BringIndexIntoViewMethod?.Invoke(panel, [index]);

    private static T? FindAncestor<T>(DependencyObject? at) where T : DependencyObject
    {
        while (at != null && at is not T)
        {
            at = at is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(at)
                : LogicalTreeHelper.GetParent(at);
        }
        return at as T;
    }
}
