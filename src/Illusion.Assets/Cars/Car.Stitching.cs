using System.Globalization;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

public sealed partial class Car
{
    /// <summary>
    /// Stitches a car out of a prefab and a frame graph that are already open — what a bridge push runs when
    /// it lands mid-session, and the reason identity is carried rather than read off the file: a push can
    /// change the very bones the stitching keys on.
    /// </summary>
    /// <param name="previous">The same car as it was stitched before. Every component that is still in the
    /// same place in the rig keeps its identity, whatever its bone is now called.</param>
    public static Car Stitch(
        PrefabFile prefab, FrameResource? frames, int lod = 0, Car? previous = null,
        string? prefabPath = null, string? extracted = null)
    {
        ArgumentNullException.ThrowIfNull(prefab);

        Rig rig = ReadRig(frames, lod);
        var faults = new List<CarFault>();
        var components = new List<CarComponent>();
        var byBone = new Dictionary<ulong, CarComponent>();
        var byAnchor = new Dictionary<long, ComponentId>();

        IReadOnlyList<CarDeformPart> parts = prefab.CarDeformParts;

        // A deform handle is claimed by its part and is NOT a component: showing deform_doorFL beside doorFL
        // reads as two doors. Measured: a handle is never also some part's own bone (0 of 1402).
        var handleBones = new HashSet<ulong>();
        foreach (CarDeformPart part in parts)
        {
            foreach (CarDeformHandle handle in part.Handles) handleBones.Add(handle.JointName);
        }

        // ── one component per deform part, named after the bone it names ──
        //
        // Every part yields exactly one, whether or not its bone resolves. A part whose bone was renamed in
        // Blender is the case the whole fault path exists for, and dropping it would hide precisely the
        // damage the modder came here to find.
        var componentOfPart = new CarComponent?[parts.Count];
        foreach (CarDeformPart part in parts)
        {
            ulong bone = part.Frame;
            bool resolves = bone != 0 && rig.BoneNames.TryGetValue(bone, out string? boneName);
            rig.BoneNames.TryGetValue(bone, out string? found);
            int joint = rig.JointOfBone.TryGetValue(bone, out int at) ? at : -1;

            var component = new CarComponent(
                Identify(previous, byAnchor, Anchor(joint, part.Index)),
                found ?? (bone == 0 ? "(no bone)" : Hex(bone)), bone, joint, resolves,
                part.Index, part.PartType, part.Kind, rig.Pieces.GetValueOrDefault(bone),
                Handles(part, rig), Damage(part));
            components.Add(component);
            componentOfPart[part.Index] = component;

            if (bone == 0)
            {
                faults.Add(new CarFault(CarFaultKind.PartWithoutBone,
                    $"part {part.Index} ({part.Kind}) names no bone", component.Id));
            }
            else if (!resolves)
            {
                faults.Add(new CarFault(CarFaultKind.ComponentBoneUnresolved,
                    $"part {part.Index} ({part.Kind}) names {Hex(bone)}, which is no bone of this car",
                    component.Id));
            }
            else if (!byBone.TryAdd(bone, component))
            {
                faults.Add(new CarFault(CarFaultKind.DuplicateComponentBone,
                    $"part {part.Index} ({part.Kind}) claims \"{found}\", already claimed by "
                    + $"part {byBone[bone].PartIndex}", component.Id));
            }
        }

        // ── one bare component per bone that carries geometry and that no part claims ──
        //
        // A third of the car is here: every licence-plate segment and every light bone, 2587 of them over the
        // corpus, each already carrying a live hit box. They are shootable parts of the car that had nowhere
        // to be edited. The 101 bones with no geometry — the rig roots, the hinge bones of tracked vehicles —
        // are not things of the car and mint nothing.
        foreach ((ulong bone, string name) in rig.BoneNames)
        {
            if (byBone.ContainsKey(bone) || handleBones.Contains(bone)) continue;
            int pieces = rig.Pieces.GetValueOrDefault(bone);
            if (pieces == 0) continue;

            int joint = rig.JointOfBone.TryGetValue(bone, out int at) ? at : -1;
            var component = new CarComponent(
                Identify(previous, byAnchor, Anchor(joint, partIndex: -1)), name, bone, joint,
                boneResolves: true, partIndex: -1, partType: 0, kind: BareKind, pieces, [], damage: null);
            components.Add(component);
            byBone[bone] = component;
        }

        // ── the body, matched by HASH ──
        //
        // The scale bone ships under four different casings, so a spelling would find three quarters of the
        // corpus and lose the rest — and the body is where every homeless marker goes, so losing it is not a
        // cosmetic failure. The part of kind "body" is the fallback for a car whose scale-bone slot is empty.
        CarComponent? body = null;
        CarPrefab? car = prefab.Car;
        if (car != null && car.ScaleBone != 0) byBone.TryGetValue(car.ScaleBone, out body);
        body ??= components.FirstOrDefault(c => c.PartType == BodyPartType);
        if (body == null) faults.Add(new CarFault(CarFaultKind.NoBody, "this car has no body component"));

        // ── nesting, off the prefab's own parent link ──
        Nest(parts, componentOfPart, byBone, rig, faults);

        List<CarMarker> markers = car == null ? [] : HangMarkers(car, rig, byBone, body, faults);
        List<CarComponent> roots = [.. components.Where(c => c.Parent == null)];

        var stitched = new Car(prefab, frames, prefabPath, extracted, lod, components, roots, body, markers,
            faults, byBone, byAnchor);
        // What the prefab file holds right now, so a later save can tell whether somebody else has written it
        // in the meantime — four other modules still write this same file directly.
        stitched.RememberPrefabOnDisk();
        // Last, because it restates every collision in its own component's space and therefore needs the
        // components and the rig it has just built.
        stitched.HangCollisions(rig);
        return stitched;
    }

