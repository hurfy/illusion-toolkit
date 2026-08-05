using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Collisions;
using Illusion.Formats.ItemDesc;

namespace Illusion.Views;

/// <summary>
/// Asks what physics shape a car part should get, which part it belongs to, and how big it is.
///
/// <para>
/// Only PRIMITIVES are offered. A convex hull is a PhysX-cooked blob and the vendored cooker knows one verb,
/// <c>-CookTriangleMesh</c>, so a hull cannot be minted at all; a box, a sphere and a capsule are pure
/// numbers and need no cooker. That is not a workaround — the shipped cars place 232 boxes, 251 capsules and
/// 14 spheres of their own.
/// </para>
/// <para>
/// The PART is the question that decides behaviour, and it used to be answered silently by whatever bone was
/// selected. Measured in game: the same shape on the BODY makes the geometry part of the car — the car
/// crashes with it and the player cannot walk through it — while on a panel it belongs to that panel and
/// only meets the world through it, including after the panel comes off.
/// </para>
/// <para>
/// Position is not asked for: the shape lands on the bone and is moved with the gizmo, the same way every
/// other placement in the toolkit is positioned.
/// </para>
/// </summary>
public sealed partial class CollisionBoxWindow : Window
{
    /// <summary>One row of the shape list: what it is, and what it is good for.</summary>
    private sealed record ShapeChoice(RigidBodyShape Kind, string Title, string Detail);

    private static readonly ShapeChoice[] Shapes =
    [
        new(RigidBodyShape.Box, "Box", "Six flat sides and sharp corners — for anything angular."),
        new(RigidBodyShape.Capsule, "Capsule",
            "A rod with rounded ends, lying along its own length. For bumpers, bars and pipes: things slide "
            + "off it instead of catching."),
        new(RigidBodyShape.Sphere, "Sphere", "One radius, round in every direction."),
    ];

    /// <summary>One row of the surface list: the table index it stands for, or null for "as shipped".</summary>
    private sealed record SurfaceChoice(int? Table, string Title, string Detail);

    /// <summary>
    /// The surfaces worth offering, as <c>MaterialsPhysics.tbl</c> INDEXES.
    ///
    /// <para>
    /// A short list rather than all 64: the table is mostly world materials — asphalt, cobbles, grass — and a
    /// car is made of five or six things. The indexes come from the catalog the collision overlay already
    /// colours by, and the raw value written to disk is this plus the bias, which the builder applies.
    /// </para>
    /// </summary>
    private static readonly SurfaceChoice[] Surfaces =
    [
        new(null, "As shipped", "What every stock car shape carries. Leaves the field at 0."),
        new(16, "Sheet metal", "plech — a body panel."),
        new(46, "Car body", "auto — the car itself."),
        new(28, "Breakable glass", "sklo_rozbitelne_1 — a window that gives."),
        new(30, "Bulletproof glass", "sklo_neprustrelne — glass that does not."),
        new(37, "Upholstery", "calouneni — a seat."),
    ];

    /// <summary>One row of the "what it is" list. <c>VolumeType</c> null means a placed physics shape.</summary>
    private sealed record KindChoice(uint? VolumeType, string Title, string Detail);

    /// <summary>
    /// The two shapes a car's collision actually comes in, plus the zone kind for completeness.
    ///
    /// <para>
    /// Measured over 1698 deformable parts of 85 cars: a part the engine calls a window carries type 0 and
    /// nothing else (527 of 527), a body carries type 5 and nothing else (407 of 407), a motor carries type 6
    /// (77 of 77). So this is not a setting on one thing — they are different things, and the type is what
    /// says which.
    /// </para>
    /// </summary>
    private static readonly KindChoice[] Kinds =
    [
        new(null, "Solid — a physics shape",
            "Points at a shape record: a box, a capsule or a sphere. This is what the body, the doors and the "
            + "bumpers are made of. It gets a handle you can drag."),
        new(0, "Glass — a plain pane",
            "A box that names nothing and is glass by its type alone, the way all 527 shipped window volumes "
            + "are. No shape record, no handle: place it by the numbers in the Prefab tab."),
        new(6, "Zone — engine bay, snow",
            "The same plain box, of the kind a motor and the snow volumes use. No shape record and no handle."),
    ];

    public CollisionBoxWindow(IReadOnlyList<CarPartChoice> parts, string? preferredBone)
    {
        ArgumentNullException.ThrowIfNull(parts);
        InitializeComponent();

        KindBox.ItemsSource = Kinds;
        KindBox.SelectedItem = Kinds[0];

        ShapeBox.ItemsSource = Shapes;
        ShapeBox.SelectedItem = Shapes[0];

        SurfaceBox.ItemsSource = Surfaces;
        SurfaceBox.SelectedItem = Surfaces[0];

        PartBox.ItemsSource = parts;
        // What the user was pointing at, when that bone is a part; otherwise the body, because "part of the
        // car" is what someone adding collision to new geometry almost always means.
        CarPartChoice? wanted = parts.FirstOrDefault(
            p => string.Equals(p.BoneName, preferredBone, StringComparison.Ordinal));
        PartBox.SelectedItem = wanted ?? parts.FirstOrDefault(p => p.IsBody) ?? parts.FirstOrDefault();

        ShowKind();
        ShowShape();
        ShowPart();
        ShowSurface();
    }

