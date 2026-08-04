using Illusion.Assets.EntityData;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>One field of an entity-data table changed — a car's mass, a gear ratio, the wheel it names.
/// Like the prefab edits, an undo that writes the working copy rather than a change held in memory.</summary>
internal sealed class TuningValueEdit : IEditAction
{
    private readonly TuningEditing.Change _change;
    private readonly Action _after;

    public TuningValueEdit(TuningEditing.Change change, Action after)
    {
        _change = change;
        _after = after;
    }

    public void Undo()
    {
        TuningEditing.Restore(_change, _change.Before);
        _after();
    }

    public void Redo()
    {
        TuningEditing.Restore(_change, _change.After);
        _after();
    }
}