    /// <summary>The engine's part kind for the body — the one part every shipped car has exactly one of.</summary>
    private const uint BodyPartType = 1;

    /// <summary>What a component with no deform part is called. Not a part kind: the file has none for it.</summary>
    private const string BareKind = "bare";

    /// <summary>
    /// WHERE a component sits, as the one number its identity is carried by across a re-stitch.
    ///
    /// <para>
    /// It is the bone's position in the rig, not its name — which is the whole point. A rename made in
    /// Blender changes the name and the hash and leaves the joint where it was, so undo and the bridge go on
    /// talking about the same door. A component whose bone does not resolve has no joint to key on and falls
    /// back to its position in the part list, which is stable for exactly as long as no part is inserted; the
    /// two live in opposite halves of the number line so they can never collide.
    /// </para>
    /// </summary>
    private static long Anchor(int joint, int partIndex) => joint >= 0 ? joint : -1L - partIndex;

    /// <summary>Re-uses the identity the previous stitch gave whatever stood at this anchor, or mints one.</summary>
    private static ComponentId Identify(Car? previous, Dictionary<long, ComponentId> byAnchor, long anchor)
    {
        ComponentId id = previous != null && previous._byAnchor.TryGetValue(anchor, out ComponentId was)
            ? was
            : ComponentId.Mint();
        // Two components on one anchor cannot both keep it — the second is a duplicate-bone fault and gets
        // an identity of its own rather than shadowing the first one's.
        if (!byAnchor.TryAdd(anchor, id)) id = ComponentId.Mint();
        return id;
    }

    private static IReadOnlyList<CarHandle> Handles(CarDeformPart part, Rig rig)
    {
        if (part.Handles.Count == 0) return [];
        var result = new List<CarHandle>(part.Handles.Count);
        foreach (CarDeformHandle handle in part.Handles)
        {
            result.Add(new CarHandle(
                handle.Index, handle.JointName,
                rig.BoneNames.TryGetValue(handle.JointName, out string? name) ? name : Hex(handle.JointName),
                handle.Range, handle.Intensity, handle.CRadius));
        }
        return result;
    }

    private static CarDamage? Damage(CarDeformPart part) =>
        part.Tuning is not { } tuning
            ? null
            : new CarDamage(tuning.Mass, tuning.Resistance, tuning.SpeedMin, tuning.SpeedMax,
                tuning.EnergyStart, tuning.EnergyDrop, part.CentreOfMass, part.EffectGroup, part.Flags);

