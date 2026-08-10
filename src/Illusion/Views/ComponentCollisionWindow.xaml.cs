using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Cars;

namespace Illusion.Views;

/// <summary>
/// Asks what a component's collision IS, what form it takes, how big it is and where it sits — and asks
/// nothing else.
///
/// <para>
/// The role is pre-filled from the component's own kind and is meant to be overridden: a part carrying two
/// kinds of volume is the normal case rather than the exception, and a door with both its own collision and
/// its glass is simply how a car is built.
/// </para>
/// <para>
/// Everything the file holds is derived from these four answers and none of it appears here — the stored
/// volume type, which bone's space the matrix goes into, the axis reversal between the two copies of it,
/// whether the extents mean a full size or half of one, the ItemDesc record a solid needs, and the mirror
/// stub in the frame graph that a car does not read at all.
/// </para>
/// </summary>
public sealed partial class ComponentCollisionWindow : Window
{
    private sealed record RoleChoice(CarCollisionRole Role, string Title, string Detail);

    private static readonly RoleChoice[] Roles =
    [
        new(CarCollisionRole.Body, "Body — the car's own solid",
            "What the body, the doors and the bumpers are made of: this is what a bullet, a bumper and a "
            + "player meet."),
        new(CarCollisionRole.Glass, "Glass — a pane",
            "A pane of glass, written the way all 527 shipped window volumes are. Always a plain box."),
        new(CarCollisionRole.Zone, "Zone — engine bay, snow",
            "A region of the car rather than a solid: the engine bay, the snow volumes, a patch. Always a "
            + "plain box."),
    ];

    private sealed record ShapeChoice(CarCollisionShape Shape, string Title, string Detail);

    private static readonly ShapeChoice[] Shapes =
    [
        new(CarCollisionShape.Box, "Box", "Six flat sides and sharp corners — for anything angular."),
        new(CarCollisionShape.Capsule, "Capsule",
            "A rod with rounded ends, lying along its own length. For bumpers, bars and pipes: things slide "
            + "off it instead of catching."),
        new(CarCollisionShape.Sphere, "Sphere", "Round in every direction."),
        new(CarCollisionShape.Cylinder, "Cylinder", "A rod with flat ends, lying along its own length."),
    ];

    private string _component = "";
    private bool _editing;

    /// <summary>Asks for a NEW collision on a component, with the role its kind implies already chosen.</summary>
    /// <param name="component">The component's name, for the window's own wording.</param>
    /// <param name="kind">Its part kind — what the role is pre-filled from.</param>
    public ComponentCollisionWindow(string component, string kind)
    {
        InitializeComponent();
        Setup(component, CarCollision.RoleFor(kind ?? ""), CarCollisionShape.Box,
            new Vector3(0.3f, 0.3f, 0.3f), Vector3.Zero, editing: false);
    }

    /// <summary>Asks for the new size and position of a collision that is already there.</summary>
    public ComponentCollisionWindow(string component, CarCollision collision)
    {
        ArgumentNullException.ThrowIfNull(collision);
        InitializeComponent();
        Setup(component, collision.Role, collision.Shape, collision.Size, collision.Position,
            editing: true);
    }

    private ComponentCollisionWindow(
        string component, CarCollisionRole role, CarCollisionShape shape, Vector3 size, Vector3 position,
        bool editing)
    {
        InitializeComponent();
        Setup(component, role, shape, size, position, editing);
    }

    /// <summary>
    /// The same question again, holding the answers that were given and the reason they were refused.
    ///
    /// <para>
    /// A WPF dialog cannot be shown twice, so this is a second window rather than the same one reopened. The
    /// point is that a modder whose numbers were refused does not have to type them all again to find out
    /// whether the next guess is any better.
    /// </para>
    /// </summary>
    public ComponentCollisionWindow Again(string refusal)
    {
        var again = new ComponentCollisionWindow(_component, Role, Shape, Size, Position, _editing)
        {
            Owner = Owner,
        };
        again.Fail(refusal);
        return again;
    }

    private void Setup(
        string component, CarCollisionRole role, CarCollisionShape shape, Vector3 size, Vector3 position,
        bool editing)
    {
        _component = component ?? "";
        _editing = editing;

        Title = editing ? $"Collision on {_component}" : $"Add collision to {_component}";
        AcceptButton.Content = editing ? "Apply" : "Add";
        FootNote.Text = editing
            ? "Size and position are in this component's own space. Then Build."
            : "It lands in this component's own space. Then Build.";

        RoleBox.ItemsSource = Roles;
        RoleBox.SelectedItem = Roles.FirstOrDefault(r => r.Role == role) ?? Roles[0];
        // A collision that is already there does not change what it IS: that would move it between two
        // different spaces and two different ways of stating a size, which is a conversion of its own rather
        // than an edit of this one.
        RoleBox.IsEnabled = !editing;

        ShapeBox.ItemsSource = Shapes;
        ShapeBox.SelectedItem = Shapes.FirstOrDefault(s => s.Shape == shape) ?? Shapes[0];
        ShapeBox.IsEnabled = !editing;

        Put(SizeX, size.X);
        Put(SizeY, size.Y);
        Put(SizeZ, size.Z);
        Put(AtX, position.X);
        Put(AtY, position.Y);
        Put(AtZ, position.Z);

        ShowRole();
        ShowShape();
    }

