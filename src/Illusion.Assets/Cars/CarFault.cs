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
}

/// <summary>
/// One thing the resolver could not stitch together, named rather than dropped.
/// </summary>
/// <param name="Kind">Which failure it is.</param>
/// <param name="What">The one line a modder can act on — which part, which bone, which marker.</param>
/// <param name="Component">The component it belongs to, when it belongs to one.</param>
public sealed record CarFault(CarFaultKind Kind, string What, ComponentId Component = default)
{
    public override string ToString() => $"{Kind}: {What}";
}