    /// <summary>
    /// Hangs each component off the one it belongs to, following the prefab's own parent link.
    ///
    /// <para>
    /// That link is written TWICE — as a name hash on every one of the 1698 shipped parts, and as an index
    /// beside it on 934 of them — and the two agree on 934 of 934. So they are not two opinions to choose
    /// between; a disagreement is a writer that moved one and not the other, and is reported as such.
    /// </para>
    /// </summary>
    private static void Nest(
        IReadOnlyList<CarDeformPart> parts, CarComponent?[] componentOfPart,
        Dictionary<ulong, CarComponent> byBone, Rig rig, List<CarFault> faults)
    {
        foreach (CarDeformPart part in parts)
        {
            if (componentOfPart[part.Index] is not { } component) continue;

            CarComponent? byHash = part.ParentFrame != 0
                ? byBone.GetValueOrDefault(part.ParentFrame)
                : null;
            CarComponent? byIndex = part.ParentIndex >= 0 && part.ParentIndex < componentOfPart.Length
                ? componentOfPart[part.ParentIndex]
                : null;

            if (part.ParentIndex >= 0 && byIndex != null && byHash != null
                && !ReferenceEquals(byIndex, byHash))
            {
                faults.Add(new CarFault(CarFaultKind.ParentLinksDisagree,
                    $"part {part.Index} names \"{byHash.Name}\" as its parent and indexes "
                    + $"\"{byIndex.Name}\"", component.Id));
            }

            CarComponent? parent = byHash ?? byIndex;
            if (parent == null)
            {
                // The top of the tree is written as a part hanging off something that is no component: the
                // body of 80 of 85 shipped cars names the rig's own root bone, which carries no geometry and
                // therefore mints nothing. That is the tree's top, not a fault. A hash naming NOTHING in the
                // archive is the fault — the reference the game follows into silence.
                if (part.ParentFrame != 0 && !rig.BoneNames.ContainsKey(part.ParentFrame)
                    && !rig.FrameByHash.ContainsKey(part.ParentFrame))
                {
                    faults.Add(new CarFault(CarFaultKind.ParentUnresolved,
                        $"part {part.Index} ({part.Kind}) hangs off {Hex(part.ParentFrame)}, which is no "
                        + "frame of this car", component.Id));
                }
                continue;
            }
            // A part naming itself is how the top of the tree is written, not a fault.
            if (ReferenceEquals(parent, component)) continue;
            if (component.IsAtOrAbove(parent))
            {
                faults.Add(new CarFault(CarFaultKind.ParentLoop,
                    $"part {part.Index} (\"{component.Name}\") hangs off \"{parent.Name}\", which already "
                    + "hangs off it", component.Id));
                continue;
            }
            component.Parent = parent;
            parent.AddChild(component);
        }
    }

    /// <summary>
    /// Resolves every marker to the component owning the bone it hangs off, and to the body otherwise.
    ///
    /// <para>
    /// A marker names a frame, which is sometimes the bone itself and sometimes a Dummy or a Point hung off
    /// one through <c>AttachmentReferences</c> — a seat is a Dummy on 213 of 213, an emitter a Point on 195
    /// of 195, a wiper a bone on 148 of 148. Either way it ends on a bone, on 1081 of 1081 shipped markers.
    /// </para>
    /// </summary>
    private static List<CarMarker> HangMarkers(
        CarPrefab car, Rig rig, Dictionary<ulong, CarComponent> byBone, CarComponent? body,
        List<CarFault> faults)
    {
        var markers = new List<CarMarker>();
        foreach ((CarMarkerRole role, int index, string label, ulong frame) in MarkerRows(car))
        {
            if (frame == 0) continue;

            (ulong bone, bool onOwnBone) = ResolveToBone(frame, rig);
            string name = rig.BoneNames.TryGetValue(frame, out string? boneName)
                ? boneName
                : FrameName(frame, rig) ?? Hex(frame);
            var marker = new CarMarker(role, index, label, frame, name, bone, onOwnBone, bone != 0);
            markers.Add(marker);

            CarComponent? owner = bone != 0 ? byBone.GetValueOrDefault(bone) : null;
            if (bone == 0)
            {
                faults.Add(new CarFault(CarFaultKind.MarkerUnresolved,
                    $"{label} names {Hex(frame)}, which reaches no bone of this car"));
            }
            (owner ?? body)?.AddMarker(marker);
        }
        return markers;
    }

