namespace Illusion.Assets.Cars;

/// <summary>
/// What the resolver could not stitch. Every one of these is shown as itself rather than hidden or refused:
/// the broken cars are the ones the toolkit is most needed for, so a car that cannot be fully understood
/// still opens and says what it could not make sense of.
/// </summary>
public enum CarFaultKind
{
    /// <summary>A deform part that names no bone at all. Its component exists and is nameless — every part
    /// yields exactly one component, whether or not it points anywhere.</summary>
    PartWithoutBone,

    /// <summary>A component whose bone is not in this car's rig — the signature of a rename made in Blender.
    /// The hash resolves to nothing, the game silently stops moving the panel, and this is the only place
    /// that can be seen before the car spawns.</summary>
    ComponentBoneUnresolved,

    /// <summary>Two components claiming the same bone. Unique on 85 of 85 shipped cars, so the bone-to-
    /// component lookup is a straight one — and when it is not, the lookup keeps the first and says so.</summary>
    DuplicateComponentBone,

    /// <summary>The prefab's two copies of a parent link — the name hash on the part and the index beside it
    /// — name different components. They agree on 934 of 934 shipped parts; a writer that moved only one of
    /// them is what this catches.</summary>
    ParentLinksDisagree,

    /// <summary>A part naming a parent that is no frame of this car at all — a reference the game follows
    /// into silence. Naming a frame that simply mints no component is how the top of the tree is written on
    /// 80 of 85 shipped cars, and is not this.</summary>
    ParentUnresolved,

    /// <summary>A parent link that would close a loop. Refused, and the component is left a root, because a
    /// tree with a cycle in it is walked forever rather than drawn.</summary>
    ParentLoop,

    /// <summary>A marker whose frame reaches no bone, so there is nothing to hang it off.</summary>
    MarkerUnresolved,

    /// <summary>No body component. The body is where every homeless marker goes, and a part of kind
    /// <c>body</c> is present on 85 of 85 shipped cars — always on the shared scale bone.</summary>
    NoBody,

    /// <summary>
    /// A row in one of the prefab's own sibling collections — a door, a window, an axle, a wiper, a driving
    /// wheel — whose bone reaches no component at all. The parallel lists do not line up one to one, and the
    /// half that names nothing would otherwise be dropped in silence.
    /// </summary>
    RowWithoutComponent,

    /// <summary>
    /// The other half of the same disagreement: a component of a kind that has a sibling collection of its
    /// own — a door, a window — with no row in it. The game reads that collection for the handle, the lock
    /// and whether the pane rolls down, so a door with no door row is a door that does not open.
    /// </summary>
    ComponentWithoutRow,

    /// <summary>
    /// A bone that no deform part claims, that still holds its seat in the split table, and whose every piece
    /// has lost its last face.
    ///
    /// <para>
    /// This is the one failure mode the tolerance rule would hide: a bare component is minted FROM its
    /// geometry, so losing the last of it does not break anything visibly — the row simply stops appearing on
    /// the next resolve and the modder is left looking for a component that was there a push ago.
    /// </para>
    /// </summary>
    BareComponentLostGeometry,

    /// <summary>
    /// A component whose frame is not on the frame name table. It loads and is invisible in game, and since
    /// nothing else in the archive disagrees, the editor is the only place this can be caught before the car
    /// is spawned.
    /// </summary>
    FrameNotOnNameTable,
}

/// <summary>
/// One thing the resolver could not stitch together, named rather than dropped.
/// </summary>
/// <param name="Kind">Which failure it is.</param>
/// <param name="What">The one line a modder can act on — which part, which bone, which marker.</param>
/// <param name="Component">The component it belongs to, when it belongs to one.</param>
/// <param name="ShipsThisWay">
/// Whether the shipped corpus is written like this — measured, not guessed.
///
/// <para>
/// It is the difference between "the toolkit lost the thread of this car" and "this car is unusual", and it
/// is a property of the FAULT rather than of its kind: a bone that still holds its seat in the split table
/// with no face left in it is how <c>berkley_kingfisher_pha</c> ships, while the same kind raised because a
/// component drew a moment ago and does not now is damage done in this session.
/// </para>
/// <para>
/// Measured 2026-08-10 over 85 cars: 41 faults on 8 of them, every one of them one of the three kinds that
/// set this. Without the distinction the panel tells a modder that a stock archive did not stitch, and a
/// diagnosis that cries wolf on a healthy car is one nobody reads on a broken one.
/// </para>
/// </param>
public sealed record CarFault(
    CarFaultKind Kind, string What, ComponentId Component = default, bool ShipsThisWay = false)
{
    /// <summary>
    /// The failure in a few words, for a modder rather than for a log. It says what is WRONG — not which
    /// enum member was raised — because the line beside it already names the part, the bone or the row.
    /// </summary>
    public string Title => Kind switch
    {
        CarFaultKind.PartWithoutBone => "Part names no bone",
        CarFaultKind.ComponentBoneUnresolved => "Bone is not in this car",
        CarFaultKind.DuplicateComponentBone => "Two parts claim one bone",
        CarFaultKind.ParentLinksDisagree => "Parent written two ways",
        CarFaultKind.ParentUnresolved => "Parent is not in this car",
        CarFaultKind.ParentLoop => "Parent link closes a loop",
        CarFaultKind.MarkerUnresolved => "Marker reaches no bone",
        CarFaultKind.NoBody => "No body",
        CarFaultKind.RowWithoutComponent => "Row with no component",
        CarFaultKind.ComponentWithoutRow => "Component with no row",
        CarFaultKind.BareComponentLostGeometry => "Geometry is gone",
        CarFaultKind.FrameNotOnNameTable => "Not on the frame name table",
        _ => Kind.ToString(),
    };

    public override string ToString() => $"{Kind}: {What}";
}
