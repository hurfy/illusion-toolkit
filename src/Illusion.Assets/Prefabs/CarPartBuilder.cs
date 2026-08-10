using System.Numerics;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Prefabs;

/// <summary>A part that was added, with everything an undo needs to take it back out.</summary>
/// <param name="Frame">The helper frame minted for it — a Dummy or a Point, hung off the chosen bone.</param>
/// <param name="Change">The prefab row, kept as bytes so the undo puts back exactly what was written.</param>
/// <param name="Donor">The frame whose graph wiring was copied — a part of the same kind when the car has
/// one. The tree needs it: a frame shows up TWICE, under its bone and in its own place in the hierarchy, and
/// the second row belongs next to the donor's.</param>
public sealed record AddedCarPart(
    FrameObjectBase Frame, PrefabEditing.ItemChange Change, CarItemKind Kind, int Bone, string Name,
    FrameObjectBase Donor);

/// <summary>
/// Adds a whole car PART, not half of one.
///
/// <para>
/// A part of a car is two things: a row in the prefab, and a FRAME for that row to name. Until now the
/// toolkit only ever wrote the row, so a part could only be added by pointing it at a frame that already
/// existed — which means every car had exactly as many climb boxes and fuel tanks as it shipped with.
/// This mints the frame too.
/// </para>
/// <para>
/// WHICH frame is not a choice: measured over the 85 shipped car prefabs (<c>--probe-car-items</c>), a seat
/// is a <c>Dummy</c> 213 times out of 213, a climb box a Dummy 280/280, a fuel tank a Dummy 87/87, and an
/// exhaust emitter a <c>Point</c> 195/195. Doors, windows, axles, wipers and the driving wheel are a BONE
/// every single time — 193, 527, 360, 148 and 83 of them — and a bone cannot be minted here, so those are
/// refused with the reason rather than given a Dummy that the game will not drive.
/// </para>
/// </summary>
public static class CarPartBuilder
{
    /// <summary>How big a minted Dummy is. Small enough to be a marker, large enough to see and grab.</summary>
    private static readonly Vector3 DummyHalfSize = new(0.05f, 0.05f, 0.05f);

    /// <summary>What a new climb box is born as — the shipped ones run about 1.6 x 0.8 x 1.8 m across, and a
    /// step nobody can stand on is not a climb box.</summary>
    private static readonly Vector3 ClimbBoxHalfSize = new(0.40f, 0.25f, 0.40f);

    /// <summary>What each kind of part hangs on, and what a new one is called.</summary>
    private static (FrameResourceObjectType Type, string Stem)? Helper(CarItemKind kind) => kind switch
    {
        CarItemKind.Seat => (FrameResourceObjectType.Dummy, "seat"),
        CarItemKind.ClimbBox => (FrameResourceObjectType.Dummy, "climb_box"),
        CarItemKind.FuelTank => (FrameResourceObjectType.Dummy, "fuel_tank"),
        CarItemKind.Exhaust => (FrameResourceObjectType.Point, "exhaust"),
        _ => null,
    };

    /// <summary>Why a kind cannot be minted — the ones that name a bone of the rig every time.</summary>
    private static string? BoneOnly(CarItemKind kind) => kind switch
    {
        CarItemKind.Door => "a door is a BONE on all 193 shipped ones",
        CarItemKind.Window => "a window is a BONE on all 527 shipped ones",
        CarItemKind.AxlePair => "an axle is a BONE on all 360 shipped ones",
        CarItemKind.Wiper => "a wiper is a BONE on all 148 shipped ones",
        CarItemKind.DrivingWheel => "the driving wheel is a BONE on all 83 shipped ones",
        _ => null,
    };

    /// <summary>Whether this kind can be added complete — frame and all — as things stand.</summary>
    public static bool CanAdd(CarItemKind kind) => Helper(kind) != null;

    /// <summary>The kinds that can be added complete, for a menu to offer.</summary>
    public static IReadOnlyList<CarItemKind> AddableKinds =>
        [CarItemKind.ClimbBox, CarItemKind.FuelTank, CarItemKind.Seat, CarItemKind.Exhaust];

