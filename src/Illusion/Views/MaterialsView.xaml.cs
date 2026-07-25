using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Illusion.Views;

/// <summary>
/// Renders a mesh's materials as a 2×N grid of tiles — per material a sphere thumbnail, the resolved dot and
/// the name. Clicking a tile raises <see cref="OpenRequested"/> with the tile's view-model; the main window
/// opens the material editor there. Bound to a list of <see cref="ViewModels.MaterialViewModel"/> via
/// <see cref="Materials"/>.
/// </summary>
public partial class MaterialsView : UserControl
{
    public MaterialsView() => InitializeComponent();

    /// <summary>The materials to render (a list of <see cref="ViewModels.MaterialViewModel"/>).</summary>
    public static readonly DependencyProperty MaterialsProperty = DependencyProperty.Register(
        nameof(Materials), typeof(IEnumerable), typeof(MaterialsView), new PropertyMetadata(null));

    public IEnumerable? Materials
    {
        get => (IEnumerable?)GetValue(MaterialsProperty);
        set => SetValue(MaterialsProperty, value);
    }

    /// <summary>A tile was clicked — the owner opens the material editor for this material/slot.</summary>
    public event Action<ViewModels.MaterialViewModel>? OpenRequested;

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ViewModels.MaterialViewModel vm) return;
        e.Handled = true;
        OpenRequested?.Invoke(vm);
    }
}
