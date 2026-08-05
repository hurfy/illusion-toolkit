using Illusion.Assets.Prefabs;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Viewport;

/// <summary>
/// Adding a whole car part — the prefab row AND the frame it names — as one undoable step.
/// <para>
/// The two halves live in different places: the row is written straight into the prefab file in the extracted
/// folder, while the frame is a node in the in-memory graph that Save writes out. An undo that took back only
/// one of them would leave either a row naming a frame that is not there (the part silently stops working) or
/// a frame nothing names (a marker floating in the archive forever), so both move together here.
/// </para>
/// </summary>
internal sealed class CarPartEdit : IEditAction
{
    private readonly FrameObjectModel _model;
    private readonly AddedCarPart _added;
    private readonly Action _refresh;

    internal CarPartEdit(FrameObjectModel model, AddedCarPart added, Action refresh)
    {
        _model = model;
        _added = added;
        _refresh = refresh;
    }

    /// <summary>The frame that was minted — the caller selects it so the gizmo can place it straight away.</summary>
    internal FrameObjectBase Frame => _added.Frame;

    public void Redo()
    {
        CarPartBuilder.Restore(_model, _added);
        _refresh();
    }

    public void Undo()
    {
        CarPartBuilder.Remove(_model, _added);
        _refresh();
    }
}
