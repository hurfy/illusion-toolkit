using Illusion.Assets.Effects;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>One number of an effect changed — a birth rate, a colour key, an operator switched off. Like
/// the tuning and prefab edits, an undo that writes the working copy rather than a change held in memory.</summary>
internal sealed class EffectValueEdit : IEditAction
{
    private readonly EffectEditing.Change _change;
    private readonly Action _after;

    public EffectValueEdit(EffectEditing.Change change, Action after)
    {
        _change = change;
        _after = after;
    }

    public void Undo()
    {
        EffectEditing.Restore(_change, _change.Before);
        _after();
    }

    public void Redo()
    {
        EffectEditing.Restore(_change, _change.After);
        _after();
    }
}

/// <summary>
/// An effect added to an archive, as a copy of one it already had.
///
/// <para>
/// Undone by putting the whole file back rather than by removing the effect again: adding one is a splice
/// that changes the size of two enclosing chunks, and the only way to be certain the file returns to what
/// it was is to keep what it was.
/// </para>
/// </summary>
internal sealed class EffectAddEdit : IEditAction
{
    private readonly string _path;
    private readonly byte[] _before;
    private readonly byte[] _after;
    private readonly Action _refresh;

    public EffectAddEdit(string path, byte[] before, byte[] after, Action refresh)
    {
        _path = path;
        _before = before;
        _after = after;
        _refresh = refresh;
    }

    public void Undo()
    {
        EffectEditing.RestoreFile(_path, _before);
        _refresh();
    }

    public void Redo()
    {
        EffectEditing.RestoreFile(_path, _after);
        _refresh();
    }
}
