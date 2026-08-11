using System.Numerics;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>
/// The markers of a car — the seats, climb boxes, fuel tanks, exhaust emitters, wipers and lights — as things
/// of the component they hang off rather than as a flat pile beside it, and the ONE path from an edit made on
/// one of them to bytes.
///
/// <para>
/// Ownership is the bone: a marker belongs to the component owning the bone its frame hangs off, and to the
/// body when no component owns that bone. What each one carries beyond the frame it names lives in a parallel
/// list of its own — a seat's type and group, a climb box's extents — and this is where those become numbers
/// a modder can edit where the marker sits instead of rows they have to count.
/// </para>
/// </summary>
public sealed partial class Car
{
    // ── reading: what a marker's own row carries ──

    /// <summary>
    /// The values a marker's row holds beyond the frame it names.
    ///
    /// <para>
    /// Four of the six roles have none: a fuel tank, an exhaust emitter, a wiper and a light are a frame name
    /// and nothing else, so they are placed by moving that frame and there is nothing here to type.
    /// </para>
    /// </summary>
    private static IReadOnlyList<CarField> MarkerFields(PrefabFile prefab, CarMarkerRole role, int index) =>
        role switch
        {
            CarMarkerRole.Seat =>
            [
                Number(prefab, "Seat number", "WHICH seat of the car this is — not its place in the list. "
                    + "Measured over the 213 shipped seats it is unique within its car and below the seat "
                    + "count, and the commonest two-seater is written 1, 0.",
                    CarFieldKind.Count, CarValueSlot.SeatIndex, index),
                Number(prefab, "Seat type", "Which kind of seat the game treats this as. What each number "
                    + "means is the game's, not the toolkit's — the shipped cars are the reference.",
                    CarFieldKind.Count, CarValueSlot.SeatType, index),
                Number(prefab, "Seat group", "Which group of seats this one belongs to.",
                    CarFieldKind.Count, CarValueSlot.SeatGroup, index),
                Point(prefab, "Where the occupant sits", "In the car's own coordinates. It is written down "
                    + "twice — here and as the marker's own place — and on 212 of the 213 shipped seats the "
                    + "two are the same point, so moving the marker rewrites this and typing it here moves "
                    + "the marker.", CarValueSlot.SeatPosition, index),
            ],
            CarMarkerRole.ClimbBox =>
            [
                Point(prefab, "Lowest corner", "The box a player may climb on, in the car's own coordinates "
                    + "and always axis-aligned. Moving or scaling the marker rewrites both corners, and "
                    + "typing them here moves the marker — the game climbs THIS and never the frame.",
                    CarValueSlot.ClimbBoxMin, index),
                Point(prefab, "Highest corner", "The other corner of the same box.",
                    CarValueSlot.ClimbBoxMax, index),
            ],
            _ => [],
        };

    /// <summary>
    /// Hangs each of the prefab's own-bone rows off the component whose bone it names — the door points that
    /// say where a handle and a lock are, the window record that says how deep a pane sits and whether it
    /// rolls down, the axle that carries the brake drum and the masses.
    ///
    /// <para>
    /// A row naming a bone no component owns goes to the body, exactly as a homeless marker does, so that
    /// nothing in the car is unreachable. That it did not line up is a fault of its own and is raised by
    /// <c>MatchRows</c>; this is only about where the numbers can be edited.
    /// </para>
    /// </summary>
    private static void HangRows(
        PrefabFile prefab, CarPrefab car, Dictionary<ulong, CarComponent> byBone, CarComponent? body)
    {
        IReadOnlyList<CarPrefab.DoorPoints> doors = car.Doors;
        for (int i = 0; i < doors.Count; i++)
        {
            Hang(doors[i].Frame, new CarComponentRow(
                Numbered("Door points", i, doors.Count), "door", i,
                [
                    Point(prefab, "Handle", "Where the door handle is, in the car's own coordinates — what "
                        + "the player reaches for.", CarValueSlot.DoorHandle, i),
                    Point(prefab, "Lock", "Where the lock is.", CarValueSlot.DoorLock, i),
                ]));
        }

        IReadOnlyList<CarPrefab.Window> windows = car.Windows;
        for (int i = 0; i < windows.Count; i++)
        {
            Hang(windows[i].Frame, new CarComponentRow(
                Numbered("Window", i, windows.Count), "window", i,
                [
                    Number(prefab, "Depth", "How deep the pane sits in its frame, in metres.",
                        CarFieldKind.Number, CarValueSlot.WindowDepth, i),
                    Number(prefab, "Rolls down", "Whether this pane can be wound down.",
                        CarFieldKind.Flag, CarValueSlot.WindowOpenable, i),
                ]));
        }

        IReadOnlyList<CarPrefab.Axle> axles = car.Axles;
        for (int i = 0; i < axles.Count; i++)
        {
            Hang(axles[i].Frame, new CarComponentRow(
                Numbered("Axle", i, axles.Count), "axle", i,
                [
                    Number(prefab, "Axle type", "Which kind of axle this is, as the game numbers them.",
                        CarFieldKind.Count, CarValueSlot.AxleType, i),
                    Number(prefab, "Brake drum radius", "In metres.",
                        CarFieldKind.Number, CarValueSlot.AxleBrakeDrumRadius, i),
                    Number(prefab, "Brake drum mass", "In kilograms.",
                        CarFieldKind.Number, CarValueSlot.AxleBrakeDrumMass, i),
                    Number(prefab, "Axle mass", "In kilograms.",
                        CarFieldKind.Number, CarValueSlot.AxleMass, i),
                ]));
        }

        void Hang(ulong bone, CarComponentRow row)
        {
            if (bone == 0) return;
            (byBone.GetValueOrDefault(bone) ?? body)?.AddRow(row);
        }
    }

