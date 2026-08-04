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
    /// Redraws the collision of whatever is selected now, as the PREFAB describes it — which is the copy the
    /// game reads. A volume that has a stub frame is drawn where the stub is rather than where the prefab
    /// says, because the stub is the handle being dragged and the save is what makes the two agree; a volume
    /// with no stub (every window, every snow volume) is drawn where it is.
    /// <para>
    /// Cheap enough to run on every selection change: it walks ONE part's volumes and re-reads that archive's
    /// prefab and its dozen small ItemDesc files.
    /// </para>
    /// </summary>
    internal void RefreshOverlay()
    {
        if (_host.Rnd is not { } renderer) return;
        renderer.ShowPartShapes = _show;
        if (!_show) { renderer.SetPartShapeLines([]); return; }

        var lines = new List<Vector3>();
        foreach ((PlacedPhysicsVolume volume, SceneDocumentAdapter document, FrameObjectCollision? handle)
                 in SelectedVolumes())
        {
            // The stub this volume is drawn by: its own when the archive resolves one, otherwise the stub
            // that is SELECTED — a shape added seconds ago is being dragged by that stub, and drawing it at
            // the prefab placement instead leaves it standing still while the gizmo walks away.
            FrameObjectCollision? by = volume.Stub ?? handle;
            Matrix4x4 world = by != null
                ? document.Node(by).WorldTransform
                : volume.World;
            if (volume.Shape != null) CarCollisionShapes.AppendWireframe(lines, volume.Shape, world);
            else CarCollisionShapes.AppendBox(lines, volume.Volume.Size * 0.5f, world);
        }
        renderer.SetPartShapeLines(lines);
    }

    /// <summary>
    /// The collision volumes the selection speaks for: everything on the deformable part a selected BONE is,
    /// or the single volume a selected stub places. Selecting the part shows what protects it; selecting one
    /// shape shows just that one.
    /// </summary>
    private IEnumerable<(PlacedPhysicsVolume Volume, SceneDocumentAdapter Document, FrameObjectCollision? Handle)>
        SelectedVolumes()
    {
        SceneDocumentAdapter? document = null;
        FrameObjectCollision? single = null;
        int bone = -1;

        if (_host.SelectedNode?.Source is FrameNodeAdapter { Frame: FrameObjectCollision one } picked)
        {
            document = picked.Document;
            single = one;
        }
        else if (SelectedBone is { } selected)
        {
            document = selected.Document;
            bone = selected.Index;
        }
        if (document == null) yield break;

        IReadOnlyList<PlacedPhysicsVolume> all = document.PhysicsVolumes();
        if (single == null)
        {
            foreach (PlacedPhysicsVolume volume in all.Where(v => v.Bone == bone))
            {
                yield return (volume, document, null);
            }
            yield break;
        }

        // The one this stub places, matched on identity first and then on the shape it names — a stub only
        // just added is the same object, but a reload or a re-resolve can hand back a different instance for
        // the same shape, and a shape that draws under its bone and vanishes when clicked is worse than
        // useless.
        var mine = all.Where(v => ReferenceEquals(v.Stub, single)
            || (v.Shape != null && v.Shape.Hash == single.Hash)).ToList();

        // Nothing matched: show the whole part rather than an empty viewport. Selecting a shape must never
        // show LESS than selecting the bone it hangs off — that reads as "the shape is gone".
        if (mine.Count == 0)
        {
            mine = [.. all.Where(v => v.Bone == single.AttachedJoint && single.AttachedTo != null)];
        }
        foreach (PlacedPhysicsVolume volume in mine) yield return (volume, document, single);
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
