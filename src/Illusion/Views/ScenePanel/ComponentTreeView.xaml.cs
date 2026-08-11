using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Assets.Cars;
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
            else if (e.NewValue is IComponentChildRow child) components.Select(child);
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
        Pick(FindAncestor<TreeViewItem>(source)?.DataContext);
    }

    // Right-click selects the row under the cursor first, so the menu acts on it.
    private void ComponentTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        object? clicked = FindAncestor<TreeViewItem>(source)?.DataContext;
        if (clicked is ComponentRowViewModel row && ReferenceEquals(_components?.Selected, row)
            && _components?.SelectedChild == null)
        {
            return;
        }
        Pick(clicked);
    }

    // One row, whichever kind it is. A child row points the menu at itself and the viewport at the frame it
    // is — a marker's own Dummy or Point, and for a collision, which is no frame at all, the bone of the
    // component that carries it.
    private void Pick(object? row)
    {
        switch (row)
        {
            case ComponentRowViewModel component: _components?.Select(component); break;
            case IComponentChildRow child: _components?.Select(child); break;
        }
    }

    /// <summary>
    /// The way BACK: everything that acts on a row and is not a component's own — sweeping unused hulls,
    /// rolling the archive back — lives on the frame tree's own menu, and a car opening on its components
    /// would otherwise hide the only path to them behind a switch nobody has been told about.
    /// </summary>
    public event Action? ShowInRawRequested;

    private void ShowInRaw_Click(object sender, RoutedEventArgs e) => ShowInRawRequested?.Invoke();

    /// <summary>A bare component asked to be made damageable — the host puts the question and applies it.</summary>
    public event Action<ComponentRowViewModel>? GrantPartRequested;

    /// <summary>A component asked for its deform part to be taken away, demoting it back to bare.</summary>
    public event Action<ComponentRowViewModel>? RemovePartRequested;

    /// <summary>A component was asked for one more collision — the host puts the question and applies it.</summary>
    public event Action<ComponentRowViewModel>? AddCollisionRequested;

    /// <summary>A collision was asked for a new size and position.</summary>
    public event Action<CollisionRowViewModel>? EditCollisionRequested;

    /// <summary>A collision was asked to go.</summary>
    public event Action<CollisionRowViewModel>? RemoveCollisionRequested;

    /// <summary>A component was asked for one more marker of a role.</summary>
    public event Action<ComponentRowViewModel, CarMarkerRole>? AddMarkerRequested;

    /// <summary>A marker was asked for new values on its own row.</summary>
    public event Action<MarkerRowViewModel>? EditMarkerRequested;

    /// <summary>A marker was asked to go.</summary>
    public event Action<MarkerRowViewModel>? RemoveMarkerRequested;

    /// <summary>One of a component's own prefab rows was asked for new values.</summary>
    public event Action<ComponentDataRowViewModel>? EditDataRowRequested;

    /// <summary>A component's own damage parameters were asked for new values.</summary>
    public event Action<ComponentDamageRowViewModel>? EditDamageRequested;

    /// <summary>One of its deform handles was asked for new crumple parameters.</summary>
    public event Action<ComponentHandleRowViewModel>? EditHandleRequested;

    // Which items the menu offers depends on what is under the cursor: a component can be given a collision
    // or a marker, and each of its child rows can be edited or taken away. An item that is shown while it
    // cannot do anything is a promise the menu does not keep — a read-only collision (a cooked hull) can only
    // be removed, and four of the six marker roles carry nothing of their own to type.
    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        CollisionRowViewModel? collision = _components?.SelectedCollision;
        MarkerRowViewModel? marker = _components?.SelectedMarker;
        ComponentDataRowViewModel? data = _components?.SelectedDataRow;
        // A marker GROUP is a heading rather than a thing: nothing can be done TO it, and everything that can
        // be done on the row it heads is the component's. So it offers what the component offers — without
        // which right-clicking "Climb boxes (4)" produced a menu holding one item, on exactly the row where
        // adding a climb box is the obvious thing to want.
        //
        // The row that says a component does not crumple is the same case for the same reason: it is a
        // statement about the component and carries nothing of its own, so right-clicking it would otherwise
        // open a menu in which nothing at all can be done.
        bool onComponent = _components?.Selected != null
            || _components?.SelectedChild is MarkerGroupRowViewModel
            || _components?.SelectedChild is ComponentHandleRowViewModel { HasFields: false };

        // A component either has a deform part or it does not, so exactly one of the two items is ever
        // offered — a bare one the grant, and one with a part the way back out of it. The body is the
        // exception on the removing side: every car has exactly one, and every marker whose bone no
        // component owns hangs off it, so it is shown with the reason rather than silently missing.
        ComponentRowViewModel? component = Component();
        bool bare = component is { IsBare: true };
        GrantPartItem.Visibility = onComponent && bare ? Visibility.Visible : Visibility.Collapsed;
        RemovePartItem.Visibility = onComponent && !bare && component != null
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemovePartItem.IsEnabled = component is { IsBody: false };
        RemovePartItem.ToolTip = component is { IsBody: true }
            ? $"\"{component.Name}\" is this car's body. Every shipped car has exactly one, and the markers "
                + "of every bone no component owns hang off it."
            : null;

        AddCollisionItem.Visibility = onComponent ? Visibility.Visible : Visibility.Collapsed;
        AddMarkerItem.Visibility = onComponent ? Visibility.Visible : Visibility.Collapsed;
        EditCollisionItem.Visibility = collision != null ? Visibility.Visible : Visibility.Collapsed;
        RemoveCollisionItem.Visibility = collision != null ? Visibility.Visible : Visibility.Collapsed;
        EditCollisionItem.IsEnabled = collision is { IsReadOnly: false };
        EditCollisionItem.ToolTip = collision?.ReadOnlyReason;

        EditMarkerItem.Visibility = marker != null ? Visibility.Visible : Visibility.Collapsed;
        RemoveMarkerItem.Visibility = marker != null ? Visibility.Visible : Visibility.Collapsed;
        EditMarkerItem.IsEnabled = marker is { HasFields: true };
        EditMarkerItem.ToolTip = marker is { HasFields: false }
            ? $"{marker.Label} is placed by its frame and carries nothing else to type."
            : null;
        EditDataRowItem.Visibility = data != null ? Visibility.Visible : Visibility.Collapsed;
        EditDataRowItem.Header = data == null ? "Row…" : data.Label + "…";

        ComponentDamageRowViewModel? damage = _components?.SelectedDamage;
        ComponentHandleRowViewModel? handle = _components?.SelectedHandle;
        EditDamageItem.Visibility = damage != null ? Visibility.Visible : Visibility.Collapsed;
        EditHandleItem.Visibility = handle != null ? Visibility.Visible : Visibility.Collapsed;
        // The row that says a component does not crumple has nothing to type. It is shown greyed rather than
        // hidden, because "nothing can be done here" is the answer the row itself is making.
        EditHandleItem.IsEnabled = handle is { HasFields: true };
        EditHandleItem.ToolTip = handle is { HasFields: false }
            ? $"\"{handle.Component.Name}\" has no deform handle, so there is nothing to tune. Adding one "
                + "means adding a deform_ bone in Blender."
            : null;

        if (AddMarkerItem.Items.Count == 0) FillAddMarker();
    }

    // The roles a marker can be added as, built once: the four whose frame the toolkit can mint. The two it
    // cannot are deliberately absent rather than shown greyed — a wiper is a bone on all 148 shipped ones and
    // a light is a slot rather than a row, and neither is a thing this menu can do anything about.
    private void FillAddMarker()
    {
        foreach (CarMarkerRole role in Car.AddableRoles)
        {
            var item = new MenuItem { Header = Capitalized(Car.Words(role)), Tag = role };
            item.Click += AddMarker_Click;
            AddMarkerItem.Items.Add(item);
        }
    }

    private static string Capitalized(string words) =>
        words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];

    private void AddMarker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: CarMarkerRole role } && Component() is { } row)
        {
            AddMarkerRequested?.Invoke(row, role);
        }
    }

    private void EditMarker_Click(object sender, RoutedEventArgs e)
    {
        if (_components?.SelectedMarker is { } row) EditMarkerRequested?.Invoke(row);
    }

    private void RemoveMarker_Click(object sender, RoutedEventArgs e)
    {
        if (_components?.SelectedMarker is { } row) RemoveMarkerRequested?.Invoke(row);
    }

    private void EditDataRow_Click(object sender, RoutedEventArgs e)
    {
        if (_components?.SelectedDataRow is { } row) EditDataRowRequested?.Invoke(row);
    }

    private void EditDamage_Click(object sender, RoutedEventArgs e)
    {
        if (_components?.SelectedDamage is { } row) EditDamageRequested?.Invoke(row);
    }

    private void EditHandle_Click(object sender, RoutedEventArgs e)
    {
        if (_components?.SelectedHandle is { Handle: not null } row) EditHandleRequested?.Invoke(row);
    }

    private void AddCollision_Click(object sender, RoutedEventArgs e)
    {
        if (Component() is { } row) AddCollisionRequested?.Invoke(row);
    }

    private void GrantPart_Click(object sender, RoutedEventArgs e)
    {
        if (Component() is { IsBare: true } row) GrantPartRequested?.Invoke(row);
    }

    private void RemovePart_Click(object sender, RoutedEventArgs e)
    {
        if (Component() is { IsBare: false } row) RemovePartRequested?.Invoke(row);
    }

    /// <summary>The component the menu acts on: the selected row, or — on one of the rows that is a statement
    /// ABOUT a component rather than a thing of its own — the component it is a statement about.</summary>
    private ComponentRowViewModel? Component() =>
        _components?.Selected
        ?? (_components?.SelectedChild as MarkerGroupRowViewModel)?.Component
        ?? (_components?.SelectedChild as ComponentHandleRowViewModel) switch
        {
            { HasFields: false } row => row.Component,
            _ => null,
        };

    private void EditCollision_Click(object sender, RoutedEventArgs e)
    {
        if (_components?.SelectedCollision is { } row) EditCollisionRequested?.Invoke(row);
    }

    private void RemoveCollision_Click(object sender, RoutedEventArgs e)
    {
        if (_components?.SelectedCollision is { } row) RemoveCollisionRequested?.Invoke(row);
    }

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