    /// <param name="axis">Which part of the addressed value this is — an axis, or the BIT a flag lives in
    /// inside a word that holds several. Zero, the whole value, for all but the deform-part flags.</param>
    private static CarField Number(
        PrefabFile prefab, string label, string hint, CarFieldKind kind, CarValueSlot slot, int index,
        int axis = 0)
    {
        float value = prefab.GetCarValue(slot, index, axis);
        return new CarField(label, hint, kind, float.IsNaN(value) ? 0f : value, Vector3.Zero)
        {
            Slot = slot,
            At = index,
            Axis = axis,
        };
    }

    private static CarField Point(
        PrefabFile prefab, string label, string hint, CarValueSlot slot, int index)
    {
        var point = new Vector3(
            Axis(prefab, slot, index, 0), Axis(prefab, slot, index, 1), Axis(prefab, slot, index, 2));
        return new CarField(label, hint, CarFieldKind.Point, 0f, point) { Slot = slot, At = index };

        static float Axis(PrefabFile prefab, CarValueSlot slot, int index, int axis)
        {
            float value = prefab.GetCarValue(slot, index, axis);
            return float.IsNaN(value) ? 0f : value;
        }
    }

    // ── writing ──

    /// <summary>
    /// Writes the values a marker's own row carries, and moves the marker itself when one of them says where
    /// it is.
    ///
    /// <para>
    /// Two roles state their place twice: the game climbs a climb box's ROW and never its Dummy, and a seat's
    /// row carries the position its Dummy stands at on 212 of the 213 shipped seats. Both are one thing the
    /// modder authored written down in two places, so typing either here moves the marker and moving the
    /// marker rewrites this — rather than leaving the two to drift into disagreeing.
    /// </para>
    /// </summary>
    /// <param name="fields">The marker's own fields with new values in them — <c>field with { … }</c>. They
    /// carry the address they are written through, so a row and its write cannot address different fields.</param>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then.</returns>
    public CarEdit? SetMarker(CarMarker marker, IReadOnlyList<CarField> fields, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(fields);
        refusal = null;

        if (Taken(marker, fields, ref refusal)) return null;

        bool placed = marker.Role is CarMarkerRole.ClimbBox or CarMarkerRole.Seat;
        List<FrameObjectBase> frames = placed && Frame(marker.Frame) is { } own ? [own] : [];
        CarState before = Snapshot([], frames);

        // What the row said about where the marker is, BEFORE the write — so that the marker is moved only
        // when that is what changed. Recomposing a frame's matrix from its own decomposition is not
        // bit-exact, so moving it "back to where it already is" because a modder changed a seat's TYPE would
        // rewrite the frame resource with a drift nobody asked for.
        Vector3[] was = Placement(marker);
        if (!Write(fields, ref refusal)) { Restore(before); return null; }
        Vector3[] now = Placement(marker);

        if (!was.SequenceEqual(now))
        {
            if (marker.Role == CarMarkerRole.ClimbBox) PlaceClimbBox(marker.Frame, marker.Index);
            else if (Frame(marker.Frame) is { } seat) MoveFrame(seat, now[0]);
        }

        return new CarEdit($"{marker.Label} changed", before, Snapshot([], frames));
    }

