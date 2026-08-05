using Illusion.Assets.Collisions;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>
/// One collision volume turned into another kind — a placed shape into glass, or back.
///
/// <para>
/// Undoing puts back the volume's own bytes rather than converting again, because a conversion is not
/// symmetric: turning something into a placed shape mints an ItemDesc record, and running that backwards
/// would leave a second one behind.
/// </para>
/// </summary>
internal sealed class CarVolumeTypeEdit : IEditAction
{
    private readonly CarPhysicsVolumes.TypeChange _change;
    private readonly Action _after;

    internal CarVolumeTypeEdit(CarPhysicsVolumes.TypeChange change, Action after)
    {
        _change = change;
        _after = after;
    }

    public void Undo()
    {
        CarPhysicsVolumes.RestoreType(_change, toBefore: true);
        _after();
    }

    public void Redo()
    {
        CarPhysicsVolumes.RestoreType(_change, toBefore: false);
        _after();
    }
}
