using System.Globalization;
using System.Windows;
using Illusion.Assets.Cars;
using Illusion.Formats.Prefab;
using Illusion.ViewModels;

namespace Illusion.Views;

/// <summary>
/// Asks what a bare component's new deform part IS and which component it hangs off — and asks nothing else.
///
/// <para>
/// A bare component is a bone with geometry that no deform part claims: a licence-plate segment, a light, a
/// wiper. There are 2587 of them over the corpus and 1787 have no row anywhere in the assembly layer, yet
/// every one already carries a live hit box — they are shootable parts of the car with nowhere to be edited.
/// Giving one a part is what puts it in the damage model, and the paragraph at the top of the window says
/// what it gains before the modder is asked to choose anything.
/// </para>
/// <para>
/// Everything else the file holds is derived: the flag word, the crumple thresholds, the six tuning numbers,
/// all THREE copies of the parent link, and the half of the struct nobody has read — which is copied from a
/// part of the same kind on this very car rather than invented.
/// </para>
/// </summary>
public sealed partial class ComponentPartWindow : Window
{
    /// <summary>One kind on offer, with what the corpus says it is.</summary>
    private sealed record KindChoice(CarPartTemplate Template, string Title, string Detail);

    /// <summary>One component the new part could hang off.</summary>
    private sealed record ParentChoice(ComponentRowViewModel Row, string Title, string Detail);

    /// <summary>
    /// What each kind is, in a sentence. The counts come from the template rather than from here, so the
    /// prose cannot drift away from what the corpus was measured to hold.
    /// </summary>
    private static readonly Dictionary<uint, string> Notes = new()
    {
        [0] = "The kind that carries no other meaning — what a licence plate, a light and a wiper are when "
            + "they are in the damage model at all. Start here unless the component really is one of the "
            + "others.",
        [4] = "A door. The heaviest kind that ships: mass 30 against the 2 a plain part starts with.",
        [5] = "A pane of glass. Every shipped window sets the flag the reference toolkit reads as "
            + "\"kill part\" — 222 of 222 — so this is the kind that shatters rather than dents.",
        [6] = "A bonnet or a boot lid — a panel that opens.",
        [7] = "A bumper. Takes the widest impact-speed window of any kind and a mass of 20.",
        [12] = "An exhaust.",
        [13] = "The engine block. The only kind that ships with a resistance of its own.",
        [15] = "The winter shell — the parts a car grows when it snows. Its flag word sets the snow bit, "
            + "which is what the effects block's snow particle ids go with.",
    };

    private readonly string _component;

    /// <param name="component">The bare component's name, for the window's own wording.</param>
    /// <param name="parents">The components it could hang off — every one that has a deform part of its own,
    /// since there is nothing for a part to hang off a bare component.</param>
    /// <param name="body">The car's body, which is where a component with no obvious owner belongs: it is
    /// already where every marker whose bone no component owns goes.</param>
    public ComponentPartWindow(
        string component, IReadOnlyList<ComponentRowViewModel> parents, ComponentRowViewModel? body)
    {
        ArgumentNullException.ThrowIfNull(parents);
        InitializeComponent();
        _component = component ?? "";

        Title = $"Make {_component} damageable";
        GainLabel.Text = Car.PartGain;

        KindBox.ItemsSource = Kinds();
        KindBox.SelectedItem = ((IReadOnlyList<KindChoice>)KindBox.ItemsSource!)[0];

        ParentChoice[] choices = [.. parents.Select(Owning)];
        ParentBox.ItemsSource = choices;
        ParentBox.SelectedItem =
            choices.FirstOrDefault(c => ReferenceEquals(c.Row, body)) ?? choices.FirstOrDefault();

        ShowKind();
        ShowParent();
    }

    private static IReadOnlyList<KindChoice> Kinds() =>
    [
        .. Car.PartKinds.Select(t => new KindChoice(t, Titled(t), Notes.GetValueOrDefault(t.Type, ""))),
    ];

    /// <summary>A kind's name and how much of the corpus the template it brings actually describes — stated,
    /// because "56 of 181 covers are written this way" is a different promise from "all of them are".</summary>
    private static string Titled(CarPartTemplate template) =>
        $"{Capitalized(template.Name)} — {template.OfKind.ToString(CultureInfo.InvariantCulture)} shipped "
        + $"parts, {template.Shipped.ToString(CultureInfo.InvariantCulture)} of them written the way a new "
        + "one will be";

    private static ParentChoice Owning(ComponentRowViewModel row) =>
        new(row, $"{row.Name} — {row.Kind}",
            $"\"{row.Name}\" is a {row.Kind}. The new part is written as hanging off it in all three places "
            + "the file keeps that link.");

    private static string Capitalized(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>The kind that was chosen.</summary>
    public CarPartTemplate Kind =>
        (KindBox.SelectedItem as KindChoice)?.Template ?? Car.DefaultPartKind;

    /// <summary>The component the new part hangs off, or null when the car has none it could.</summary>
    public ComponentRowViewModel? HangsOff => (ParentBox.SelectedItem as ParentChoice)?.Row;

    /// <summary>
    /// The same question again, holding the answers that were given and the reason they were refused.
    ///
    /// <para>
    /// A WPF dialog cannot be shown twice, so this is a second window rather than the same one reopened — the
    /// same shape <see cref="ComponentCollisionWindow.Again"/> has, and for the same reason: a modder whose
    /// choice was refused should not have to make it again to find out whether the next one is any better.
    /// </para>
    /// </summary>
    public ComponentPartWindow Again(string refusal)
    {
        var again = new ComponentPartWindow(
            _component, [.. ((IEnumerable<ParentChoice>)ParentBox.ItemsSource!).Select(c => c.Row)],
            HangsOff)
        {
            Owner = Owner,
        };
        again.KindBox.SelectedItem =
            ((IEnumerable<KindChoice>)again.KindBox.ItemsSource!).FirstOrDefault(k => k.Template == Kind);
        again.Fail(refusal);
        return again;
    }

    private void Kind_Changed(object sender, RoutedEventArgs e) => ShowKind();

    private void Parent_Changed(object sender, RoutedEventArgs e) => ShowParent();

    private void ShowKind()
    {
        if (KindHint == null) return;   // still building the window
        KindHint.Text = (KindBox.SelectedItem as KindChoice)?.Detail ?? "";
    }

    private void ShowParent()
    {
        if (ParentHint == null) return; // still building the window
        ParentHint.Text = (ParentBox.SelectedItem as ParentChoice)?.Detail ?? "";
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (HangsOff == null)
        {
            Fail("This car has no component with a deform part for this one to hang off.");
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Says what the aggregate refused, in the window rather than as a notice behind it.</summary>
    public void Fail(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.Visibility = Visibility.Visible;
    }
}
