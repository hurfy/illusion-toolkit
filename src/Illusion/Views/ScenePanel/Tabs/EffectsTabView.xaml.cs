using System.Windows;
using System.Windows.Controls;

namespace Illusion.Views;

/// <summary>
/// The Effects tab: the archive's own effects — for a car, its fire and its rain — one at a time, with each
/// generation's operators as folding bands.
/// </summary>
public partial class EffectsTabView : UserControl
{
    public EffectsTabView() => InitializeComponent();

    /// <summary>
    /// "Add a copy of this effect" was clicked. The rail answers it against the selection it already holds,
    /// rather than this tab reaching for a view-model through its DataContext: that chain is inherited and
    /// invisible, and the day something breaks it the button would quietly stop doing anything, with no
    /// error anywhere — which is the failure mode this panel exists to make impossible.
    /// </summary>
    public event Action? AddCopyRequested;

    private void AddEffectCopy_Click(object sender, RoutedEventArgs e) => AddCopyRequested?.Invoke();
}
