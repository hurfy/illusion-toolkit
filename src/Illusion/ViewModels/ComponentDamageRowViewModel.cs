using System.ComponentModel;
using System.Globalization;
using Illusion.Assets.Cars;

namespace Illusion.ViewModels;

/// <summary>
/// What a component does when it is HIT, shown under it: its own mass and centre of mass, its resistance, its
/// speed window, its energy start and drop, its effect group and the flags the engine switches on.
///
/// <para>
/// The two labelled "of this component" are deliberately not called <c>Mass</c> and <c>Centre of mass</c>. The
/// car CLASS carries a mass and a centre of mass of its own in its EDS record, which the Tuning tab shows, and
/// they are different quantities on the same car — a modder who cannot tell which of the two they are changing
/// is being invited to change the wrong one.
/// </para>
/// <para>
/// The row says the PART KIND beside it rather than leaving it to be read off the bone name: a cover names
/// <c>doorBL</c> on seven shipped cars, and bumpers name bones called <c>rezerva</c> and <c>krytmotoru</c>.
/// </para>
/// </summary>
public sealed class ComponentDamageRowViewModel : INotifyPropertyChanged, IComponentChildRow
{
    internal ComponentDamageRowViewModel(ComponentRowViewModel component) => Component = component;

    /// <summary>The component row it hangs under.</summary>
    public ComponentRowViewModel Component { get; }

    /// <summary>The numbers themselves, each carrying the address it is written back through.</summary>
    public IReadOnlyList<CarField> Fields => Component.Component.DamageFields;

    /// <summary>What to call it.</summary>
    public string Label => "Damage";

    /// <summary>
    /// The part KIND, on the row — the file's own word for what this component is.
    ///
    /// <para>
    /// Shown and never inferred: a cover names a door bone seven times over the corpus, so reading the kind off
    /// the name misleads on exactly the cars this view exists for.
    /// </para>
    /// </summary>
    public string Kind => Component.Kind;

    /// <summary>
    /// The one number worth seeing without opening it: how heavy the damage model treats this panel as being.
    ///
    /// <para>
    /// Read off the aggregate's own typed reading rather than out of the fields, because a field's ADDRESS is
    /// internal to the aggregate on purpose and matching one by its label would make a row break when the
    /// label is reworded. A part the file carries no tuning block for has no mass to show and says its kind
    /// alone.
    /// </para>
    /// </summary>
    public string Summary => Component.Component.Damage is not { } damage
        ? Kind
        : string.Create(CultureInfo.InvariantCulture, $"{Kind} · {damage.Mass:0.###} kg");

    /// <summary>Every one of them, one per line, for the row's tooltip — with the flags that are ON named
    /// rather than left as a word of bits.</summary>
    public string Tip => string.Join("\n",
        [$"A {Kind} — what it takes to move this component when it is hit.",
         .. Fields.Select(f => $"{f.Label}: {f.Text}")]);

    /// <summary>Whether there is anything to type. A part the file carries no tuning block for still has its
    /// centre of mass, its effect group and its flags, so this is false only on a car with no damage model at
    /// all.</summary>
    public bool HasFields => Fields.Count > 0;

    /// <summary>Damage is a fact about the component's own deform part, not a frame — so clicking the row
    /// leaves the viewport pointed at the component's bone.</summary>
    public ulong FrameHash => 0;

    /// <summary>It follows its component through the panel's search.</summary>
    public bool HasSearchMatch => Component.HasSearchMatch;

    private bool _isSelected;

    /// <summary>Whether this row is the one the menu will act on.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    public override string ToString() => $"{Label} ({Kind})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>
/// One deform handle of a component — a bone the panel bends AROUND when it is hit, with how far it may
/// travel, how hard it resists and over what radius the panel follows it — or, when the component has none,
/// the plain statement that it does not crumple.
///
/// <para>
/// A handle is not a component and must never be shown as one: the modder who sees <c>deform_doorFL</c> in the
/// frame tree beside <c>doorFL</c> reads two doors. It is a row ON its component, which is also the only place
/// its three numbers had ever been reachable from.
/// </para>
/// <para>
/// The empty state is not decoration. 1093 of the 1698 shipped parts carry no handle at all, so "does not
/// crumple" is the commoner of the two answers, and a component that showed an empty list instead would leave
/// the modder wondering whether the toolkit had failed to read something.
/// </para>
/// </summary>
public sealed class ComponentHandleRowViewModel : INotifyPropertyChanged, IComponentChildRow
{
    internal ComponentHandleRowViewModel(CarHandle? handle, ComponentRowViewModel component)
    {
        Handle = handle;
        Component = component;
    }

    /// <summary>The handle this row is, or null when the row is the statement that there are none.</summary>
    public CarHandle? Handle { get; }

    /// <summary>The component row it hangs under.</summary>
    public ComponentRowViewModel Component { get; }

    /// <summary>Whether there is a handle here at all — false on the row that says the component does not
    /// crumple, which is the one row under a component that nothing can be done to.</summary>
    public bool HasFields => Handle is { Fields.Count: > 0 };

    /// <summary>The handle bone's name, or the statement itself.</summary>
    public string Label => Handle?.Name ?? "Does not crumple";

    /// <summary>How far it travels, for the row — the number that says at a glance whether this handle does
    /// much.</summary>
    public string Summary => Handle is not { } handle
        ? "no deform handles"
        : string.Create(CultureInfo.InvariantCulture,
            $"{handle.Range.X:0.###}, {handle.Range.Y:0.###}, {handle.Range.Z:0.###}");

    /// <summary>Its three numbers, one per line, for the row's tooltip.</summary>
    public string Tip => Handle is not { } handle
        ? $"\"{Component.Name}\" has no deform handle, so it does not cave in when it is hit — which is how "
            + "1093 of the 1698 shipped parts are written."
        : string.Join("\n",
            [$"\"{handle.Name}\" is the bone \"{Component.Name}\" crumples around.",
             .. handle.Fields.Select(f => $"{f.Label}: {f.Text}")]);

    /// <summary>
    /// A handle names a BONE of the rig, and it is deliberately not handed to the viewport.
    ///
    /// <para>
    /// Selecting it would light a bone the tree shows no row for — the whole reason a handle is not a component
    /// — and the gizmo, the property tabs and Delete all follow the viewport's selection. So the row points
    /// them at the component that crumples instead, which is the thing the modder is actually working on.
    /// </para>
    /// </summary>
    public ulong FrameHash => 0;

    /// <summary>It follows its component through the panel's search.</summary>
    public bool HasSearchMatch => Component.HasSearchMatch;

    private bool _isSelected;

    /// <summary>Whether this row is the one the menu will act on.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    public override string ToString() => Label;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
