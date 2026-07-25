using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Illusion.Views;

/// <summary>
/// The project's dropdown field: ComboBox semantics (ItemsSource / SelectedItem / SelectionChanged)
/// over the property panel's Flags-row visuals. <see cref="DisplayPath"/> names the property shown
/// for each item (empty = the item's ToString). SelectionChanged is raised for programmatic changes
/// too, matching the ComboBox behavior the callers were written against.
/// </summary>
public partial class DropDownField : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(DropDownField),
        new PropertyMetadata(null, (d, _) => ((DropDownField)d).OnItemsSourceChanged()));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(object), typeof(DropDownField),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((DropDownField)d).OnSelectedItemChanged()));

    public static readonly DependencyProperty DisplayPathProperty = DependencyProperty.Register(
        nameof(DisplayPath), typeof(string), typeof(DropDownField),
        new PropertyMetadata("", (d, _) => ((DropDownField)d).OnDisplayPathChanged()));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public string DisplayPath
    {
        get => (string)GetValue(DisplayPathProperty);
        set => SetValue(DisplayPathProperty, value);
    }

    /// <summary>Raised whenever <see cref="SelectedItem"/> changes — by a user pick or by code.</summary>
    public event RoutedEventHandler? SelectionChanged;

    private bool _syncing; // the control is moving the inner list itself — ignore the echo

    public DropDownField() => InitializeComponent();

    private void OnItemsSourceChanged()
    {
        _syncing = true;
        List.ItemsSource = ItemsSource;
        List.SelectedItem = SelectedItem;
        _syncing = false;
    }

    private void OnDisplayPathChanged()
    {
        List.DisplayMemberPath = DisplayPath;
        Btn.Content = DisplayOf(SelectedItem);
    }

    private void OnSelectedItemChanged()
    {
        Btn.Content = DisplayOf(SelectedItem);
        _syncing = true;
        List.SelectedItem = SelectedItem;
        _syncing = false;
        SelectionChanged?.Invoke(this, new RoutedEventArgs());
    }

    private string DisplayOf(object? item)
    {
        if (item == null) return "";
        if (DisplayPath.Length == 0) return item.ToString() ?? "";
        return item.GetType().GetProperty(DisplayPath)?.GetValue(item)?.ToString() ?? "";
    }

    // A user pick: adopt it and close the popup (the SelectedItem callback raises SelectionChanged).
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || List.SelectedItem == null) return;
        SelectedItem = List.SelectedItem;
        Btn.IsChecked = false;
    }

    // Re-clicking the already-selected row changes no selection — still close the popup, like a combo.
    private void List_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && ItemsControl.ContainerFromElement(List, src) != null)
            Btn.IsChecked = false;
    }

    // Popup opening: land the list on the current value.
    private void Btn_Checked(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        List.SelectedItem = SelectedItem;
        _syncing = false;
        if (List.SelectedItem != null) List.ScrollIntoView(List.SelectedItem);
    }

    // An outside click closes the popup directly — untoggle the button, or the next click on it
    // would need two presses to reopen.
    private void Popup_Closed(object sender, EventArgs e) => Btn.IsChecked = false;
}
