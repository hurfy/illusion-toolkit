using Illusion.Assets.Sds;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>
/// One reversible change to what an archive CARRIES — a resource dropped, imported or pasted in the content
/// browser. The scene's other edits live in memory until Save; these land in the extracted working copy the
/// moment they happen, so like the car-collision box edit they are the kind of undo that does I/O.
/// <para>
/// Neither direction moves payload bytes around. A delete only unsays the manifest entry and an undone import
/// only unsays the one it added, which leaves a file on disk that the manifest does not name — invisible to
/// packing, and exactly what makes the opposite direction a single manifest line again. What IS moved is a
/// payload that got overwritten: its previous bytes wait in the working copy's undo shadow.
/// </para>
/// </summary>
internal sealed class ArchiveContentEdit : IEditAction
{
    private readonly string _folder;
    private readonly Action _after;

    // A delete: the entries it unsaid, with every field needed to say them again.
    private readonly IReadOnlyList<ArchiveEditing.RemovedEntry> _dropped;

    // An import or a paste: what it added (filled in on the first undo, which is when the fields are still
    // readable) and what it overwrote.
    private readonly ArchiveEditing.WriteResult? _written;
    private IReadOnlyList<ArchiveEditing.RemovedEntry> _added = [];

    private ArchiveContentEdit(
        string folder,
        IReadOnlyList<ArchiveEditing.RemovedEntry> dropped,
        ArchiveEditing.WriteResult? written,
        Action after)
    {
        _folder = folder;
        _dropped = dropped;
        _written = written;
        _after = after;
    }

    /// <summary>The undo of a delete: say the entries again.</summary>
    public static ArchiveContentEdit ForDelete(
        string folder, ArchiveEditing.DeleteResult result, Action after)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ArchiveContentEdit(folder, result.Removed, null, after);
    }

    /// <summary>The undo of an import or a paste: unsay what it added, put back what it replaced.</summary>
    public static ArchiveContentEdit ForWrite(
        string folder, ArchiveEditing.WriteResult result, Action after)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ArchiveContentEdit(folder, [], result, after);
    }

    public void Undo()
    {
        if (_written != null)
        {
            _added = ArchiveEditing.UndoWrite(_folder, _written);
        }
        else
        {
            ArchiveEditing.RestoreDeleted(_folder, _dropped);
        }
        _after();
    }

    public void Redo()
    {
        if (_written != null)
        {
            ArchiveEditing.RedoWrite(_folder, _added, _written.Replaced);
        }
        else
        {
            ArchiveEditing.DropEntries(_folder, _dropped.Select(entry => entry.ManifestName));
        }
        _after();
    }
}
