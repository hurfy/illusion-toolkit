using System.Numerics;

namespace Illusion.Formats.Prefab;

/// <summary>
/// How the shipped cars write one KIND of deformable part — the numbers a new part of that kind is given, so
/// that a minted one is written the way the corpus writes its neighbours rather than the way a default
/// constructor writes zeroes.
///
/// <para>
/// Every field here is the commonest WHOLE combination the 85 shipped prefabs carry for that kind, taken
/// together rather than field by field: a per-field mode can assemble a part no car is actually written as.
/// <see cref="PrefabFile.MintablePartKinds"/> says how many parts of each kind carry it.
/// </para>
/// </summary>
/// <param name="Type">The engine's own part kind — <c>S_InitDeformPart.Unk0</c>.</param>
/// <param name="Flags">The 32-bit flag word. Bit 8 is set on every one of the 1698 shipped parts and nothing
/// has read it; the named bits are in <c>Car.Damage</c>, and a modder edits them there afterwards.</param>
/// <param name="Threshold">Both <c>unk5</c> and <c>unk6</c>, which are equal on all 1698 shipped parts and
/// whose meaning is unread. 200 on the panels that come off, 80 on the body and its fixtures, 60 on glass.</param>
/// <param name="SpeedMin">The bottom of the impact-speed window, in metres per second — the shipped numbers
/// are round in km/h (1.388889 is 5 km/h, 11.111112 is 40, 8.333334 is 30, 4.166667 is 15).</param>
/// <param name="Damping">The <c>common</c> block's <c>unk7</c>, an int holding float bits — 0.5f on 1546 of
/// the 1698 shipped parts and 1.0f on 136. Written raw, because what it means has not been read.</param>
/// <param name="Shipped">How many shipped parts of this kind are written exactly this way, out of how many
/// there are — the honesty of the template, stated where a reader can see it.</param>
public sealed record CarPartTemplate(
    uint Type, uint Flags, float Threshold,
    float SpeedMin, float SpeedMax, float Resistance, float Mass, float EnergyStart, float EnergyDrop,
    int Damping, int Shipped, int OfKind)
{
    /// <summary>That kind in words — <c>normal</c>, <c>door</c>, <c>window</c>.</summary>
    public string Name => PrefabFile.PartKindName(Type);
}

/// <summary>
/// What still names a deform part, and therefore what a removal would have to renumber or orphan.
///
/// <para>
/// A part is addressed by its POSITION in the list, and the deformation block indexes it from four other
/// places. Three of them belong to the machinery that makes a panel come OFF the car — a joint, an owner
/// deform, and the hash→index table that points a bone at its owner deform — and none of that has been read.
/// So a caller is given the counts and decides; this layer states the facts and writes no prose.
/// </para>
/// </summary>
/// <param name="Children">Parts hanging off this one, by either copy of the parent link.</param>
/// <param name="Joints">Joints naming it, by index or through a break-energy row.</param>
/// <param name="Owners">Owner-deform rows naming it, by index or by its bone's hash.</param>
/// <param name="Drains">Drain-energy rows on other parts naming it.</param>
/// <param name="IndexRows">Rows of the deformation block's own hash→index table naming its bone.</param>
public sealed record CarPartLinks(int Children, int Joints, int Owners, int Drains, int IndexRows)
{
    /// <summary>Whether anything at all still names this part.</summary>
    public bool Any => Children > 0 || Joints > 0 || Owners > 0 || Drains > 0 || IndexRows > 0;
}

