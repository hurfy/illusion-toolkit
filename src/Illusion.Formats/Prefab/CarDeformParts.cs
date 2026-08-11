using System.Numerics;

namespace Illusion.Formats.Prefab;

/// <summary>
/// One deformable part of a car: a bone, everything the damage model knows about it, and the collision
/// volumes hung off it. This is the layer that actually gives a car its physics — see
/// <see cref="PrefabFile.CarDeformParts"/>.
/// </summary>
/// <param name="Index">Position in the prefab's part list — how every edit addresses it.</param>
/// <param name="PartType">The engine's own part kind (1 body, 4 door, 6 cover, 5 window, 13 motor, …).</param>
/// <param name="Kind">That kind in words, or the number when it is one nothing has named yet.</param>
/// <param name="Frame">FNV64 of the bone this part is; a type-5 volume below is placed in ITS space.</param>
/// <param name="ParentFrame">FNV64 of the part this one hangs off — and the space a self-describing volume
/// below is written in, which is not the same thing.</param>
/// <param name="PartTransform">The part's own placement, as the file writes it (axes already converted).
/// Read but not used by the editor: what it is relative to has not been measured.</param>
/// <param name="Effects">The particle ids this part carries — see <see cref="CarPartEffects"/>.</param>
public sealed record CarDeformPart(
    int Index, uint PartType, string Kind, uint Flags, ulong Frame, ulong ParentFrame,
    IReadOnlyList<CarPhysicsVolume> Volumes, Matrix4x4 PartTransform,
    IReadOnlyList<CarPartEffects> Effects)
{
    /// <summary>
    /// The SECOND copy of the parent link: the position of the parent part in the same list, or -1 when the
    /// file leaves it unset (65535).
    ///
    /// <para>
    /// The prefab writes the parent twice — as <see cref="ParentFrame"/> on every one of the 1698 shipped
    /// parts, and as this index on 934 of them — and the two agree on 934 of 934. So this is not a second
    /// opinion to choose between, it is a second field a writer has to move, and the reason it is surfaced
    /// at all is that moving only one of them leaves a car whose damage model disagrees with itself.
    /// </para>
    /// </summary>
    public int ParentIndex { get; init; } = -1;

    /// <summary>
    /// Which effect group a hit on this part belongs to. Proven in game: parts sharing the number throw the
    /// same particle, and it is neither the part kind nor the parent chain — see
    /// <see cref="CarValueSlot.DeformPartEffectGroup"/> for the readings that settled it.
    /// </summary>
    public byte EffectGroup { get; init; }

    /// <summary>The damage model's centre of mass for THIS part — not the car class's, which lives in the
    /// EDS record and is a different quantity on the same car.</summary>
    public Vector3 CentreOfMass { get; init; }

    /// <summary>
    /// The bones this part crumples AROUND, with how far and how hard. Never parts of their own: a deform
    /// joint is some part's handle on 1402 of 1402, and is never also a part's own bone (0 of 1402).
    /// </summary>
    public IReadOnlyList<CarDeformHandle> Handles { get; init; } = [];

    /// <summary>What it takes to move this part, or null when the file carries no tuning block for it.</summary>
    public CarDeformTuning? Tuning { get; init; }
}

/// <summary>
/// The effects block at the tail of a deformable part — the ids of the particles it plays.
///
/// <para>
/// Read, not written, and surfaced because of a question nothing else in the archive could answer: a shot
/// at a car plays a different impact for glass than for sheet metal, and neither the shape's own surface
/// nor the part's kind decides it (both refuted in game). This block sits beside the collision volumes,
/// carries named particle ids, and had never been opened.
/// </para>
/// </summary>
/// <param name="Packs">Sub-entries the reference toolkit leaves entirely unnamed.</param>
public sealed record CarPartEffects(
    int Index, short ParticleBreakId, short ParticleHingeVersionId,
    short Snow0, short Snow1, short Snow2, short Snow3, float ParticleScale, int Packs);

