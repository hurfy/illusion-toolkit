using System.Windows.Controls;
using Illusion.ViewModels;

namespace Illusion.Views;

/// <summary>
/// The Materials tab: the selected object's materials as a grid of sphere-thumbnail tiles.
/// <para>
/// Opening the material editor belongs to a window rather than to a tab, so a tile click is passed on as
/// <see cref="OpenRequested"/> for the panel's host to answer.
/// </para>
/// </summary>
public partial class MaterialsTabView : UserControl
{
    public MaterialsTabView()
    {
        InitializeComponent();
        MaterialsPanel.OpenRequested += vm => OpenRequested?.Invoke(vm);
    }

    /// <summary>A material tile was clicked — the host opens (or re-focuses) the material editor on it.</summary>
    public event Action<MaterialViewModel>? OpenRequested;
}