    /// <summary>
    /// Mints the helper frame for a new part, hangs it off <paramref name="bone"/> and writes the prefab row
    /// that names it. The frame lands at the bone and is dragged into place afterwards, like every other
    /// placement in the toolkit.
    /// </summary>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is written then.</returns>
    public static AddedCarPart? Add(
        FrameObjectModel model, int bone, CarItemKind kind, string extracted, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(model);
        refusal = null;

        if (BoneOnly(kind) is { } boneOnly)
        {
            refusal = $"{Words(kind)} cannot be minted yet — {boneOnly}, and the toolkit cannot add a bone to "
                + "a rig. Add the bone in Blender first, then point a new part at it.";
            return null;
        }
        if (Helper(kind) is not { } helper)
        {
            refusal = $"{Words(kind)} is not a part this can add on its own";
            return null;
        }

        HashName[] bones;
        try { bones = model.GetSkeletonObject().BoneNames ?? []; }
        catch (Exception) { refusal = "the model's rig cannot be read"; return null; }
        if (bone < 0 || bone >= bones.Length) { refusal = "that bone is not part of this model's rig"; return null; }
        // The joint index rides as a single byte on the attachment.
        if (bone > byte.MaxValue) { refusal = "that bone is past the 255th, which an attachment cannot name"; return null; }

        FrameResource resource = model.Resource;
        string name = UniqueName(resource, helper.Stem);

        // How a helper frame hangs in THIS archive, copied rather than invented: ParentIndex1 cascades the
        // transform and ParentIndex2 anchors it to a scene, and swapping them is what sends an attachment off
        // to the model's origin. The donor is a frame of the SAME kind of part when the car has one — a climb
        // box copied from another climb box lands where climb boxes live, while copying the first Dummy in the
        // archive can land it under a completely different holder.
        FrameObjectBase? donor = Donor(resource, helper.Type, SiblingFrame(extracted, kind));
        if (donor == null)
        {
            refusal = "this archive has no frame to copy the wiring from";
            return null;
        }

        // The factory already files the new frame under its own RefID — adding it again throws.
        FrameObjectBase frame = FrameFactory.ConstructFrameByObjectID(resource, helper.Type);
        frame.Name = new HashName(name);
        frame.LocalTransform = Matrix4x4.Identity;
        frame.SetParent(ParentInfo.ParentType.ParentIndex1, donor.Parent);
        frame.SetParent(ParentInfo.ParentType.ParentIndex2, donor.Root);
        frame.IsOnFrameTable = donor.IsOnFrameTable;
        frame.FrameNameTableFlags = donor.FrameNameTableFlags;
        if (frame is FrameObjectDummy dummy)
        {
            // A climb box starts out big enough to be one. The generic placeholder is 5 cm, which is right
            // for a seat or a tank marker and useless for a step: the shipped climb boxes are around 1.6 x
            // 0.8 x 1.8 m, so a new one arrives at a modest fraction of that rather than as a speck the user
            // has to find before they can scale it.
            Vector3 half = kind == CarItemKind.ClimbBox ? ClimbBoxHalfSize : DummyHalfSize;
            dummy.Bounds = new Formats.Mathematics.BoundingBox(-half, half);
        }
        model.AttachToJoint(frame, (byte)bone);

        // The prefab row last: if it will not take one, the frame goes back out so nothing half-made is left.
        PrefabEditing.ItemChange? change = PrefabEditing.AddItemIn(
            extracted, kind, Fnv64.Hash(name), $"Add {Words(kind)}");
        if (change == null)
        {
            model.DetachFromJoints(frame);
            frame.SetParent(ParentInfo.ParentType.ParentIndex1, null);
            frame.SetParent(ParentInfo.ParentType.ParentIndex2, null);
            resource.FrameObjects.Remove(frame.RefID);
            refusal = "the car's prefab would not take another " + Words(kind);
            return null;
        }

        // A climb box's row states the box and a seat's row states where its occupant sits, and both were just
        // COPIED from the donor — so without this the new one sits exactly on top of an existing box and there
        // is nothing new to climb, however the Dummy is dragged. Through the aggregate, which is the one path
        // from a change to a car's bytes. Only the two kinds whose row says where the part IS: the others
        // carry nothing a frame could contradict, and a stitch of the whole car to find that out is a cost
        // paid for nothing.
        if (kind is CarItemKind.ClimbBox or CarItemKind.Seat)
        {
            Cars.Car.SyncMarkers(extracted, resource, [frame], out _);
            // …and the row a REDO puts back is re-read after that sync rather than before it. The bytes
            // AddItemIn captured are the donor's copy, so replaying them would give the part back with the
            // neighbour's box or the neighbour's seating position and nothing would sync it again.
            change = Resynced(change) ?? change;
        }

        return new AddedCarPart(frame, change, kind, bone, name, donor);
    }

