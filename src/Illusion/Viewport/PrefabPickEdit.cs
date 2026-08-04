using Illusion.Assets.Prefabs;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>
/// One slot of a car's assembly pointed at a different frame. Like the car-collision box edit, this is an
/// undo that does I/O: a prefab has no in-memory stage of its own, so the pick lands in the working copy the
/// moment it is made and taking it back has to rewrite the file.
/// </summary>
internal sealed class PrefabPickEdit : IEditAction
{
    private readonly PrefabEditing.Change _change;
    private readonly Action _after;

    public PrefabPickEdit(PrefabEditing.Change change, Action after)
    {
        _change = change;
        _after = after;
    }

    public void Undo()
    {
        PrefabEditing.Restore(_change, _change.Before);
        _after();
    }

    public void Redo()
    {
        PrefabEditing.Restore(_change, _change.After);
        _after();
    }
}
