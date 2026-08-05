using Illusion.Formats.IO;

namespace Illusion.Formats.Prefab;

/// <summary>
/// One addressable frame reference inside a car's assembly. Named rather than expressed as a path into the
/// wire model, so the edit surface stays a short, closed list — every one of these is a slot the game reads,
/// and nothing else in the entry can be reached from outside.
/// </summary>
public enum CarFrameSlot
{
    RootFrame,
    ScaleBone,
    Body,
    RestBone,
    Headlight,
    Backlight,
    Toplight,
    SnowRest,
    DrivingWheel,
    FuelTank,
    Exhaust,
    Wiper,
    SeatFrame,
    SeatDoor,
    DoorFrame,
    WindowFrame,
    AxleFrame,
    AxleBrakeDrum,
    ClimbBone,
    ClimbDummy,
    MotorVentilator,
    LocalWindEmitter,
    BusSeat,
    EnterBus,
    AxleRotWing,
    WindowCheckBone,
    DcbDoor,
    DeformPartParent,
    DrWheelSnapWheel,
    DrWheelSnapLeft,
    DrWheelSnapRight,
}

/// <summary>
/// A number or a flag inside a car's assembly — everything that is not a frame reference. Addressed the same
/// way a slot is: named here, indexed into its list, and for a position, one axis at a time.
/// </summary>
public enum CarValueSlot
{
    WindowDepth,
    WindowOpenable,
    SeatType,
    SeatPosition,
    DoorHandle,
    DoorLock,
    AxleType,
    AxleBrakeDrumRadius,
    AxleBrakeDrumMass,
    AxleMass,
    ClimbBoxMin,
    ClimbBoxMax,

    SeatFlags,
    SeatGroup,
    SeatTargetAim,
    SeatTargetSeat,
    SeatLock,
    SeatDirection,

    WheelDeformAngleMax,
    WheelDeformEnergyMax,
    WheelArmLength,
    WheelPosOnBrakeDrum,
    BrakeDrumWidth,
    BrakeDrumInertia,

    BoneRange,
    ReduceBboxZ,

    DcbResistance,
    DcbHitpoints,

    /// <summary>The damage model: one deformable part of the body, and how much it takes to move it.</summary>
    DeformPartType,
    DeformPartFlags,
    DeformCentreOfMass,
    DeformSpeedMin,
    DeformSpeedMax,
    DeformResistance,
    DeformMass,
    DeformEnergyStart,
    DeformEnergyDrop,

    /// <summary>Where a collision volume sits, in the space of the bone its part is. Indexed FLAT across the
    /// whole car — a volume belongs to a part, but the panel addresses everything by one number.</summary>
    CollisionVolumePosition,

    /// <summary>The full size of a volume that describes itself. Meaningless for one that names a shape:
    /// the shape is the size, and the shipped ones all carry a throwaway centimetre here.</summary>
    CollisionVolumeSize,
}

/// <summary>
/// A kind of part a car's assembly can gain or lose. Coarser than <see cref="CarFrameSlot"/> on purpose:
/// a door is one thing with three fields, and adding "a door frame" without the handle and lock beside it
/// would be adding half a door.
///
/// <para>
/// A DEFORMABLE PART is deliberately not on this list, and adding it is not a small job. A part is addressed
/// by its POSITION in the car's part list, and five separate tables hold those positions: the part's own two
/// index lists (measured — every value of both lands inside the part list, 764 of 764 and 334 of 334, and no
/// other list they could be indexing takes all of them), <c>DrainEnergy.DrainPart</c>,
/// <c>DropParts.DropPart</c>, and <c>PartBreakEnergy.PartId</c>. Inserting or dropping a part renumbers
/// everything after it, and every one of those references then names the wrong panel — with no error, in a
/// system whose only symptom is that damage behaves oddly. Whoever adds it renumbers all five.
/// </para>
/// </summary>
public enum CarItemKind
{
    Seat,
    Door,
    Window,

    /// <summary>Two axles. The file stores a PAIR count and the reader doubles it, so they are only ever
    /// added and dropped two at a time.</summary>
    AxlePair,

    ClimbBox,
    DrivingWheel,
    FuelTank,
    Exhaust,
    Wiper,

    /// <summary>One collision volume of a deformable part — what a car is actually shot at.</summary>
    CollisionVolume,
}

/// <summary>
/// A prefab container (.prf / PrefabLoader): a size-header wrapped around a list of prefab entries,
/// each a hash, a type, an unknown int, a size and that many bytes of bit-packed InitData. Ported
/// from MafiaToolkit; the container is typed and the per-type InitData (~12 vehicle/door/wagon/…
/// variants) is preserved raw (deferred), so the file round-trips byte-exact — including the
/// type 0/1/11 variants MafiaToolkit cannot parse.
/// </summary>
public sealed partial class PrefabFile
{
    /// <summary>The typed wire model. Internal until the per-type InitData is typed.</summary>
    internal Native.Model.PrefabFileW Wire { get; set; } = new();

    /// <summary>Number of prefab entries in the container.</summary>
    public int PrefabCount => Wire.Prefabs.Count;

    /// <summary>The name hashes the container is keyed by — how an entity finds its init data.</summary>
    public IReadOnlyList<ulong> Hashes => [.. Wire.Prefabs.Select(p => p.Hash)];