public sealed partial class PrefabFile
{
    /// <summary>
    /// The kinds a NEW deformable part may be given, with what the shipped cars write each one as.
    ///
    /// <para>
    /// Five of the engine's thirteen kinds are deliberately absent. <c>body</c> because a car has exactly one
    /// and it is marked as such by a field nothing else sets (<c>unk2</c> is 1 on 85 of 85 bodies and 0 on the
    /// other 1613 parts); <c>wheel</c>, <c>lid</c> and <c>tyre</c> because no shipped car carries one, so
    /// there is nothing to copy their numbers from; <c>plow</c> and the unnamed kind 11 because between them
    /// they ship 15 times, on the tracked vehicles alone.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CarPartTemplate> MintablePartKinds { get; } =
    [
        // Measured 2026-08-11 over the 85 shipped prefabs (`--probe-car-parts`). The speed windows read as
        // round km/h figures, which is what says they are metres per second rather than an arbitrary scale.
        new(0, 0x00000108u, 80f, 3f, 10f, 0f, 2f, 1f, 0f, 0x3F000000, 59, 213),
        new(4, 0x00000302u, 200f, 1.388889f, 11.111112f, 0f, 30f, 1f, 0f, 0x3F000000, 184, 217),
        new(5, 0x00000112u, 60f, 4.166667f, 8.333334f, 0f, 2f, 1f, 0f, 0x3F000000, 120, 527),
        new(6, 0x00000302u, 200f, 1.388889f, 8.333334f, 0f, 10f, 1f, 0f, 0x3F000000, 56, 181),
        new(7, 0x0000030Au, 200f, 1.388889f, 11.111112f, 0f, 20f, 1f, 0f, 0x3F000000, 107, 124),
        new(12, 0x0000030Au, 200f, 3f, 10f, 0f, 5f, 1f, 0f, 0x3F800000, 54, 77),
        new(13, 0x00000108u, 80f, 5.555556f, 27.777779f, 0.14999999f, 2f, 1f, 0f, 0x3F800000, 76, 78),
        new(15, 0x80000508u, 80f, 3f, 10f, 0f, 2f, 1f, 0f, 0x3F000000, 167, 181),
    ];

    /// <summary>The template for one kind, or null when that kind cannot be minted.</summary>
    public static CarPartTemplate? PartTemplate(uint type) =>
        MintablePartKinds.FirstOrDefault(t => t.Type == type);

    /// <summary>1.0f as the bits the <c>common</c> block's own <c>unk2</c> carries — the same single value on
    /// all 1698 shipped parts.</summary>
    private const int CommonUnk2Value = 0x3F800000;

    /// <summary>The file's "no parent part", which is a <c>ushort</c> and not -1.</summary>
    private const ushort NoParentIndex = 65535;

