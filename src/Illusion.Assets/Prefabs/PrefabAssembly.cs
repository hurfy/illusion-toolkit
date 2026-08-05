using System.Globalization;
using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.ItemDesc;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Prefabs;

/// <summary>
/// An archive's PREFAB, read for SHOWING: every entry it carries, and — for the variants the core decodes —
/// every frame reference resolved against the archive's own frame and bone names.
///
/// <para>
/// The point of resolving is not the pretty name, it is the ones that DO NOT resolve. A prefab addresses a
/// frame by the FNV64 hash of its name, and a hash that names nothing does not fail: the door simply stops
/// opening, with no error anywhere. Renaming a bone through the Blender bridge is enough to cause it. This
/// is the only place in the toolkit that can see it before the archive is built.
/// </para>
/// <para>
/// Read-only, and deliberately so — <c>CarPrefab</c> has no setters, and a view that pretended otherwise
/// would be lying about what Save writes.
/// </para>
/// </summary>
public sealed class PrefabAssembly
{
    private PrefabAssembly(IReadOnlyList<PrefabEntryView> entries, IReadOnlyList<FrameChoice> choices)
    {
        Entries = entries;
        FrameChoices = choices;
    }

    /// <summary>Every entry of every PREFAB resource in the archive, in file order.</summary>
    public IReadOnlyList<PrefabEntryView> Entries { get; }

    /// <summary>
    /// Every frame in the archive a reference may be pointed at, by name. This is the whole edit vocabulary:
    /// a slot is set by choosing one of these, never by typing a hash, because a hash that names nothing
    /// fails silently and a name picked from this list cannot.
    /// </summary>
    public IReadOnlyList<FrameChoice> FrameChoices { get; }

    /// <summary>Whether anything in the archive names a frame that is not there.</summary>
    public bool HasDangling => Entries.Any(e => e.DanglingCount > 0);

    /// <summary>The init-data variants the core decodes rather than carrying opaquely. Everything else is a
    /// sized blob that round-trips byte-exact — shown as such, never guessed at.</summary>
    private static readonly Dictionary<int, string> TypeNames = new()
    {
        [2] = "S_CarInitData",
        [3] = "S_COInitData",
        [4] = "S_ActorDeformInitData",
        [5] = "S_WheelInitData",
        [6] = "S_PhThingActorBaseInitData",
        [7] = "S_DoorInitData",
        [8] = "S_LiftInitData",
        [9] = "S_BoatInitData",
        [10] = "S_WagonInitData",
    };

    /// <summary>
    /// Reads an archive's assembly. Opens the frame resource to build the name table, so it belongs on a
    /// background thread. Returns null when the archive carries no PREFAB at all — which is the common case
    /// (257 of 1324 archives carry one).
    /// </summary>
    /// <param name="live">Frame names from the graph that is OPEN, when there is one. A frame minted this
    /// session — the Dummy a new climb box hangs on — is not on disk until Save, so without these it reads
    /// as a bare hash and cannot be picked in any other slot: the part would look broken the moment it was
    /// made. Live names win over the file's.</param>
    /// <param name="boneWorlds">Where the car's bones stand, when the archive is on a stage. A collision
    /// volume is written in the space of a bone, and most car bones are turned relative to the car — so
    /// without these the position rows show numbers whose axes are not the ones the viewport draws.</param>
    public static PrefabAssembly? Read(
        FileInfo archive, IReadOnlyDictionary<ulong, string>? live = null,
        IReadOnlyDictionary<ulong, Matrix4x4>? boneWorlds = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return ReadFrom(MafiaEnvironment.ExtractedDir(archive), live, boneWorlds);
    }

    /// <summary>The same, from a working copy that is not the archive's own — what the regression harness
    /// reads, so an edit it makes never lands in the player's install.</summary>
    public static PrefabAssembly? ReadFrom(
        string extracted, IReadOnlyDictionary<ulong, string>? live = null,
        IReadOnlyDictionary<ulong, Matrix4x4>? boneWorlds = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(extracted);

        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extracted).GetFiles("PREFAB"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return null; }
        if (files.Count == 0) return null;

        Dictionary<ulong, string> names = FrameNames(extracted);
        foreach ((ulong hash, string name) in live ?? new Dictionary<ulong, string>()) names[hash] = name;
        Dictionary<ulong, ushort> surfaces = ShapeSurfaces(extracted);
        var entries = new List<PrefabEntryView>();