    /// <summary>Writes the values one of a component's own-bone rows carries — a door's handle and lock, a
    /// window's depth, an axle's masses.</summary>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then.</returns>
    public CarEdit? SetRow(CarComponentRow row, IReadOnlyList<CarField> fields, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(fields);
        refusal = null;

        CarState before = Snapshot([], []);
        if (!Write(fields, ref refusal)) { Restore(before); return null; }
        return new CarEdit($"{row.Label} changed", before, Snapshot([], []));
    }

    /// <summary>
    /// Puts every field where it belongs, refusing the whole set rather than half of it — and writing only the
    /// ones whose value is not already there.
    ///
    /// <para>
    /// The skip is not an optimisation, it is what keeps this from undoing somebody else's edit. A field was
    /// read when the row was BUILT, and the fields come back from a dialog the modder may have had open for a
    /// while; meanwhile the Prefab tab writes the very same numbers straight to the working copy and re-stitches
    /// nothing. Writing all of them back would put every value the modder did not touch to what it was when the
    /// row was built — silently reverting that other edit and reporting success. Writing only what differs makes
    /// an untouched field mean "leave it alone", which is what it says on screen.
    /// </para>
    /// <para>
    /// The finiteness check stays ahead of the skip: a value that is not a number is refused even when the file
    /// happens to hold the same nonsense.
    /// </para>
    /// </summary>
    private bool Write(IReadOnlyList<CarField> fields, ref string? refusal)
    {
        foreach (CarField field in fields)
        {
            if (!Finite(field))
            {
                refusal = $"\"{field.Label}\" has to be a number";
                return false;
            }
            bool ok = field.Kind == CarFieldKind.Point
                ? Put(field, 0, field.Point.X) && Put(field, 1, field.Point.Y) && Put(field, 2, field.Point.Z)
                : Put(field, field.Axis, field.Number);
            if (!ok)
            {
                refusal = $"the prefab would not take \"{field.Label}\" — that row is no longer there";
                return false;
            }
        }
        return true;
    }

    /// <summary>One number of one field, written only when the prefab does not already hold it. A slot that
    /// answers NaN is one this car has no row for, and saying so is the caller's refusal.</summary>
    private bool Put(CarField field, int axis, float value)
    {
        float has = Prefab.GetCarValue(field.Slot, field.At, axis);
        if (float.IsNaN(has)) return false;
        return has == value || Prefab.SetCarValue(field.Slot, field.At, axis, value);
    }

    /// <summary>
    /// Whether a seat is being given a number another seat of this car already has.
    ///
    /// <para>
    /// Refused, because it is the one invariant the seat numbering has: measured over the 213 seats of the 85
    /// shipped cars it is unique within its car 213 of 213 and below the seat count 213 of 213 — a permutation
    /// of 0…n−1 — and the add and remove paths keep it that way on purpose. A car with two seat 1s and no
    /// seat 0 is a shape nothing shipped and nothing here can say what the game does with.
    /// </para>
    /// </summary>
    private bool Taken(CarMarker marker, IReadOnlyList<CarField> fields, ref string? refusal)
    {
        if (marker.Role != CarMarkerRole.Seat || Prefab.Car is not { } car) return false;
        CarField? number = fields.FirstOrDefault(f => f.Slot == CarValueSlot.SeatIndex);
        if (number == null) return false;

        var want = (uint)MathF.Max(0f, MathF.Round(number.Number));
        IReadOnlyList<CarPrefab.Seat> seats = car.Seats;
        for (int i = 0; i < seats.Count; i++)
        {
            if (i == marker.Index || seats[i].Index != want) continue;
            refusal = $"seat {want.ToString(System.Globalization.CultureInfo.InvariantCulture)} is already "
                + "this car's — every seat of a car has a number of its own, on all 213 shipped ones. Give "
                + "the other seat a different number first, or pick one nothing is using.";
            return true;
        }
        return false;
    }

    private static bool Finite(CarField field) =>
        field.Kind == CarFieldKind.Point
            ? float.IsFinite(field.Point.X) && float.IsFinite(field.Point.Y) && float.IsFinite(field.Point.Z)
            : float.IsFinite(field.Number);

