using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Illusion.Views;

/// <summary>
/// A row of toolbar buttons that folds into a drop-down when the toolbar has no room for it. The narrow form
/// is not a second copy of the buttons: the strip holding them is moved into the popup and back, so they keep
/// their identity, their state and their handlers, and the toolbar never shows two of each.
/// <para>
/// Buttons go in <see cref="Items"/> (property-element syntax in XAML) rather than in <c>Content</c>, which a
/// UserControl already spends on its own visual tree. Folding is decided by <see cref="ToolbarRowPanel"/> —
/// the row is what knows how much space is left over — so <see cref="IsCompact"/> is set from outside.
/// </para>
/// </summary>
public partial class CompactStrip : UserControl, ICompactable
{
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(CompactStrip),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure,
            (d, e) => ((CompactStrip)d).OnIsCompactChanged((bool)e.NewValue)));

    public CompactStrip()
    {
        InitializeComponent();

        // Same as the toolbar's other button-with-a-list: hovering is enough to see what is in it.
        HoverPopup.Attach(DropBtn, DropPopup);
    }

    /// <summary>The buttons themselves, in row order.</summary>
    public UIElementCollection Items => ButtonHost.Children;

    /// <summary>Whether the buttons are folded into the drop-down. Set by the toolbar row, not from XAML.</summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    private void OnIsCompactChanged(bool compact)
    {
        // One strip, two possible hosts: an element has to leave its parent before it can join another.
        if (compact)
        {
            Root.Children.Remove(ButtonHost);
            PopupBox.Child = ButtonHost;
            DropUi.Visibility = Visibility.Visible;
        }
        else
        {
            DropBtn.IsChecked = false;
            PopupBox.Child = null;
            if (!Root.Children.Contains(ButtonHost)) Root.Children.Insert(0, ButtonHost);
            DropUi.Visibility = Visibility.Collapsed;
        }

        // The row switches forms in the middle of its own measure and measures this group again right away, so
        // the new width has to be there on the spot. Measure() short-circuits on any element that is not marked
        // dirty, and a host swap marks only the grid it happened in — not the presenter WPF puts between a
        // UserControl and its content. Mark the whole chain, or the group answers with the width of the form it
        // just left, and the row lays the toolbar out around a group that is no longer that wide.
        for (DependencyObject? node = Root; node is not null && !ReferenceEquals(node, this);
             node = VisualTreeHelper.GetParent(node))
        {
            (node as UIElement)?.InvalidateMeasure();
        }
        InvalidateMeasure();
    }

    // A pick closes the drop-down, the way a menu does — the buttons themselves handle what was picked.
    private void PopupBox_MouseUp(object sender, MouseButtonEventArgs e) => DropBtn.IsChecked = false;

    // An outside click closes the popup directly — untoggle the button, or reopening it would take two presses.
    private void Popup_Closed(object sender, EventArgs e) => DropBtn.IsChecked = false;
}
