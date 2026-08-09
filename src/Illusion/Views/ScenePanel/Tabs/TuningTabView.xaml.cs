using System.Windows.Controls;

namespace Illusion.Views;

/// <summary>
/// The Tuning tab: the entity-data tables the archive carries — for a car, how it drives. Pure markup over
/// <see cref="ViewModels.SelectionViewModel"/>; every edit commits through the row it is bound to, so there
/// is nothing here for a handler to do.
/// </summary>
public partial class TuningTabView : UserControl
{
    public TuningTabView() => InitializeComponent();
}