/// <summary>
/// One deform handle of a part — a bone the panel bends around when it is hit, and the three numbers that
/// say how much: how far it may travel, how hard it resists, and over what radius the panel follows it.
/// </summary>
/// <param name="Index">Position in the part's own handle list — how an edit addresses it.</param>
/// <param name="JointName">FNV64 of the handle bone's name; <c>deform_*</c> on 1301 of 1402 shipped ones.</param>
/// <param name="Range">How far the handle may move, per axis.</param>
public sealed record CarDeformHandle(
    int Index, ulong JointName, Vector3 OriginalPosition, Vector3 Range, Vector3 MoveAccumulator,
    float Intensity, float CRadius);

/// <summary>
/// What it takes to move one deformable part: the damage model's own numbers, per part.
/// <para>
/// These are the part's, not the car's. The car class carries a <c>Mass</c> and a <c>CenterOfMass</c> of its
/// own in the EDS record, and the two must never be shown under the same bare label.
/// </para>
/// </summary>
public sealed record CarDeformTuning(
    float SpeedMin, float SpeedMax, float Resistance, float Mass, float EnergyStart, float EnergyDrop);

public sealed partial class PrefabFile
{
    /// <summary>
    /// The car's deformable parts and the collision volumes on them, in file order.
    ///
    /// <para>
    /// This — not the <c>FrameObjectCollision</c> stubs in the frame graph — is where a car's physics lives.
    /// Both copies of a placement are in shipped archives and they agree (1060 of 1097 pairs, the rest
    /// already edited), but only this one is read: moving a stub changes nothing in game, which is what sent
    /// the search here in the first place.
    /// </para>
    /// </summary>
    public IReadOnlyList<CarDeformPart> CarDeformParts
    {
        get
        {
            List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
            if (parts == null) return [];

            var result = new List<CarDeformPart>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                Native.Model.PrefabDeformPartW part = parts[i];
                var volumes = new List<CarPhysicsVolume>();
                foreach (Native.Model.PrefabCollVolumeW v in Volumes(part))
                {
                    // The matrix is reversed and the EXTENTS ARE NOT, which is the one thing here that looks
                    // like an oversight and is not. The reversal was measured on the shipped stub/volume
                    // pairs, and every one of those is a type-5 volume whose extents are the placeholder 1 cm
                    // on all three axes — so it was verified for the matrix and never once for the numbers,
                    // and only a self-describing volume has numbers worth reversing.
                    //
                    // Measured (`--probe-car-physics`, "are a pane's in-plane extents the right way round"):
                    // a car window is wider than it is tall, on every car ever made. Taken as written, 373 of
                    // 415 shipped window panes come out that way; reversed, 69 do. A windscreen read 1.35 m
                    // TALL by 0.43 m across, and the door glass stood on edge. The file indexes its extents
                    // against the basis the reversal PRODUCES, not the one it starts from.
                    volumes.Add(new CarPhysicsVolume(
                        volumes.Count, v.VolumeType, SwapAxes(ToMatrix(v.Transform)),
                        v.Extents, v.Unk4Hashes.Count > 1 ? v.Unk4Hashes[1] : 0));
                }
                var effects = new List<CarPartEffects>();
                // Common is an optional block on the wire, so the core models it as a 0-or-1 list.
                foreach (Native.Model.PrefabDeformPartEffectsW e in
                         part.Common.SelectMany(c => c.PartEffects))
                {
                    effects.Add(new CarPartEffects(
                        effects.Count, e.ParticleBreakId, e.ParticleHingeVersionId,
                        e.SnowParticleId0, e.SnowParticleId1, e.SnowParticleId2, e.SnowParticleId3,
                        e.ParticleScale, e.Packs.Count));
                }
                var handles = new List<CarDeformHandle>(part.SmDeformBones.Count);
                foreach (Native.Model.PrefabSmDeformBoneW bone in part.SmDeformBones)
                {
                    handles.Add(new CarDeformHandle(
                        handles.Count, bone.SmJointName, bone.OriginalPosition, bone.Range,
                        bone.MoveAccumulator, bone.Intensity, bone.CRadius));
                }
                Native.Model.PrefabDeformPartCommonW? common =
                    part.Common.Count > 0 ? part.Common[0] : null;

                result.Add(new CarDeformPart(
                    i, part.PartType, PartKindName(part.PartType), part.Flags,
                    part.Unk3.Count > 0 ? part.Unk3[0] : 0, part.ParentDeformPartName, volumes,
                    SwapAxes(ToMatrix(part.PartTransform)), effects)
                {
                    // 65535 is the file's "no parent", and it is a ushort — so it is turned into -1 here
                    // rather than left as a magic number every caller would have to know.
                    ParentIndex = part.Unk17 == 65535 ? -1 : part.Unk17,
                    EffectGroup = part.Unk19,
                    CentreOfMass = part.CentreOfMass,
                    Handles = handles,
                    Tuning = common == null
                        ? null
                        : new CarDeformTuning(common.SpeedMin, common.SpeedMax, common.Resistance,
                            common.Mass, common.EnergyStart, common.EnergyDrop),
                });
            }
            return result;
        }
    }

    // ── flat addressing, so a handle is reached the way a collision volume is ──

    /// <summary>How many deform handles the whole car has, counted across every deformable part.</summary>
    public int CarHandleCount()
    {
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        return parts?.Sum(p => p.SmDeformBones.Count) ?? 0;
    }

    /// <summary>Which part and which of its handles a flat index means, or null when it is past the end.</summary>
    public (int Part, int Handle)? CarHandleAt(int flat)
    {
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        if (parts == null || flat < 0) return null;
        int seen = 0;
        for (int part = 0; part < parts.Count; part++)
        {
            int here = parts[part].SmDeformBones.Count;
            if (flat < seen + here) return (part, flat - seen);
            seen += here;
        }
        return null;
    }

    /// <summary>
    /// The flat index of a part's handle — the inverse of <see cref="CarHandleAt"/>.
    /// </summary>
    /// <param name="handle">Which of that part's handles. A COUNT is allowed, naming the seat one more would
    /// take, so that a caller can address the end of a part's list; anything past that is -1 rather than an
    /// address inside the NEXT part's handles, which is how a modder ends up tuning a panel they never opened.</param>
    /// <returns>-1 when there is no such part or no such handle.</returns>
    public int CarHandleIndex(int part, int handle)
    {
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        if (parts == null || part < 0 || part >= parts.Count) return -1;
        if (handle < 0 || handle > parts[part].SmDeformBones.Count) return -1;
        int seen = 0;
        for (int i = 0; i < part; i++) seen += parts[i].SmDeformBones.Count;
        return seen + handle;
    }

    private float GetHandleValue(CarValueSlot slot, int flat, int axis)
    {
        if (Handle(flat) is not { } handle) return float.NaN;
        return slot switch
        {
            CarValueSlot.DeformHandleIntensity => handle.Intensity,
            CarValueSlot.DeformHandleRadius => handle.CRadius,
            _ => axis switch { 0 => handle.Range.X, 1 => handle.Range.Y, _ => handle.Range.Z },
        };
    }

    private bool SetHandleValue(CarValueSlot slot, int flat, int axis, float value)
    {
        if (Handle(flat) is not { } handle) return false;
        switch (slot)
        {
            case CarValueSlot.DeformHandleIntensity: handle.Intensity = value; return true;
            case CarValueSlot.DeformHandleRadius: handle.CRadius = value; return true;
            default:
                handle.Range = axis switch
                {
                    0 => handle.Range with { X = value },
                    1 => handle.Range with { Y = value },
                    _ => handle.Range with { Z = value },
                };
                return true;
        }
    }

    private Native.Model.PrefabSmDeformBoneW? Handle(int flat)
    {
        if (CarHandleAt(flat) is not { } at) return null;
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        return parts?[at.Part].SmDeformBones[at.Handle];
    }

    /// <summary>The part kinds, as the reference toolkit reads <c>S_InitDeformPart.Unk0</c>.</summary>
    private static string PartKindName(uint type) => type switch
    {
        0 => "normal", 1 => "body", 2 => "wheel", 3 => "lid", 4 => "door", 5 => "window",
        6 => "cover", 7 => "bumper", 12 => "exhaust", 13 => "motor", 14 => "tyre", 15 => "snow",
        16 => "plow",
        _ => type.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