    /// <summary>
    /// Gives a bone a deformable part of its own — the whole of turning a licence plate, a light or a wiper
    /// into something the damage model knows about.
    ///
    /// <para>
    /// The new part is a COPY of a shipped part of the same kind on this very car, with everything the toolkit
    /// authors overwritten and everything that was the donor's own cleared: its volumes, its deform handles,
    /// its drain-energy rows, its children and the two lists that tie it into the machinery that makes a panel
    /// come off. What rides along is the half of the <c>common</c> block nobody has read — <c>unk5_data</c>
    /// and the effects row, which carries eighteen fields of which the toolkit names six. Inventing those
    /// would produce a part that exists and behaves like nothing; copying the neighbour gives values that are
    /// already right for this car. It is the same rule <see cref="AddCarItem"/> follows, for the same reason.
    /// </para>
    /// <para>
    /// THE PARENT LINK IS WRITTEN THREE TIMES, not two. As a name hash on the child, as an index beside it,
    /// and as the child's index in the PARENT's own list — measured over the corpus: a part's index is in its
    /// parent's <c>unk20</c> on 934 of 934, and every one of those 934 entries names a part claiming that
    /// parent back. A writer that moved two of the three would leave a car whose damage model disagrees with
    /// itself.
    /// </para>
    /// <para>
    /// The part is APPENDED, which is what keeps this from renumbering anything: every index the file already
    /// holds is below the new one and stays where it was.
    /// </para>
    /// </summary>
    /// <param name="parentIndex">Which part this one hangs off. Its bone supplies the hash copy of the link,
    /// so the two copies cannot disagree.</param>
    /// <param name="effectGroup">Which particle a hit on it throws — the parent's, by default, since
    /// components sharing the number behave alike.</param>
    /// <returns>The new part's index, or -1 when this file has no car, the kind cannot be minted, the bone is
    /// empty or the parent is not a part of this car.</returns>
    public int AddCarPart(uint partType, ulong bone, int parentIndex, byte effectGroup)
    {
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        if (parts == null || parts.Count == 0 || bone == 0) return -1;
        if (parentIndex < 0 || parentIndex >= parts.Count) return -1;
        if (parts[parentIndex].Unk3.Count == 0) return -1;
        if (PartTemplate(partType) is not { } template) return -1;

        Native.Model.PrefabDeformPartW part = Clone(Donor(parts, partType));

        part.PartType = partType;
        part.Flags = template.Flags;
        // 1 on 85 of 85 bodies and 0 on the other 1613 parts, so it is the body's own marker and a copy of
        // the body must not carry it out.
        part.Unk2 = 0;
        part.Unk3.Clear();
        part.Unk3.Add(bone);
        part.Unk4 = 1f;                                     // 1.0 on all 1698
        part.Unk5 = template.Threshold;
        part.Unk6 = template.Threshold;
        // The bone's own origin. The centre of mass is the modder's to move, on the component's Damage row.
        part.CentreOfMass = Vector3.Zero;
        part.InternalImpulses.Clear();                      // empty on all 1698
        part.DropParts.Clear();                             // empty on all 1698
        part.DrainEnergy.Clear();                           // indexes other parts — the donor's, not ours
        part.CollisionVolumes.Clear();
        part.CollisionVolumes.Add(new Native.Model.PrefabCollVolumeCollectionW());
        part.SmDeformBones.Clear();
        // The detach machinery: unk14 names owner-deform rows, and a minted part is in none of them.
        part.Unk14.Clear();
        part.PartTransform = Identity();
        part.ParentDeformPartName = parts[parentIndex].Unk3[0];
        part.Unk17 = (ushort)parentIndex;
        part.Unk18 = 0;                                     // 0 on all 1698
        part.Unk19 = effectGroup;
        part.Unk20.Clear();
        part.Unk21Data.Clear();                             // empty on all 1698
        part.Unk22RelData.Clear();                          // empty on all 1698
        part.Unk23 = 0;                                     // 0 on all 1698
        part.Unk24 = 0;                                     // 0 on all 1698

        // The common block is present on all 1698, so a part without one is a part the game reads no tuning
        // for at all.
        while (part.Common.Count > 1) part.Common.RemoveAt(part.Common.Count - 1);
        if (part.Common.Count == 0) part.Common.Add(new Native.Model.PrefabDeformPartCommonW());
        Native.Model.PrefabDeformPartCommonW common = part.Common[0];
        common.SpeedMin = template.SpeedMin;
        common.SpeedMax = template.SpeedMax;
        common.Resistance = template.Resistance;
        common.Mass = template.Mass;
        common.EnergyStart = template.EnergyStart;
        common.EnergyDrop = template.EnergyDrop;
        common.Unk2.Clear();
        common.Unk2.Add(CommonUnk2Value);                   // one row holding 1.0f on all 1698
        common.Unk3Transform.Clear();                       // empty on all 1698
        common.Unk4.Clear();                                // empty on all 1698
        common.Unk6Value.Clear();                           // empty on 1674 of 1698, and it holds NAMES
        common.Unk7 = template.Damping;
        common.Unk8.Clear();                                // empty on all 1698

        parts.Add(part);
        int at = parts.Count - 1;
        parts[parentIndex].Unk20.Add((ushort)at);
        return at;
    }