    /// <summary>
    /// Adds a marker of a kind that is a HELPER frame: the Dummy or Point is minted, hung off the component's
    /// own bone, and the prefab row that names it is written beside it.
    ///
    /// <para>
    /// The kinds that are a bone every time are refused with the reason rather than given a Dummy the game
    /// will not read — measured over the 85 shipped prefabs, a wiper is a bone on all 148 of them, and the
    /// toolkit cannot add a bone to a rig. A light is refused for a different reason: a car has exactly one
    /// headlight, backlight and toplight slot, so a light is POINTED at a frame rather than added.
    /// </para>
    /// </summary>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then.</returns>
    public CarEdit? AddMarker(CarComponent component, CarMarkerRole role, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(component);
        refusal = MintRefusal(component, role);
        if (refusal != null) return null;

        (FrameResourceObjectType type, string stem) = Helper(role)!.Value;
        FrameResource resource = Frames!;
        FrameObjectModel model = Model!;

        FrameObjectBase? donor = Donor(type, SiblingFrame(role));
        if (donor == null)
        {
            refusal = "this archive has no frame to copy the wiring from";
            return null;
        }

        // The state to come BACK to is captured now and assembled at the end, because what this intent
        // touches does not exist yet: an undo has to know the frame it must take out of the graph.
        byte[] prefabWas = Prefab.ToBytes();
        FrameObjectModel.HitBoxInfo[]? boxesWere = CopyBoxes();

        string name = UniqueFrameName(stem);
        // The factory already files the new frame under its own RefID — adding it again throws.
        FrameObjectBase frame = FrameFactory.ConstructFrameByObjectID(resource, type);
        frame.Name = new HashName(name);
        frame.LocalTransform = Matrix4x4.Identity;
        // The wiring is the archive's own convention rather than an invention: ParentIndex1 cascades the
        // transform and ParentIndex2 anchors the frame to a scene, and getting the two the wrong way round is
        // what sends an attachment off to the model's origin.
        frame.SetParent(ParentInfo.ParentType.ParentIndex1, donor.Parent);
        frame.SetParent(ParentInfo.ParentType.ParentIndex2, donor.Root);
        frame.IsOnFrameTable = donor.IsOnFrameTable;
        frame.FrameNameTableFlags = donor.FrameNameTableFlags;
        if (frame is FrameObjectDummy dummy)
        {
            // A climb box starts out big enough to be one: the shipped ones run about 1.6 × 0.8 × 1.8 m, and
            // a step nobody can stand on is not a climb box. Everything else is a marker, and 5 cm is small
            // enough to be one and large enough to grab.
            Vector3 half = role == CarMarkerRole.ClimbBox ? ClimbBoxHalfSize : MarkerHalfSize;
            dummy.Bounds = new BoundingBox(-half, half);
        }
        model.AttachToJoint(frame, (byte)component.BoneJoint);
        TouchFrames(nameTable: true);

        // The prefab row last: if it will not take one, the frame goes back out so nothing half-made is left.
        CarItemKind kind = ItemKind(role)!.Value;
        int index = Prefab.CarItemCount(kind);
        if (!Prefab.AddCarItem(kind, Fnv64.Hash(name)))
        {
            DropFrame(frame);
            refusal = $"the car's prefab would not take another {Words(role)}";
            return null;
        }
        // The new row was COPIED from the last one there, so without this a new climb box sits exactly on top
        // of an existing one and there is nothing new to climb, however the Dummy is dragged, and a new seat
        // seats its occupant wherever the previous one did. This makes each row say what its OWN frame says.
        SyncMarkers([frame]);

        var before = new CarState(
            prefabWas, [],
            [new CarFrameState(frame, Present: false, frame.LocalTransform, component.BoneJoint,
                Parent: null, Root: null, Order: -1, Attached: -1)],
            boxesWere);
        return new CarEdit(
            $"{Words(role)} added to \"{component.Name}\"", before, Snapshot([], [frame]));
    }

    /// <summary>
    /// Takes a marker off its component: the prefab row goes, and the helper frame it named goes with it when
    /// nothing else in the assembly still names that frame.
    ///
    /// <para>
    /// Only a Dummy or a Point is ever taken away. A wiper's row names a BONE, and removing a bone renumbers
    /// every vertex weight in the model — the row goes and the bone stays, which leaves the wiper as what it
    /// then is: a bone with geometry that nothing claims.
    /// </para>
    /// </summary>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is changed then.</returns>
    public CarEdit? RemoveMarker(CarMarker marker, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(marker);
        refusal = null;

        FrameObjectBase? frame = Frame(marker.Frame);
        List<FrameObjectBase> frames = frame == null ? [] : [frame];
        CarState before = Snapshot([], frames);

        if (marker.Role == CarMarkerRole.Light)
        {
            // A light is one of three single slots rather than a row of a list, so taking it away is emptying
            // the slot — which is how the cars that carry no toplight are written.
            if (LightSlot(marker.Index) is not { } slot || !Prefab.SetCarFrame(slot, 0, 0))
            {
                refusal = "that light is no longer there";
                return null;
            }
        }
        else if (ItemKind(marker.Role) is not { } kind || Prefab.TakeCarItem(kind, marker.Index) == null)
        {
            refusal = $"the prefab would not give the {Words(marker.Role)} up";
            return null;
        }

        // The frame goes only when the assembly has stopped naming it — another row may point at the same
        // Dummy, and taking it out would remove that one's marker as a side effect.
        if (frame is FrameObjectDummy or FrameObjectPoint && !Named(marker.Frame)) DropFrame(frame);

        return new CarEdit(
            $"{marker.Label} removed from \"{Owner(marker)}\"", before, Snapshot([], frames));
    }