        // The ones the core cannot read are counted, not listed. A city district carries over a thousand
        // S_COInitData entries; a thousand identical "not decoded" cards says nothing a single line does not.
        var opaque = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);

        foreach (string file in files)
        {
            PrefabFile prefab;
            try { prefab = PrefabFile.Load(file); }
            catch (Exception ex) when (ex is IOException or SdsFormatException) { continue; }

            for (int i = 0; i < prefab.PrefabCount; i++)
            {
                (int type, int size) = prefab.Entries[i];
                if (Describe(prefab, i, type, size, names, surfaces, boneWorlds) is { Decoded: true } decoded)
                {
                    entries.Add(decoded);
                    continue;
                }
                string name = TypeName(type);
                (int count, long bytes) = opaque.GetValueOrDefault(name);
                opaque[name] = (count + 1, bytes + size);
            }
        }

        foreach ((string type, (int count, long bytes)) in opaque.OrderByDescending(p => p.Value.Count))
        {
            entries.Add(new PrefabEntryView(
                count == 1 ? "1 entry" : $"{count} entries",
                type,
                bytes.ToString("N0", CultureInfo.InvariantCulture) + " B",
                decoded: false, [],
                "not decoded by the core — carried and saved back byte for byte"));
        }
        if (entries.Count == 0) return null;

        var choices = names
            .Select(p => new FrameChoice(p.Key, p.Value))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new PrefabAssembly(entries, choices);
    }

    // Every name in the archive that a prefab could be pointing at: the frame objects themselves, and the
    // bones inside every skinned model — a car's doors and axles are bones, not frame objects.
    private static Dictionary<ulong, string> FrameNames(string extracted)
    {
        var names = new Dictionary<ulong, string>();
        try
        {
            if (SdsMeshLoader.OpenScene(extracted).FrameResource is not { FrameObjects: not null } frame) return names;
            foreach (object o in frame.FrameObjects.Values)
            {
                if (o is FrameObjectBase f && f.Name.String is { Length: > 0 } n) names[f.Name.Hash] = n;
            }
            foreach (FrameObjectModel model in frame.FrameObjects.Values.OfType<FrameObjectModel>())
            {
                foreach (HashName bone in model.GetSkeletonObject().BoneNames ?? [])
                {
                    if (bone.String is { Length: > 0 } bn) names[bone.Hash] = bn;
                }
            }
        }
        catch (Exception)
        {
            // A resolve table is a nicety: without it every reference reads as a bare hash, which is still
            // more than the user has today. It must never stop the tab from opening.
        }
        return names;
    }

    private static string TypeName(int type) =>
        TypeNames.TryGetValue(type, out string? name)
            ? name
            : "type " + type.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The physics surface each of the archive's own shapes names, keyed by the DATA hash a prefab volume
    /// addresses it with. Every shipped car shape carries 0 ("unassigned"), so a non-zero one is a shape the
    /// toolkit minted with a surface asked for — worth showing, because it is invisible everywhere else.
    /// </summary>
    private static Dictionary<ulong, ushort> ShapeSurfaces(string extracted)
    {
        var found = new Dictionary<ulong, ushort>();
        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extracted).GetFiles("ItemDesc"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return found; }

        foreach (string file in files)
        {
            try
            {
                ItemDescFile shape = ItemDescFile.Load(file);
                if (shape.Element is RigidBodyElement rigid) found[rigid.DataHash] = rigid.MaterialId;
            }
            catch (Exception ex) when (ex is IOException or SdsFormatException) { /* unreadable shape */ }
        }
        return found;
    }

    private static PrefabEntryView Describe(
        PrefabFile prefab, int index, int type, int size, Dictionary<ulong, string> names,
        Dictionary<ulong, ushort> surfaces, IReadOnlyDictionary<ulong, Matrix4x4>? boneWorlds)
    {
        string typeName = TypeName(type);
        string owner = "0x" + prefab.Hashes[index].ToString("X16", CultureInfo.InvariantCulture);
        string bytes = size.ToString("N0", CultureInfo.InvariantCulture) + " B";

        // Only the car variant is laid out here. The other two the core decodes (Wheel, PhysThingBase) carry
        // no frame references worth a panel of their own, and the remaining six are not decoded at all.
        CarPrefab? car = prefab.DecodedKinds[index] != 0 ? prefab.Car : null;
        if (car != null && type == 2)
        {
            List<PrefabGroupView> groups2 = CarGroups(car, names, prefab, surfaces, boneWorlds);
            int total2 = groups2.Sum(g => g.Rows.Count(r => r.Kind == PrefabRefKind.Reference));
            int dangling2 = groups2.Sum(g => g.DanglingCount);
            return new PrefabEntryView(owner, typeName, bytes, decoded: true, groups2,
                dangling2 == 0
                    ? $"{total2} references, all resolved"
                    : $"{total2 - dangling2} of {total2} references resolve — {dangling2} dangling");
        }
        return new PrefabEntryView(owner, typeName, bytes, decoded: false, [],
            "not decoded by the core — carried and saved back byte for byte");
    }

    private static List<PrefabGroupView> CarGroups(
        CarPrefab car, Dictionary<ulong, string> names, PrefabFile file,
        Dictionary<ulong, ushort> surfaces, IReadOnlyDictionary<ulong, Matrix4x4>? boneWorlds)
    {
        PrefabRefView Reference(string label, ulong hash, CarFrameSlot slot, int index, string? detail = null)
        {
            if (hash == 0) return new PrefabRefView(label, "—", PrefabRefKind.Unset, detail, slot, index);
            return names.TryGetValue(hash, out string? name)
                ? new PrefabRefView(label, name, PrefabRefKind.Reference, detail, slot, index)
                : new PrefabRefView(label, "0x" + hash.ToString("X16", CultureInfo.InvariantCulture),
                    PrefabRefKind.Dangling, detail ?? "no frame in this archive hashes to this", slot, index);
        }

        var groups = new List<PrefabGroupView>
        {
            new("Chassis",
            [
                Reference("Root frame", car.RootFrame, CarFrameSlot.RootFrame, 0),
                Reference("Scale bone", car.ScaleBone, CarFrameSlot.ScaleBone, 0),
                Reference("Body", car.BodyFrame, CarFrameSlot.Body, 0),
                Reference("Rest bone", car.RestBone, CarFrameSlot.RestBone, 0),
            ]),
            new("Lights",
            [
                Reference("Headlight", car.HeadlightModel, CarFrameSlot.Headlight, 0),
                Reference("Backlight", car.BacklightModel, CarFrameSlot.Backlight, 0),
                Reference("Toplight", car.ToplightModel, CarFrameSlot.Toplight, 0),
                Reference("Snow rest", car.SnowRest, CarFrameSlot.SnowRest, 0),
            ]),
        };

        // One band per list, not one band called "running gear" holding four different lists — a band's "+"
        // has to mean exactly one thing, and "add to running gear" cannot.
        AddList(groups, "Driving wheels", "Wheel", car.DrivingWheels, CarFrameSlot.DrivingWheel, Reference);
        AddList(groups, "Fuel tanks", "Tank", car.FuelTanks, CarFrameSlot.FuelTank, Reference);
        AddList(groups, "Exhausts", "Exhaust", car.ExhaustEmitters, CarFrameSlot.Exhaust, Reference);
        AddList(groups, "Wipers", "Wiper", car.Wipers, CarFrameSlot.Wiper, Reference);

        // Below: one block of rows per PART, headed by its own line. Everything the game reads is a row of
        // its own — a depth typed into a field, a position dragged in a vector box — rather than grey prose
        // under a name, which is what the first version did and left half the file unreachable.
        //
        // Each of these bands is added whether or not it has anything in it, for the same reason AddList's
        // are: the band header carries the "+", so a band that disappears with its last part is a one-way
        // door. An empty one says so and offers the button.
        var seats = new List<PrefabRefView>();
        for (int i = 0; i < car.Seats.Count; i++)
        {
            CarPrefab.Seat seat = car.Seats[i];
            // The part's OWN row is its frame, labelled with the part — a separate header line saying
            // "Seat 1" over a row saying the bone name is the same thing said twice.
            seats.Add(Part($"Seat {i + 1}", seat.Frame, CarFrameSlot.SeatFrame, i, CarItemKind.Seat));
            seats.Add(Sub(Reference("Entered by", seat.DoorFrame, CarFrameSlot.SeatDoor, i)));
            seats.Add(Sub(Vector("Sits at", seat.Position, CarValueSlot.SeatPosition, i)));
            seats.Add(Sub(Number("Type", seat.Type, CarValueSlot.SeatType, i, "F0")));
        }
        groups.Add(new PrefabGroupView("Seats", seats));

        var doors = new List<PrefabRefView>();
        for (int i = 0; i < car.Doors.Count; i++)
        {
            CarPrefab.DoorPoints door = car.Doors[i];
            doors.Add(Part($"Door {i + 1}", door.Frame, CarFrameSlot.DoorFrame, i, CarItemKind.Door));
            doors.Add(Sub(Vector("Handle", door.HandlePosition, CarValueSlot.DoorHandle, i)));
            doors.Add(Sub(Vector("Lock", door.LockPosition, CarValueSlot.DoorLock, i)));
        }
        groups.Add(new PrefabGroupView("Doors", doors));

        var windows = new List<PrefabRefView>();
        for (int i = 0; i < car.Windows.Count; i++)
        {
            CarPrefab.Window window = car.Windows[i];
            windows.Add(Part($"Window {i + 1}", window.Frame, CarFrameSlot.WindowFrame, i, CarItemKind.Window));
            windows.Add(Sub(Number("Depth", window.Depth, CarValueSlot.WindowDepth, i, "F3")));
            windows.Add(Sub(Flag("Opens", window.IsOpenable, CarValueSlot.WindowOpenable, i)));
        }
        groups.Add(new PrefabGroupView("Windows", windows));

        var axles = new List<PrefabRefView>();
        for (int i = 0; i < car.Axles.Count; i++)
        {
            CarPrefab.Axle axle = car.Axles[i];
            axles.Add(Part($"Axle {i + 1}", axle.Frame, CarFrameSlot.AxleFrame, i,
                CarItemKind.AxlePair, i / 2));
            axles.Add(Sub(Reference("Brake drum", axle.BrakeDrum, CarFrameSlot.AxleBrakeDrum, i)));
            axles.Add(Sub(Number("Axle type", axle.Type, CarValueSlot.AxleType, i, "F0")));
            axles.Add(Sub(Number("Drum radius", axle.BrakeDrumRadius, CarValueSlot.AxleBrakeDrumRadius, i, "F3")));
            axles.Add(Sub(Number("Drum mass", axle.BrakeDrumMass, CarValueSlot.AxleBrakeDrumMass, i, "F1")));
            axles.Add(Sub(Number("Axle mass", axle.AxleMass, CarValueSlot.AxleMass, i, "F1")));
        }
        groups.Add(new PrefabGroupView("Axles", axles));

        var climbs = new List<PrefabRefView>();
        for (int i = 0; i < car.ClimbBoxes.Count; i++)
        {
            CarPrefab.ClimbBox box = car.ClimbBoxes[i];
            climbs.Add(Part($"Climb box {i + 1}", box.Dummy, CarFrameSlot.ClimbDummy, i,
                CarItemKind.ClimbBox));
            climbs.Add(Sub(Reference("On bone", box.Bone, CarFrameSlot.ClimbBone, i)));
            climbs.Add(Sub(Vector("Corner", box.Min, CarValueSlot.ClimbBoxMin, i)));
            climbs.Add(Sub(Vector("Opposite", box.Max, CarValueSlot.ClimbBoxMax, i)));
        }
        groups.Add(new PrefabGroupView("Climb boxes", climbs));

        // The rest of what the car names, in the shape the lists above use. These are the slots that had no
        // row at all until now — a car was editable in its doors and axles and unreachable everywhere else.
        var body = new List<PrefabRefView>
        {
            Reference("Motor ventilator", file.GetCarFrame(CarFrameSlot.MotorVentilator, 0),
                CarFrameSlot.MotorVentilator, 0),
            Vector("Bone range", new Vector3(
                file.GetCarValue(CarValueSlot.BoneRange, 0, 0),
                file.GetCarValue(CarValueSlot.BoneRange, 0, 1),
                file.GetCarValue(CarValueSlot.BoneRange, 0, 2)), CarValueSlot.BoneRange, 0),
            Number("Reduce bbox Z", file.GetCarValue(CarValueSlot.ReduceBboxZ, 0), CarValueSlot.ReduceBboxZ,
                0, "F3"),
        };
        groups.Add(new PrefabGroupView("Body", body));

        Slots(groups, "Wind emitters", "Emitter", CarFrameSlot.LocalWindEmitter, file, Reference);
        Slots(groups, "Bus seats", "Seat", CarFrameSlot.BusSeat, file, Reference);
        Slots(groups, "Bus entries", "Entry", CarFrameSlot.EnterBus, file, Reference);

        var snaps = new List<PrefabRefView>();
        for (int i = 0; i < file.CarSlotCount(CarFrameSlot.DrWheelSnapWheel); i++)
        {
            snaps.Add(Reference($"Steering wheel {i + 1}",
                file.GetCarFrame(CarFrameSlot.DrWheelSnapWheel, i), CarFrameSlot.DrWheelSnapWheel, i));
            snaps.Add(Sub(Reference("Left hand",
                file.GetCarFrame(CarFrameSlot.DrWheelSnapLeft, i), CarFrameSlot.DrWheelSnapLeft, i)));
            snaps.Add(Sub(Reference("Right hand",
                file.GetCarFrame(CarFrameSlot.DrWheelSnapRight, i), CarFrameSlot.DrWheelSnapRight, i)));
        }
        if (snaps.Count > 0) groups.Add(new PrefabGroupView("Steering wheel grip", snaps));

        var dcb = new List<PrefabRefView>();
        for (int i = 0; i < file.CarSlotCount(CarFrameSlot.DcbDoor); i++)
        {
            dcb.Add(Reference($"Door {i + 1}", file.GetCarFrame(CarFrameSlot.DcbDoor, i),
                CarFrameSlot.DcbDoor, i));
            dcb.Add(Sub(Number("Resistance", file.GetCarValue(CarValueSlot.DcbResistance, i),
                CarValueSlot.DcbResistance, i, "F2")));
            dcb.Add(Sub(Number("Hitpoints", file.GetCarValue(CarValueSlot.DcbHitpoints, i),
                CarValueSlot.DcbHitpoints, i, "F2")));
        }
        if (dcb.Count > 0) groups.Add(new PrefabGroupView("Door damage", dcb));

        // The damage model. It is most of the file's size and, until now, a single line saying how many parts
        // there were. Every part's own tuning is here; the effect and collision trees hanging under it are
        // still carried byte for byte and not laid out — they are hundreds of fields with no known meaning.
        var parts = new List<PrefabRefView>();
        for (int i = 0; i < file.CarSlotCount(CarFrameSlot.DeformPartParent); i++)
        {
            parts.Add(Reference($"Part {i + 1}", file.GetCarFrame(CarFrameSlot.DeformPartParent, i),
                CarFrameSlot.DeformPartParent, i));
            parts.Add(Sub(Number("Type", file.GetCarValue(CarValueSlot.DeformPartType, i),
                CarValueSlot.DeformPartType, i, "F0")));
            parts.Add(Sub(Number("Flags", file.GetCarValue(CarValueSlot.DeformPartFlags, i),
                CarValueSlot.DeformPartFlags, i, "F0")));
            parts.Add(Sub(Vector("Centre of mass", new Vector3(
                file.GetCarValue(CarValueSlot.DeformCentreOfMass, i, 0),
                file.GetCarValue(CarValueSlot.DeformCentreOfMass, i, 1),
                file.GetCarValue(CarValueSlot.DeformCentreOfMass, i, 2)),
                CarValueSlot.DeformCentreOfMass, i)));
            foreach ((string label, CarValueSlot slot) in new[]
                     {
                         ("Mass", CarValueSlot.DeformMass),
                         ("Resistance", CarValueSlot.DeformResistance),
                         ("Speed min", CarValueSlot.DeformSpeedMin),
                         ("Speed max", CarValueSlot.DeformSpeedMax),
                         ("Energy start", CarValueSlot.DeformEnergyStart),
                         ("Energy drop", CarValueSlot.DeformEnergyDrop),
                     })
            {
                float value = file.GetCarValue(slot, i);
                if (!float.IsNaN(value)) parts.Add(Sub(Number(label, value, slot, i, "F2")));
            }
        }
        if (parts.Count > 0) groups.Add(new PrefabGroupView("Damage", parts));

        // What the car is actually shot at. This is not a detail of the damage model — it IS the collision:
        // the frame graph's collision stubs are a second copy of these placements that the game never reads,
        // so a shape only exists in game because a volume here names it.
        var volumes = new List<PrefabRefView>();
        int volumeCount = 0;
        foreach (CarDeformPart part in file.CarDeformParts)
        {
            if (part.Volumes.Count == 0) continue;
            volumeCount += part.Volumes.Count;
            string bone = names.TryGetValue(part.Frame, out string? found) ? found : "?";
            volumes.Add(new PrefabRefView($"{bone} ({part.Kind})",
                part.Volumes.Count == 1 ? "1 volume" : $"{part.Volumes.Count} volumes",
                PrefabRefKind.Fact, null));

            foreach (CarPhysicsVolume volume in part.Volumes)
            {
                int flat = file.CarVolumeIndex(part.Index, volume.Index);
                // A shape's SURFACE, when it names one. Shipped shapes all carry 0, so this row is how a
                // toolkit-minted shape shows what it was given — the field is reachable nowhere else.
                string surface = "";
                if (volume.NamesShape && surfaces.TryGetValue(volume.ShapeHash, out ushort id) && id != 0)
                {
                    // ForRawId, not ForTableIndex: the field on disk is a PhysX slot id and the table index is
                    // that minus the bias. Reading it as a table index named the surface two places off — and
                    // that is the same mistake that made the first in-game surface test come back empty.
                    surface = $"  ·  {CollisionMaterialCatalog.ForRawId(id).Name}";
                }
                // WHAT it is, as a picker rather than a fact. The kinds are not interchangeable — a window is
                // type 0 on all 527 shipped ones, a body type 5 on all 407 — and until this could be changed
                // the only way to turn one into the other was to delete it and lose its placement.
                volumes.Add(Sub(new PrefabRefView(
                    "What it is",
                    VolumeKindName(volume.VolumeType) + (volume.NamesShape ? surface : ""),
                    PrefabRefKind.Reference,
                    volume.NamesShape
                        ? "places a physics shape from this archive's ItemDesc — its size is the shape's"
                        : "a box that says what it is by its type; it states its own full size")
                {
                    Item = CarItemKind.CollisionVolume,
                    ItemIndex = flat,
                    Index = flat,
                    Options = VolumeKinds,
                    ChoosesVolumeType = true,
                }));
                // Shown in the CAR's axes, not in the bone's.
                //
                // A volume is stored in the space of a bone — its own part's when it places a shape, that of
                // the part it hangs off when it describes itself — and most car bones are turned relative to
                // the car. Read raw, the field lied about which way it pointed: on a door-mounted window,
                // typing into Z moved the box along the car and into Y moved it upwards. So the bone is
                // divided out here and multiplied back in on the way to the file; what stays in the file is
                // untouched, and X is across the car, Y along it, Z up — the same axes the viewport draws.
                ulong owner = volume.NamesShape ? part.Frame : (part.ParentFrame == 0 ? part.Frame : part.ParentFrame);
                Matrix4x4? space = boneWorlds != null && boneWorlds.TryGetValue(owner, out Matrix4x4 at)
                    ? at
                    : null;
                var stored = new Vector3(
                    file.GetCarValue(CarValueSlot.CollisionVolumePosition, flat, 0),
                    file.GetCarValue(CarValueSlot.CollisionVolumePosition, flat, 1),
                    file.GetCarValue(CarValueSlot.CollisionVolumePosition, flat, 2));
                volumes.Add(Sub(Vector("Position",
                    space is { } m ? Vector3.Transform(stored, m) : stored,
                    CarValueSlot.CollisionVolumePosition, flat) with
                {
                    Space = space,
                    Detail = space == null
                        ? $"measured from \"{names.GetValueOrDefault(owner, bone)}\", in ITS axes"
                        : "in the car's own axes: X across, Y along, Z up",
                }));
                if (!volume.NamesShape)
                {
                    volumes.Add(Sub(Vector("Size", new Vector3(
                        file.GetCarValue(CarValueSlot.CollisionVolumeSize, flat, 0),
                        file.GetCarValue(CarValueSlot.CollisionVolumeSize, flat, 1),
                        file.GetCarValue(CarValueSlot.CollisionVolumeSize, flat, 2)),
                        CarValueSlot.CollisionVolumeSize, flat)));
                }
            }
        }
        // Counted in VOLUMES, not in the part rows they hang under — deleting the two that shared a part used
        // to take the header from 19 to 18, because what it was counting was parts that still had a volume left.
        if (volumes.Count > 0) groups.Add(new PrefabGroupView("Collision", volumes, volumeCount));

        return groups;

        // A part IS its frame row: the label names the part, the picker names the bone, and the row carries
        // the part identity so the remove button has something to remove. A header line over it saying the
        // same thing in other words was the tab's own noise, not the file's.
        PrefabRefView Part(string label, ulong hash, CarFrameSlot slot, int index, CarItemKind kind,
            int itemIndex = -1) =>
            Reference(label, hash, slot, index) with { Item = kind, ItemIndex = itemIndex };

        static PrefabRefView Sub(PrefabRefView row) => row with { Sub = true };

        static PrefabRefView Number(string label, float value, CarValueSlot slot, int index, string format) =>
            new PrefabRefView(label, value.ToString(format, CultureInfo.InvariantCulture),
                PrefabRefKind.Number, null, null, index, slot, value) { Format = format };

        static PrefabRefView Flag(string label, bool value, CarValueSlot slot, int index) =>
            new(label, value ? "yes" : "no", PrefabRefKind.Flag, null, null, index, slot, value ? 1 : 0);

        static PrefabRefView Vector(string label, Vector3 v, CarValueSlot slot, int index) =>
            new(label, Point(v), PrefabRefKind.Vector, null, null, index, slot, v.X, v.Y, v.Z);
    }

    /// <summary>The kinds a collision volume can be, as a picker offers them. The hash slot carries the type
    /// number, which is all a type is.</summary>
    private static readonly IReadOnlyList<FrameChoice> VolumeKinds =
    [
        new(5, "Solid — a physics shape"),
        new(0, "Glass"),
        new(6, "Zone — engine bay, snow"),
    ];

    /// <summary>What a volume type is, in words.</summary>
    private static string VolumeKindName(uint type) => type switch
    {
        0 => "Glass",
        5 => "Solid — a physics shape",
        6 => "Zone — engine bay, snow",
        _ => "type " + type.ToString(CultureInfo.InvariantCulture),
    };


    // A band built straight off a slot's own count — for the lists that have no view-model of their own.
    private static void Slots(
        List<PrefabGroupView> groups, string title, string label, CarFrameSlot slot, PrefabFile file,
        Func<string, ulong, CarFrameSlot, int, string?, PrefabRefView> reference)
    {
        int count = file.CarSlotCount(slot);
        var rows = new List<PrefabRefView>();
        for (int i = 0; i < count; i++)
        {
            rows.Add(reference(count == 1 ? label : $"{label} {i + 1}", file.GetCarFrame(slot, i), slot, i, null));
        }
        if (rows.Count > 0) groups.Add(new PrefabGroupView(title, rows));
    }

    // One band per hash list, so its "+" means one thing. The band stands whether or not the list has
    // anything in it: the "+" lives on the header, so a band that vanishes with its last row takes the only
    // way of putting one back with it — a car that loses its last wiper could never have another.
    private static void AddList(
        List<PrefabGroupView> groups, string title, string label, IReadOnlyList<ulong> hashes,
        CarFrameSlot slot, Func<string, ulong, CarFrameSlot, int, string?, PrefabRefView> reference)
    {
        var rows = new List<PrefabRefView>();
        for (int i = 0; i < hashes.Count; i++)
        {
            rows.Add(reference(hashes.Count == 1 ? label : $"{label} {i + 1}", hashes[i], slot, i, null));
        }
        groups.Add(new PrefabGroupView(title, rows));
    }

    private static string Point(Vector3 v) =>
        string.Create(CultureInfo.InvariantCulture, $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})");
}