    /// <summary>
    /// Takes a deformable part away, and every index that pointed past it back one.
    ///
    /// <para>
    /// The renumbering is the whole difficulty. Six places in the deformation block address a part by its
    /// position — the parent link's index copy, the parent's own children list, the drain-energy rows, a
    /// joint's four fields, a joint's break-energy rows and an owner deform's two index lists — and a list
    /// that lost an entry silently repoints every one of them at its neighbour. Every index PAST the removed
    /// part comes back one, here, whoever the caller is.
    /// </para>
    /// <para>
    /// <b>An index AT the removed part is a different question, and this cannot answer it.</b> A list entry
    /// can be dropped and is — the children list, the drain-energy rows, the break-energy rows — and the
    /// parent index has the file's own 65535 to fall back to. A joint's four fields and an owner deform's
    /// index lists have neither: every one of the 2716 shipped joint fields is a live part index and the
    /// format offers no "no part", so a joint that named the part being removed cannot be repaired, only
    /// left naming its neighbour. That is why <see cref="CarPartLinksOf"/> exists and why the caller is
    /// expected to ask it FIRST — this method is the writer, not the gate.
    /// </para>
    /// </summary>
    /// <returns>The part's bytes, so an undo could put back exactly what was there, or null when there is no
    /// such part.</returns>
    public byte[]? TakeCarPart(int at)
    {
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        if (parts == null || at < 0 || at >= parts.Count) return null;

        byte[] bytes = Pack(w => parts[at].WriteTo(w));
        parts.RemoveAt(at);

        foreach (Native.Model.PrefabDeformPartW part in parts)
        {
            part.Unk17 = part.Unk17 == at ? NoParentIndex
                : part.Unk17 != NoParentIndex && part.Unk17 > at ? (ushort)(part.Unk17 - 1)
                : part.Unk17;
            Shift(part.Unk20, at);
            for (int i = part.DrainEnergy.Count - 1; i >= 0; i--)
            {
                uint drain = part.DrainEnergy[i].DrainPart;
                if (drain == at) part.DrainEnergy.RemoveAt(i);
                else if (drain > at) part.DrainEnergy[i].DrainPart = drain - 1;
            }
        }

        if (Deformation() is { } deformation)
        {
            foreach (Native.Model.PrefabJointW joint in deformation.Joints)
            {
                joint.Unk0 = Below(joint.Unk0, at);
                joint.Unk1 = Below(joint.Unk1, at);
                joint.Unk2 = Below(joint.Unk2, at);
                joint.Unk3 = Below(joint.Unk3, at);
                for (int i = joint.PartBreakEnergy.Count - 1; i >= 0; i--)
                {
                    int part = joint.PartBreakEnergy[i].PartId;
                    if (part == at) joint.PartBreakEnergy.RemoveAt(i);
                    else if (part > at) joint.PartBreakEnergy[i].PartId = part - 1;
                }
            }
            foreach (Native.Model.PrefabOwnerDeformW owner in deformation.OwnerDeforms)
            {
                Shift(owner.Unk4, at);
                Shift(owner.Unk6, at);
            }
        }
        return bytes;
    }

