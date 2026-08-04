using Illusion.Assets.Prefabs;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>One number of a car's assembly changed — a window's depth, an axle's mass, one axis of where an
/// occupant sits. Like the rest of the prefab edits, an undo that writes the working copy.</summary>
internal sealed class PrefabValueEdit : IEditAction
{
    private readonly PrefabEditing.ValueChange _change;
    private readonly Action _after;

    public PrefabValueEdit(PrefabEditing.ValueChange change, Action after)
    {
        _change = change;
        _after = after;
    }

    public void Undo()
    {
        PrefabEditing.RestoreValue(_change, _change.Before);
        _after();
    }

    public void Redo()
    {
        PrefabEditing.RestoreValue(_change, _change.After);
        _after();
    }
}