    /// <summary>
    /// Rewrites the rows of the markers whose FRAME has been moved, so that dragging one changes what the game
    /// reads and not only what the editor draws.
    ///
    /// <para>
    /// Two of the six roles say where they are twice. A climb box's row carries a Min/Max in the car's own
    /// coordinates and the game climbs the ROW; a seat's row carries a position beside the Dummy it names, and
    /// on 212 of the 213 shipped seats that position IS where the Dummy stands. Either way the frame is where
    /// the modder edits and the row is what the game reads, so one has to follow the other.
    /// </para>
    /// <para>
    /// Only the frames that were actually MOVED — the same rule the collision stubs follow, and for the same
    /// reason: 18 of the 280 shipped climb-box rows already disagree with their Dummy, and rewriting those
    /// from a frame nobody touched would change cars nobody asked about.
    /// </para>
    /// </summary>
    /// <returns>How many rows had to change. Nothing is written to a file — <see cref="Save"/> does that.</returns>
    public int SyncMarkers(IReadOnlyCollection<FrameObjectBase> moved)
    {
        ArgumentNullException.ThrowIfNull(moved);
        if (Prefab.Car is not { } car || moved.Count == 0) return 0;

        var byHash = new Dictionary<ulong, FrameObjectBase>();
        foreach (FrameObjectBase frame in moved)
        {
            if (frame.Name?.String is { Length: > 0 } name) byHash[Fnv64.Hash(name)] = frame;
        }

        int written = 0;
        IReadOnlyList<CarPrefab.ClimbBox> boxes = car.ClimbBoxes;
        for (int i = 0; i < boxes.Count; i++)
        {
            if (byHash.GetValueOrDefault(boxes[i].Dummy) is not FrameObjectDummy dummy) continue;
            (Vector3 min, Vector3 max) = BoxOf(dummy);
            if (Near(min, boxes[i].Min) && Near(max, boxes[i].Max)) continue;

            bool ok = true;
            for (int axis = 0; axis < 3; axis++)
            {
                ok &= Prefab.SetCarValue(CarValueSlot.ClimbBoxMin, i, axis, Axis(min, axis));
                ok &= Prefab.SetCarValue(CarValueSlot.ClimbBoxMax, i, axis, Axis(max, axis));
            }
            if (ok) written++;
        }

        IReadOnlyList<CarPrefab.Seat> seats = car.Seats;
        for (int i = 0; i < seats.Count; i++)
        {
            if (byHash.GetValueOrDefault(seats[i].Frame) is not { } frame) continue;
            Vector3 at = frame.WorldTransform.Translation;
            if (Near(at, seats[i].Position)) continue;

            bool ok = true;
            for (int axis = 0; axis < 3; axis++)
            {
                ok &= Prefab.SetCarValue(CarValueSlot.SeatPosition, i, axis, Axis(at, axis));
            }
            if (ok) written++;
        }
        return written;
    }

    /// <summary>
    /// The same, for a frame graph the caller already holds — what a frame-resource save runs so that dragging
    /// a marker changes what the game reads and not only what the editor draws.
    /// </summary>
    /// <param name="lost">What the save would not deliver, or null when it delivered everything. A save can be
    /// REFUSED — the prefab has to survive being written and read back, and it refuses outright if somebody
    /// else has written the file since — and a caller that threw this away would let the frame move while the
    /// row the game reads stayed where it was, silently.</param>
    /// <returns>How many rows changed. 0 with a <paramref name="lost"/> means nothing was written at all.</returns>
    public static int SyncMarkers(
        string extracted, FrameResource frames, IReadOnlyCollection<FrameObjectBase> moved,
        out string? lost)
    {
        ArgumentException.ThrowIfNullOrEmpty(extracted);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(moved);
        lost = null;
        if (moved.Count == 0) return 0;

        foreach (string file in PrefabFiles(extracted))
        {
            PrefabFile prefab;
            try { prefab = PrefabFile.Load(file); }
            catch (Exception ex) when (ex is IOException or SdsFormatException) { continue; }
            if (prefab.Car == null) continue;

            Car car = Stitch(prefab, frames, lod: 0, previous: null, prefabPath: file, extracted: extracted);
            int written = car.SyncMarkers(moved);
            if (written == 0) return 0;

            CarSave saved = car.Save();
            if (saved.Ok) return written;
            lost = string.Join("; ", saved.Lost);
            return 0;
        }
        return 0;
    }

