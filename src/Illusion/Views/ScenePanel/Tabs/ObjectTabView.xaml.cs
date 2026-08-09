using System.Windows.Controls;

namespace Illusion.Views;

/// <summary>
/// The Object tab: a transformable frame object's own transform, the properties every frame object carries,
/// and the parent it hangs off. Markup over <see cref="ViewModels.SelectionViewModel"/> and nothing else.
/// </summary>
public partial class ObjectTabView : UserControl
{
    public ObjectTabView() => InitializeComponent();
}
