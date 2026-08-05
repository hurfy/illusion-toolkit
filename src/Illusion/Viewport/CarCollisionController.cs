using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Collisions;
using Illusion.Domain;
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
    /// Takes the prefab as the truth again, then redraws — for an edit made to the FILE rather than to the
    /// scene (the Prefab tab, and the undo of one).
    ///
    /// <para>
    /// Two things go stale on such an edit and only one of them is the cache. A volume that has a stub is
    /// drawn where the STUB is, because the stub is the handle a gizmo drags; typing a new position into the
    /// panel moves the prefab and leaves that handle behind, so the box would sit still no matter what the
    /// number said. Snapping the stubs onto the prefab is what makes the two agree again — the same repair
    /// the loader performs, and safe here for the same reason it is unsafe in
    /// <see cref="RefreshOverlay"/>: nothing is mid-drag, so there is no newer placement to overwrite.
    /// </para>
    /// </summary>
    internal void AdoptPrefabPlacements()
    {
        foreach (SceneDocumentAdapter document in Documents()) document.AdoptPrefabPlacements();
        RefreshOverlay();
    }

    /// <summary>
    /// Drops everything read so far — for a scene reset, where the documents are being unloaded.
    ///
    /// <para>
    /// The cache is keyed by document instance and a reload mints new ones, so a stale entry was never
    /// served; it was simply held. Letting go of it at the reset is what keeps a restored archive from
    /// carrying the memory of the one it replaced.
    /// </para>
    /// </summary>
    internal void Forget() => _cache.Clear();

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
        if (!_show) { renderer.SetPartShapeLines([], []); return; }

        var lines = new List<Vector3>();
        var colors = new List<Vector4>();
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

                int before = lines.Count;
                if (volume.Shape != null) CarCollisionShapes.AppendWireframe(lines, volume.Shape, world);
                else CarCollisionShapes.AppendBox(lines, volume.Volume.Size * 0.5f, world);

                // One colour per volume, because a car's collision is several different things drawn on top
                // of one another and an unlabelled wireframe cannot say which is which.
                Vector4 tint = ColorOf(volume);
                for (int i = before; i < lines.Count; i++) colors.Add(tint);
            }
        }
        renderer.SetPartShapeLines(lines, colors);
    }

    /// <summary>
    /// What colour a volume is drawn in — its KIND, and its surface when it names one.
    ///
    /// <para>
    /// The kinds are not variants of one thing. A type-5 volume places a physics shape and is the body, the
    /// doors, the bumpers; a type-0 volume IS the glass (all 527 shipped window volumes are type 0 and none
    /// is anything else); type 6 is a zone — the engine bay, the snow. A shape that names a physics surface
    /// is a different thing again, and takes that surface's own colour from the catalog the world's collision
    /// is painted with, so the two layers read the same way.
    /// </para>
    /// </summary>
    private static Vector4 ColorOf(PlacedPhysicsVolume volume)
    {
        if (volume.Volume.NamesShape)
        {
            // A surface that was asked for wins: it is the one thing here a person set deliberately, and
            // seeing it is how they know it took.
            ushort raw = (volume.Shape?.Element as Illusion.Formats.ItemDesc.RigidBodyElement)?.MaterialId ?? 0;
            if (raw != 0)
            {
                Vector3 named = CollisionMaterialCatalog.ColorForRawId(raw);
                return new Vector4(named, 0.95f);
            }
            return new Vector4(1f, 0.45f, 0.25f, 0.95f);     // solid, no surface named — the old orange
        }

        return volume.Volume.VolumeType switch
        {
            0 => new Vector4(0.42f, 0.80f, 0.95f, 0.75f),    // glass
            6 => new Vector4(0.95f, 0.80f, 0.35f, 0.60f),    // a zone: engine bay, snow
            _ => new Vector4(0.70f, 0.55f, 0.95f, 0.60f),    // the rare type 2, so it is never mistaken
        };
    }

    /// <summary>Every frame document on the stage — for callers outside this controller that need the open
    /// graph rather than the file (the prefab panel resolving a frame minted this session).</summary>
    internal IEnumerable<SceneDocumentAdapter> StageDocuments() => Documents();

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
    /// <param name="surface">Physics-surface index the shape names, or 0 for "as the shipped ones".</param>
    internal void AddBoxToPart(
        Illusion.Formats.ItemDesc.RigidBodyShape kind, Vector3 size, int joint, int? surface = null)
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
            model, joint, name, kind, size, Landing(model, joint), extracted, out string? refusal, surface);
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

    /// <summary>
    /// Changes what an existing volume IS, addressed the way the Prefab tab addresses one: by the single flat
    /// number that folds its part and its position together.
    /// </summary>
    internal bool ChangeVolumeType(FileInfo archive, int flat, uint newType, out string? refusal)
    {
        refusal = null;
        foreach (SceneDocumentAdapter document in Documents())
        {
            if (!string.Equals(document.SourceArchive.FullName, archive.FullName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string extracted = MafiaEnvironment.ExtractedDir(document.SourceArchive);
            if (Illusion.Assets.Prefabs.PrefabEditing.OpenFirst(extracted) is not { } prefab
                || prefab.CarVolumeAt(flat) is not { } at)
            {
                refusal = "that volume is no longer there";
                return false;
            }

            CarPhysicsVolumes.TypeChange? change = document.ChangeVolumeType(
                at.Part, at.Volume, newType, out refusal);
            if (change == null) return false;

            _host.History.Push(new CarVolumeTypeEdit(change, RefreshOverlay));
            _host.Persistence.MarkArchiveModified(document.SourceArchive);
            RefreshOverlay();
            return true;
        }

        refusal = "that archive is not on the stage";
        return false;
    }

    /// <summary>
    /// Gives a part a SELF-DESCRIBING volume — a box that names no shape and says what it is by its type:
    /// 0 is glass, 6 is a zone like the engine bay. No ItemDesc record, no stub frame, no gizmo handle, which
    /// is exactly how every shipped one is built; it is placed by the numbers in the Prefab tab afterwards.
    /// </summary>
    /// <param name="fullSize">The box's full size, not half.</param>
    internal void AddZoneToPart(uint volumeType, Vector3 fullSize, int joint)
    {
        if (SelectedBone is not { } selected)
        {
            _host.RaiseNotice("select a bone first — a collision volume hangs off a part, not off the scene", true);
            return;
        }

        FrameObjectModel model = selected.Model;
        string extracted = MafiaEnvironment.ExtractedDir(selected.Document.SourceArchive);
        string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray();
        string boneName = joint >= 0 && joint < bones.Length ? bones[joint] : selected.BoneName;

        // The space is the PARENT part's bone, not this part's — see CarPhysicsVolumes.Load.
        ulong space = CarPhysicsVolumes.SpaceFrameFor(extracted, boneName);
        int spaceBone = joint;
        for (int i = 0; i < bones.Length; i++)
        {
            if (Illusion.Formats.Hashing.Fnv64.Hash(bones[i]) == space) { spaceBone = i; break; }
        }

        CarPhysicsVolumes.VolumeChange? added = CarPhysicsVolumes.AddZone(
            extracted, boneName, Landing(model, spaceBone), fullSize, volumeType);
        if (added == null)
        {
            _host.RaiseNotice(
                $"no volume added — \"{boneName}\" is not a deformable part of this car", true);
            return;
        }

        ShowShapes = true;
        _host.History.Push(new CarZoneEdit(added, RefreshOverlay));
        _host.Persistence.MarkArchiveModified(selected.Document.SourceArchive);
        RefreshOverlay();

        string what = volumeType == 0 ? "glass" : $"a type-{volumeType} zone";
        _host.RaiseNotice(
            $"added {what} to \"{boneName}\" — it has no handle to drag, like every shipped one: "
            + "move and resize it in the Prefab tab, under Collision. Then Build.");
    }

    /// <summary>
    /// Where a new shape lands: on the thing that was selected, written in the chosen PART's bone space.
    ///
    /// <para>
    /// The two are not the same bone as often as it looks. The dialog answers "which part" with the body
    /// whenever the selection is not itself a deformable part — and a climb box, a fuel tank, a seat, any
    /// Dummy at all is not one — so a shape asked for while pointing at a climb box used to appear at the
    /// body bone, which on a car is its centre. That is the "the transform is strange in places" report: the
    /// box was correct and simply nowhere near what was being pointed at.
    /// </para>
    /// <para>
    /// Rotation and position travel, scale does not: a placement has nowhere to put a scale (see
    /// <see cref="CarPhysicsVolumes.BakeScales"/>), so inheriting a scaled Dummy's factor would silently
    /// resize the shape at the next save.
    /// </para>
    /// </summary>
    private Matrix4x4 Landing(FrameObjectModel model, int joint)
    {
        if (_host.SelectedNode?.Source is not IFrameNode pointed) return Matrix4x4.Identity;
        Matrix4x4 local = TransformMath.ComputeLocalTransform(
            pointed.WorldTransform, model.GetJointWorldTransform(joint));
        TransformMath.TryDecompose(local, out _, out Quaternion rotation, out Vector3 position);
        return TransformMath.Compose(rotation, Vector3.One, position);
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
