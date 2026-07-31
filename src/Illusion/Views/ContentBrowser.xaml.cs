using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Assets.Library;

namespace Illusion.Views;

/// <summary>
/// The resource library's navigator: the folder tree on the left, that folder's sub-folders and archives on
/// the right, and a search box that queries the whole game rather than the open folder. A single click
/// selects; a double click walks into a folder or puts an archive on the stage
/// (<see cref="EntryActivated"/>).
/// <para>
/// The control is a strip plus a body. Collapsing hides the body and leaves the strip, which is what keeps a
/// 1280x720 window usable — the viewport's height is the scarce resource, and the browser lives at the
/// bottom of it. The host owns the row height; the browser only says which form it is in
/// (<see cref="IsCollapsed"/> / <see cref="CollapsedChanged"/>).
/// </para>
/// </summary>
public partial class ContentBrowser : UserControl
{
    public static readonly DependencyProperty IsSearchingProperty = DependencyProperty.Register(
        nameof(IsSearching), typeof(bool), typeof(ContentBrowser), new PropertyMetadata(false));

    private LibraryCatalog? _catalog;
    private LibraryFolder? _folder;

    public ContentBrowser()
    {
        InitializeComponent();
        UpdateEmptyState();
    }

    /// <summary>An archive the user asked for by name — double-clicked, or picked and confirmed. The host
    /// decides what that means (in Library mode: load it onto the stage).</summary>
    public event Action<LibraryEntry>? EntryActivated;

    /// <summary>The browser was folded away or opened again — the host re-sizes its row.</summary>
    public event Action? CollapsedChanged;

    /// <summary>Whether only the strip is showing. Driven by the strip's own chevron; settable so the host can
    /// restore the last state.</summary>
    public bool IsCollapsed
    {
        get => CollapseBtn.IsChecked != true;
        set => CollapseBtn.IsChecked = !value;
    }

    /// <summary>True while the contents pane is showing search hits from the whole game rather than one
    /// folder's content. Bound by the card template, which only names a hit's folder when it is a hit.</summary>
    public bool IsSearching
    {
        get => (bool)GetValue(IsSearchingProperty);
        private set => SetValue(IsSearchingProperty, value);
    }

    /// <summary>The archive card currently picked, if the selected row is one (a folder row is not).</summary>
    public LibraryEntry? SelectedEntry => Contents.SelectedItem as LibraryEntry;

    /// <summary>Points the browser at a game install. Passing null empties it — which is the state before a
    /// game path has been picked, not an error.</summary>
    public void SetCatalog(LibraryCatalog? catalog)
    {
        _catalog = catalog;
        SearchBox.Text = "";
        FolderTree.ItemsSource = catalog?.Roots;
        _folder = null;
        Contents.ItemsSource = null;

        // Open on the first category rather than on nothing: the browser's whole point is that content is
        // reachable without hunting for it. Through OpenFolder, so the tree row is highlighted too — a pane
        // showing one folder while the tree highlights none reads as a bug.
        if (catalog is { Roots.Count: > 0 }) OpenFolder(catalog.Roots[0]);
        UpdateEmptyState();
    }

    /// <summary>
    /// Reveals one archive: expands the tree to the folder holding it, opens that folder and picks the card.
    /// This is what the map's "Open in library" jump lands on, and how the stage's archive is re-picked after
    /// the catalog is rebuilt.
    /// </summary>
    public bool Reveal(LibraryEntry entry)
    {
        if (_catalog == null) return false;
        foreach (LibraryFolder root in _catalog.Roots)
        {
            var path = new List<LibraryFolder>();
            if (!FindPath(root, entry, path)) continue;
            IsCollapsed = false;
            SearchBox.Text = "";
            ExpandPath(path);
            OpenFolder(path[^1]);     // moves the tree too — the card alone would leave the panes disagreeing
            Contents.SelectedItem = entry;
            Contents.ScrollIntoView(entry);
            return true;
        }
        return false;
    }

    // Depth-first walk down to the folder that directly holds the archive; `path` comes back as the chain of
    // folders from the root to it, which is what the tree has to expand.
    private static bool FindPath(LibraryFolder folder, LibraryEntry entry, List<LibraryFolder> path)
    {
        path.Add(folder);
        if (folder.Entries.Contains(entry)) return true;
        foreach (LibraryFolder child in folder.Folders)
        {
            if (FindPath(child, entry, path)) return true;
        }
        path.RemoveAt(path.Count - 1);
        return false;
    }

