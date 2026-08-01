using System.Globalization;
using System.Numerics;
using System.Windows;

namespace Illusion.Views;

/// <summary>
/// Asks how big a car part's collision box should be. The caller adds it
/// (<c>CarCollisionController.AddBoxToSelectedBone</c>); this only reports the size.
/// <para>
/// Size and not position: the box lands on the bone and is moved with the gizmo from there, which is the same
/// way every other placement in the toolkit is positioned — typing three more numbers into a dialog would be a
/// worse way to say "a bit further forward".
/// </para>
/// </summary>
public sealed partial class CollisionBoxWindow : Window
{
    public CollisionBoxWindow(string boneName)
    {
        InitializeComponent();
        BoneLabel.Text = string.IsNullOrWhiteSpace(boneName) ? "(unnamed bone)" : boneName;
    }

    /// <summary>The size that was accepted, or null while the dialog was cancelled.</summary>
    public Vector3? Dimensions { get; private set; }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(SizeX.Text, out float x) || !TryRead(SizeY.Text, out float y) || !TryRead(SizeZ.Text, out float z))
        {
            Fail("Every side needs a number.");
            return;
        }
        if (x <= 0f || y <= 0f || z <= 0f)
        {
            Fail("A box needs a positive size on every side.");
            return;
        }

        Dimensions = new Vector3(x, y, z);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Fail(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.Visibility = Visibility.Visible;
    }

    // Accepts both separators: a decimal comma is what a Russian or German keyboard produces, and rejecting
    // "0,3" as "not a number" would be the dialog being pedantic about something it can simply understand.
    private static bool TryRead(string text, out float value) =>
        float.TryParse(text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
