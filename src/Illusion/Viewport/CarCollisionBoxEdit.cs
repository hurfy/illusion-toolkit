using Illusion.Assets.Collisions;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Viewport;

/// <summary>
/// Adding a box for a car part to be shot at, as one undoable step.
/// <para>
/// The pieces are not all in the frame graph: the shape is a file in the extracted folder AND a line in the
/// archive's manifest, while the stub that names it is a frame hung off a bone. Undo has to take all of it
/// back, or a second add would find the shape's file name taken and the graph carrying a stub nobody sees.
/// </para>
/// <para>
/// Redo cannot simply re-run the builder: that would mint a fresh hash and a fresh file, so an undo/redo pair
/// would leave the archive with two shapes where the user made one. It re-attaches the SAME stub and rewrites
/// the same file instead.
/// </para>
/// </summary>
internal sealed class CarCollisionBoxEdit : IEditAction
{
    private readonly FrameObjectModel _model;
    private readonly AddedCollisionBox _added;
    private readonly Action _refresh;
    private readonly byte[] _shapeBytes;

    internal CarCollisionBoxEdit(FrameObjectModel model, AddedCollisionBox added, Action refresh)
    {
        _model = model;
        _added = added;
        _refresh = refresh;
        _shapeBytes = added.Shape.ToBytes();
    }

    /// <summary>The stub that was added — the caller selects it so the gizmo can place it straight away.</summary>
    internal FrameObjectCollision Frame => _added.Frame;

    public void Redo()
    {
        // Both the file and the manifest line that names it: packing reads the manifest, so a shape put back
        // on disk and not re-announced would simply not be in the archive.
        CarCollisionBuilder.Restore(_added, _shapeBytes);
        if (!_model.Resource.FrameObjects.ContainsKey(_added.Frame.RefID))
        {
            _model.Resource.FrameObjects.Add(_added.Frame.RefID, _added.Frame);
        }
        if ((_model.AttachmentReferences ?? []).All(r => !ReferenceEquals(r.Attachment, _added.Frame)))
        {
            _model.AttachToJoint(_added.Frame, (byte)_added.Bone);
        }
        _refresh();
    }

    public void Undo()
    {
        CarCollisionBuilder.Remove(_model, _added);
        _refresh();
    }
}