/// <summary>What one row IS — which decides how it is drawn, how it is edited, and whether it is a problem.</summary>
public enum PrefabRefKind
{
    /// <summary>Names a frame that is in this archive.</summary>
    Reference,

    /// <summary>Names a frame that is NOT in this archive. The part it drives silently does nothing in game.</summary>
    Dangling,

    /// <summary>Left empty on purpose — a car with no top light has no top light. Not a fault.</summary>
    Unset,

    /// <summary>A number the game reads: a depth, a mass, a radius.</summary>
    Number,

    /// <summary>A yes/no the game reads: whether a window opens.</summary>
    Flag,

    /// <summary>A position in the car's own space: where a hand grabs a door, where an occupant sits.</summary>
    Vector,

    /// <summary>A count or a total the toolkit only reports — nothing to edit.</summary>
    Fact,

    /// <summary>Not a value at all: the line that says which part the rows under it belong to.</summary>
    Header,
}

/// <summary>One frame of the archive, as an option a reference can be pointed at.</summary>
public sealed record FrameChoice(ulong Hash, string Name)
{
    /// <summary>What the picker shows.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// One row: what the slot is for, what is in it, how to read that — and, when it is a reference the toolkit
/// can move, WHICH slot it is, so an edit knows where to write.
/// </summary>
public sealed record PrefabRefView(
    string Label, string Value, PrefabRefKind Kind, string? Detail,
    CarFrameSlot? Slot = null, int Index = 0,
    CarValueSlot? ValueSlot = null, float X = 0, float Y = 0, float Z = 0,
    CarItemKind? Item = null, bool Sub = false, int ItemIndex = -1)
{
    /// <summary>How this row's number is written out — a count has no decimals, a mass has one, a position
    /// has three. Kept so a row edited in the panel can re-render itself the way it was first read, instead
    /// of going stale the moment somebody types into it.</summary>
    public string? Format { get; init; }

    /// <summary>
    /// The matrix this row's X/Y/Z were converted OUT of, when they were — so the value typed back in can be
    /// converted into it again. Null means the row's numbers are the file's own.
    ///
    /// <para>
    /// A collision volume is stored in the space of a bone, and most car bones are turned relative to the
    /// car; the panel shows the car's axes instead, because a field whose Z runs along the car is a field
    /// that cannot be used. Both directions have to know the same matrix, which is why it rides on the row.
    /// </para>
    /// </summary>
    public Matrix4x4? Space { get; init; }

    /// <summary>
    /// The choices THIS row offers, when they are not the archive's frames — today only the kinds a collision
    /// volume can be. Null means the ordinary frame list.
    /// </summary>
    public IReadOnlyList<FrameChoice>? Options { get; init; }

    /// <summary>Whether picking from <see cref="Options"/> changes a collision volume's KIND rather than
    /// pointing a slot at a frame. The two go through the same dropdown and mean nothing alike.</summary>
    public bool ChoosesVolumeType { get; init; }

    /// <summary>Where the PART sits in its own list — which is not where its frame sits in the frame list:
    /// axles are listed one per row but stored two at a time, so an axle row's part index is its pair.</summary>
    public int PartIndex => ItemIndex < 0 ? Index : ItemIndex;

    /// <summary>Whether this row can be pointed at another frame. A number is edited, not pointed.</summary>
    public bool CanEdit => Slot != null || ChoosesVolumeType;

    /// <summary>Whether this row holds a value the game reads and the toolkit can write.</summary>
    public bool CanSet => ValueSlot != null;
}

/// <summary>One band of rows — the doors, the seats, the axles.</summary>
public sealed class PrefabGroupView
{
    public PrefabGroupView(string title, IReadOnlyList<PrefabRefView> rows, int? count = null)
    {
        Title = title;
        Rows = rows;
        _count = count;
    }

