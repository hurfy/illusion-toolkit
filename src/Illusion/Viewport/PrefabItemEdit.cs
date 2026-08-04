using Illusion.Assets.Prefabs;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>
/// A part gained or lost by a car's assembly. The two directions are the same pair of moves with the arrow
/// reversed, which is what makes the undo exact: the part is carried in the change as its own bytes, so what
/// comes back is what was there rather than a copy of its neighbour.
/// </summary>
internal sealed class PrefabItemEdit : IEditAction
{
    private readonly PrefabEditing.ItemChange _change;
    private readonly bool _added;
    private readonly Action _after;

    public PrefabItemEdit(PrefabEditing.ItemChange change, bool added, Action after)
    {
        _change = change;
        _added = added;
        _after = after;
    }

    public void Undo()
    {
        if (_added) PrefabEditing.TakeAway(_change);
        else PrefabEditing.PutBack(_change);
        _after();
    }

    public void Redo()
    {
        if (_added) PrefabEditing.PutBack(_change);
        else PrefabEditing.TakeAway(_change);
        _after();
    }
}