    /// <summary>
    /// Which lists a marker can come out of. Deliberately the six that hang a helper off a bone: a door, a
    /// window and an axle are components in their own right, and listing them here would show the same thing
    /// twice under two names.
    /// </summary>
    private static IEnumerable<(CarMarkerRole Role, int Index, string Label, ulong Frame)> MarkerRows(
        CarPrefab car)
    {
        for (int i = 0; i < car.Seats.Count; i++)
        {
            yield return (CarMarkerRole.Seat, i, Numbered("Seat", i, car.Seats.Count), car.Seats[i].Frame);
        }
        for (int i = 0; i < car.ClimbBoxes.Count; i++)
        {
            yield return (CarMarkerRole.ClimbBox, i, Numbered("Climb box", i, car.ClimbBoxes.Count),
                car.ClimbBoxes[i].Dummy);
        }
        for (int i = 0; i < car.FuelTanks.Count; i++)
        {
            yield return (CarMarkerRole.FuelTank, i, Numbered("Fuel tank", i, car.FuelTanks.Count),
                car.FuelTanks[i]);
        }
        for (int i = 0; i < car.ExhaustEmitters.Count; i++)
        {
            yield return (CarMarkerRole.ExhaustEmitter, i,
                Numbered("Exhaust", i, car.ExhaustEmitters.Count), car.ExhaustEmitters[i]);
        }
        for (int i = 0; i < car.Wipers.Count; i++)
        {
            yield return (CarMarkerRole.Wiper, i, Numbered("Wiper", i, car.Wipers.Count), car.Wipers[i]);
        }
        yield return (CarMarkerRole.Light, 0, "Headlight", car.HeadlightModel);
        yield return (CarMarkerRole.Light, 1, "Backlight", car.BacklightModel);
        yield return (CarMarkerRole.Light, 2, "Toplight", car.ToplightModel);
    }

    private static string Numbered(string what, int index, int count) =>
        count == 1 ? what : $"{what} {(index + 1).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The bone a frame ends on: itself when it is one, otherwise the joint it is attached to.</summary>
    private static (ulong Bone, bool OnOwnBone) ResolveToBone(ulong frame, Rig rig)
    {
        if (rig.BoneNames.ContainsKey(frame)) return (frame, true);
        if (rig.FrameByHash.TryGetValue(frame, out FrameObjectBase? found)
            && rig.JointOfFrame.TryGetValue(found, out int joint)
            && joint >= 0 && joint < rig.BonesByJoint.Length)
        {
            return (Fnv64.Hash(rig.BonesByJoint[joint]), false);
        }
        return (0, false);
    }

    private static string? FrameName(ulong hash, Rig rig) =>
        rig.FrameByHash.TryGetValue(hash, out FrameObjectBase? found) ? found.Name?.String : null;

    private static string Hex(ulong hash) => "0x" + hash.ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>
    /// What the frame graph contributes to the stitching: the bone names, where each bone sits in the rig,
    /// what hangs off which joint, and which bones carry geometry at the level being read.
    /// </summary>
    private sealed class Rig
    {
        internal string[] BonesByJoint { get; set; } = [];

        /// <summary>Every bone of every model in the archive, by the FNV64 of its name.</summary>
        internal Dictionary<ulong, string> BoneNames { get; } = [];

        /// <summary>Where a bone sits in the car's own rig — the anchor a component's identity keys on.</summary>
        internal Dictionary<ulong, int> JointOfBone { get; } = [];

        internal Dictionary<ulong, FrameObjectBase> FrameByHash { get; } = [];

        /// <summary>Which joint a helper frame hangs off, through <c>AttachmentReferences</c>.</summary>
        internal Dictionary<FrameObjectBase, int> JointOfFrame { get; } = [];

        /// <summary>How many split pieces each bone carries at the level being read.</summary>
        internal Dictionary<ulong, int> Pieces { get; } = [];
    }