    /// <summary>Each entry's type id and the size of its init-data blob, in file order. The blob itself stays
    /// inside the core; this is what a caller can ask about it while it is opaque.</summary>
    public IReadOnlyList<(int Type, int Size)> Entries => [.. Wire.Prefabs.Select(p => (p.PrefabType, p.Data.Length))];

    /// <summary>
    /// Which entries the core decoded rather than carrying opaquely, by type id (0 = still opaque). Typing is
    /// going variant by variant, so this is how much of a container is actually understood — and the number a
    /// probe asserts against so a regression shows up as coverage falling, not as silence.
    /// </summary>
    public IReadOnlyList<int> DecodedKinds => [.. Wire.Prefabs.Select(p => p.TypedKind)];

    /// <summary>
    /// The assembly of the car this container describes, or null when it holds none. Everything in it names a
    /// FRAME of the car's own model by FNV64 hash — a bone, a dummy, a point — which is what makes it the
    /// description of how the thing is put together rather than a table of numbers.
    /// </summary>
    public CarPrefab? Car
    {
        get
        {
            Native.Model.PrefabEntryW? entry = Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0);
            return entry == null ? null : new CarPrefab(entry.CarInit[0]);
        }
    }

    /// <summary>
    /// Points one of the car's slots at a different frame, by the FNV64 hash of that frame's name.
    ///
    /// <para>
    /// This is the ONLY way the toolkit changes a prefab, and it is deliberately narrow: a slot named by an
    /// enum, an index inside it, and a hash. The container is written from the TYPED model for the variants
    /// the core decodes, so the new hash reaches the file; nothing else in the entry is touched, and the
    /// bit-packed tail rides along untouched with it.
    /// </para>
    /// </summary>
    /// <returns>False when this file has no car entry, or the index is past the end of that slot's list —
    /// never a silent no-op.</returns>
    public bool SetCarFrame(CarFrameSlot slot, int index, ulong hash)
    {
        Native.Model.PrefabEntryW? entry = Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0);
        if (entry == null) return false;
        Native.Model.PrefabCarInitW car = entry.CarInit[0];

        switch (slot)
        {
            case CarFrameSlot.RootFrame when car.Deformation.Count > 0:
                car.Deformation[0].RootFrameName = hash; return true;
            case CarFrameSlot.ScaleBone when car.Deformation.Count > 0:
                car.Deformation[0].ScaleBoneFrameName = hash; return true;
            case CarFrameSlot.Body when car.Other.Count > 0:
                car.Other[0].VehicleBodyName = hash; return true;
            case CarFrameSlot.RestBone when car.Other.Count > 0:
                car.Other[0].RestBoneName = hash; return true;
            case CarFrameSlot.Headlight when car.Other.Count > 0:
                car.Other[0].HeadlightModelName = hash; return true;
            case CarFrameSlot.Backlight when car.Other.Count > 0:
                car.Other[0].BacklightModelName = hash; return true;
            case CarFrameSlot.Toplight when car.Other.Count > 0:
                car.Other[0].ToplightModelName = hash; return true;
            case CarFrameSlot.SnowRest when car.Other.Count > 0:
                car.Other[0].SnowRestName = hash; return true;

            case CarFrameSlot.DrivingWheel when car.Other.Count > 0:
                return Put(car.Other[0].DrivingWheels, index, hash);
            case CarFrameSlot.FuelTank when car.Other.Count > 0:
                return Put(car.Other[0].FuelTanks, index, hash);
            case CarFrameSlot.Exhaust when car.Other.Count > 0:
                return Put(car.Other[0].ExhaustEmitters, index, hash);
            case CarFrameSlot.Wiper:
                return Put(car.WipersFrameName, index, hash);

            case CarFrameSlot.SeatFrame when index >= 0 && index < car.Seats.Count:
                car.Seats[index].FrameName = hash; return true;
            case CarFrameSlot.SeatDoor when index >= 0 && index < car.Seats.Count:
                car.Seats[index].DoorIndexFrameName = hash; return true;
            case CarFrameSlot.DoorFrame when index >= 0 && index < car.DoorPoints.Count:
                car.DoorPoints[index].DoorFrameName = hash; return true;
            case CarFrameSlot.WindowFrame when car.Other.Count > 0
                                               && index >= 0 && index < car.Other[0].WindowData.Count:
                car.Other[0].WindowData[index].WindowFrameName = hash; return true;
            case CarFrameSlot.AxleFrame when index >= 0 && index < car.Axles.Count:
                car.Axles[index].AxleName = hash; return true;
            case CarFrameSlot.AxleBrakeDrum when index >= 0 && index < car.Axles.Count:
                car.Axles[index].BrakeDrumName = hash; return true;
            case CarFrameSlot.ClimbBone when index >= 0 && index < car.ClimbBoxes.Count:
                car.ClimbBoxes[index].BoneFrameName = hash; return true;
            case CarFrameSlot.ClimbDummy when index >= 0 && index < car.ClimbBoxes.Count:
                car.ClimbBoxes[index].DummyFrameName = hash; return true;

            case CarFrameSlot.MotorVentilator when car.Other.Count > 0:
                car.Other[0].MotorVentilatorName = hash; return true;
            case CarFrameSlot.LocalWindEmitter when car.Other.Count > 0:
                return Put(car.Other[0].LocalWindEmitters, index, hash);
            case CarFrameSlot.BusSeat:
                return Put(car.BusSeatsFrameName, index, hash);
            case CarFrameSlot.EnterBus:
                return Put(car.EnterBusFrameName, index, hash);
            case CarFrameSlot.AxleRotWing when index >= 0 && index < car.Axles.Count:
                car.Axles[index].RotWingName = hash; return true;
            case CarFrameSlot.WindowCheckBone when car.Other.Count > 0
                                                   && index >= 0 && index < car.Other[0].WindowData.Count:
                return Put(car.Other[0].WindowData[index].CheckBoneFrameName, 0, hash);
            case CarFrameSlot.DcbDoor when car.Other.Count > 0
                                           && index >= 0 && index < car.Other[0].DcbData.Count:
                car.Other[0].DcbData[index].DoorFrameName = hash; return true;
            case CarFrameSlot.DeformPartParent when Deform(car) is { } d && index >= 0 && index < d.Count:
                d[index].ParentDeformPartName = hash; return true;
            case CarFrameSlot.DrWheelSnapWheel when index >= 0 && index < car.DrWheelSnap.Count:
                car.DrWheelSnap[index].DrWheelFrameName = hash; return true;
            case CarFrameSlot.DrWheelSnapLeft when index >= 0 && index < car.DrWheelSnap.Count:
                car.DrWheelSnap[index].LeftSnapFrameName = hash; return true;
            case CarFrameSlot.DrWheelSnapRight when index >= 0 && index < car.DrWheelSnap.Count:
                car.DrWheelSnap[index].RightSnapFrameName = hash; return true;

            default:
                return false;
        }

        static bool Put(List<ulong> list, int index, ulong hash)
        {
            if (index < 0 || index >= list.Count) return false;
            list[index] = hash;
            return true;
        }
    }

    /// <summary>
    /// Reads the frame a slot names. The mirror of <see cref="SetCarFrame"/>, so a row and the write behind
    /// it can never end up addressing two different fields.
    /// </summary>
    /// <returns>0 when the slot or the index is not there — which is also what an empty slot reads as.</returns>
    public ulong GetCarFrame(CarFrameSlot slot, int index)
    {
        if (Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return 0;
        Native.Model.PrefabCarInitW car = entry.CarInit[0];
        Native.Model.PrefabOtherInitW? other = car.Other.Count > 0 ? car.Other[0] : null;
        Native.Model.PrefabDeformationInitW? deform =
            car.Deformation.Count > 0 ? car.Deformation[0] : null;

        return slot switch
        {
            CarFrameSlot.RootFrame => deform?.RootFrameName ?? 0,
            CarFrameSlot.ScaleBone => deform?.ScaleBoneFrameName ?? 0,
            CarFrameSlot.Body => other?.VehicleBodyName ?? 0,
            CarFrameSlot.RestBone => other?.RestBoneName ?? 0,
            CarFrameSlot.Headlight => other?.HeadlightModelName ?? 0,
            CarFrameSlot.Backlight => other?.BacklightModelName ?? 0,
            CarFrameSlot.Toplight => other?.ToplightModelName ?? 0,
            CarFrameSlot.SnowRest => other?.SnowRestName ?? 0,
            CarFrameSlot.MotorVentilator => other?.MotorVentilatorName ?? 0,
            CarFrameSlot.DrivingWheel => Get(other?.DrivingWheels, index),
            CarFrameSlot.FuelTank => Get(other?.FuelTanks, index),
            CarFrameSlot.Exhaust => Get(other?.ExhaustEmitters, index),
            CarFrameSlot.LocalWindEmitter => Get(other?.LocalWindEmitters, index),
            CarFrameSlot.Wiper => Get(car.WipersFrameName, index),
            CarFrameSlot.BusSeat => Get(car.BusSeatsFrameName, index),
            CarFrameSlot.EnterBus => Get(car.EnterBusFrameName, index),
            CarFrameSlot.SeatFrame when In(index, car.Seats.Count) => car.Seats[index].FrameName,
            CarFrameSlot.SeatDoor when In(index, car.Seats.Count) => car.Seats[index].DoorIndexFrameName,
            CarFrameSlot.DoorFrame when In(index, car.DoorPoints.Count) => car.DoorPoints[index].DoorFrameName,
            CarFrameSlot.WindowFrame when other != null && In(index, other.WindowData.Count)
                => other.WindowData[index].WindowFrameName,
            CarFrameSlot.WindowCheckBone when other != null && In(index, other.WindowData.Count)
                => Get(other.WindowData[index].CheckBoneFrameName, 0),
            CarFrameSlot.AxleFrame when In(index, car.Axles.Count) => car.Axles[index].AxleName,
            CarFrameSlot.AxleBrakeDrum when In(index, car.Axles.Count) => car.Axles[index].BrakeDrumName,
            CarFrameSlot.AxleRotWing when In(index, car.Axles.Count) => car.Axles[index].RotWingName,
            CarFrameSlot.ClimbBone when In(index, car.ClimbBoxes.Count) => car.ClimbBoxes[index].BoneFrameName,
            CarFrameSlot.ClimbDummy when In(index, car.ClimbBoxes.Count) => car.ClimbBoxes[index].DummyFrameName,
            CarFrameSlot.DcbDoor when other != null && In(index, other.DcbData.Count)
                => other.DcbData[index].DoorFrameName,
            CarFrameSlot.DeformPartParent when Deform(car) is { } d && In(index, d.Count)
                => d[index].ParentDeformPartName,
            CarFrameSlot.DrWheelSnapWheel when In(index, car.DrWheelSnap.Count)
                => car.DrWheelSnap[index].DrWheelFrameName,
            CarFrameSlot.DrWheelSnapLeft when In(index, car.DrWheelSnap.Count)
                => car.DrWheelSnap[index].LeftSnapFrameName,
            CarFrameSlot.DrWheelSnapRight when In(index, car.DrWheelSnap.Count)
                => car.DrWheelSnap[index].RightSnapFrameName,
            _ => 0,
        };

        static ulong Get(List<ulong>? list, int index) =>
            list != null && In(index, list.Count) ? list[index] : 0;
    }

    /// <summary>How many the slot's list holds — 1 for the slots that are a single field.</summary>
    public int CarSlotCount(CarFrameSlot slot)
    {
        if (Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return 0;
        Native.Model.PrefabCarInitW car = entry.CarInit[0];
        Native.Model.PrefabOtherInitW? other = car.Other.Count > 0 ? car.Other[0] : null;

        return slot switch
        {
            CarFrameSlot.LocalWindEmitter => other?.LocalWindEmitters.Count ?? 0,
            CarFrameSlot.BusSeat => car.BusSeatsFrameName.Count,
            CarFrameSlot.EnterBus => car.EnterBusFrameName.Count,
            CarFrameSlot.DcbDoor => other?.DcbData.Count ?? 0,
            CarFrameSlot.DeformPartParent => Deform(car)?.Count ?? 0,
            CarFrameSlot.DrWheelSnapWheel => car.DrWheelSnap.Count,
            // A window's check bone is optional and most cars ship none. Reporting one anyway would offer a
            // row that cannot be written, since there is no record behind it to write into.
            CarFrameSlot.WindowCheckBone =>
                other is { WindowData.Count: > 0 } && other.WindowData[0].CheckBoneFrameName.Count > 0 ? 1 : 0,
            _ => 1,
        };
    }

    /// <summary>
    /// Reads one number or flag out of the assembly. <paramref name="axis"/> picks a component of a position
    /// (0 = X) and is ignored by the scalars.
    /// </summary>
    /// <returns>NaN when the slot or the index is not there — a value that cannot be mistaken for a reading.</returns>
    public float GetCarValue(CarValueSlot slot, int index, int axis = 0)
    {
        if (slot is CarValueSlot.CollisionVolumePosition or CarValueSlot.CollisionVolumeSize)
        {
            return GetVolumeValue(slot, index, axis);
        }
        if (Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return float.NaN;
        Native.Model.PrefabCarInitW car = entry.CarInit[0];
        Native.Model.PrefabOtherInitW? other = car.Other.Count > 0 ? car.Other[0] : null;

        return slot switch
        {
            CarValueSlot.WindowDepth when other != null && In(index, other.WindowData.Count)
                => other.WindowData[index].Depth,
            CarValueSlot.WindowOpenable when other != null && In(index, other.WindowData.Count)
                => other.WindowData[index].IsOpenable,
            CarValueSlot.SeatType when In(index, car.Seats.Count) => car.Seats[index].SeatType,
            CarValueSlot.SeatPosition when In(index, car.Seats.Count) => Axis(car.Seats[index].Position, axis),
            CarValueSlot.DoorHandle when In(index, car.DoorPoints.Count)
                => Axis(car.DoorPoints[index].HandlePos, axis),
            CarValueSlot.DoorLock when In(index, car.DoorPoints.Count)
                => Axis(car.DoorPoints[index].LockPos, axis),
            CarValueSlot.AxleType when In(index, car.Axles.Count) => car.Axles[index].AxleType,
            CarValueSlot.AxleBrakeDrumRadius when In(index, car.Axles.Count)
                => car.Axles[index].Wheel.BrakeDrumRadius,
            CarValueSlot.AxleBrakeDrumMass when In(index, car.Axles.Count)
                => car.Axles[index].Wheel.BrakeDrumMass,
            CarValueSlot.AxleMass when In(index, car.Axles.Count) => car.Axles[index].Wheel.AxleMass,
            CarValueSlot.ClimbBoxMin when In(index, car.ClimbBoxes.Count)
                => Axis(car.ClimbBoxes[index].BoxMin, axis),
            CarValueSlot.ClimbBoxMax when In(index, car.ClimbBoxes.Count)
                => Axis(car.ClimbBoxes[index].BoxMax, axis),

            CarValueSlot.SeatFlags when In(index, car.Seats.Count) => car.Seats[index].Flags,
            CarValueSlot.SeatGroup when In(index, car.Seats.Count) => car.Seats[index].SeatGroup,
            CarValueSlot.SeatTargetAim when In(index, car.Seats.Count)
                => Axis(car.Seats[index].TargetAim, axis),
            CarValueSlot.SeatTargetSeat when In(index, car.Seats.Count)
                => Axis(car.Seats[index].TargetSeat, axis),
            CarValueSlot.SeatLock when In(index, car.Seats.Count) => Axis(car.Seats[index].LockPos, axis),
            CarValueSlot.SeatDirection when In(index, car.Seats.Count)
                => Axis(car.Seats[index].Direction, axis),

            CarValueSlot.WheelDeformAngleMax when In(index, car.Axles.Count)
                => car.Axles[index].Wheel.DeformAngleMax,
            CarValueSlot.WheelDeformEnergyMax when In(index, car.Axles.Count)
                => car.Axles[index].Wheel.DeformEnergyMax,
            CarValueSlot.WheelArmLength when In(index, car.Axles.Count) => car.Axles[index].Wheel.ArmLength,
            CarValueSlot.WheelPosOnBrakeDrum when In(index, car.Axles.Count)
                => Axis(car.Axles[index].Wheel.WheelPosOnBrakeDrum, axis),
            CarValueSlot.BrakeDrumWidth when In(index, car.Axles.Count)
                => car.Axles[index].Wheel.BrakeDrumWidth,
            CarValueSlot.BrakeDrumInertia when In(index, car.Axles.Count)
                => car.Axles[index].Wheel.BrakeDrumInertia,

            CarValueSlot.BoneRange when other != null => Axis(other.BoneRange, axis),
            CarValueSlot.ReduceBboxZ when other != null => other.ReduceBboxZ,

            CarValueSlot.DcbResistance when other != null && In(index, other.DcbData.Count)
                => other.DcbData[index].Resistance,
            CarValueSlot.DcbHitpoints when other != null && In(index, other.DcbData.Count)
                => other.DcbData[index].Hitpoints,

            CarValueSlot.DeformPartType when Deform(car) is { } d && In(index, d.Count) => d[index].PartType,
            CarValueSlot.DeformPartFlags when Deform(car) is { } d && In(index, d.Count) => d[index].Flags,
            CarValueSlot.DeformCentreOfMass when Deform(car) is { } d && In(index, d.Count)
                => Axis(d[index].CentreOfMass, axis),
            CarValueSlot.DeformSpeedMin when Tuning(car, index) is { } t => t.SpeedMin,
            CarValueSlot.DeformSpeedMax when Tuning(car, index) is { } t => t.SpeedMax,
            CarValueSlot.DeformResistance when Tuning(car, index) is { } t => t.Resistance,
            CarValueSlot.DeformMass when Tuning(car, index) is { } t => t.Mass,
            CarValueSlot.DeformEnergyStart when Tuning(car, index) is { } t => t.EnergyStart,
            CarValueSlot.DeformEnergyDrop when Tuning(car, index) is { } t => t.EnergyDrop,

            _ => float.NaN,
        };
    }

    /// <summary>Writes one number or flag. Same addressing as <see cref="GetCarValue"/>.</summary>
    public bool SetCarValue(CarValueSlot slot, int index, int axis, float value)
    {
        if (slot is CarValueSlot.CollisionVolumePosition or CarValueSlot.CollisionVolumeSize)
        {
            return SetVolumeValue(slot, index, axis, value);
        }
        if (Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return false;
        Native.Model.PrefabCarInitW car = entry.CarInit[0];
        Native.Model.PrefabOtherInitW? other = car.Other.Count > 0 ? car.Other[0] : null;

        switch (slot)
        {
            case CarValueSlot.WindowDepth when other != null && In(index, other.WindowData.Count):
                other.WindowData[index].Depth = value; return true;
            case CarValueSlot.WindowOpenable when other != null && In(index, other.WindowData.Count):
                other.WindowData[index].IsOpenable = (byte)(value != 0 ? 1 : 0); return true;
            case CarValueSlot.SeatType when In(index, car.Seats.Count):
                car.Seats[index].SeatType = (uint)Math.Max(0, value); return true;
            case CarValueSlot.SeatPosition when In(index, car.Seats.Count):
                car.Seats[index].Position = With(car.Seats[index].Position, axis, value); return true;
            case CarValueSlot.DoorHandle when In(index, car.DoorPoints.Count):
                car.DoorPoints[index].HandlePos = With(car.DoorPoints[index].HandlePos, axis, value); return true;
            case CarValueSlot.DoorLock when In(index, car.DoorPoints.Count):
                car.DoorPoints[index].LockPos = With(car.DoorPoints[index].LockPos, axis, value); return true;
            case CarValueSlot.AxleType when In(index, car.Axles.Count):
                car.Axles[index].AxleType = (uint)Math.Max(0, value); return true;
            case CarValueSlot.AxleBrakeDrumRadius when In(index, car.Axles.Count):
                car.Axles[index].Wheel.BrakeDrumRadius = value; return true;
            case CarValueSlot.AxleBrakeDrumMass when In(index, car.Axles.Count):
                car.Axles[index].Wheel.BrakeDrumMass = value; return true;
            case CarValueSlot.AxleMass when In(index, car.Axles.Count):
                car.Axles[index].Wheel.AxleMass = value; return true;
            case CarValueSlot.ClimbBoxMin when In(index, car.ClimbBoxes.Count):
                car.ClimbBoxes[index].BoxMin = With(car.ClimbBoxes[index].BoxMin, axis, value); return true;
            case CarValueSlot.ClimbBoxMax when In(index, car.ClimbBoxes.Count):
                car.ClimbBoxes[index].BoxMax = With(car.ClimbBoxes[index].BoxMax, axis, value); return true;

            case CarValueSlot.SeatFlags when In(index, car.Seats.Count):
                car.Seats[index].Flags = (uint)Math.Max(0, value); return true;
            case CarValueSlot.SeatGroup when In(index, car.Seats.Count):
                car.Seats[index].SeatGroup = (uint)Math.Max(0, value); return true;
            case CarValueSlot.SeatTargetAim when In(index, car.Seats.Count):
                car.Seats[index].TargetAim = With(car.Seats[index].TargetAim, axis, value); return true;
            case CarValueSlot.SeatTargetSeat when In(index, car.Seats.Count):
                car.Seats[index].TargetSeat = With(car.Seats[index].TargetSeat, axis, value); return true;
            case CarValueSlot.SeatLock when In(index, car.Seats.Count):
                car.Seats[index].LockPos = With(car.Seats[index].LockPos, axis, value); return true;
            case CarValueSlot.SeatDirection when In(index, car.Seats.Count):
                car.Seats[index].Direction = With(car.Seats[index].Direction, axis, value); return true;

            case CarValueSlot.WheelDeformAngleMax when In(index, car.Axles.Count):
                car.Axles[index].Wheel.DeformAngleMax = value; return true;
            case CarValueSlot.WheelDeformEnergyMax when In(index, car.Axles.Count):
                car.Axles[index].Wheel.DeformEnergyMax = value; return true;
            case CarValueSlot.WheelArmLength when In(index, car.Axles.Count):
                car.Axles[index].Wheel.ArmLength = value; return true;
            case CarValueSlot.WheelPosOnBrakeDrum when In(index, car.Axles.Count):
                car.Axles[index].Wheel.WheelPosOnBrakeDrum =
                    With(car.Axles[index].Wheel.WheelPosOnBrakeDrum, axis, value); return true;
            case CarValueSlot.BrakeDrumWidth when In(index, car.Axles.Count):
                car.Axles[index].Wheel.BrakeDrumWidth = value; return true;
            case CarValueSlot.BrakeDrumInertia when In(index, car.Axles.Count):
                car.Axles[index].Wheel.BrakeDrumInertia = value; return true;

            case CarValueSlot.BoneRange when other != null:
                other.BoneRange = With(other.BoneRange, axis, value); return true;
            case CarValueSlot.ReduceBboxZ when other != null:
                other.ReduceBboxZ = value; return true;

            case CarValueSlot.DcbResistance when other != null && In(index, other.DcbData.Count):
                other.DcbData[index].Resistance = value; return true;
            case CarValueSlot.DcbHitpoints when other != null && In(index, other.DcbData.Count):
                other.DcbData[index].Hitpoints = value; return true;

            case CarValueSlot.DeformPartType when Deform(car) is { } d && In(index, d.Count):
                d[index].PartType = (uint)Math.Max(0, value); return true;
            case CarValueSlot.DeformPartFlags when Deform(car) is { } d && In(index, d.Count):
                d[index].Flags = (uint)Math.Max(0, value); return true;
            case CarValueSlot.DeformCentreOfMass when Deform(car) is { } d && In(index, d.Count):
                d[index].CentreOfMass = With(d[index].CentreOfMass, axis, value); return true;
            case CarValueSlot.DeformSpeedMin when Tuning(car, index) is { } t: t.SpeedMin = value; return true;
            case CarValueSlot.DeformSpeedMax when Tuning(car, index) is { } t: t.SpeedMax = value; return true;
            case CarValueSlot.DeformResistance when Tuning(car, index) is { } t:
                t.Resistance = value; return true;
            case CarValueSlot.DeformMass when Tuning(car, index) is { } t: t.Mass = value; return true;
            case CarValueSlot.DeformEnergyStart when Tuning(car, index) is { } t:
                t.EnergyStart = value; return true;
            case CarValueSlot.DeformEnergyDrop when Tuning(car, index) is { } t:
                t.EnergyDrop = value; return true;

            default:
                return false;
        }
    }

    private static bool In(int index, int count) => index >= 0 && index < count;

    /// <summary>The damage model's parts, or null when this car carries none.</summary>
    private static List<Native.Model.PrefabDeformPartW>? Deform(Native.Model.PrefabCarInitW car) =>
        car.Deformation.Count > 0 ? car.Deformation[0].DeformParts : null;

    /// <summary>The tuning block a deform part carries — the numbers that say how hard it is to move.</summary>
    private static Native.Model.PrefabDeformPartCommonW? Tuning(
        Native.Model.PrefabCarInitW car, int index) =>
        Deform(car) is { } parts && In(index, parts.Count) && parts[index].Common.Count > 0
            ? parts[index].Common[0]
            : null;

    private static float Axis(System.Numerics.Vector3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };

    private static System.Numerics.Vector3 With(System.Numerics.Vector3 v, int axis, float value) => axis switch
    {
        0 => v with { X = value },
        1 => v with { Y = value },
        _ => v with { Z = value },
    };

    /// <summary>How many of a kind the car has right now.</summary>
    public int CarItemCount(CarItemKind kind)
    {
        if (Car == null || Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return 0;
        Native.Model.PrefabCarInitW car = entry.CarInit[0];
        return kind switch
        {
            CarItemKind.Seat => car.Seats.Count,
            CarItemKind.Door => car.DoorPoints.Count,
            CarItemKind.Window => car.Other.Count > 0 ? car.Other[0].WindowData.Count : 0,
            CarItemKind.AxlePair => car.Axles.Count / 2,
            CarItemKind.ClimbBox => car.ClimbBoxes.Count,
            CarItemKind.DrivingWheel => car.Other.Count > 0 ? car.Other[0].DrivingWheels.Count : 0,
            CarItemKind.FuelTank => car.Other.Count > 0 ? car.Other[0].FuelTanks.Count : 0,
            CarItemKind.Exhaust => car.Other.Count > 0 ? car.Other[0].ExhaustEmitters.Count : 0,
            CarItemKind.Wiper => car.WipersFrameName.Count,
            CarItemKind.CollisionVolume => CarVolumeCount(),
            _ => 0,
        };
    }

    /// <summary>
    /// Appends one more of a kind, pointed at <paramref name="frameHash"/>.
    ///
    /// <para>
    /// The new item is a COPY of the last one there, with its frame swapped. A seat and a door are more than
    /// a frame name — a seat carries where the occupant sits, a door where its handle and lock are, an axle
    /// its masses — and inventing those numbers would produce a part that exists and behaves like nothing.
    /// Copying the neighbour gives values that are already right for this car, and leaves the user one thing
    /// to move rather than seven to guess. With nothing to copy the item starts zeroed.
    /// </para>
    /// <para>
    /// AXLES COME IN PAIRS. The file stores a pair count and the reader multiplies it by two, so adding one
    /// axle would desync everything after it — one call here adds both halves and keeps the count honest.
    /// </para>
    /// </summary>
    public bool AddCarItem(CarItemKind kind, ulong frameHash)
    {
        if (Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return false;
        Native.Model.PrefabCarInitW car = entry.CarInit[0];
        Native.Model.PrefabOtherInitW? other = car.Other.Count > 0 ? car.Other[0] : null;

        switch (kind)
        {
            case CarItemKind.Seat:
                var seat = CloneLast(car.Seats, (s, w) => s.WriteTo(w), Native.Model.PrefabSeatW.ReadFrom);
                seat.FrameName = frameHash;
                seat.SeatIndex = (uint)car.Seats.Count;
                car.Seats.Add(seat);
                return true;

            case CarItemKind.Door:
                var door = CloneLast(car.DoorPoints, (d, w) => d.WriteTo(w), Native.Model.PrefabDoorPointsW.ReadFrom);
                door.DoorFrameName = frameHash;
                car.DoorPoints.Add(door);
                return true;

            case CarItemKind.Window when other != null:
                var window = CloneLast(
                    other.WindowData, (x, w) => x.WriteTo(w), Native.Model.PrefabWindowDataW.ReadFrom);
                window.WindowFrameName = frameHash;
                other.WindowData.Add(window);
                return true;

            case CarItemKind.AxlePair:
                var left = CloneLast(car.Axles, (a, w) => a.WriteTo(w), Native.Model.PrefabAxleW.ReadFrom);
                var right = CloneLast(car.Axles, (a, w) => a.WriteTo(w), Native.Model.PrefabAxleW.ReadFrom);
                left.AxleName = frameHash;
                car.Axles.Add(left);
                car.Axles.Add(right);
                car.AxlePairs = (uint)(car.Axles.Count / 2);
                return true;

            case CarItemKind.ClimbBox:
                var box = CloneLast(car.ClimbBoxes, (b, w) => b.WriteTo(w), Native.Model.PrefabClimbBoxW.ReadFrom);
                box.DummyFrameName = frameHash;
                car.ClimbBoxes.Add(box);
                return true;

            case CarItemKind.DrivingWheel when other != null:
                other.DrivingWheels.Add(frameHash);
                return true;
            case CarItemKind.FuelTank when other != null:
                other.FuelTanks.Add(frameHash);
                return true;
            case CarItemKind.Exhaust when other != null:
                other.ExhaustEmitters.Add(frameHash);
                return true;
            case CarItemKind.Wiper:
                car.WipersFrameName.Add(frameHash);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Drops one and hands back its bytes, so an undo can put back exactly what was there rather than a
    /// look-alike. An axle pair takes both of its halves with it, and the stored pair count with them.
    /// </summary>
    /// <returns>The removed item, serialized, or null when there is nothing at that index.</returns>
    public byte[]? TakeCarItem(CarItemKind kind, int index)
    {
        if (index < 0) return null;
        if (kind == CarItemKind.CollisionVolume) return TakeCarVolumeFlat(index);
        if (Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return null;
        Native.Model.PrefabCarInitW car = entry.CarInit[0];
        Native.Model.PrefabOtherInitW? other = car.Other.Count > 0 ? car.Other[0] : null;

        switch (kind)
        {
            case CarItemKind.Seat when index < car.Seats.Count:
                byte[] seat = Pack(w => car.Seats[index].WriteTo(w));
                car.Seats.RemoveAt(index);
                for (int i = 0; i < car.Seats.Count; i++) car.Seats[i].SeatIndex = (uint)i;
                return seat;
            case CarItemKind.Door when index < car.DoorPoints.Count:
                byte[] door = Pack(w => car.DoorPoints[index].WriteTo(w));
                car.DoorPoints.RemoveAt(index);
                return door;
            case CarItemKind.Window when other != null && index < other.WindowData.Count:
                byte[] window = Pack(w => other.WindowData[index].WriteTo(w));
                other.WindowData.RemoveAt(index);
                return window;
            case CarItemKind.AxlePair when index * 2 + 1 < car.Axles.Count:
                byte[] pair = Pack(w =>
                {
                    car.Axles[index * 2].WriteTo(w);
                    car.Axles[(index * 2) + 1].WriteTo(w);
                });
                car.Axles.RemoveRange(index * 2, 2);
                car.AxlePairs = (uint)(car.Axles.Count / 2);
                return pair;
            case CarItemKind.ClimbBox when index < car.ClimbBoxes.Count:
                byte[] box = Pack(w => car.ClimbBoxes[index].WriteTo(w));
                car.ClimbBoxes.RemoveAt(index);
                return box;
            case CarItemKind.DrivingWheel when other != null && index < other.DrivingWheels.Count:
                return TakeHash(other.DrivingWheels, index);
            case CarItemKind.FuelTank when other != null && index < other.FuelTanks.Count:
                return TakeHash(other.FuelTanks, index);
            case CarItemKind.Exhaust when other != null && index < other.ExhaustEmitters.Count:
                return TakeHash(other.ExhaustEmitters, index);
            case CarItemKind.Wiper when index < car.WipersFrameName.Count:
                return TakeHash(car.WipersFrameName, index);
            default:
                return null;
        }
    }

    /// <summary>Puts a taken item back where it was — the undo of <see cref="TakeCarItem"/>.</summary>
    public bool PutCarItem(CarItemKind kind, int index, byte[] item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (index < 0) return false;
        if (kind == CarItemKind.CollisionVolume) return PutCarVolumeFlat(index, item);
        if (Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0) is not { } entry) return false;
        Native.Model.PrefabCarInitW car = entry.CarInit[0];
        Native.Model.PrefabOtherInitW? other = car.Other.Count > 0 ? car.Other[0] : null;

        using var buffer = new MemoryStream(item, writable: false);
        var reader = new BinaryReader(buffer);
        switch (kind)
        {
            case CarItemKind.Seat when index <= car.Seats.Count:
                car.Seats.Insert(index, Native.Model.PrefabSeatW.ReadFrom(reader));
                for (int i = 0; i < car.Seats.Count; i++) car.Seats[i].SeatIndex = (uint)i;
                return true;
            case CarItemKind.Door when index <= car.DoorPoints.Count:
                car.DoorPoints.Insert(index, Native.Model.PrefabDoorPointsW.ReadFrom(reader));
                return true;
            case CarItemKind.Window when other != null && index <= other.WindowData.Count:
                other.WindowData.Insert(index, Native.Model.PrefabWindowDataW.ReadFrom(reader));
                return true;
            case CarItemKind.AxlePair when index * 2 <= car.Axles.Count:
                car.Axles.Insert(index * 2, Native.Model.PrefabAxleW.ReadFrom(reader));
                car.Axles.Insert((index * 2) + 1, Native.Model.PrefabAxleW.ReadFrom(reader));
                car.AxlePairs = (uint)(car.Axles.Count / 2);
                return true;
            case CarItemKind.ClimbBox when index <= car.ClimbBoxes.Count:
                car.ClimbBoxes.Insert(index, Native.Model.PrefabClimbBoxW.ReadFrom(reader));
                return true;
            case CarItemKind.DrivingWheel when other != null && index <= other.DrivingWheels.Count:
                other.DrivingWheels.Insert(index, reader.ReadUInt64()); return true;
            case CarItemKind.FuelTank when other != null && index <= other.FuelTanks.Count:
                other.FuelTanks.Insert(index, reader.ReadUInt64()); return true;
            case CarItemKind.Exhaust when other != null && index <= other.ExhaustEmitters.Count:
                other.ExhaustEmitters.Insert(index, reader.ReadUInt64()); return true;
            case CarItemKind.Wiper when index <= car.WipersFrameName.Count:
                car.WipersFrameName.Insert(index, reader.ReadUInt64()); return true;
            default:
                return false;
        }
    }

    private static byte[] TakeHash(List<ulong> list, int index)
    {
        byte[] bytes = Pack(w => w.Write(list[index]));
        list.RemoveAt(index);
        return bytes;
    }

    private static byte[] Pack(Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        write(new BinaryWriter(buffer));
        return buffer.ToArray();
    }

    // The last item of a list, copied through the wire model's own reader and writer — the one copy that is
    // guaranteed to be deep and to stay right when a field is added to the generated model. A fresh one when
    // there is nothing to copy from.
    private static T CloneLast<T>(List<T> list, Action<T, BinaryWriter> write, Func<BinaryReader, T> read)
        where T : new()
    {
        if (list.Count == 0) return new T();
        using var buffer = new MemoryStream();
        write(list[^1], new BinaryWriter(buffer));
        buffer.Position = 0;
        return read(new BinaryReader(buffer));
    }

    public static PrefabFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static PrefabFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadPrefab(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.PrefabToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