    // Expands each level in turn, forcing the next level's containers into existence as it goes — the tree is
    // virtualized, so an unexpanded branch has no TreeViewItem to expand yet.
    private void ExpandPath(IReadOnlyList<LibraryFolder> path)
    {
        ItemsControl level = FolderTree;
        foreach (LibraryFolder folder in path)
        {
            level.UpdateLayout();
            if (level.ItemContainerGenerator.ContainerFromItem(folder) is not TreeViewItem item) return;
            item.IsExpanded = true;
            item.BringIntoView();
            level = item;
        }
    }

    private void SelectFolder(LibraryFolder folder)
    {
        _folder = folder;
        if (!IsSearching) ShowFolder(folder);
    }

    private void ShowFolder(LibraryFolder folder)
    {
        // Sub-folders above archives: a category that only gathers folders (Characters) would otherwise open
        // onto an empty pane, and walking into one is a double click either way.
        var rows = new List<object>(folder.Folders.Count + folder.Entries.Count);
        rows.AddRange(folder.Folders);
        rows.AddRange(folder.Entries);
        Contents.ItemsSource = rows;
        PathText.Text = folder.Path + $"  ·  {folder.TotalEntries} archives";
        UpdateEmptyState();
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is LibraryFolder folder) SelectFolder(folder);
    }

    private void Contents_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The double click has to have landed on a row: the empty area below the last one also raises it.
        if (!(e.OriginalSource is DependencyObject src && FindRow(src) != null)) return;

        switch (Contents.SelectedItem)
        {
            case LibraryFolder folder:
                SearchBox.Text = "";      // walking into a folder leaves the search behind
                OpenFolder(folder);
                break;
            case LibraryEntry entry:
                EntryActivated?.Invoke(entry);
                break;
        }
    }

    private static ListBoxItem? FindRow(DependencyObject? node)
    {
        while (node != null && node is not ListBoxItem)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return node as ListBoxItem;
    }

    /// <summary>
    /// Navigates to a folder: opens it in the contents pane AND moves the tree's selection onto it, because
    /// two panes disagreeing about where you are is worse than either being wrong. This is what a double
    /// click on a folder row does.
    /// </summary>
    internal void OpenFolder(LibraryFolder folder)
    {
        if (_catalog == null) { SelectFolder(folder); return; }
        foreach (LibraryFolder root in _catalog.Roots)
        {
            var path = new List<LibraryFolder>();
            if (!FindFolderPath(root, folder, path)) continue;
            ExpandPath(path);
            if (Container(path) is { } item) { item.IsSelected = true; return; }
            break;
        }
        SelectFolder(folder); // not reachable from a root (should not happen): still show it
    }

    private static bool FindFolderPath(LibraryFolder folder, LibraryFolder target, List<LibraryFolder> path)
    {
        path.Add(folder);
        if (ReferenceEquals(folder, target)) return true;
        foreach (LibraryFolder child in folder.Folders)
        {
            if (FindFolderPath(child, target, path)) return true;
        }
        path.RemoveAt(path.Count - 1);
        return false;
    }

    private TreeViewItem? Container(IReadOnlyList<LibraryFolder> path)
    {
        ItemsControl level = FolderTree;
        TreeViewItem? item = null;
        foreach (LibraryFolder folder in path)
        {
            level.UpdateLayout();
            item = level.ItemContainerGenerator.ContainerFromItem(folder) as TreeViewItem;
            if (item == null) return null;
            level = item;
        }
        return item;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        IsSearching = query.Length > 0;

        if (!IsSearching)
        {
            if (_folder != null) ShowFolder(_folder);
            else { Contents.ItemsSource = null; PathText.Text = ""; UpdateEmptyState(); }
            return;
        }

        // The search is over the flat index, not the open folder — finding a car you cannot place in the tree
        // is the reason the box is there.
        var hits = new List<object>();
        if (_catalog != null)
        {
            foreach (LibraryEntry entry in _catalog.AllEntries)
                if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) hits.Add(entry);
        }
        Contents.ItemsSource = hits;
        PathText.Text = $"Search “{query}”  ·  {hits.Count} of {_catalog?.AllEntries.Count ?? 0} archives";
        UpdateEmptyState();
    }

    private void Collapse_Changed(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" in XAML fires this during InitializeComponent, before the body exists.
        if (Body == null) return;
        Body.Visibility = IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedChanged?.Invoke();
    }

    private void UpdateEmptyState()
    {
        int count = (Contents.ItemsSource as ICollection<object>)?.Count ?? 0;
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _catalog == null ? "No game folder yet"
            : IsSearching ? "Nothing matches"
            : _folder == null ? "Pick a folder on the left"
            : "This folder holds no archives";
    }
}

/// <summary>Byte count as a short human size ("1.2 MB") for the archive cards. Sizes there are a hint at what
/// an archive is, not a figure to compute with, so one decimal is enough.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes) return "";
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