    /// <summary>What the badge counts when the band's own rows would answer wrongly — see <see cref="PartCount"/>.</summary>
    private readonly int? _count;

    public string Title { get; }

    public IReadOnlyList<PrefabRefView> Rows { get; }

    public int DanglingCount => Rows.Count(r => r.Kind == PrefabRefKind.Dangling);

    /// <summary>
    /// How many THINGS the band holds — four doors, not the twelve rows it takes to describe them. A field
    /// row is part of the thing above it, never a thing of its own.
    /// <para>
    /// A band may say so itself. Collision counts VOLUMES: its top-level rows are the parts the volumes hang
    /// off, so counting rows made the header read "19" for a car with 22 volumes, and deleting the two that
    /// shared a part dropped it to 18 — a number that moved by one for two deletions and looked like a
    /// miscount.
    /// </para>
    /// </summary>
    public int PartCount => _count ?? Rows.Count(r => !r.Sub);

    /// <summary>What the group's header says beside its name: how many, and how many are broken.</summary>
    public string Badge => DanglingCount == 0
        ? PartCount.ToString(CultureInfo.InvariantCulture)
        : $"{PartCount} · {DanglingCount} dangling";

    public bool HasDangling => DanglingCount > 0;
}

/// <summary>One prefab entry: whose it is, what kind, and what it is made of.</summary>
public sealed class PrefabEntryView
{
    public PrefabEntryView(
        string owner, string typeName, string sizeText, bool decoded,
        IReadOnlyList<PrefabGroupView> groups, string status)
    {
        Owner = owner;
        TypeName = typeName;
        SizeText = sizeText;
        Decoded = decoded;
        Groups = groups;
        Status = status;
    }

    /// <summary>The owning actor's name hash. Shown raw: resolving it needs an .act that a car does not
    /// ship, and inventing a name for it would be exactly the guess this panel exists to avoid.</summary>
    public string Owner { get; }

    public string TypeName { get; }

    public string SizeText { get; }

    public bool Decoded { get; }

    public IReadOnlyList<PrefabGroupView> Groups { get; }

    /// <summary>One line under the header: the resolve tally, or why there is none.</summary>
    public string Status { get; }

    public int DanglingCount => Groups.Sum(g => g.DanglingCount);

    public bool HasDangling => DanglingCount > 0;
}
