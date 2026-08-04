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

    /// <summary>Whether the overlay is drawing the car's collision.</summary>
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
    /// The volumes of each loaded document, kept between redraws. Reading them means opening the archive's
    /// prefab and every ItemDesc file it lists, and the overlay is now redrawn on every frame of a gizmo
    /// drag — doing that off disk each time would put a dozen file reads inside the drag loop.
    /// </summary>
    private readonly Dictionary<SceneDocumentAdapter, IReadOnlyList<PlacedPhysicsVolume>> _cache = new();

    /// <summary>
    /// Redraws after re-reading the archives. This is the DEFAULT: anything that could have changed a volume
    /// — an add, a remove, a save, a scene change, a number typed into the shape editor — goes through here,
    /// so the cache can never serve a stale shape. Only the gizmo drag takes the cached path, and a drag is
    /// exactly the moment when nothing on disk moves.
    /// </summary>
    internal void RefreshOverlay()
    {
        _cache.Clear();
        Redraw();
    }

    /// <summary>Redraws from what was already read — for the inside of a gizmo drag, which runs per frame.</summary>
    internal void RefreshOverlayWhileDragging() => Redraw();

    /// <summary>
    /// Redraws the car's collision as the PREFAB describes it, which is the copy the game reads.
    ///
    /// <para>
    /// EVERYTHING the loaded cars carry, not just the selected part. Scoping it to the selection meant the
    /// answer to "where is this car solid" depended on what happened to be clicked, and a shape you have to
    /// hunt for by selecting bones one at a time is a shape you cannot judge.
    /// </para>
    /// <para>
    /// A volume that has a stub frame is drawn where the STUB is rather than where the prefab says: the stub
    /// is the handle being dragged, and the save is what makes the two agree. One with no stub — every
    /// window, every snow volume — is drawn where the prefab puts it.
    /// </para>
    /// </summary>
    private void Redraw()
    {
        if (_host.Rnd is not { } renderer) return;
        renderer.ShowPartShapes = _show;
        if (!_show) { renderer.SetPartShapeLines([]); return; }

        var lines = new List<Vector3>();
        foreach (SceneDocumentAdapter document in Documents())
        {
            if (!_cache.TryGetValue(document, out IReadOnlyList<PlacedPhysicsVolume>? volumes))
            {
                _cache[document] = volumes = document.PhysicsVolumes();
            }
            foreach (PlacedPhysicsVolume volume in volumes)
            {
                Matrix4x4 world = volume.Stub != null
                    ? document.Node(volume.Stub).WorldTransform
                    : volume.World;
                if (volume.Shape != null) CarCollisionShapes.AppendWireframe(lines, volume.Shape, world);
                else CarCollisionShapes.AppendBox(lines, volume.Volume.Size * 0.5f, world);
            }
        }
        renderer.SetPartShapeLines(lines);
    }

    /// <summary>Every frame document on the stage right now. A document with no car prefab answers with an
    /// empty list and costs one manifest read, which is why this can afford to ask all of them.</summary>
    private IEnumerable<SceneDocumentAdapter> Documents()
    {
        var seen = new HashSet<SceneDocumentAdapter>();
        foreach (SceneNode root in _host.Tree.Roots)
        {
            foreach (SceneDocumentAdapter document in DocumentsUnder(root, seen)) yield return document;
        }
    }

    private static IEnumerable<SceneDocumentAdapter> DocumentsUnder(SceneNode node, HashSet<SceneDocumentAdapter> seen)
    {
        if (node.Source is SceneDocumentAdapter document && seen.Add(document)) yield return document;
        foreach (SceneNode child in node.Children)
        {
            foreach (SceneDocumentAdapter found in DocumentsUnder(child, seen)) yield return found;
        }
    }

    /// <summary>
    /// The deformable parts of the selected car — what the dialog offers. Which one a shape is given to is
    /// the question that decides its behaviour, so it is asked rather than inferred from the selection.
    /// </summary>
    internal IReadOnlyList<CarPartChoice> PartChoices()
    {
        if (SelectedBone is not { } bone) return [];
        return CarPhysicsVolumes.PartChoices(
            MafiaEnvironment.ExtractedDir(bone.Document.SourceArchive), bone.Model.Resource);
    }

    /// <summary>
    /// Gives one of the car's deformable parts a collision box and selects it, so the gizmo can place it
    /// straight away. Reports through the host's notice line; nothing is written when it refuses.
    /// </summary>
    /// <param name="kind">Box, sphere or capsule — the primitives, which are pure numbers.</param>
    /// <param name="size">Box: half-extents. Sphere: X is the radius. Capsule: X radius, Y length.</param>
    /// <param name="joint">The part's bone — both copies of the placement are read in ITS space.</param>
    internal void AddBoxToPart(Illusion.Formats.ItemDesc.RigidBodyShape kind, Vector3 size, int joint)
    {
        if (SelectedBone is not { } selected)
        {
            _host.RaiseNotice("select a bone first — a collision box hangs off a part, not off the scene", true);
            return;
        }

        SceneNode? boneNode = _host.SelectedNode;
        FrameObjectModel model = selected.Model;
        string extracted = MafiaEnvironment.ExtractedDir(selected.Document.SourceArchive);
        string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray();
        string boneName = joint >= 0 && joint < bones.Length ? bones[joint] : selected.BoneName;
        string name = UniqueName(model, boneName);

        AddedCollisionBox? added = CarCollisionBuilder.AddShape(
            model, joint, name, kind, size, Matrix4x4.Identity, extracted, out string? refusal);
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
                Source = selected.Document.Node(added.Frame),
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
        // Turn the layer on: a shape that exists and is invisible reads as "nothing happened", and the
        // whole point of adding one is to place it by eye.
        ShowShapes = true;
        _host.History.Push(edit);
        _host.Persistence.MarkFrameModified(boneNode ?? _host.SelectedNode!);
        if (row != null) _host.Select(row);

        // Say which PART it belongs to, not just where the row landed: the row sits under the bone that was
        // selected until the archive is loaded again, while the shape belongs to the part that was chosen.
        _host.RaiseNotice(
            $"collision box added to \"{boneName}\" — drag it into place, then Save and Build. "
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