    /// <summary>
    /// What still names one deformable part — the question a caller has to ask before taking it away.
    /// </summary>
    public CarPartLinks CarPartLinksOf(int at)
    {
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        if (parts == null || at < 0 || at >= parts.Count) return new CarPartLinks(0, 0, 0, 0, 0);

        ulong bone = parts[at].Unk3.Count > 0 ? parts[at].Unk3[0] : 0;
        int children = 0, joints = 0, owners = 0, drains = 0, rows = 0;

        for (int i = 0; i < parts.Count; i++)
        {
            if (i == at) continue;
            Native.Model.PrefabDeformPartW part = parts[i];
            // Either copy of the parent link counts: a child written only by hash is still a child, and
            // leaving it naming a part that is gone is the fault the tree calls an unresolved parent.
            if (part.Unk17 == at || (bone != 0 && part.ParentDeformPartName == bone)) children++;
            foreach (Native.Model.PrefabDrainEnergyW drain in part.DrainEnergy)
            {
                if (drain.DrainPart == at) drains++;
            }
        }

        if (Deformation() is { } deformation)
        {
            foreach (Native.Model.PrefabJointW joint in deformation.Joints)
            {
                if (joint.Unk0 == at || joint.Unk1 == at || joint.Unk2 == at || joint.Unk3 == at) joints++;
                foreach (Native.Model.PrefabPartBreakEnergyW energy in joint.PartBreakEnergy)
                {
                    if (energy.PartId == at) joints++;
                }
            }
            foreach (Native.Model.PrefabOwnerDeformW owner in deformation.OwnerDeforms)
            {
                if (bone != 0 && (owner.Unk0 == bone || owner.Unk1 == bone)) owners++;
                if (owner.Unk4.Contains((ushort)at) || owner.Unk6.Contains((ushort)at)) owners++;
                foreach (Native.Model.PrefabPartMatrixW matrix in owner.PartTransforms)
                {
                    if (bone != 0 && matrix.PartHashName == bone) owners++;
                }
            }
            foreach (Native.Model.PrefabHashIndexW pair in deformation.Unk1Pairs)
            {
                if (bone != 0 && pair.Hash == bone) rows++;
            }
        }
        return new CarPartLinks(children, joints, owners, drains, rows);
    }

    /// <summary>Drops the entries naming a part that has gone and brings the ones past it back one.</summary>
    private static void Shift(List<ushort> indices, int removed)
    {
        for (int i = indices.Count - 1; i >= 0; i--)
        {
            if (indices[i] == removed) indices.RemoveAt(i);
            else if (indices[i] > removed) indices[i]--;
        }
    }

    /// <summary>An index past a part that has gone, brought back one. A value AT the removed part is left
    /// alone deliberately: the fields this is used for carry no "no part" to write instead — see
    /// <see cref="TakeCarPart"/>.</summary>
    private static ushort Below(ushort value, int removed) =>
        value != NoParentIndex && value > removed ? (ushort)(value - 1) : value;

    /// <summary>
    /// The part a new one is copied from: one of the same kind on this car, or the body, or whatever is
    /// there. The body is present on 85 of 85 shipped cars, so the third case is only reached on a car
    /// something has already been done to.
    /// </summary>
    private static Native.Model.PrefabDeformPartW Donor(
        List<Native.Model.PrefabDeformPartW> parts, uint partType) =>
        parts.FirstOrDefault(p => p.PartType == partType)
        ?? parts.FirstOrDefault(p => p.PartType == BodyPartType)
        ?? parts[0];

    private const uint BodyPartType = 1;

    /// <summary>A deep copy, through the wire model's own reader and writer — the one copy that stays right
    /// when a field is added to the generated model.</summary>
    private static Native.Model.PrefabDeformPartW Clone(Native.Model.PrefabDeformPartW part)
    {
        using var buffer = new MemoryStream();
        part.WriteTo(new BinaryWriter(buffer));
        buffer.Position = 0;
        return Native.Model.PrefabDeformPartW.ReadFrom(new BinaryReader(buffer));
    }

    private static Native.Model.PrefabTransformW Identity() => new()
    {
        Row0 = new Vector3(1f, 0f, 0f),
        Row1 = new Vector3(0f, 1f, 0f),
        Row2 = new Vector3(0f, 0f, 1f),
        Translation = Vector3.Zero,
    };

    /// <summary>The car's deformation block — where the parts and everything that indexes them live.</summary>
    private Native.Model.PrefabDeformationInitW? Deformation()
    {
        Native.Model.PrefabEntryW? entry = Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0);
        Native.Model.PrefabCarInitW? car = entry?.CarInit[0];
        return car is { Deformation.Count: > 0 } ? car.Deformation[0] : null;
    }
}
