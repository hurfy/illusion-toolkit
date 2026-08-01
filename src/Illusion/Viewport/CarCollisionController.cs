using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Collisions;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// Giving a car part something to be shot at.
///
/// <para>
/// A car's mesh is not what a bullet hits. Its collision is a handful of physics shapes hung off its bones, and
/// geometry added in Blender gets none — so shots pass straight through the new part. This is where a bone is
/// handed one: pick the bone, say how big, and a box shape plus the stub that places it go into the archive.
/// </para>
/// <para>
/// A BOX and not a hull of the geometry: a convex hull is a PhysX-cooked blob and the vendored cooker exposes
/// exactly one verb, <c>-CookTriangleMesh</c>, so hulls cannot be minted at all. Cars ship 307 boxes of their
/// own, so this is their own vocabulary rather than a workaround (<c>--probe-car-collision</c>).
/// </para>
/// </summary>
internal sealed class CarCollisionController
{
    private readonly D3DImageHost _host;

    internal CarCollisionController(D3DImageHost host) => _host = host;

    /// <summary>The bone the selection points at, or null when it points at anything else.</summary>
    internal BoneNodeAdapter? SelectedBone => _host.SelectedNode?.Source as BoneNodeAdapter;

    /// <summary>Whether a box can be added right now — a bone has to be selected.</summary>
    internal bool CanAddBox => SelectedBone != null;

    /// <summary>Whether the overlay is drawing the selected part's shapes.</summary>
    internal bool ShowShapes
    {
        get => _show;
        set
        {
            _show = value;
            RefreshOverlay();
        }
    }

    private bool _show;

    /// <summary>
    /// Redraws the shape wireframe for whatever is selected now. Cheap enough to run on every selection change:
    /// it only ever walks ONE part's stubs, which is a handful, and re-reads that archive's ItemDesc files —
    /// twelve small files on a car.
    /// </summary>
    internal void RefreshOverlay()
    {
        if (_host.Rnd is not { } renderer) return;
        renderer.ShowPartShapes = _show;
        if (!_show) { renderer.SetPartShapeLines([]); return; }

        var lines = new List<Vector3>();
        foreach ((FrameObjectCollision stub, SceneDocumentAdapter document) in SelectedStubs())
        {
            string extracted = MafiaEnvironment.ExtractedDir(document.SourceArchive);
            Dictionary<ulong, ResolvedCollisionShape> shapes = CarCollisionShapes.Load(extracted, [stub]);
            if (!shapes.TryGetValue(stub.Hash, out ResolvedCollisionShape? resolved)) continue;
            CarCollisionShapes.AppendWireframe(lines, resolved, document.Node(stub).WorldTransform);
        }
        renderer.SetPartShapeLines(lines);
    }

    /// <summary>
    /// The collision stubs the selection speaks for: everything hanging off a selected BONE, or the selected
    /// stub itself. Selecting the part shows what protects it; selecting one shape shows just that one.
    /// </summary>
    private IEnumerable<(FrameObjectCollision Stub, SceneDocumentAdapter Document)> SelectedStubs()
    {
        if (_host.SelectedNode?.Source is FrameNodeAdapter { Frame: FrameObjectCollision one } picked)
        {
            yield return (one, picked.Document);
            yield break;
        }
        if (SelectedBone is not { } bone) yield break;
        foreach (FrameObjectModel.AttachmentReference reference in bone.Model.AttachmentReferences ?? [])
        {
            if (reference.JointIndex == bone.Index && reference.Attachment is FrameObjectCollision stub)
            {
                yield return (stub, bone.Document);
            }
        }
    }

    /// <summary>
    /// Adds a box collision to the selected bone and selects it, so the gizmo can place it straight away.
    /// Reports through the host's notice line; nothing is written when it refuses.
    /// </summary>
    /// <param name="dimensions">Box size along each axis.</param>
    internal void AddBoxToSelectedBone(Vector3 dimensions)
    {
        if (SelectedBone is not { } bone)
        {
            _host.RaiseNotice("select a bone first — a collision box hangs off a part, not off the scene", true);
            return;
        }

        SceneNode? boneNode = _host.SelectedNode;
        FrameObjectModel model = bone.Model;
        string extracted = MafiaEnvironment.ExtractedDir(bone.Document.SourceArchive);
        string name = UniqueName(model, bone.BoneName);

        AddedCollisionBox? added = CarCollisionBuilder.AddBox(
            model, bone.Index, name, dimensions, Matrix4x4.Identity, extracted, out string? refusal);
        if (added == null)
        {
            _host.RaiseNotice("no collision box added: " + (refusal ?? "unknown reason"), true);
            return;
        }

        // The tree row for it, under the bone, exactly where the loader would have put it on a reload — so the
        // new shape is selectable and draggable now rather than after a round trip through disk.
        SceneNode? row = null;
        if (boneNode != null)
        {
            row = new SceneNode($"{name}  (Collision)", "Attachment", false)
            {
                Source = bone.Document.Node(added.Frame),
            };
            boneNode.AddChild(row);
            boneNode.IsExpanded = true;
        }

        var edit = new CarCollisionBoxEdit(model, added, () =>
        {
            if (boneNode == null || row == null) return;
            if (model.Resource.FrameObjects.ContainsKey(added.Frame.RefID))
            {
                if (!boneNode.Children.Contains(row)) boneNode.AddChild(row);
            }
            else
            {
                boneNode.Children.Remove(row);
            }
        });
        _host.History.Push(edit);
        _host.Persistence.MarkFrameModified(boneNode ?? _host.SelectedNode!);
        if (row != null) _host.Select(row);

        _host.RaiseNotice(
            $"collision box added to \"{bone.BoneName}\" — drag it into place, then Save and Build. "
            + "Ctrl+Z takes it back.");
    }

    /// <summary>A frame name the archive is not already using.</summary>
    private static string UniqueName(FrameObjectModel model, string bone)
    {
        string stem = string.IsNullOrWhiteSpace(bone) ? "part" : bone;
        for (int n = 0; ; n++)
        {
            string candidate = n == 0
                ? $"box_{stem}_Collision"
                : $"box_{stem}{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}_Collision";
            bool taken = model.Resource.FrameObjects.Values.OfType<FrameObjectBase>()
                .Any(f => string.Equals(f.Name.ToString(), candidate, StringComparison.Ordinal));
            if (!taken) return candidate;
        }
    }
}