    /// <summary>The volume type to write, or null for a placed physics shape.</summary>
    public uint? VolumeType => (KindBox.SelectedItem as KindChoice)?.VolumeType;

    private void Kind_Changed(object sender, RoutedEventArgs e)
    {
        ShowKind();
        ShowShape();
    }

    private void ShowKind()
    {
        if (KindHint == null) return;   // still building the window
        KindHint.Text = (KindBox.SelectedItem as KindChoice)?.Detail ?? "";

        // A plain box has no shape record, so neither the shape kind nor its surface means anything — and a
        // control that is shown while it cannot do anything is a promise the window does not keep.
        bool solid = VolumeType == null;
        Show(ShapeCaption, ShapeBox, solid);
        Show(ShapeHint, null, solid);
        Show(SurfaceCaption, SurfaceBox, solid);
        Show(SurfaceHint, null, solid);
    }

    private static void Show(UIElement caption, UIElement? field, bool visible)
    {
        caption.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (field != null) field.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The chosen surface as a table index, or null for "as shipped".</summary>
    public int? Surface => (SurfaceBox.SelectedItem as SurfaceChoice)?.Table;

    private void Surface_Changed(object sender, RoutedEventArgs e) => ShowSurface();

    private void ShowSurface()
    {
        if (SurfaceHint == null) return;   // still building the window
        SurfaceHint.Text = (SurfaceBox.SelectedItem as SurfaceChoice)?.Detail ?? "";
    }

    /// <summary>The shape that was accepted, or null while the dialog was cancelled.</summary>
    public RigidBodyShape? Kind { get; private set; }

    /// <summary>Box: half-extents. Sphere: X is the radius. Capsule: X is the radius, Y the straight
    /// length.</summary>
    public Vector3? Size { get; private set; }

    /// <summary>The part the shape was given to — the bone it hangs off comes from here.</summary>
    public CarPartChoice? Part => PartBox.SelectedItem as CarPartChoice;

    private ShapeChoice Chosen => ShapeBox.SelectedItem as ShapeChoice ?? Shapes[0];

    private void Shape_Changed(object sender, RoutedEventArgs e) => ShowShape();

    private void Part_Changed(object sender, RoutedEventArgs e) => ShowPart();

    private void ShowShape()
    {
        if (SizeCaption == null) return;   // still building the window
        ShapeHint.Text = Chosen.Detail;

        // A plain box states its FULL size, the way every shipped self-describing volume does — measured on
        // 1049 of them, 63 of which would be bigger than their own car if the numbers were half-sizes.
        if (VolumeType != null)
        {
            SizeCaption.Text = "Size (metres) — the whole box, not half of it";
            Show(LabelX, SizeX, "Across the car");
            Show(LabelY, SizeY, "Along the car");
            Show(LabelZ, SizeZ, "Up");
            return;
        }

        switch (Chosen.Kind)
        {
            case RigidBodyShape.Sphere:
                SizeCaption.Text = "Radius (metres)";
                Show(LabelX, SizeX, "Radius");
                Hide(LabelY, SizeY);
                Hide(LabelZ, SizeZ);
                break;
            case RigidBodyShape.Capsule:
                SizeCaption.Text = "Radius and length (metres) — the rounded caps add the radius at each end";
                Show(LabelX, SizeX, "Radius");
                Show(LabelY, SizeY, "Length");
                Hide(LabelZ, SizeZ);
                break;
            default:
                SizeCaption.Text = "Half-size (metres) — how far the box reaches from its centre on each side";
                Show(LabelX, SizeX, "X");
                Show(LabelY, SizeY, "Y");
                Show(LabelZ, SizeZ, "Z");
                break;
        }
    }

    private void ShowPart()
    {
        if (PartHint == null) return;
        PartHint.Text = Part is not { } part
            ? ""
            : part.IsBody
                ? "Part of the car itself: everything that touches the car touches this shape, and the "
                    + "player cannot walk through it."
                : $"Part of the {part.Kind}: the shape moves with it and meets the world through it — "
                    + "including after that panel comes off. Pick the body to make it part of the car.";
    }

    private static void Show(TextBlock label, TextBox field, string caption)
    {
        label.Text = caption;
        label.Visibility = Visibility.Visible;
        field.Visibility = Visibility.Visible;
    }

    private static void Hide(TextBlock label, TextBox field)
    {
        label.Visibility = Visibility.Collapsed;
        field.Visibility = Visibility.Collapsed;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Part == null)
        {
            Fail("This archive has no deformable part to hang a collision shape off.");
            return;
        }

        var read = new float[3];
        TextBox[] fields = [SizeX, SizeY, SizeZ];
        int needed = VolumeType != null
            ? 3                                  // a plain box is three full sizes
            : Chosen.Kind switch
            {
                RigidBodyShape.Sphere => 1,
                RigidBodyShape.Capsule => 2,
                _ => 3,
            };
        for (int i = 0; i < needed; i++)
        {
            if (!TryRead(fields[i].Text, out read[i]))
            {
                Fail("Every number has to be a number.");
                return;
            }
            if (read[i] <= 0f)
            {
                Fail("Every number has to be greater than zero.");
                return;
            }
        }

        Kind = Chosen.Kind;
        Size = new Vector3(read[0], read[1], read[2]);
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
