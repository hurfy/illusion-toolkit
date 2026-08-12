using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>
/// What a component does when it is HIT: its own damage parameters, the flags the engine switches on, and the
/// deform handles that decide how far and how hard it crumples — as numbers a modder edits where the component
/// sits, and the one path from such an edit to bytes.
///
/// <para>
/// None of it had a place before. The damage model is most of a prefab's size, and the only surface that ever
/// showed it listed every part flat as <c>Part 1</c>, <c>Part 2</c>, <c>Part 3</c> with a raw flag word beside
/// it — the part struct carries no name field, so that was the only label available. The handles were never
/// shown at all: a modder saw <c>deform_doorFL</c> in the frame tree beside <c>doorFL</c> and read two doors.
/// </para>
/// <para>
/// 1093 of the 1698 shipped parts carry no handle, so "this component does not crumple" is the common answer
/// and is stated rather than left as an empty list.
/// </para>
/// </summary>
public sealed partial class Car
{
    // ── reading ──

    /// <summary>
    /// The damage model's own numbers for one deformable part.
    ///
    /// <para>
    /// The six tuning numbers come from the part's <c>common</c> block and are absent when the file carries
    /// none for it; the centre of mass, the effect group and the flags are on the part itself and are always
    /// there. So a part with no tuning block still has something to show, which is why this is not gated on
    /// <see cref="CarComponent.Damage"/> being non-null.
    /// </para>
    /// </summary>
    private static IReadOnlyList<CarField> DamageFields(PrefabFile prefab, CarDeformPart part)
    {
        int at = part.Index;
        var fields = new List<CarField>(13);
        if (part.Tuning != null)
        {
            fields.Add(Number(prefab, "Mass of this component",
                "How heavy the damage model treats this one panel as being, in kilograms. NOT the car's own "
                + "mass — that is the car class's, in the Tuning tab, and it is a different quantity on the "
                + "same car.",
                CarFieldKind.Number, CarValueSlot.DeformMass, at));
            fields.Add(Number(prefab, "Resistance",
                "How hard this component is to move at all. What the number means beyond \"more resists more\" "
                + "is the game's; the shipped cars are the reference.",
                CarFieldKind.Number, CarValueSlot.DeformResistance, at));
            fields.Add(Number(prefab, "Speed window, lowest",
                "The bottom of the impact-speed window this component answers to.",
                CarFieldKind.Number, CarValueSlot.DeformSpeedMin, at));
            fields.Add(Number(prefab, "Speed window, highest", "And the top of it.",
                CarFieldKind.Number, CarValueSlot.DeformSpeedMax, at));
            fields.Add(Number(prefab, "Energy at the start",
                "How much of the hit's energy this component takes to begin with.",
                CarFieldKind.Number, CarValueSlot.DeformEnergyStart, at));
            fields.Add(Number(prefab, "Energy drop", "And how quickly that energy falls away.",
                CarFieldKind.Number, CarValueSlot.DeformEnergyDrop, at));
        }
        fields.Add(Number(prefab, "Part kind",
            "Which kind of panel the engine treats this as — 1 body, 4 door, 5 window, 6 cover, 13 motor. It "
            + "is the file's own word for the component and the reason the tree does not read it off the "
            + "bone's name: a cover names doorBL on seven shipped cars.",
            CarFieldKind.Count, CarValueSlot.DeformPartType, at));
        fields.Add(Point(prefab, "Centre of mass of this component",
            "The point the damage model swings this panel about. NOT the car's centre of mass — that one is "
            + "the car class's, in the Tuning tab.", CarValueSlot.DeformCentreOfMass, at));
        fields.Add(Number(prefab, "Effect group",
            "Which particle a shot on this component throws. Proven in game: components sharing the number "
            + "behave alike — a door and both its windows are 3 on berkley_kingfisher, the bonnet and the "
            + "patch that hangs on it are both 4. What decides which effect a GROUP gets is not known, so it "
            + "is best read as \"behave like that panel\".",
            CarFieldKind.Count, CarValueSlot.DeformPartEffectGroup, at));
        foreach ((string label, int bit, string hint) in PartFlags)
        {
            // Addressed by the BIT it lives in, so writing one leaves the other thirty-one — including the
            // twenty-seven nobody has named — exactly as they were.
            fields.Add(Number(prefab, label, hint, CarFieldKind.Flag,
                CarValueSlot.DeformPartFlagBit, at, bit));
        }
        return fields;
    }

