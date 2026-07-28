using System.Windows;
using System.Windows.Controls;

namespace Illusion.Views;

/// <summary>
/// Picks which city_crash prop to place. The list is the loaded archive's own Translokator table, filtered to the
/// rows that resolve to real geometry — an archive cannot spawn anything else, so there is nothing else to offer.
/// The caller does the placing (<c>TranslokatorEditController.PlaceObject</c>); this only reports the choice.
/// </summary>
public sealed partial class CrashObjectWindow : Window
{
    /// <summary>One offer in the list: a table row plus the columns shown beside it.</summary>
    public sealed record Choice(string Name, int Count, float Distance, object Row)
    {
        public string CountText => Count == 1 ? "1 copy" : $"{Count} copies";
        public string DistanceText => $"{Distance:0} m";
    }

    private readonly List<Choice> _all;

    public CrashObjectWindow(IReadOnlyList<Choice> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        InitializeComponent();
        _all = [.. objects];
        Apply(string.Empty);
    }

    /// <summary>The chosen row, or null when the dialog was cancelled.</summary>
    public object? SelectedRow { get; private set; }

    /// <summary>Whether the copy should also go into the other season's archive.</summary>
    public bool BothSeasons => BothSeasonsBox.IsChecked == true;

    /// <summary>Whether a seasonal twin exists at all; without one the switch is meaningless.</summary>
    public void SetSeasonalSwitchAvailable(bool available)
    {
        BothSeasonsBox.IsEnabled = available;
        if (!available)
        {
            BothSeasonsBox.IsChecked = false;
            BothSeasonsBox.ToolTip = "This archive has no seasonal counterpart to place into.";
        }
    }

    private void Apply(string filter)
    {
        List<Choice> shown = filter.Length == 0
            ? _all
            : _all.FindAll(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        ObjectList.ItemsSource = shown;
        EmptyLabel.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => Apply(SearchBox.Text ?? string.Empty);

    private void ObjectList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PlaceBtn.IsEnabled = ObjectList.SelectedItem is Choice;

    private void ObjectList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ObjectList.SelectedItem is Choice) Place_Click(sender, e);
    }

    private void Place_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectList.SelectedItem is not Choice choice) return;
        SelectedRow = choice.Row;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