    /// <summary>
    /// The box a climb-box row states for a Dummy: the Dummy's OWN extents, scaled as the frame is scaled, and
    /// centred where the frame stands.
    ///
    /// <para>
    /// It is deliberately not the box's eight corners through the world matrix. Every one of the 280 shipped
    /// climb-box Dummies is turned relative to the car, so the two readings are different boxes — and the row
    /// is this one on 262 of 280 and the eight-corner one on 3. Reading it the other way and writing the
    /// result back moved a shipped row by as much as 6.68 m.
    /// </para>
    /// </summary>
    public static (Vector3 Min, Vector3 Max) BoxOf(FrameObjectDummy dummy)
    {
        ArgumentNullException.ThrowIfNull(dummy);
        Matrix4x4 world = dummy.WorldTransform;
        TransformMath.TryDecompose(world, out Vector3 scale, out _, out _);
        Vector3 half = (dummy.Bounds.Max - dummy.Bounds.Min) * 0.5f * Vector3.Abs(scale);
        Vector3 centre = Vector3.Transform((dummy.Bounds.Min + dummy.Bounds.Max) * 0.5f, world);
        return (centre - half, centre + half);
    }

    // ── the derivation ──

    /// <summary>
    /// Places a climb box's Dummy onto the box its row now states — the inverse of
    /// <see cref="SyncMarkers(IReadOnlyCollection{FrameObjectBase})"/>, and what makes the row and the frame
    /// one authored box rather than two.
    /// </summary>
    private void PlaceClimbBox(ulong frameHash, int index)
    {
        if (Frame(frameHash) is not FrameObjectDummy dummy) return;

        Vector3 min = Stored(CarValueSlot.ClimbBoxMin, index);
        Vector3 max = Stored(CarValueSlot.ClimbBoxMax, index);
        Vector3 half = Vector3.Abs(max - min) * 0.5f;
        // The size goes into the Dummy's own box and the frame is left unscaled, because that is the shape the
        // shipped cars are in — the row states extents, and a scale on the frame would state them twice.
        dummy.Bounds = new BoundingBox(-half, half);
        MoveFrame(dummy, (min + max) * 0.5f, Vector3.One);
    }

    /// <summary>What a marker's row says about where the marker is — a climb box's two corners, a seat's one
    /// position, and nothing at all for the four roles that are placed by their frame alone.</summary>
    private Vector3[] Placement(CarMarker marker) => marker.Role switch
    {
        CarMarkerRole.ClimbBox =>
        [
            Stored(CarValueSlot.ClimbBoxMin, marker.Index), Stored(CarValueSlot.ClimbBoxMax, marker.Index),
        ],
        CarMarkerRole.Seat => [Stored(CarValueSlot.SeatPosition, marker.Index)],
        _ => [],
    };

    /// <summary>A point the prefab holds, read through the same accessor a field is written through.</summary>
    private Vector3 Stored(CarValueSlot slot, int index) => new(
        Prefab.GetCarValue(slot, index, 0),
        Prefab.GetCarValue(slot, index, 1),
        Prefab.GetCarValue(slot, index, 2));

    /// <summary>Puts a marker's frame where its row now says it is, keeping the turn it already has: the rows
    /// state a position and never an orientation, so throwing the frame's turn away would be a second edit
    /// nobody asked for.</summary>
    private void MoveFrame(FrameObjectBase frame, Vector3 at, Vector3? scale = null)
    {
        TransformMath.TryDecompose(frame.WorldTransform, out Vector3 was, out Quaternion turn, out _);
        frame.LocalTransform = TransformMath.ComputeLocalTransform(
            TransformMath.Compose(turn, scale ?? was, at), ParentWorld(frame));
        TouchFrames();
    }