    private static Rig ReadRig(FrameResource? frames, int lod)
    {
        var rig = new Rig();
        if (frames?.FrameObjects == null) return rig;

        foreach (object o in frames.FrameObjects.Values)
        {
            if (o is FrameObjectBase f && f.Name?.String is { Length: > 0 } name)
            {
                rig.FrameByHash.TryAdd(Fnv64.Hash(name), f);
            }
        }

        // The car's own model first, so a bone's joint index — and the order the bare components come out in
        // — is the rig's own. A car ships one skinned model; the others, when there are any, only widen the
        // set of names a reference can resolve against.
        var models = frames.FrameObjects.Values.OfType<FrameObjectModel>().ToList();
        FrameObjectModel? car = models.FirstOrDefault();
        if (car == null) return rig;

        for (int m = 0; m < models.Count; m++)
        {
            HashName[] bones;
            try { bones = models[m].GetSkeletonObject().BoneNames ?? []; }
            catch (Exception) { continue; }

            if (m == 0) rig.BonesByJoint = [.. bones.Select(b => b.ToString())];
            for (int i = 0; i < bones.Length; i++)
            {
                string name = bones[i].ToString();
                if (name.Length == 0) continue;
                ulong hash = Fnv64.Hash(name);
                rig.BoneNames.TryAdd(hash, name);
                if (m == 0) rig.JointOfBone.TryAdd(hash, i);
            }
        }

        foreach (FrameObjectModel.AttachmentReference reference in car.AttachmentReferences ?? [])
        {
            if (reference.Attachment != null) rig.JointOfFrame[reference.Attachment] = reference.JointIndex;
        }

        ReadGeometry(car, rig, lod);
        return rig;
    }

    /// <summary>
    /// Which bones carry geometry at one level, and how much.
    ///
    /// <para>
    /// A split's bone is <c>BoneRemapIDs[BlendIndex]</c> resolved through THAT LEVEL'S OWN remap table —
    /// reading the raw <c>BlendIndex</c> as a bone id is right 2.2 % of the time, and reusing LOD 0's table
    /// for LOD 1 is wrong outright, since the two tables are different sizes. A piece counts at a level when
    /// one of its face ranges fits inside that level's index buffer: the split table is one block shared by
    /// both levels, so the ranges are what say which level a piece is actually drawn at.
    /// </para>
    /// <para>
    /// Counting a bone's PIECES instead — which the 2026-08-08 census did, and which is where the figure of
    /// 2587 bare bones comes from — differs on exactly one bone in the corpus: <c>deform_top_roof</c> on
    /// <c>berkley_kingfisher_pha</c> holds five pieces carrying no material burst and no face range at all.
    /// It draws nothing, so it is not a component and the measured figure is 2586.
    /// </para>
    /// </summary>
    private static void ReadGeometry(FrameObjectModel car, Rig rig, int lod)
    {
        FrameBlendInfo.BoneIndexInfo[] levels;
        try { levels = car.GetBlendInfoObject().BoneIndexInfos ?? []; }
        catch (Exception) { return; }
        if (lod < 0 || lod >= levels.Length) return;

        byte[] remap = levels[lod].BoneRemapIDs ?? [];
        int indices = car.GetIndexBuffer(lod)?.GetData()?.Length ?? 0;

        foreach (FrameObjectModel.WeightedByMeshSplit split in car.BlendMeshSplits ?? [])
        {
            int bone = split.BlendIndex < remap.Length ? remap[split.BlendIndex] : -1;
            if (bone < 0 || bone >= rig.BonesByJoint.Length) continue;
            string name = rig.BonesByJoint[bone];
            if (name.Length == 0) continue;

            int drawn = 0;
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
            {
                if (Fits(piece, indices)) drawn++;
            }
            if (drawn == 0) continue;
            ulong hash = Fnv64.Hash(name);
            rig.Pieces[hash] = rig.Pieces.GetValueOrDefault(hash) + drawn;
        }

        static bool Fits(FrameObjectModel.BlendMeshSplitInfo piece, int indices)
        {
            foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
            {
                foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                {
                    if (range.StartIndex + (range.NumFaces * 3) <= indices) return true;
                }
            }
            return false;
        }
    }
}