    /// <summary>
    /// The same change, holding the row as it stands NOW — after the sync that rewrote it from the part's own
    /// frame.
    /// </summary>
    /// <returns>Null when the row cannot be read back, in which case the caller keeps the bytes it had: an
    /// undo that puts back a slightly stale row is worse than nothing only if it puts back nothing.</returns>
    private static PrefabEditing.ItemChange? Resynced(PrefabEditing.ItemChange change)
    {
        PrefabFile prefab;
        try { prefab = PrefabFile.Load(change.PrefabPath); }
        catch (Exception ex) when (ex is IOException or Formats.SdsFormatException) { return null; }

        // Taken and put straight back: the only way to read one row's bytes is to serialize it, and taking it
        // is what does that. Nothing is written — the file on disk already holds the synced row.
        byte[]? bytes = prefab.TakeCarItem(change.Kind, change.Index);
        if (bytes == null) return null;
        prefab.PutCarItem(change.Kind, change.Index, bytes);
        return change with { Item = bytes };
    }

    /// <summary>The frame an EXISTING part of this kind names, or 0 — the best donor there is.</summary>
    private static ulong SiblingFrame(string extracted, CarItemKind kind)
    {
        PrefabFile? prefab = PrefabEditing.OpenFirst(extracted);
        if (prefab?.Car is not { } car) return 0;
        return kind switch
        {
            CarItemKind.ClimbBox => car.ClimbBoxes.Count > 0 ? car.ClimbBoxes[^1].Dummy : 0,
            CarItemKind.FuelTank => car.FuelTanks.Count > 0 ? car.FuelTanks[^1] : 0,
            CarItemKind.Seat => car.Seats.Count > 0 ? car.Seats[^1].Frame : 0,
            CarItemKind.Exhaust => car.ExhaustEmitters.Count > 0 ? car.ExhaustEmitters[^1] : 0,
            _ => 0,
        };
    }

    /// <summary>Takes an added part back out — both halves, because half a part is what this exists to avoid.</summary>
    public static void Remove(FrameObjectModel model, AddedCarPart added)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(added);

        PrefabEditing.TakeAway(added.Change);
        model.DetachFromJoints(added.Frame);
        added.Frame.SetParent(ParentInfo.ParentType.ParentIndex1, null);
        added.Frame.SetParent(ParentInfo.ParentType.ParentIndex2, null);
        model.Resource.FrameObjects.Remove(added.Frame.RefID);
    }

    /// <summary>Puts a removed part back — the redo of <see cref="Remove"/>, frame and row together.</summary>
    public static bool Restore(FrameObjectModel model, AddedCarPart added)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(added);

        model.Resource.FrameObjects[added.Frame.RefID] = added.Frame;
        model.AttachToJoint(added.Frame, (byte)added.Bone);
        return PrefabEditing.PutBack(added.Change);
    }

    /// <summary>What a kind is called in a sentence the user reads.</summary>
    public static string Words(CarItemKind kind) => kind switch
    {
        CarItemKind.ClimbBox => "a climb box",
        CarItemKind.FuelTank => "a fuel tank",
        CarItemKind.Seat => "a seat",
        CarItemKind.Exhaust => "an exhaust emitter",
        CarItemKind.Door => "a door",
        CarItemKind.Window => "a window",
        CarItemKind.AxlePair => "an axle pair",
        CarItemKind.Wiper => "a wiper",
        CarItemKind.DrivingWheel => "a driving wheel",
        CarItemKind.CollisionVolume => "a collision volume",
        _ => kind.ToString(),
    };

    /// <summary>
    /// The frame to copy the graph wiring from. First choice is the one an existing part of the same kind
    /// names (<paramref name="sibling"/>, by FNV64 of its name); then any frame of the right type; and last a
    /// collision stub, which every car has and which hangs the same way.
    /// </summary>
    private static FrameObjectBase? Donor(
        FrameResource resource, FrameResourceObjectType type, ulong sibling)
    {
        List<FrameObjectBase> frames = [.. resource.FrameObjects.Values.OfType<FrameObjectBase>()];
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

    /// <summary>A name no frame in the archive has — the prefab addresses it by FNV64, so a clash is a part
    /// silently pointing at someone else's frame.</summary>
    private static string UniqueName(FrameResource resource, string stem)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (FrameObjectBase frame in resource.FrameObjects.Values)
        {
            string? name = frame.Name?.ToString();
            if (name != null) taken.Add(name);
        }
        for (int i = 1; ; i++)
        {
            string candidate = $"{stem}{i:00}";
            if (taken.Add(candidate)) return candidate;
        }
    }
}
