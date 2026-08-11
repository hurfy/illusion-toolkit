using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>
/// Making a light, a licence plate or a wiper damageable — and being able to change your mind.
///
/// <para>
/// A third of a car is not in the assembly layer at all: 2587 bones carry geometry that no deform part claims,
/// 1787 of them have no row anywhere in the prefab, and every single one already carries a live hit box. They
/// are shootable parts of the car with nowhere to be edited. Giving one a deform part turns it into a full
/// component — it can then take collision, damage parameters, crumple handles and markers like any other —
/// and taking that part away demotes it back, leaving the bone, the geometry and the hit boxes exactly as they
/// were.
/// </para>
/// <para>
/// The two halves are deliberately symmetric. Adding a component was a one-way door for as long as there was
/// no way back out of it, and a modder experimenting with which of a car's forty light bones should crumple
/// would have had to hand-edit the prefab to undo a guess.
/// </para>
/// </summary>
public sealed partial class Car
{
    /// <summary>
    /// The kinds a component can be given, with what the shipped cars of each kind are written as.
    ///
    /// <para>
    /// Eight of the engine's thirteen. The five that are absent are absent for reasons a modder would
    /// otherwise discover in game — see <see cref="PrefabFile.MintablePartKinds"/>.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CarPartTemplate> PartKinds => PrefabFile.MintablePartKinds;

    /// <summary>The kind a bare component is offered first: <c>normal</c>, which is what a licence plate, a
    /// light and a wiper all are — 213 shipped parts, and the only kind that carries no other meaning.</summary>
    public static CarPartTemplate DefaultPartKind => PartKinds[0];

    /// <summary>
    /// What a bare component would gain by being given a deform part — the sentence the offer is made with.
    ///
    /// <para>
    /// Stated rather than left implicit because the modder is being asked to choose a kind and a parent for a
    /// thing they have never had to think about: what the part IS for is exactly the list of what appears
    /// under the component afterwards.
    /// </para>
    /// </summary>
    public const string PartGain =
        "It gains its own damage parameters — mass, resistance, the speed window it answers to, its centre of "
        + "mass and which particle it throws when hit — and it can then be given collision, deform handles "
        + "and markers of its own. Its bone, its geometry and its hit boxes are not touched: it already takes "
        + "bullets, and it goes on taking them exactly as it did.\n\n"
        + "It starts with no collision, and every one of the 1698 shipped parts has at least one — so give it "
        + "one next, or the damage parameters may be numbers the game never reaches.";

    /// <summary>
    /// Gives a bare component a deform part of its own.
    ///
    /// <para>
    /// Derived here and never asked: every field of the part except the kind, the bone and the parent. The
    /// numbers a part of that kind ships with come from a measured template, and the half of the struct nobody
    /// has read is copied from a part of the same kind on this very car — see
    /// <see cref="PrefabFile.AddCarPart"/> for why a copy rather than a default.
    /// </para>
    /// <para>
    /// The part is APPENDED to the list, which is what makes "every other component is left byte for byte as
    /// it was" true by construction: every index the file already holds is below the new one.
    /// </para>
    /// </summary>
    /// <param name="parent">Which component this one hangs off. Both copies of the link are written from it,
    /// and the third — the child's index in the parent's own list — with them.</param>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then.</returns>
    public CarEdit? GrantDeformPart(
        CarComponent component, CarPartTemplate kind, CarComponent parent, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(parent);
        refusal = null;

        if (!component.IsBare)
        {
            refusal = $"\"{component.Name}\" already has a deform part — it is a {component.Kind}. Take that "
                + "one away first if you want a different kind.";
            return null;
        }
        if (component.BoneHash == 0 || !component.BoneResolves)
        {
            refusal = $"\"{component.Name}\" names a bone this car does not have, so a deform part written "
                + "for it would name nothing. Put the bone back in Blender and push again.";
            return null;
        }
        if (parent.IsBare || parent.PartIndex < 0)
        {
            refusal = $"\"{parent.Name}\" has no deform part of its own, so there is nothing for this one to "
                + "hang off. Give it one first, or hang this off the body.";
            return null;
        }
        if (ReferenceEquals(parent, component))
        {
            refusal = "a component cannot hang off itself";
            return null;
        }
        if (PrefabFile.PartTemplate(kind.Type) == null)
        {
            refusal = $"\"{kind.Name}\" is not a kind a new part can be given";
            return null;
        }

        CarState before = Snapshot([], [], boxes: false);
        // The parent's own effect group, because components sharing the number behave alike in game and a
        // plate on the body throwing what the body throws is the answer a modder would not have to correct.
        byte group = parent.Damage?.EffectGroup ?? 0;
        if (Prefab.AddCarPart(kind.Type, component.BoneHash, parent.PartIndex, group) < 0)
        {
            refusal = "the car's prefab would not take another deform part";
            Restore(before);
            return null;
        }

        return new CarEdit(
            $"\"{component.Name}\" is now a {kind.Name} of \"{parent.Name}\"",
            before, Snapshot([], [], boxes: false));
    }

