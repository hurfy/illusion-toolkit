using System.Windows;
using System.Windows.Controls;
using Illusion.ViewModels;

namespace Illusion.Views;

/// <summary>
/// The Prefab tab: how the archive's actor is assembled out of its own frames — the bands of parts, the
/// frames each names, and the dangling references among them.
/// </summary>
public partial class PrefabTabView : UserControl
{
    public PrefabTabView() => InitializeComponent();

    // The tab's two buttons. Plain Click handlers reading the row off the DataContext, the same shape the
    // scene tree's context menu uses — a command would have to carry the row anyway.
    private void PrefabAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PrefabGroupRowsViewModel group }) group.Add();
    }

    private void PrefabRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PrefabRowViewModel row }) return;
        // A part is a handful of numbers the user cannot see from here, and dropping one is not a keystroke
        // away from being undone in the game — ask, and say what it is.
        if (AppDialog.Show(Window.GetWindow(this), new DialogOptions
            {
                Title = "Remove part",
                Heading = row.RemoveTip + "?",
                Text = "It stops being part of the car the next time the archive is built. Ctrl+Z puts it "
                     + "back exactly as it was.",
                Icon = DialogIcon.Question,
                Buttons = DialogButtons.YesCancel,
                ConfirmText = "Remove",
            }).Confirmed)
        {
            row.Remove();
        }
    }
}
