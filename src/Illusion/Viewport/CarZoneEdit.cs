using Illusion.Assets.Collisions;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>
/// One self-describing collision volume added to a car — a pane of glass, or a zone like the engine bay.
///
/// <para>
/// Simpler than its type-5 sibling and deliberately so: there is no ItemDesc record and no stub frame to take
/// back, because a volume of this kind is nothing but a row in the prefab. Undo drops the row, redo puts the
/// very same bytes back at the very same index.
/// </para>
/// </summary>
internal sealed class CarZoneEdit : IEditAction
{
    private readonly CarPhysicsVolumes.VolumeChange _change;
    private readonly Action _after;

    internal CarZoneEdit(CarPhysicsVolumes.VolumeChange change, Action after)
    {
        _change = change;
        _after = after;
    }

    public void Undo()
    {
        CarPhysicsVolumes.Remove(_change);
        _after();
    }

    public void Redo()
    {
        CarPhysicsVolumes.Restore(_change);
        _after();
    }
}