    /// <summary>
    /// The flags of a deformable part that have a known meaning, and the bit each one lives in.
    ///
    /// <para>
    /// Annotated by the reference toolkit rather than measured in game — its own comment reads
    /// <c>[2 = ALWAYS DYNAMIC?] [16 = KILL PART] [400 = SNOW] [8192 = AIBOX] [262144 = FADE OFF]</c>, with the
    /// question mark on the first and one of the five written in hex among four in decimal. So the names are
    /// offered as the reference toolkit's reading, and the shipped cars are the oracle for what each does.
    /// </para>
    /// <para>
    /// The word holds thirty-two bits and only these five are named. Five OTHER bits are set on shipped parts
    /// and have no name at all — bit 8 on every one of the 1698, bits 3 and 9 on most, bit 31 on 438 and bit 29
    /// on the two plough parts — and the widest word that ships is 0x80000508, which a float cannot even carry
    /// exactly. So each flag is written through its own bit: reading the word, flipping and writing it back
    /// would round the top of it away and lose meanings nobody has read yet.
    /// </para>
    /// <para>
    /// Measured 2026-08-11 over the 85 shipped cars (<c>--probe-car-damage</c>): always-dynamic on 921 parts,
    /// kill-part on 222 and every one of them a window, snow on 221 of which 181 are parts of kind
    /// <c>snow</c> — so the corpus corroborates three of the five names. AI box and fade off are set on
    /// NOTHING that ships, which is what their hints say.
    /// </para>
    /// </summary>
    private static readonly (string Label, int Bit, string Hint)[] PartFlags =
    [
        ("Always dynamic", 1, "The reference toolkit reads bit 2 as \"always dynamic\", with a question mark "
            + "of its own — nothing has measured what it does. Set on 921 of the 1698 shipped parts, mostly "
            + "the panels that move: windows, doors, covers and bumpers."),
        ("Kill part", 4, "The reference toolkit reads bit 16 as \"kill part\". Set on 222 shipped parts and "
            + "every single one of them is a window, which is the corpus agreeing with the name."),
        ("Snow", 10, "The reference toolkit reads bit 0x400 as \"snow\". Set on 221 shipped parts, 181 of "
            + "them parts of kind snow — the same parts whose effects block carries the snow particle ids."),
        ("AI box", 13, "The reference toolkit reads bit 0x2000 as \"AI box\". No shipped part sets it, so "
            + "turning it on is doing something no car in the game does."),
        ("Fade off", 18, "The reference toolkit reads bit 0x40000 as \"fade off\". No shipped part sets it "
            + "either."),
    ];

    /// <summary>The three numbers one deform handle carries — how far the bone may travel, how hard it
    /// resists, and over what radius the panel follows it.</summary>
    private static IReadOnlyList<CarField> HandleFields(PrefabFile prefab, int flat) =>
    [
        Point(prefab, "Range", "How far this handle may travel, per axis, in metres. It is the whole of how "
            + "far the panel caves in around it.", CarValueSlot.DeformHandleRange, flat),
        Number(prefab, "Intensity", "How hard the handle resists being moved.",
            CarFieldKind.Number, CarValueSlot.DeformHandleIntensity, flat),
        Number(prefab, "Radius", "Over what radius the panel follows the handle. Outside it the panel keeps "
            + "its shape.", CarFieldKind.Number, CarValueSlot.DeformHandleRadius, flat),
    ];

    // ── writing ──

    /// <summary>
    /// Writes a component's own damage parameters — its mass and centre of mass, its resistance, its speed
    /// window, its energy start and drop, its effect group and its flags.
    ///
    /// <para>
    /// Nothing is derived from any of them, so nothing is recomputed: hit boxes and per-bone bounds come from
    /// GEOMETRY, and a component is exactly as shootable after its mass changes as it was before. An edit here
    /// moves the fields it names inside the part that was read and touches nothing else — which is what makes
    /// "saving leaves every other component byte for byte as it was" true by construction rather than by care.
    /// </para>
    /// </summary>
    /// <param name="fields">The component's own damage fields with new values in them —
    /// <c>field with { … }</c>. They carry the address they are written through, so a row and its write cannot
    /// address different fields.</param>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then.</returns>
    public CarEdit? SetDamage(
        CarComponent component, IReadOnlyList<CarField> fields, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(fields);
        refusal = null;

        if (component.IsBare)
        {
            refusal = $"\"{component.Name}\" has no deform part, so there is nothing for damage parameters to "
                + "be on. Give it one first and it gets its own.";
            return null;
        }
        return Written($"\"{component.Name}\" damage changed", fields, ref refusal);
    }

    /// <summary>
    /// Writes one deform handle's crumple parameters: how far it travels, how hard it resists, and over what
    /// radius the panel follows it.
    /// </summary>
    /// <param name="handle">Which handle, for the one line the modder reads. The numbers themselves carry the
    /// address they are written through, resolved when the component was stitched — so this and
    /// <paramref name="fields"/> have to have come from the same stitch, which is what the caller's re-read
    /// guarantees.</param>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then.</returns>
    public CarEdit? SetHandle(
        CarComponent component, CarHandle handle, IReadOnlyList<CarField> fields, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(fields);
        refusal = null;
        return Written(
            $"how \"{component.Name}\" crumples around \"{handle.Name}\" changed", fields, ref refusal);
    }

    /// <summary>
    /// One intent that is nothing but numbers in the prefab: snapshot, write, and put the car back untouched if
    /// any of them is refused.
    ///
    /// <para>
    /// The snapshot deliberately leaves the hit boxes OUT. Nothing here is derived from geometry — a component
    /// is exactly as shootable after its mass changes as it was before — and a state that carried them would
    /// write them back over the live model on every undo and mark the frame graph dirty, so taking back a mass
    /// would rewrite the archive's frame resource.
    /// </para>
    /// </summary>
    private CarEdit? Written(string what, IReadOnlyList<CarField> fields, ref string? refusal)
    {
        CarState before = Snapshot([], [], boxes: false);
        if (!Write(fields, ref refusal)) { Restore(before); return null; }
        return new CarEdit(what, before, Snapshot([], [], boxes: false));
    }
}