    /// <summary>What a frame's local transform is measured against — the joint it hangs off when it is
    /// attached to one, which is how every marker of a car is placed, and its graph parent otherwise.</summary>
    private static Matrix4x4 ParentWorld(FrameObjectBase frame)
    {
        if (frame.AttachedTo is { } model) return model.GetJointWorldTransform(frame.AttachedJoint);
        return frame.Parent?.WorldTransform ?? frame.Root?.WorldTransform ?? Matrix4x4.Identity;
    }

    /// <summary>How big a minted marker is. Small enough to be one, large enough to see and grab.</summary>
    private static readonly Vector3 MarkerHalfSize = new(0.05f);

    /// <summary>What a new climb box is born as — the shipped ones run about 1.6 × 0.8 × 1.8 m across.</summary>
    private static readonly Vector3 ClimbBoxHalfSize = new(0.40f, 0.25f, 0.40f);

    /// <summary>Why a marker of this role cannot be minted on this component, or null when it can.</summary>
    private string? MintRefusal(CarComponent component, CarMarkerRole role)
    {
        if (Unaddable(role) is { } why) return $"{Words(role)} cannot be added — {why}";
        if (Helper(role) == null || ItemKind(role) == null) return $"{Words(role)} is not a marker this can add";
        if (!component.BoneResolves || component.BoneJoint < 0)
        {
            return $"\"{component.Name}\" names a bone this car does not have, so there is nowhere to hang a "
                + "marker. Put the bone back in Blender and push again.";
        }
        // The joint index rides as a single byte on the attachment.
        if (component.BoneJoint > byte.MaxValue)
        {
            return "that bone is past the 255th, which an attachment cannot name";
        }
        if (Frames?.FrameObjects == null || Model == null)
        {
            return "this car has no frame graph to mint a marker into";
        }
        return null;
    }

    /// <summary>What each addable role hangs on, and what a new one is called. Measured over the 85 shipped
    /// prefabs: a seat is a Dummy 213/213, a climb box a Dummy 280/280, a fuel tank a Dummy 87/87 and an
    /// exhaust emitter a Point 195/195.</summary>
    private static (FrameResourceObjectType Type, string Stem)? Helper(CarMarkerRole role) => role switch
    {
        CarMarkerRole.Seat => (FrameResourceObjectType.Dummy, "seat"),
        CarMarkerRole.ClimbBox => (FrameResourceObjectType.Dummy, "climb_box"),
        CarMarkerRole.FuelTank => (FrameResourceObjectType.Dummy, "fuel_tank"),
        CarMarkerRole.ExhaustEmitter => (FrameResourceObjectType.Point, "exhaust"),
        _ => null,
    };

    /// <summary>Why a role cannot be added as things stand — the one whose row names a bone of the rig every
    /// time, and the light, which is a slot rather than a list.</summary>
    private static string? Unaddable(CarMarkerRole role) => role switch
    {
        CarMarkerRole.Wiper => "a wiper is a BONE on all 148 shipped ones, and the toolkit cannot add a bone "
            + "to a rig. Add the bone in Blender first, then point a new row at it.",
        CarMarkerRole.Light => "a car has exactly one headlight, one backlight and one toplight, and each of "
            + "the three is a slot naming a frame the archive already has rather than a row that can be "
            + "added. Point one of them at a frame instead.",
        _ => null,
    };

    /// <summary>Which of the prefab's lists a role is a row of.</summary>
    private static CarItemKind? ItemKind(CarMarkerRole role) => role switch
    {
        CarMarkerRole.Seat => CarItemKind.Seat,
        CarMarkerRole.ClimbBox => CarItemKind.ClimbBox,
        CarMarkerRole.FuelTank => CarItemKind.FuelTank,
        CarMarkerRole.ExhaustEmitter => CarItemKind.Exhaust,
        CarMarkerRole.Wiper => CarItemKind.Wiper,
        _ => null,
    };

    /// <summary>Which of the three light slots a light marker is.</summary>
    private static CarFrameSlot? LightSlot(int index) => index switch
    {
        0 => CarFrameSlot.Headlight,
        1 => CarFrameSlot.Backlight,
        2 => CarFrameSlot.Toplight,
        _ => null,
    };

    /// <summary>The roles a modder can add as things stand — the four whose frame the toolkit can mint.</summary>
    public static IReadOnlyList<CarMarkerRole> AddableRoles =>
        [CarMarkerRole.Seat, CarMarkerRole.ClimbBox, CarMarkerRole.FuelTank, CarMarkerRole.ExhaustEmitter];

