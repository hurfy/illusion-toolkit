using Illusion.Assets.Cars;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>
/// One component-level intent on the undo stack — however many structures it touched.
///
/// <para>
/// Adding a solid collision writes a prefab volume, an ItemDesc record, a manifest entry, a frame stub and a
/// handful of hit boxes; adding a marker writes a prefab row and a helper frame. Each is ONE thing the modder
/// did, so it is one Ctrl+Z, and what comes back is the SNAPSHOT the aggregate took rather than a reversed
/// derivation: the hit-box rule reproduces 65.3 % of the boxes a car ships with, so reversing it would leave
/// the other third rebuilt and the car quietly different from the one the modder started with.
/// </para>
/// </summary>
internal sealed class CarEditAction : IEditAction
{
    private readonly CarEdit _edit;
    private readonly Func<Car?> _car;
    private readonly Action<CarState> _after;
    private readonly Action<string> _refused;
    private readonly string? _prefab;

    /// <param name="car">The car as it stands NOW. The frames and hit boxes in the snapshot belong to the
    /// frame graph the viewport is drawing, which survives a re-stitch — the aggregate around it does not, so
    /// the live one is asked for rather than held.</param>
    /// <param name="refused">Told what a save would not deliver. An undo that quietly fails is worse than one
    /// that refuses: the modder goes on editing a car whose file no longer matches what the tree shows.</param>
    /// <param name="after">Told which state the car is now in, so a view over it can put back the rows that
    /// belong to the frames that state holds — a marker's Dummy comes and goes with the undo, and a scene
    /// tree that keeps its row is one whose row acts on a frame the graph no longer has.</param>
    internal CarEditAction(
        CarEdit edit, Func<Car?> car, Action<CarState> after, Action<string> refused)
    {
        _edit = edit;
        _car = car;
        _after = after;
        _refused = refused;
        _prefab = car()?.PrefabPath;
    }

    public void Undo() => Apply(_edit.Before, _edit.After);

    public void Redo() => Apply(_edit.After, _edit.Before);

    /// <summary>
    /// Puts one of the two states back, and puts the OTHER one back if the save will not take it.
    ///
    /// <para>
    /// <c>Car.Save</c> refuses by returning rather than by throwing, and the state it refuses is already in
    /// memory by then — in the frame graph the viewport draws and the next Build serializes, not in a copy.
    /// Leaving it there would let a Build write a frame resource that disagrees with the prefab beside it,
    /// with nothing having said the undo did not happen.
    /// </para>
    /// </summary>
    /// <param name="want">The state this step moves to.</param>
    /// <param name="back">The state it came from — what the file still holds if the save refuses.</param>
    private void Apply(CarState want, CarState back)
    {
        if (_car() is not { } car) return;
        // The snapshot is one car's bytes. If the stage has moved on to a DIFFERENT car since — the modder
        // closed this one and opened another — restoring it would write this car's prefab over that one's,
        // which is the worst thing an undo can do. It does nothing instead.
        if (!string.Equals(car.PrefabPath, _prefab, StringComparison.OrdinalIgnoreCase)) return;

        car.Restore(want);
        CarSave saved = car.Save();
        if (!saved.Ok)
        {
            // Back to what the file still holds, so memory and disk agree again. This second save writes
            // nothing — the bytes are already there — which is what makes it safe to run after a refusal.
            car.Restore(back);
            car.Save();
            _refused(string.Join("; ", saved.Lost));
            _after(back);
            return;
        }
        _after(want);
    }
}
