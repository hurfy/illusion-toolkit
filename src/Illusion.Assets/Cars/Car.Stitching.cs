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

        List<CarMarker> markers = car == null ? [] : HangMarkers(prefab, car, rig, byBone, body, faults);
        // …and the rows that name a component's OWN bone, which belong to it the same way and for the same
        // reason: a door's handle and lock, a window's depth, an axle's masses.
        if (car != null) HangRows(prefab, car, byBone, body);
        List<CarComponent> roots = [.. components.Where(c => c.Parent == null)];

        // ── what did not stitch, beyond what the passes above already named ──
        if (car != null) MatchRows(car, byBone, components, rig, faults);
        LostGeometry(rig, byBone, handleBones, previous, lod, byAnchor, faults);
        OffTheNameTable(rig, components, body, faults);

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
    /// Lines the deform parts up against the prefab's OTHER collections, in both directions.
    ///
    /// <para>
    /// A door is written down in two places that share nothing but a hash: the deform part that says it
    /// crumples, and the door row that says where its handle and its lock are. Nothing in the format keeps the
    /// two in step, so each can name a bone the other has never heard of — and both halves of that
    /// disagreement are worth a modder's attention, because each of them is half a door.
    /// </para>
    /// <para>
    /// Only the collections that name a BONE are lined up. Seats, climb boxes, fuel tanks and exhaust
    /// emitters name a Dummy or a Point hung off one, and they are already stitched as markers.
    /// </para>
    /// </summary>
    private static void MatchRows(
        CarPrefab car, Dictionary<ulong, CarComponent> byBone, List<CarComponent> components, Rig rig,
        List<CarFault> faults)
    {
        // A car whose frame resource could not be opened stitches on purpose (see Car.ReadFrom), and there is
        // nothing to line the rows up AGAINST — every one of them would name a hash nobody has heard of, and
        // sixty raw-hex lines would bury the one fault that says the rig never loaded.
        if (rig.BoneNames.Count == 0) return;

        foreach ((string label, ulong bone) in BoneRows(car))
        {
            // Drawn ANYWHERE, not drawn at this level. A component's existence is LOD-scoped and 4882 of 5046
            // bones carry nothing at the far level, so asking byBone alone would turn a switch to LOD 1 into
            // one long fault about a car nobody has touched.
            if (bone == 0 || byBone.ContainsKey(bone) || rig.Drawn.Contains(bone)) continue;
            faults.Add(new CarFault(CarFaultKind.RowWithoutComponent,
                $"{label} names {Named(bone, rig)}, which is no component of this car",
                ShipsThisWay: true));
        }

        // The other direction, and only for the two kinds the file keeps a collection of. A bumper has no
        // bumper list to be missing from, so asking whether it is in one would invent a fault the format has
        // no room for.
        var doors = new HashSet<ulong>(car.Doors.Select(d => d.Frame));
        var windows = new HashSet<ulong>(car.Windows.Select(w => w.Frame));
        foreach (CarComponent component in components)
        {
            if (component.BoneHash == 0) continue;
            HashSet<ulong>? rows = component.PartType switch
            {
                DoorPartType => doors,
                WindowPartType => windows,
                _ => null,
            };
            if (rows == null || rows.Contains(component.BoneHash)) continue;
            faults.Add(new CarFault(CarFaultKind.ComponentWithoutRow,
                $"part {component.PartIndex} (\"{component.Name}\", {component.Kind}) has no row in this "
                + $"car's {component.Kind} list", component.Id, ShipsThisWay: true));
        }
    }

    /// <summary>The engine's part kinds that have a sibling collection keyed on the part's own bone.</summary>
    private const uint DoorPartType = 4;

    private const uint WindowPartType = 5;

    /// <summary>Every row of the prefab that names a BONE, labelled the way a modder would name it. Measured:
    /// door 193/193, window 527/527, axle 360/360, wiper 148/148 and steering wheel 83/83 name a bone and
    /// never a helper frame.</summary>
    private static IEnumerable<(string Label, ulong Bone)> BoneRows(CarPrefab car)
    {
        IReadOnlyList<CarPrefab.DoorPoints> doors = car.Doors;
        for (int i = 0; i < doors.Count; i++)
        {
            yield return (Numbered("Door", i, doors.Count), doors[i].Frame);
        }
        IReadOnlyList<CarPrefab.Window> windows = car.Windows;
        for (int i = 0; i < windows.Count; i++)
        {
            yield return (Numbered("Window", i, windows.Count), windows[i].Frame);
        }
        IReadOnlyList<CarPrefab.Axle> axles = car.Axles;
        for (int i = 0; i < axles.Count; i++)
        {
            yield return (Numbered("Axle", i, axles.Count), axles[i].Frame);
            yield return (Numbered("Brake drum", i, axles.Count), axles[i].BrakeDrum);
            // "Rot wing" rather than "Axle rot wing": three collections come out of one loop, and a label
            // that starts with another label's is one nothing downstream can tell apart.
            yield return (Numbered("Rot wing", i, axles.Count), axles[i].RotWing);
        }
        for (int i = 0; i < car.Wipers.Count; i++)
        {
            yield return (Numbered("Wiper", i, car.Wipers.Count), car.Wipers[i]);
        }
        for (int i = 0; i < car.DrivingWheels.Count; i++)
        {
            yield return (Numbered("Driving wheel", i, car.DrivingWheels.Count), car.DrivingWheels[i]);
        }
    }

    /// <summary>
    /// Bones whose geometry is gone — the one failure a tolerant reader would otherwise hide.
    ///
    /// <para>
    /// A bare component is minted FROM its geometry, so losing the last of it breaks nothing visibly: the row
    /// simply stops appearing on the next resolve, and the modder is left hunting for a licence plate that was
    /// there a push ago. It reads two ways, and both are here because they are different accidents. A bone
    /// still holding its SEAT in the split table with no face left in it is a rebuild that emptied the pieces;
    /// a bone that had a component last time and mints none now is a push that took the split with it, which
    /// is the irreversible one — geometry weighted to that bone afterwards has nowhere to go.
    /// </para>
    /// </summary>
    private static void LostGeometry(
        Rig rig, Dictionary<ulong, CarComponent> byBone, HashSet<ulong> handleBones, Car? previous, int lod,
        Dictionary<long, ComponentId> byAnchor, List<CarFault> faults)
    {
        var said = new HashSet<ulong>();
        foreach (ulong bone in rig.DeadSeats)
        {
            if (byBone.ContainsKey(bone) || handleBones.Contains(bone)) continue;
            said.Add(bone);
            // Shipped: `deform_top_roof` on berkley_kingfisher_pha is written exactly this way, and it is the
            // same bone the component census names as the difference between 2587 and 2586.
            faults.Add(new CarFault(CarFaultKind.BareComponentLostGeometry,
                $"{Named(bone, rig)} still holds its seat in the split table and no piece of it has a face "
                + "left", ShipsThisWay: true));
        }

        // Against the previous stitch, and only at the SAME level: a component that has geometry at LOD 0 and
        // none at LOD 1 is 96.7 % of them, and calling that a loss would make the far level one long fault.
        if (previous == null || previous.Lod != lod) return;
        foreach (CarComponent was in previous.Components)
        {
            int joint = was.BoneJoint;
            if (!was.IsBare || joint < 0 || joint >= rig.BonesByJoint.Length) continue;
            // Still standing under this anchor — renamed, or given a deform part of its own, but not lost.
            if (byAnchor.ContainsKey(Anchor(joint, partIndex: -1))) continue;

            // The bone this joint holds NOW, and the one the component stood on THEN. Both have to be dark
            // before this is a loss: a push that inserts a bone renumbers every joint above it, and reading
            // the new rig at the old joint number would then report a component that has merely moved down a
            // row — still drawn, still in the tree — as having lost everything.
            ulong bone = Fnv64.Hash(rig.BonesByJoint[joint]);
            // Only geometry loss. A bone some part has since claimed as a deform handle stopped being a
            // component for a reason of its own, and naming that a lost panel would be a false alarm.
            if (rig.Pieces.GetValueOrDefault(bone) > 0 || rig.Pieces.GetValueOrDefault(was.BoneHash) > 0
                || handleBones.Contains(bone) || !said.Add(bone))
            {
                continue;
            }
            faults.Add(new CarFault(CarFaultKind.BareComponentLostGeometry,
                $"\"{was.Name}\" had geometry when this car was last read and has none now", was.Id));
        }
    }

    /// <summary>
    /// Components the game will load and not draw.
    ///
    /// <para>
    /// The frame name table is what says which frames of an archive are instantiated at all; a frame the table
    /// does not reach loads and is invisible, and nothing else in the archive disagrees — no reader complains,
    /// no count is off. So the editor is the only place it can be caught, and the alternative is spawning the
    /// car and noticing that a part of it is not there.
    /// </para>
    /// <para>
    /// REACHED, not listed. Measured over the 85 shipped cars: not one of their models is on the table itself
    /// — what is on it is 247 holder frames, three per car (the car, its <c>_rain</c> variant and a numbered
    /// entry), and the model hangs off one of them at exactly one hop, 85 of 85. So membership is a question
    /// about the parent chain, and asking it of the frame alone would call every shipped car invisible.
    /// </para>
    /// </summary>
    private static void OffTheNameTable(
        Rig rig, List<CarComponent> components, CarComponent? body, List<CarFault> faults)
    {
        // The model first, because every component of a car is a weight group inside one skinned mesh: it out
        // of the table's reach is not one invisible panel, it is the whole car.
        if (rig.Model is { } model && !Reaches(model))
        {
            faults.Add(new CarFault(CarFaultKind.FrameNotOnNameTable,
                $"the model \"{model.Name?.String}\" this car is drawn from is not reached from the frame "
                + "name table, so none of it is drawn in game", body?.Id ?? ComponentId.None));
        }
        foreach (CarComponent component in components)
        {
            if (component.BoneHash == 0
                || !rig.FrameByHash.TryGetValue(component.BoneHash, out FrameObjectBase? frame)
                || ReferenceEquals(frame, rig.Model) || Reaches(frame))
            {
                continue;
            }
            faults.Add(new CarFault(CarFaultKind.FrameNotOnNameTable,
                $"\"{component.Name}\" has a frame of its own that the frame name table does not reach, so "
                + "it loads and is invisible in game", component.Id));
        }
    }

    /// <summary>Whether the frame name table reaches this frame — itself, or anything above it. Depth-capped
    /// rather than trusting the chain: a parent loop in an edited archive would otherwise be walked for
    /// ever, and this runs on every scene change.</summary>
    private static bool Reaches(FrameObjectBase frame)
    {
        FrameObjectBase? at = frame;
        for (int depth = 0; at != null && depth < 64; depth++, at = at.Parent)
        {
            if (at.IsOnFrameTable) return true;
        }
        return false;
    }

    /// <summary>A bone as a modder would read it: its name in quotes, or the bare hash when the rig has none.</summary>
    private static string Named(ulong bone, Rig rig) =>
        rig.BoneNames.TryGetValue(bone, out string? name) ? $"\"{name}\"" : Hex(bone);

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
        PrefabFile prefab, CarPrefab car, Rig rig, Dictionary<ulong, CarComponent> byBone,
        CarComponent? body, List<CarFault> faults)
    {
        var markers = new List<CarMarker>();
        foreach ((CarMarkerRole role, int index, string label, ulong frame) in MarkerRows(car))
        {
            if (frame == 0) continue;

            (ulong bone, bool onOwnBone) = ResolveToBone(frame, rig);
            string name = rig.BoneNames.TryGetValue(frame, out string? boneName)
                ? boneName
                : FrameName(frame, rig) ?? Hex(frame);
            var marker = new CarMarker(role, index, label, frame, name, bone, onOwnBone, bone != 0,
                MarkerFields(prefab, role, index));
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

        /// <summary>The model the car is drawn from — one skinned mesh, whose bones ARE its parts.</summary>
        internal FrameObjectModel? Model { get; set; }

        /// <summary>Every bone of every model in the archive, by the FNV64 of its name.</summary>
        internal Dictionary<ulong, string> BoneNames { get; } = [];

        /// <summary>Where a bone sits in the car's own rig — the anchor a component's identity keys on.</summary>
        internal Dictionary<ulong, int> JointOfBone { get; } = [];

        internal Dictionary<ulong, FrameObjectBase> FrameByHash { get; } = [];

        /// <summary>Which joint a helper frame hangs off, through <c>AttachmentReferences</c>.</summary>
        internal Dictionary<FrameObjectBase, int> JointOfFrame { get; } = [];

        /// <summary>How many split pieces each bone carries at the level being read.</summary>
        internal Dictionary<ulong, int> Pieces { get; } = [];

        /// <summary>
        /// Bones that hold a seat in the split table and no face anywhere in it. Not a per-level question —
        /// the split table is one block shared by both levels, so a bone with no face range at all is drawn
        /// at neither.
        /// </summary>
        internal HashSet<ulong> DeadSeats { get; } = [];

        /// <summary>The other side of the same reading: bones whose splits hold at least one face, at either
        /// level. What says a row points at real geometry even when the level on screen does not draw it.
        /// </summary>
        internal HashSet<ulong> Drawn { get; } = [];
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
        rig.Model = car;

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
        if (levels.Length == 0) return;

        // ── the SEAT pass, which is not a per-level question ──
        //
        // It reads through level 0's table whatever level is being shown: the split table is one block shared
        // by both, and level 1's table names a tenth of the bones (73.3 against 10.8), so asking it would
        // answer "gone" for most of the car. It also runs for a level that does NOT EXIST — 3 of the 85 cars
        // ship a single LOD — because "does this bone draw anywhere" has an answer there too, and without one
        // every prefab row of a single-LOD car reads as naming nothing the moment the switch moves.
        byte[] seats = levels[0].BoneRemapIDs ?? [];
        foreach (FrameObjectModel.WeightedByMeshSplit split in car.BlendMeshSplits ?? [])
        {
            if (Bone(split.BlendIndex, seats, rig) is not { } seated) continue;
            if (HasFaces(split)) rig.Drawn.Add(seated); else rig.DeadSeats.Add(seated);
        }
        // A bone with two splits, one emptied and one still drawn, has not lost its geometry.
        rig.DeadSeats.ExceptWith(rig.Drawn);

        // ── and the per-level one: how much this bone actually draws at the level being read ──
        if (lod < 0 || lod >= levels.Length) return;
        byte[] remap = levels[lod].BoneRemapIDs ?? [];
        int indices = car.GetIndexBuffer(lod)?.GetData()?.Length ?? 0;

        foreach (FrameObjectModel.WeightedByMeshSplit split in car.BlendMeshSplits ?? [])
        {
            if (Bone(split.BlendIndex, remap, rig) is not { } bone) continue;

            int drawn = 0;
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
            {
                if (Fits(piece, indices)) drawn++;
            }
            if (drawn == 0) continue;
            rig.Pieces[bone] = rig.Pieces.GetValueOrDefault(bone) + drawn;
        }

        static ulong? Bone(int blendIndex, byte[] remap, Rig rig)
        {
            int bone = blendIndex < remap.Length ? remap[blendIndex] : -1;
            if (bone < 0 || bone >= rig.BonesByJoint.Length) return null;
            string name = rig.BonesByJoint[bone];
            return name.Length == 0 ? null : Fnv64.Hash(name);
        }

        // Whether the split holds a face AT ALL — the question the seat pass asks. A range that does not fit
        // THIS level's index buffer is geometry at the other level, not geometry that is gone.
        static bool HasFaces(FrameObjectModel.WeightedByMeshSplit split)
        {
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
            {
                foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                {
                    foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                    {
                        if (range.NumFaces > 0) return true;
                    }
                }
            }
            return false;
        }

        // A range of NO faces is not geometry at this level, it is an emptied piece — and reading it as drawn
        // is what would mint a component for a bone the seat pass has just called dead, silently swallowing
        // the fault written for exactly that accident. Measured: no shipped car carries one (the component
        // census's 2586 does not move), so this only ever fires on a car something has been done to.
        static bool Fits(FrameObjectModel.BlendMeshSplitInfo piece, int indices)
        {
            foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
            {
                foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                {
                    if (range.NumFaces > 0 && range.StartIndex + (range.NumFaces * 3) <= indices) return true;
                }
            }
            return false;
        }
    }
}