    /// <summary>What a role is called in a sentence a modder reads.</summary>
    public static string Words(CarMarkerRole role) => role switch
    {
        CarMarkerRole.Seat => "a seat",
        CarMarkerRole.ClimbBox => "a climb box",
        CarMarkerRole.FuelTank => "a fuel tank",
        CarMarkerRole.ExhaustEmitter => "an exhaust emitter",
        CarMarkerRole.Wiper => "a wiper",
        _ => "a light",
    };

    /// <summary>A role as a group of rows is headed.</summary>
    public static string RoleName(CarMarkerRole role) => role switch
    {
        CarMarkerRole.Seat => "Seats",
        CarMarkerRole.ClimbBox => "Climb boxes",
        CarMarkerRole.FuelTank => "Fuel tanks",
        CarMarkerRole.ExhaustEmitter => "Exhaust emitters",
        CarMarkerRole.Wiper => "Wipers",
        _ => "Lights",
    };

    /// <summary>The component a marker hangs off, by name — for the one line an edit tells the modder.</summary>
    private string Owner(CarMarker marker) =>
        (ComponentOfBone(marker.Bone) ?? Body)?.Name ?? "this car";

    /// <summary>The frame a hash names, or null when the graph holds none.</summary>
    private FrameObjectBase? Frame(ulong hash)
    {
        if (hash == 0 || Frames?.FrameObjects == null) return null;
        foreach (object o in Frames.FrameObjects.Values)
        {
            if (o is FrameObjectBase frame && frame.Name?.String is { Length: > 0 } name
                && Fnv64.Hash(name) == hash)
            {
                return frame;
            }
        }
        return null;
    }

    /// <summary>Whether anything in the assembly still names this frame — what decides if removing a marker
    /// may take its Dummy with it.</summary>
    private bool Named(ulong hash)
    {
        if (Prefab.Car is not { } car) return false;
        foreach (ulong reference in car.AllFrameReferences)
        {
            if (reference == hash) return true;
        }
        return false;
    }

    /// <summary>The frame an EXISTING marker of this role names, or 0 — the best donor there is.</summary>
    private ulong SiblingFrame(CarMarkerRole role)
    {
        if (Prefab.Car is not { } car) return 0;
        return role switch
        {
            CarMarkerRole.ClimbBox => car.ClimbBoxes.Count > 0 ? car.ClimbBoxes[^1].Dummy : 0,
            CarMarkerRole.FuelTank => car.FuelTanks.Count > 0 ? car.FuelTanks[^1] : 0,
            CarMarkerRole.Seat => car.Seats.Count > 0 ? car.Seats[^1].Frame : 0,
            CarMarkerRole.ExhaustEmitter => car.ExhaustEmitters.Count > 0 ? car.ExhaustEmitters[^1] : 0,
            _ => 0,
        };
    }

    /// <summary>
    /// The frame to copy the graph wiring from. First choice is the one an existing marker of the same role
    /// names, then any frame of the right type, and last a collision stub, which every car has and which hangs
    /// the same way — a climb box copied from another climb box lands where climb boxes live, while copying
    /// the first Dummy in the archive can land it under a completely different holder.
    /// </summary>
    private FrameObjectBase? Donor(FrameResourceObjectType type, ulong sibling)
    {
        List<FrameObjectBase> frames = [.. Frames!.FrameObjects.Values.OfType<FrameObjectBase>()];
        if (sibling != 0)
        {
            FrameObjectBase? named = frames.FirstOrDefault(f =>
                f.Name?.String is { Length: > 0 } n && Fnv64.Hash(n) == sibling);
            if (named != null) return named;
        }
        FrameObjectBase? sameType = frames.FirstOrDefault(f =>
            (type == FrameResourceObjectType.Dummy && f is FrameObjectDummy)
            || (type == FrameResourceObjectType.Point && f is FrameObjectPoint));
        return sameType ?? frames.OfType<FrameObjectCollision>().FirstOrDefault();
    }

    /// <summary>A name no frame in the archive has — the prefab addresses it by FNV64, so a clash is a marker
    /// silently pointing at somebody else's frame.</summary>
    private string UniqueFrameName(string stem)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (object o in Frames!.FrameObjects.Values)
        {
            if (o is FrameObjectBase frame && frame.Name?.ToString() is { } name) taken.Add(name);
        }
        for (int i = 1; ; i++)
        {
            string candidate = $"{stem}{i:00}";
            if (taken.Add(candidate)) return candidate;
        }
    }

    private static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-4f;

    private static float Axis(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
}
