using System.Globalization;

namespace Illusion.Assets.Cars;

/// <summary>
/// Whether the toolkit can grow or shrink a car's RIG — and, until it can, the reasons it gives for not
/// trying.
///
/// <para>
/// This is the place the rig writer plugs into. Adding a component whose bone already exists is an ordinary
/// edit and works today (<see cref="Car.GrantDeformPart"/>); adding one that needs a NEW bone is the same
/// operation with one more step, and that step does not exist yet. Gate G1 of the rig plan is not passed:
/// the rest pose alone is written down in four places, and four of the fields the gate requires have never
/// been measured. So the operation is offered, refuses, and says what a modder can do instead.
/// </para>
/// <para>
/// The two conditions below are deliberately SEPARATE, and neither is a mood. Adding a bone APPENDS to every
/// per-bone array, so nothing that already exists is renumbered and the writer is a measurable amount of work
/// away. Removing one renumbers every vertex weight in the model and every index that names a later bone,
/// and it is not in the rig plan at all — so a rig writer landing must not switch component removal on as a
/// side effect of switching minting on. When each lands, its condition answers for itself, its refusal stops
/// firing, and nothing above it moves: not the menu, not the window, not a signature.
/// </para>
/// </summary>
public static class CarRig
{
    /// <summary>
    /// Whether a bone can be minted. The ONE condition the mint refusal hangs off — see
    /// <see cref="Car.AddComponent"/>, which is where the writer goes when there is one.
    /// </summary>
    public static bool CanMintBone => false;

    /// <summary>Whether a bone can be taken away, which is what removing a component outright means. Its own
    /// condition, for the reason in the type's own summary.</summary>
    public static bool CanRemoveBone => false;

    /// <summary>
    /// How many bones a rig can usefully hold: an attachment names its joint in ONE BYTE, so a rig past this
    /// has bones no attachment can point at.
    /// </summary>
    public const int BoneCeiling = 255;

    /// <summary>
    /// The widest remap pool the game itself ships — <c>cars_universal</c>'s. A pool is the palette one
    /// material's draw reaches into, and a material can only weight its vertices to bones its own pool holds,
    /// so a new bone has to join the pool of every material that uses it.
    /// </summary>
    public const int WidestShippedPool = 60;

    /// <summary>
    /// Why a bone cannot be minted, in words a modder can act on: make it in Blender, push it, and point a
    /// component at it.
    ///
    /// <para>
    /// The ceilings are stated HERE, against this car's own numbers, because this is the only place they are
    /// load-bearing — a modder reading "255" in a plan cannot tell whether their car is near it.
    /// </para>
    /// </summary>
    /// <param name="bone">The name that was asked for.</param>
    /// <param name="bones">How many bones this car's rig holds.</param>
    /// <param name="pool">How many bones its widest remap pool holds.</param>
    public static string WhyNoBone(string bone, int bones, int pool) =>
        $"\"{bone}\" is no bone of this car, and the toolkit cannot make one yet. A bone is written down in "
        + "at least four places in the model — the rest pose alone is in four — and the writer waits on "
        + "measurements that are not finished.\n\n"
        + "Make the bone in Blender, push it, and then add the component to it: a component whose bone is "
        + "already there is given its part today.\n\n"
        + $"{Ceilings(bones, pool)}";

    /// <summary>
    /// Why a component cannot be removed outright — and what takes its place, since demoting it is available
    /// and is what a modder asking for this usually wants.
    /// </summary>
    public static string WhyNoRemoval(string component, bool hasPart) =>
        $"\"{component}\" is a bone of this car's model, and removing the component means removing the bone. "
        + "Every vertex weight in the car is numbered against the bone list, so taking one out of the middle "
        + "renumbers all of them — and the prefab names bones by the hash of their name, which does not fail "
        + "when it resolves to nothing, it simply stops working.\n\n"
        + (hasPart
            ? "\"Remove deform part\" takes the damage model away instead and leaves the bone, its geometry "
                + "and its hit boxes exactly as they are. To take the bone itself away, delete it in Blender "
                + "and push."
            : "This component has no deform part to take away either — it is the bone and its geometry and "
                + "nothing else. To take it away, delete the bone in Blender and push.");

    /// <summary>What is left of the operation once the condition lets it through and there is still no writer
    /// behind it. Unreachable while <see cref="CanMintBone"/> and <see cref="CanRemoveBone"/> both answer
    /// false; it is here so that flipping one of them alone can never half-do an intent.</summary>
    public const string NoWriter =
        "the rig writer is not wired into this operation yet, so nothing was changed";

    /// <summary>The two ceilings, each against what this car already holds.</summary>
    private static string Ceilings(int bones, int pool)
    {
        string count = bones.ToString(CultureInfo.InvariantCulture);
        string widest = pool.ToString(CultureInfo.InvariantCulture);
        string ceiling = BoneCeiling.ToString(CultureInfo.InvariantCulture);
        string shipped = WidestShippedPool.ToString(CultureInfo.InvariantCulture);
        return "The two ceilings a new bone will answer to, against this car: an attachment names its bone in "
            + $"one byte, so a rig cannot usefully pass {ceiling} bones — this one holds {count}; and a "
            + "material can only weight its vertices to the bones its own remap pool holds, the widest the "
            + $"game ships being {shipped} — this car's widest holds {widest}.";
    }
}
