using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Viewport;

/// <summary>
/// One model's per-piece hit boxes swapped for freshly computed ones, undoably.
///
/// <para>
/// Undo puts the previous ARRAY back rather than recomputing: the boxes that shipped hold a turn this
/// toolkit cannot reproduce (see <c>HitBoxBuilder</c>), so a rebuild is a one-way door unless the originals
/// are kept. Both directions recompute the block size beside the array, because the reader takes that byte
/// count as the number of boxes.
/// </para>
/// </summary>
internal sealed class HitBoxRebuildEdit : IEditAction
{
    private readonly FrameObjectModel _model;
    private readonly FrameObjectModel.HitBoxInfo[] _before;
    private readonly FrameObjectModel.HitBoxInfo[] _after;

    internal HitBoxRebuildEdit(
        FrameObjectModel model, FrameObjectModel.HitBoxInfo[] before, FrameObjectModel.HitBoxInfo[] after)
    {
        _model = model;
        _before = before;
        _after = after;
    }

    public void Undo() => Apply(_before);

    public void Redo() => Apply(_after);

    private void Apply(FrameObjectModel.HitBoxInfo[] boxes)
    {
        _model.HitBoxes = boxes;
        _model.RecomputeSplitCounters();
    }
}
