using Illusion.Assets.Bridge;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Bridge;

/// <summary>
/// One undoable step for a rig that came back from Blender. The whole rest table is swapped rather than
/// each bone replayed through the editor's own setter — that setter carries a bone's descendants, and the
/// matrices Blender sends are absolute with the hierarchy already resolved, so replaying them would apply
/// every parent's motion to its children a second time.
/// </summary>
internal sealed class BonePoseEdit : INodeEdit
{
    private readonly D3DImageHost _host;
    private readonly SceneNode _node;
    private readonly BonePosePush.Result _pose;

    public BonePoseEdit(D3DImageHost host, SceneNode node, BonePosePush.Result pose)
    {
        _host = host;
        _node = node;
        _pose = pose;
    }

    public string Describe() => _pose.Moved.Count == 1
        ? $"Pose bone {_pose.Moved[0]}"
        : $"Pose {_pose.Moved.Count} bones";

    public IEnumerable<SceneNode> Nodes => [_node];

    public void Undo() => Apply(undo: true);

    public void Redo() => Apply(undo: false);

    private void Apply(bool undo)
    {
        if (!_host.Tree.IsInScene(_node)) return;
        BonePosePush.Write(_pose, undo);
        _host.Streamer.RefreshRig(_node);
        _host.Persistence.MarkFrameModified(_node);
        _host.RaiseSelectionTransformChanged();
    }
}
