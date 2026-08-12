using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>
/// The rows that belong to the CAR rather than to any one panel of it — the frames the assembly names once
/// (its root, its scale bone, its rest bone, the ventilator it turns), the numbers that describe the whole
/// body, and the lists that hang off nothing in particular: the wind emitters, the bus seats and entries, the
/// grip the driver's hands take on the steering wheel.
///
/// <para>
/// They go on the BODY, for the reason a homeless marker does: the body is not a panel, it is the car itself,
/// and it is present on 85 of 85 shipped cars. Putting them anywhere else would mean inventing a row that is
/// not a thing of the car, and leaving them out would make them reachable nowhere at all — which is what the
/// old Prefab tab was for.
/// </para>
/// </summary>
public sealed partial class Car
{
    private static void HangCarRows(PrefabFile prefab, CarPrefab car, Rig rig, CarComponent? body)
    {
        if (body == null) return;

        body.AddRow(new CarComponentRow("Chassis", "chassis", 0,
        [
            Frame(prefab, rig, "Root frame", "The frame the whole car hangs off.",
                CarFrameSlot.RootFrame, 0),
            Frame(prefab, rig, "Scale bone", "The bone the car is scaled on — and the one that decides which "
                + "component is the BODY, so pointing it elsewhere moves every homeless marker with it.",
                CarFrameSlot.ScaleBone, 0),
            Frame(prefab, rig, "Body frame", "The frame the game treats as the car's body.",
                CarFrameSlot.Body, 0),
            Frame(prefab, rig, "Rest bone", "The bone the car rests on when it is parked.",
                CarFrameSlot.RestBone, 0),
            Frame(prefab, rig, "Snow rest", "Where snow settles on the car, when it carries one.",
                CarFrameSlot.SnowRest, 0),
            Frame(prefab, rig, "Motor ventilator", "The fan the engine turns.",
                CarFrameSlot.MotorVentilator, 0),
            // The three lights are SLOTS, not a list: a car has one of each or none. Each one that is filled
            // is also a marker on the component its frame hangs off, and that is where it is placed — but a
            // marker only exists while the slot is filled, so an empty slot would be reachable nowhere and
            // taking a light off would be a one-way door.
            Frame(prefab, rig, "Headlight", "The frame the headlights are drawn from. Empty on a car with "
                + "none; filled, it also shows as a light on the component it hangs off, which is where it "
                + "is moved.", CarFrameSlot.Headlight, 0),
            Frame(prefab, rig, "Backlight", "And the rear lights.", CarFrameSlot.Backlight, 0),
            Frame(prefab, rig, "Toplight", "And the roof light, which most cars do not carry.",
                CarFrameSlot.Toplight, 0),
            Point(prefab, "Bone range", "How far the damage model lets a bone travel, per axis.",
                CarValueSlot.BoneRange, 0),
            Number(prefab, "Reduce bounding box Z", "How much is taken off the car's bounding box "
                + "vertically.", CarFieldKind.Number, CarValueSlot.ReduceBboxZ, 0),
        ])
        {
            Hint = "What this car names once: the frames the whole assembly hangs on, and the two numbers "
                + "that describe the body as a whole.",
        });

        // The lists. Each is one row carrying one frame field per entry, because they are a list of frames
        // and nothing else — a band of its own for four hashes would be four rows saying one word each.
        Slots(prefab, rig, body, "Driving wheels", "driving wheel", CarFrameSlot.DrivingWheel,
            car.DrivingWheels.Count,
            "Which wheels the engine drives. A bone on all 83 shipped ones.");
        Slots(prefab, rig, body, "Wind emitters", "wind emitter", CarFrameSlot.LocalWindEmitter,
            prefab.CarSlotCount(CarFrameSlot.LocalWindEmitter),
            "Where the car blows air from as it moves.");
        Slots(prefab, rig, body, "Bus seats", "bus seat", CarFrameSlot.BusSeat,
            prefab.CarSlotCount(CarFrameSlot.BusSeat),
            "The standing places a bus carries beyond its own seats.");
        Slots(prefab, rig, body, "Bus entries", "bus entry", CarFrameSlot.EnterBus,
            prefab.CarSlotCount(CarFrameSlot.EnterBus),
            "Where a passenger boards.");

        // The steering wheel and the two hands that hold it are one thing said three ways, so they are one
        // row per wheel rather than three lists that have to be read side by side.
        int wheels = prefab.CarSlotCount(CarFrameSlot.DrWheelSnapWheel);
        for (int i = 0; i < wheels; i++)
        {
            body.AddRow(new CarComponentRow(Numbered("Steering wheel grip", i, wheels), "grip", i,
            [
                Frame(prefab, rig, "Steering wheel", "The wheel itself.",
                    CarFrameSlot.DrWheelSnapWheel, i),
                Frame(prefab, rig, "Left hand", "Where the driver's left hand takes hold of it.",
                    CarFrameSlot.DrWheelSnapLeft, i),
                Frame(prefab, rig, "Right hand", "And the right.",
                    CarFrameSlot.DrWheelSnapRight, i),
            ])
            {
                Hint = "The wheel the driver holds, and where each hand takes hold of it.",
            });
        }
    }

    private static void Slots(
        PrefabFile prefab, Rig rig, CarComponent body, string title, string one, CarFrameSlot slot, int count,
        string hint)
    {
        if (count <= 0) return;
        var fields = new List<CarField>(count);
        for (int i = 0; i < count; i++)
        {
            fields.Add(Frame(prefab, rig, Numbered(Capitalize(one), i, count), hint, slot, i));
        }
        body.AddRow(new CarComponentRow(title, one, 0, fields) { Hint = hint });
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>
    /// One frame reference, read and given the list it may be pointed at.
    ///
    /// <para>
    /// A hash that resolves to nothing is shown as the bare hash and marked, rather than hidden: it is the
    /// signature of a rename made in Blender, the game follows it into silence, and the editor is the only
    /// place it can be seen before the car is built.
    /// </para>
    /// </summary>
    private static CarField Frame(
        PrefabFile prefab, Rig rig, string label, string hint, CarFrameSlot slot, int index)
    {
        ulong hash = prefab.GetCarFrame(slot, index);
        string? name = rig.NameOf(hash);
        return new CarField(label, hint, CarFieldKind.Frame, 0f, System.Numerics.Vector3.Zero)
        {
            FrameSlot = slot,
            At = index,
            Frame = hash,
            FrameName = hash == 0 ? "" : name ?? Hex(hash),
            FrameResolves = hash == 0 || name != null,
            Choices = rig.Choices,
        };
    }
}