    /// <summary>
    /// Takes a component's deform part away, demoting it back to a bare component.
    ///
    /// <para>
    /// The bone, its geometry and its hit boxes are left alone — the component goes on being drawn and goes on
    /// taking bullets, exactly as the other 2587 bare bones of the corpus do. What goes with the part is what
    /// hung off it: its collision volumes, and the shape records and mirror stubs nothing else names.
    /// </para>
    /// <para>
    /// A part that is still NAMED by something is refused rather than removed, with what holds it. Six places
    /// in the deformation block address a part by its position and the removal renumbers all of them, but
    /// three of those belong to the machinery that makes a panel come off the car — a joint, an owner deform,
    /// the hash→index table — and none of it has been read. A car whose bumper stopped detaching because its
    /// neighbour was demoted is not a failure a modder could diagnose.
    /// </para>
    /// </summary>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then.</returns>
    public CarEdit? RemoveDeformPart(CarComponent component, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(component);
        refusal = null;

        if (component.IsBare)
        {
            refusal = $"\"{component.Name}\" has no deform part to take away — it is already a bare component.";
            return null;
        }
        if (component.PartIndex < 0 || component.PartIndex >= Prefab.CarDeformParts.Count)
        {
            refusal = "that component is no longer in the car";
            return null;
        }
        if (component.PartType == BodyPartType)
        {
            refusal = $"\"{component.Name}\" is this car's body. Every shipped car has exactly one, and the "
                + "markers of every bone no component owns hang off it — taking it away would leave the car "
                + "with no damage model at all.";
            return null;
        }
        // The two halves are meant to be symmetric, and here is where they would stop being. A bare component
        // is minted ONLY from a bone that resolves and carries geometry, so demoting one that has neither
        // leaves no row at all — the component disappears from the tree, and the grant that would put it back
        // refuses a bone that does not resolve. A demotion has to be a demotion, not a deletion.
        if (!component.BoneResolves || component.BoneHash == 0)
        {
            refusal = $"\"{component.Name}\" names a bone this car does not have, so there would be nothing "
                + "left of it after the part went — and no way to give it one back. Put the bone back in "
                + "Blender and push again.";
            return null;
        }
        if (!component.HasGeometry)
        {
            // Measured on the focus car: a window's pane is a part whose bone draws nothing at LOD 0, so this
            // is an ordinary shipped case rather than a broken one — which is why the reason says what would
            // happen instead of promising a level that may not draw it either.
            refusal = $"\"{component.Name}\" draws nothing at this level of detail, and a component with no "
                + "part is only shown where its bone is drawn — so there would be nothing left here to give "
                + "the part back to. If it is drawn at another level, do it there.";
            return null;
        }
        CarPartLinks links = Prefab.CarPartLinksOf(component.PartIndex);
        if (links.Any)
        {
            refusal = $"\"{component.Name}\" is still named by {Holding(links)}, and taking the part away "
                + "would leave that pointing at a part which is gone.";
            return null;
        }

        // What goes WITH it: the shape records its own volumes name and no OTHER part's volume does, and
        // those records' mirror stubs.
        //
        // The count has to exclude this part, which is what makes it a different question from the one
        // RemoveCollision asks. A component's two volumes naming one record is a shape the whole car stops
        // using the moment the part goes — and asking "does exactly one volume name it" would answer two,
        // leave the record behind, and orphan both it and its stub with no component left to remove them from.
        var shapeFiles = new List<string>();
        var stubs = new List<FrameObjectCollision>();
        var records = new List<CarShapeRecord>();
        foreach (CarPhysicsVolume volume in Prefab.CarDeformParts[component.PartIndex].Volumes)
        {
            if (volume.ShapeHash == 0 || NamesOf(volume.ShapeHash, besides: component.PartIndex) != 0
                || !_shapesByData.TryGetValue(volume.ShapeHash, out CarShapeRecord? record)
                || shapeFiles.Contains(record.File))
            {
                continue;
            }
            records.Add(record);
            shapeFiles.Add(record.File);
            if (_stubsByFile.TryGetValue(record.Shape.Hash, out FrameObjectCollision? stub)) stubs.Add(stub);
        }

        // Deliberately without the hit boxes. Nothing here touches geometry, and a state that carried them
        // would write them back over the live model on every undo and demand a rewritten frame resource for
        // an edit that cannot have changed one.
        CarState before = Snapshot(shapeFiles, stubs, boxes: false);
        string kind = component.Kind;
        if (Prefab.TakeCarPart(component.PartIndex) == null)
        {
            refusal = "the prefab would not give the deform part up";
            return null;
        }
        foreach (CarShapeRecord record in records)
        {
            _pendingShapes[record.File] = null;
            Forget(record.File);
            _stubsByFile.Remove(record.Shape.Hash);
        }
        foreach (FrameObjectCollision stub in stubs) DropFrame(stub);

        return new CarEdit(
            $"\"{component.Name}\" is no longer a {kind} — it is a bare component again",
            before, Snapshot(shapeFiles, stubs, boxes: false));
    }

    /// <summary>What still holds a part, in the words a modder would use for it.</summary>
    private static string Holding(CarPartLinks links)
    {
        var held = new List<string>(4);
        if (links.Children > 0)
        {
            held.Add(links.Children == 1
                ? "a component hanging off it"
                : $"{links.Children.ToString(System.Globalization.CultureInfo.InvariantCulture)} components "
                    + "hanging off it");
        }
        if (links.Joints > 0) held.Add("one of this car's joints");
        if (links.Owners > 0 || links.IndexRows > 0) held.Add("the machinery that makes it come off the car");
        if (links.Drains > 0) held.Add("another part's drain-energy row");
        return string.Join(", ", held);
    }
}
