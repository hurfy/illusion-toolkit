namespace Illusion.Assets.Cars;

/// <summary>What a marker IS to the car — which parallel list it came out of.</summary>
public enum CarMarkerRole
{
    /// <summary>Where an occupant sits. A Dummy on 213 of 213 shipped seats.</summary>
    Seat,

    /// <summary>Where the player may climb on. A Dummy on 280 of 280 — but the game reads the ROW's
    /// world-space corners, not the Dummy, so moving the frame alone changes nothing.</summary>
    ClimbBox,

    /// <summary>Where the tank is. A Dummy on 87 of 87.</summary>
    FuelTank,

    /// <summary>Where the exhaust smokes from. A Point on 195 of 195, and a car carries more of them than
    /// the one <c>exhaust</c> bone, so the extras sit on sibling bones.</summary>
    ExhaustEmitter,

    /// <summary>A wiper. A BONE, not a helper hung off one, and no deform part ever claims it.</summary>
    Wiper,

    /// <summary>A light model — headlight, backlight or toplight. A Point.</summary>
    Light,
}

/// <summary>
/// One helper the car hangs off a bone: a seat, a climb box, a tank, an emitter, a light, a wiper.
///
/// <para>
/// A marker belongs to the component owning the bone it hangs off, and to the body when no component owns
/// that bone. Measured over the corpus, 823 of 1081 land on a bone a deform part owns directly; the other
/// 258 all reach an owned bone at exactly one hop up the rig and land on the body in 254 of those — which is
/// what makes the body the safe default rather than a guess.
/// </para>
/// </summary>
/// <param name="Role">Which list it came from.</param>
/// <param name="Index">Its position in that list — how an edit addresses it.</param>
/// <param name="Label">What to call it on a row: "Seat 2", "Headlight".</param>
/// <param name="Frame">FNV64 of the frame the row names — often a Dummy or a Point, sometimes the bone.</param>
/// <param name="Name">That frame's name, or the bare hash when nothing in the archive resolves to it.</param>
/// <param name="Bone">FNV64 of the bone it ends up on, 0 when it reaches none.</param>
/// <param name="OnOwnBone">Whether the row names the bone directly rather than a helper hung off one.</param>
/// <param name="Resolved">Whether it reached a bone at all. False on 0 of 1081 shipped markers, so it is the
/// signature of a rename or a hand-edited prefab rather than of anything the game ships.</param>
public sealed record CarMarker(
    CarMarkerRole Role, int Index, string Label, ulong Frame, string Name, ulong Bone,
    bool OnOwnBone, bool Resolved)
{
    public override string ToString() => Label;
}
