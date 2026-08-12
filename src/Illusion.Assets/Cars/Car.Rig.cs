using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>
/// Adding a component to a car, and taking one away — the two intents that reach past the assembly layer into
/// the RIG, and the place the rig writer plugs into.
///
/// <para>
/// A component is a bone. So adding one is two steps: mint the bone, then give it a deform part. The second
/// step is ticket 09's and works today, which is why adding a component to a bone that is already there is an
/// ordinary edit — a light or a licence plate made damageable without leaving the toolkit. The first step does
/// not exist: gate G1 of the rig plan is not passed, four of the fields it requires are unmeasured, and a rig
/// half-written is a car whose skin resolves to the wrong bones.
/// </para>
/// <para>
/// So the operation is OFFERED and refuses with what a modder can do instead — make the bone in Blender, push
/// it, and point a component at it. Removing a component outright is refused the same way and for a harder
/// reason: it would renumber every vertex weight in the model. Both refusals hang off a condition of their own
/// in <see cref="CarRig"/>, and when a writer lands it is the condition that changes and nothing else.
/// </para>
/// </summary>
public sealed partial class Car
{
    /// <summary>
    /// Adds a component to this car: the bone it is, the kind of part it carries, and which component it
    /// hangs off.
    ///
    /// <para>
    /// One entry point for both halves of the question, deliberately. A modder adding a component does not
    /// think "is this bone already in the rig" — they name the thing they want and the toolkit either points a
    /// part at a bone that is there or mints one. Today the second of those refuses, and the day it stops
    /// refusing this signature, the window above it and the menu item above that do not move.
    /// </para>
    /// </summary>
    /// <param name="bone">The bone the component is — its name in Blender, and the name the tree will show.</param>
    /// <param name="kind">What the part IS: one of <see cref="PartKinds"/>.</param>
    /// <param name="parent">Which component it hangs off. Both copies of the prefab's parent link are written
    /// from it, and the third — the child's index in the parent's own list — with them.</param>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then, and
    /// nothing is written — a refusal that leaves half an edit behind is worse than no feature.</returns>
    public CarEdit? AddComponent(
        string bone, CarPartTemplate kind, CarComponent parent, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(parent);
        refusal = null;

        string name = (bone ?? "").Trim();
        if (name.Length == 0)
        {
            refusal = "a component IS a bone, so it needs the bone's name — the one it has in Blender.";
            return null;
        }

        ulong hash = Fnv64.Hash(name);
        // The bone is already in the rig and already draws: this is the whole of adding a component today,
        // and it is ticket 09's grant unchanged. Every field of the new part but the kind, the bone and the
        // parent is derived there.
        if (ComponentOfBone(hash) is { } existing) return GrantDeformPart(existing, kind, parent, out refusal);

        if (_bones.TryGetValue(hash, out string? dark))
        {
            // In the rig, and not a component. Two different accidents, and they take two different answers:
            // a bone some part crumples AROUND is a list on that part and is claimed, whatever it draws;
            // anything else here is a bone nothing is weighted to — the rig roots and the hinge bones of
            // tracked vehicles, 101 of them over the corpus. Telling the first of those to weight geometry to
            // it would be advice that changes nothing.
            refusal = HandleOwner(hash) is { } owner
                ? $"\"{dark}\" is a deform handle of \"{owner}\" — a bone that part crumples AROUND, and a "
                    + "list on it rather than a thing of the car. No shipped car writes a bone as both (0 of "
                    + "1402), so it cannot be a component of its own while that part holds it. Name another "
                    + $"bone, or tune this one where it lives, on \"{owner}\"."
                : $"\"{dark}\" is a bone of this car, but nothing of the car is drawn from it — nothing is "
                    + "weighted to it, which is how the rig's own roots and hinge bones are written. Weight "
                    + "some geometry to it in Blender and push, and it will be here to give a part to.";
            return null;
        }

        // ── where the rig writer plugs in ──
        //
        // ONE condition, and it is the whole of what stands between this and a new bone. When
        // FrameSkeletonEditing lands, CarRig.CanMintBone answers for itself, this refusal stops firing, and
        // the mint goes below — with the grant above it as the second half, unchanged.
        if (!CarRig.CanMintBone)
        {
            (int bones, int pool) = RigCeilings();
            refusal = CarRig.WhyNoBone(name, bones, pool);
            return null;
        }
        refusal = CarRig.NoWriter;
        return null;
    }

    /// <summary>
    /// Takes a component off the car altogether — which means taking its BONE away, and is refused.
    ///
    /// <para>
    /// Not a caution: every vertex weight in the model is numbered against the bone list, and so are the mesh
    /// splits, the remap pools and the per-bone arrays inside the skeleton, so taking one bone out of the
    /// middle renumbers all of them. The prefab, meanwhile, names bones by the hash of their name, and a hash
    /// that resolves to nothing does not fail — it silently stops working.
    /// </para>
    /// <para>
    /// What a modder asking for this usually wants is <see cref="RemoveDeformPart"/>, which takes the damage
    /// model away and leaves the bone, its geometry and its hit boxes alone; the refusal says so.
    /// </para>
    /// </summary>
    /// <returns>Null, today always, with the reason in <paramref name="refusal"/> and nothing changed.</returns>
    public CarEdit? RemoveComponent(CarComponent component, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(component);

        // Its own condition, not the mint's. Adding a bone appends and renumbers nothing; removing one
        // renumbers everything, and it is not in the rig plan at all — so a rig writer landing must not switch
        // this on as a side effect.
        refusal = CarRig.CanRemoveBone
            ? CarRig.NoWriter
            : CarRig.WhyNoRemoval(component.Name, !component.IsBare);
        return null;
    }

    /// <summary>The component whose deform part crumples around this bone, when one does — what says a bone
    /// is a handle rather than a thing of the car. Null when no part claims it.</summary>
    private string? HandleOwner(ulong bone)
    {
        foreach (CarDeformPart part in Prefab.CarDeformParts)
        {
            foreach (CarDeformHandle handle in part.Handles)
            {
                if (handle.JointName != bone) continue;
                // Named by its COMPONENT, not by its part index: "a deform handle of doorFL" is the sentence
                // a modder can act on, and "of part 7" is the label this whole view exists to take away.
                return ComponentOfBone(part.Frame)?.Name
                    ?? part.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return null;
    }

    /// <summary>
    /// The two numbers a new bone would have to fit under, read off this car: how many bones its rig holds,
    /// and how many its widest remap pool holds.
    ///
    /// <para>
    /// Read rather than remembered, because a car that has been through Blender is not the car that shipped.
    /// Both fall back to zero on an archive whose model will not open, which reads in the refusal as "nothing
    /// measured" rather than as a claim about the car.
    /// </para>
    /// </summary>
    private (int Bones, int Pool) RigCeilings()
    {
        FrameObjectModel? model = Frames?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
        if (model == null) return (0, 0);

        int bones = 0, pool = 0;
        // Both accessors build their block on first ask and throw out of the native reader when the resource
        // is not there — the same reason the stitcher's own reads are wrapped.
        try { bones = model.GetSkeletonObject().BoneNames?.Length ?? 0; }
        catch (Exception) { /* no skeleton to measure — the refusal says zero rather than guessing */ }
        try
        {
            foreach (FrameBlendInfo.BoneIndexInfo level in model.GetBlendInfoObject().BoneIndexInfos ?? [])
            {
                foreach (byte size in level.BonesPerRemapPool ?? []) pool = Math.Max(pool, size);
            }
        }
        catch (Exception) { /* likewise: no blend info, no pool to report */ }
        return (bones, pool);
    }
}