    /// <summary>What the collision is — chosen, never derived from the file.</summary>
    public CarCollisionRole Role => (RoleBox.SelectedItem as RoleChoice)?.Role ?? CarCollisionRole.Body;

    /// <summary>The form it takes.</summary>
    public CarCollisionShape Shape => (ShapeBox.SelectedItem as ShapeChoice)?.Shape ?? CarCollisionShape.Box;

    /// <summary>Its whole size in metres, once the dialog was accepted.</summary>
    public Vector3 Size { get; private set; }

    /// <summary>Where it sits, in the component's own space, once the dialog was accepted.</summary>
    public Vector3 Position { get; private set; }

    private void Role_Changed(object sender, RoutedEventArgs e)
    {
        ShowRole();
        ShowShape();
    }

    private void Shape_Changed(object sender, RoutedEventArgs e) => ShowShape();

    private void ShowRole()
    {
        if (RoleHint == null) return;   // still building the window
        RoleHint.Text = (RoleBox.SelectedItem as RoleChoice)?.Detail ?? "";

        // Glass and zones describe themselves, and a self-describing volume has nowhere to say it is anything
        // but a box. Offering the other three would be a promise the file cannot keep.
        bool solid = Role == CarCollisionRole.Body;
        ShapeCaption.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;
        ShapeBox.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;
        ShapeHint.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;
        if (!solid) ShapeBox.SelectedItem = Shapes[0];
    }

    private void ShowShape()
    {
        if (ShapeHint == null) return;   // still building the window
        ShapeHint.Text = (ShapeBox.SelectedItem as ShapeChoice)?.Detail ?? "";
        PositionCaption.Text = $"Position in {_component}'s own space (metres)";

        switch (Role == CarCollisionRole.Body ? Shape : CarCollisionShape.Box)
        {
            case CarCollisionShape.Sphere:
                SizeCaption.Text = "Size in metres — how wide across, the whole way";
                SizeLabelX.Text = "Across";
                SizeLabelY.Text = "";
                SizeLabelZ.Text = "";
                Enable(SizeY, false);
                Enable(SizeZ, false);
                SizeY.Text = SizeX.Text;
                SizeZ.Text = SizeX.Text;
                break;
            case CarCollisionShape.Capsule:
            case CarCollisionShape.Cylinder:
                // ONE width, under two boxes — a capsule and a cylinder are round across, so the second
                // number follows the first rather than sitting there waiting to be silently discarded.
                SizeCaption.Text = "Size in metres — how wide across, and how long end to end";
                SizeLabelX.Text = "Across";
                SizeLabelY.Text = "";
                SizeLabelZ.Text = "End to end";
                Enable(SizeY, false);
                Enable(SizeZ, true);
                SizeY.Text = SizeX.Text;
                break;
            default:
                SizeCaption.Text = "Size in metres — the whole box, not half of it";
                SizeLabelX.Text = "X";
                SizeLabelY.Text = "Y";
                SizeLabelZ.Text = "Z";
                Enable(SizeY, true);
                Enable(SizeZ, true);
                break;
        }
    }

    private static void Enable(TextBox field, bool on)
    {
        field.IsEnabled = on;
        field.Opacity = on ? 1.0 : 0.45;
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        // The width fields a round shape does not use follow the one it does, so a number typed before the
        // shape was chosen cannot survive as a size the modder never asked for. Only the LARGEST of X and Y
        // would reach the record otherwise, and nothing on the window would say which of the two won.
        if (Role == CarCollisionRole.Body)
        {
            if (Shape is CarCollisionShape.Sphere) { SizeY.Text = SizeX.Text; SizeZ.Text = SizeX.Text; }
            else if (Shape is CarCollisionShape.Capsule or CarCollisionShape.Cylinder)
            {
                SizeY.Text = SizeX.Text;
            }
        }

        if (!Read(SizeX, out float sx) || !Read(SizeY, out float sy) || !Read(SizeZ, out float sz))
        {
            Fail("Every size has to be a number.");
            return;
        }
        if (sx <= 0f || sy <= 0f || sz <= 0f)
        {
            Fail("Every size has to be greater than zero.");
            return;
        }
        if (!Read(AtX, out float x) || !Read(AtY, out float y) || !Read(AtZ, out float z))
        {
            Fail("Every position has to be a number.");
            return;
        }

        Size = new Vector3(sx, sy, sz);
        Position = new Vector3(x, y, z);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Says what the aggregate refused, in the window rather than as a notice behind it — the modder
    /// is still standing in front of the numbers that caused it.</summary>
    public void Fail(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.Visibility = Visibility.Visible;
    }

    private static void Put(TextBox field, float value) =>
        field.Text = value.ToString("0.####", CultureInfo.InvariantCulture);

    // Accepts both separators: a decimal comma is what a Russian or a German keyboard produces, and rejecting
    // "0,3" as "not a number" would be the dialog being pedantic about something it can simply understand.
    private static bool Read(TextBox field, out float value) =>
        float.TryParse(field.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
            out value);
}
