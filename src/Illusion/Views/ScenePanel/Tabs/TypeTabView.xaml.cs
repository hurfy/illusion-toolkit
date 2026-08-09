using System.Windows.Controls;

namespace Illusion.Views;

/// <summary>
/// The per-type property tab: whatever the selected object's own type contributes (a mesh's blocks, a
/// light's cone). One tab whose header and rows both come from the selection.
/// </summary>
public partial class TypeTabView : UserControl
{
    public TypeTabView() => InitializeComponent();
}
