using System.Windows;
using System.Windows.Controls;
using Illusion.ViewModels;

namespace Illusion.Views;

/// <summary>
/// The Effects tab: the archive's own effects — for a car, its fire and its rain — one at a time, with each
/// generation's operators as folding bands.
/// </summary>
public partial class EffectsTabView : UserControl
{
    public EffectsTabView() => InitializeComponent();

    private void AddEffectCopy_Click(object sender, RoutedEventArgs e) =>
        (DataContext as SelectionViewModel)?.AddEffectCopy();
}
