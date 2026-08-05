using System.Numerics;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.ItemDesc;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Collisions;

/// <summary>A deformable part a new collision shape can be hung off, named the way a person would pick it.</summary>
/// <param name="Kind">What the part is — body, cover, door, bumper — which is what decides the consequence.</param>
/// <param name="Bone">The joint the shape has to hang off, so both copies of its placement mean the same.</param>
public sealed record CarPartChoice(int Part, string Kind, int Bone, string BoneName)
{
    /// <summary>Whether this is the car's own body — the part that makes a shape part of the CAR.</summary>
    public bool IsBody => string.Equals(Kind, "body", StringComparison.Ordinal);

    /// <summary>One line for a picker: the bone, and what kind of part it is.</summary>
    public string Display => $"{BoneName}  —  {Kind}";

    public override string ToString() => Display;
}

/// <summary>One collision volume of a car, resolved: which part it belongs to, what shape it places, and
/// where that ends up in the world.</summary>
/// <param name="Bone">Index of the bone the part is, or -1 when the archive has no such bone.</param>
/// <param name="Shape">The ItemDesc record the volume names, when it names one.</param>
/// <param name="Stub">The frame that carries the SAME placement — the handle the editor drags. Null for a
/// volume that describes itself, which is most windows and every snow volume.</param>
public sealed record PlacedPhysicsVolume(
    int Part, string PartKind, int Bone, string BoneName,
    CarPhysicsVolume Volume, ItemDescFile? Shape, string? ShapeFile,
    FrameObjectCollision? Stub, Matrix4x4 World);

/// <summary>
/// A car's real physics, read and written where the game actually reads it: the collision volumes the PREFAB
/// hangs off each deformable part.
///
/// <para>
/// This was not obvious and it cost a wrong conclusion. A car ships <see cref="FrameObjectCollision"/> stubs
/// that name ItemDesc shapes, and they look like the answer — but moving one changes nothing in game, and
/// measurement says why (<c>--probe-car-physics</c>): the same placement is written down TWICE, once as the
/// stub's matrix and once in the prefab, and the prefab is the copy that is read. Of 1097 shipped pairs 1060
/// agree exactly (axes reversed, <c>stub[i][j] == prefab[2-i][2-j]</c>); the ones that disagree are archives
/// somebody has already edited through the frame graph, and their collision stayed where the prefab put it.
/// </para>
/// <para>
/// The link is the shape's DATA hash, not its file hash: a volume names <c>ItemDesc.Element.DataHash</c>
/// (1097 of 1097), while a stub names the record's own file hash. Both keys are needed to walk from one to
/// the other, which is what this class does.
/// </para>
/// </summary>
public static class CarPhysicsVolumes
{
    /// <summary>Every collision volume the car's prefab describes, resolved against the frame graph.</summary>
    public static IReadOnlyList<PlacedPhysicsVolume> Load(string extractedFolder, FrameResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var found = new List<PlacedPhysicsVolume>();

        (PrefabFile Prefab, string Path)? opened = OpenCarPrefab(extractedFolder);
        if (opened == null) return found;
        FrameObjectModel? model = resource.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
        if (model == null) return found;

        string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray();
        var boneByHash = new Dictionary<ulong, int>();
        for (int i = 0; i < bones.Length; i++) boneByHash.TryAdd(Fnv64.Hash(bones[i]), i);

        Dictionary<ulong, (ItemDescFile Shape, string File)> byData = ShapesByDataHash(extractedFolder);
        var stubByFileHash = new Dictionary<ulong, FrameObjectCollision>();
        foreach (FrameObjectCollision stub in resource.FrameObjects!.Values.OfType<FrameObjectCollision>())
        {
            stubByFileHash.TryAdd(stub.Hash, stub);
        }

        IReadOnlyList<CarDeformPart> allParts = opened.Value.Prefab.CarDeformParts;
        foreach (CarDeformPart part in allParts)
        {
            int bone = boneByHash.TryGetValue(part.Frame, out int index) ? index : -1;
            string boneName = bone >= 0 ? bones[bone] : "";

            // The bone of the part this one HANGS OFF — the space a self-describing volume is written in.
            // A part with no parent (the body) falls back to its own bone, which for a car body is the
            // identity, so the two readings coincide there and nothing changes for it.
            int parentBone = bone;
            if (part.ParentFrame != 0 && boneByHash.TryGetValue(part.ParentFrame, out int above))
            {
                parentBone = above;
            }

            foreach (CarPhysicsVolume volume in part.Volumes)
            {
                ItemDescFile? shape = null;
                string? file = null;
                FrameObjectCollision? stub = null;
                if (volume.ShapeHash != 0
                    && byData.TryGetValue(volume.ShapeHash, out (ItemDescFile Shape, string File) hit))
                {
                    shape = hit.Shape;
                    file = hit.File;
                    stubByFileHash.TryGetValue(hit.Shape.Hash, out stub);
                }

                // TWO spaces, and which one applies is decided by the volume's own type.
                //
                // A type-5 volume PLACES a physics shape, and it is written in its own part's bone space —
                // the same space its stub frame uses, which is why the two matrices are byte-identical. A
                // volume that describes itself (a window pane, the engine bay, the roof) is written in the
                // space of the part it HANGS OFF instead.
                //
                // Measured on all 85 extracted cars (`--probe-car-physics`, section "volumes with no stub"):
                // a car is symmetric, so left and right twins have to mirror, and that is the yardstick.
                // Reading the loose ones in their own bone's space misses the mirror by 1.196 m on average
                // and leaves 47% of them off the car entirely; reading them in the parent part's bone space
                // misses by 0.030 m — forty times closer — and puts 98.8% on the car. The shape-placing ones
                // answer the opposite way (0.125 m in their own bone's space, 1.300 m in the parent's), so
                // this is not one rule misapplied but two rules that were being served by one.
                int space = volume.NamesShape ? bone : parentBone;
                Matrix4x4 world = space >= 0
                    ? model.PlaceOnJoint(volume.Transform, space)
                    : volume.Transform;
                found.Add(new PlacedPhysicsVolume(
                    part.Index, part.Kind, bone, boneName, volume, shape, file, stub, world));
            }
        }
        return found;
    }

    /// <summary>
    /// Writes the placement of every given stub through to the prefab volume that names its shape — the half
    /// of the archive the game actually reads.
    ///
    /// <para>
    /// Only rotation and position travel. A frame can be scaled and a PhysX shape transform cannot: scaling
    /// the stub would write a matrix the engine has no way to honour, so a box is resized through its own
    /// dimensions instead (which is what the shape editor does).
    /// </para>
    /// </summary>
    /// <returns>How many volumes were rewritten; 0 leaves the file untouched.</returns>
    public static int SyncStubs(string extractedFolder, IEnumerable<FrameObjectCollision> stubs)
    {
        ArgumentNullException.ThrowIfNull(stubs);
        List<FrameObjectCollision> list = [.. stubs];
        if (list.Count == 0) return 0;

        (PrefabFile Prefab, string Path)? opened = OpenCarPrefab(extractedFolder);
        if (opened == null) return 0;
        (PrefabFile prefab, string path) = opened.Value;

        // A scaled stub first: the scale is baked into the SHAPE, because the placement cannot carry it.
        BakeScales(extractedFolder, list);

        // stub → shape file hash → shape data hash → the volume that names it.
        Dictionary<ulong, ulong> dataByFile = DataHashByFileHash(extractedFolder);
        var wanted = new Dictionary<ulong, Matrix4x4>();
        foreach (FrameObjectCollision stub in list)
        {
            if (dataByFile.TryGetValue(stub.Hash, out ulong data)) wanted[data] = stub.LocalTransform;
        }
        if (wanted.Count == 0) return 0;

        int written = 0;
        foreach (CarDeformPart part in prefab.CarDeformParts)
        {
            foreach (CarPhysicsVolume volume in part.Volumes)
            {
                if (volume.ShapeHash == 0
                    || !wanted.TryGetValue(volume.ShapeHash, out Matrix4x4 placement)) continue;
                if (prefab.SetCarVolume(part.Index, volume.Index, WithoutScale(placement), volume.Size))
                {
                    written++;
                }
            }
        }

        if (written > 0) AtomicFile.WriteAllBytes(path, prefab.ToBytes());
        return written;
    }

    /// <summary>
    /// Moves each collision stub onto the placement its prefab volume gives it, in memory only.
    ///
    /// <para>
    /// The two copies of a placement can disagree — 37 of the 1097 shipped pairs already do — and when they
    /// do, the prefab is the one that is true: the car's collision is where the prefab put it, whatever the
    /// frame graph says. A stub left standing somewhere else is a handle that lies about what it holds, so it
    /// is snapped on load. Nothing is written to disk; the frame is only saved if the archive is saved for
    /// some other reason.
    /// </para>
    /// </summary>
    /// <returns>How many stubs had to move.</returns>
    /// <param name="keep">Stubs the user has moved and not yet saved. Their new placement exists only in the
    /// frame, so snapping them to the prefab would throw a drag away — and this runs after edits made in the
    /// property panel, which happen mid-placement.</param>
    public static int AlignStubsToPrefab(
        string extractedFolder, FrameResource resource, IReadOnlyCollection<FrameObjectCollision>? keep = null)
    {
        int moved = 0;
        foreach (PlacedPhysicsVolume volume in Load(extractedFolder, resource))
        {
            if (volume.Stub == null) continue;
            if (keep != null && keep.Contains(volume.Stub)) continue;
            Matrix4x4 want = volume.Volume.Transform;
            Matrix4x4 have = volume.Stub.LocalTransform;
            if ((want.Translation - have.Translation).LengthSquared() < 1e-8f
                && MathF.Abs(want.M11 - have.M11) < 1e-4f && MathF.Abs(want.M22 - have.M22) < 1e-4f
                && MathF.Abs(want.M33 - have.M33) < 1e-4f && MathF.Abs(want.M12 - have.M12) < 1e-4f
                && MathF.Abs(want.M13 - have.M13) < 1e-4f && MathF.Abs(want.M21 - have.M21) < 1e-4f
                && MathF.Abs(want.M23 - have.M23) < 1e-4f && MathF.Abs(want.M31 - have.M31) < 1e-4f
                && MathF.Abs(want.M32 - have.M32) < 1e-4f)
            {
                continue;
            }
            volume.Stub.LocalTransform = want;
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Folds a stub's SCALE into the shape it names, and leaves the stub's matrix unscaled.
    ///
    /// <para>
    /// A placement has nowhere to put a scale: neither the prefab volume nor a PhysX shape transform carries
    /// one. Dropping it silently is what made a box drawn 0.41 m thick in the editor reach 0.10 m in game —
    /// the character's arm went through the part and a crash sank into it. A primitive can absorb the scale
    /// in its own numbers instead, exactly the way a district hull absorbs one into its vertices.
    /// </para>
    /// <para>
    /// A COOKED hull cannot: its geometry is a PhysX blob and rescaling it means re-cooking, which the
    /// vendored cooker cannot do for a convex shape. Those keep the old behaviour — the scale is dropped —
    /// and <c>--probe-car-physics</c> is what says so out loud.
    /// </para>
    /// </summary>
    /// <returns>How many shapes absorbed a scale.</returns>
    public static int BakeScales(string extractedFolder, IEnumerable<FrameObjectCollision> stubs)
    {
        ArgumentNullException.ThrowIfNull(stubs);
        List<FrameObjectCollision> list = [.. stubs];
        Dictionary<ulong, ResolvedCollisionShape> shapes =
            CarCollisionShapes.Load(extractedFolder, list);

        int baked = 0;
        foreach (FrameObjectCollision stub in list)
        {
            Matrix4x4 m = stub.LocalTransform;
            var scale = new Vector3(
                new Vector3(m.M11, m.M12, m.M13).Length(),
                new Vector3(m.M21, m.M22, m.M23).Length(),
                new Vector3(m.M31, m.M32, m.M33).Length());
            if (MathF.Abs(scale.X - 1f) < 1e-4f && MathF.Abs(scale.Y - 1f) < 1e-4f
                && MathF.Abs(scale.Z - 1f) < 1e-4f)
            {
                continue;
            }
            if (scale.X <= 0f || scale.Y <= 0f || scale.Z <= 0f) continue;
            if (!shapes.TryGetValue(stub.Hash, out ResolvedCollisionShape? found)
                || found.Shape.Element is not RigidBodyElement rigid)
            {
                continue;
            }

            switch (rigid.Shape)
            {
                case RigidBodyShape.Box:
                    rigid.BoxDimensions *= scale;
                    break;
                case RigidBodyShape.Sphere:
                    // A sphere has one number, so only a uniform scale means anything; the largest is the
                    // safe reading — a shape that grew is better than one that quietly did not.
                    rigid.Radius *= MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
                    break;
                case RigidBodyShape.Capsule:
                case RigidBodyShape.Cylinder:
                    // The axis is local Z (measured — see CarCollisionShapes.AppendCapsule), so the height
                    // takes Z and the radius takes the wider of the two across it.
                    rigid.Radius *= MathF.Max(scale.X, scale.Y);
                    rigid.Height *= scale.Z;
                    break;
                default:
                    continue;   // a cooked hull cannot be rescaled without a cooker we do not have
            }

            try { AtomicFile.WriteAllBytes(found.File, found.Shape.ToBytes()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            // The matrix keeps its rotation and position and loses the scale it just gave away.
            stub.LocalTransform = WithoutScale(m);
            baked++;
        }
        return baked;
    }

    /// <summary>What an added volume was, so it can be taken back exactly.</summary>
    /// <param name="Item">The volume's own bytes — a redo puts these back rather than a look-alike.</param>
    public sealed record VolumeChange(string PrefabPath, int Part, int Volume, byte[] Item);

    /// <summary>
    /// Hangs a new collision volume off the deformable part that IS <paramref name="boneName"/>, naming the
    /// physics shape <paramref name="shapeDataHash"/>. This is the half that makes a new shape real: an
    /// ItemDesc record nothing names is carried by the archive and used by nothing.
    /// </summary>
    /// <returns>Null when this car has no deformable part for that bone — nothing is written then.</returns>
    public static VolumeChange? Add(
        string extractedFolder, string boneName, Matrix4x4 boneSpace, ulong shapeDataHash)
    {
        (PrefabFile Prefab, string Path)? opened = OpenCarPrefab(extractedFolder);
        if (opened == null) return null;
        (PrefabFile prefab, string path) = opened.Value;

        int part = prefab.FindCarPartByFrame(Fnv64.Hash(boneName ?? ""));
        if (part < 0) return null;

        int volume = prefab.AddCarVolume(part, WithoutScale(boneSpace), Vector3.One, shapeDataHash);
        if (volume < 0) return null;

        byte[] item = prefab.TakeCarVolume(part, volume) ?? [];
        prefab.PutCarVolume(part, volume, item);
        AtomicFile.WriteAllBytes(path, prefab.ToBytes());
        return new VolumeChange(path, part, volume, item);
    }

    /// <summary>
    /// Hangs a SELF-DESCRIBING volume off a deformable part — a box that names no shape and says what it is
    /// by its type alone.
    ///
    /// <para>
    /// The other half of a car's collision, and the half the toolkit could not make. A volume of type 5
    /// places an ItemDesc shape and is the body, the doors, the bumpers; a volume of type 0 IS the glass —
    /// all 527 shipped window volumes are type 0 and none of them is anything else — and type 6 is a zone,
    /// the engine bay and the snow. They carry no material field at all: the type is the identity. Until
    /// this existed, every box the editor added was type 5, so it was body collision whichever part it was
    /// hung on, and asking for one on a window changed nothing.
    /// </para>
    /// <para>
    /// Written in the space of the part this part HANGS OFF, which is where a self-describing volume lives —
    /// see <see cref="Load"/>. There is no stub frame and no gizmo handle, exactly as the shipped ones have
    /// none; it is placed by the numbers in the Prefab tab.
    /// </para>
    /// </summary>
    /// <param name="fullSize">The box's FULL size, not half — measured on 1049 shipped volumes.</param>
    /// <returns>Null when this car has no deformable part for that bone; nothing is written then.</returns>
    public static VolumeChange? AddZone(
        string extractedFolder, string boneName, Matrix4x4 inParentSpace, Vector3 fullSize, uint volumeType)
    {
        (PrefabFile Prefab, string Path)? opened = OpenCarPrefab(extractedFolder);
        if (opened == null) return null;
        (PrefabFile prefab, string path) = opened.Value;

        int part = prefab.FindCarPartByFrame(Fnv64.Hash(boneName ?? ""));
        if (part < 0) return null;

        int volume = prefab.AddCarVolume(part, WithoutScale(inParentSpace), fullSize, 0, volumeType);
        if (volume < 0) return null;

        byte[] item = prefab.TakeCarVolume(part, volume) ?? [];
        prefab.PutCarVolume(part, volume, item);
        AtomicFile.WriteAllBytes(path, prefab.ToBytes());
        return new VolumeChange(path, part, volume, item);
    }

    /// <summary>What a type-5 volume states for its own size: nothing, because the shape states it. Every
    /// shipped one carries this.</summary>
    private static readonly Vector3 ShapePlaceholderExtents = new(0.01f, 0.01f, 0.01f);

    /// <summary>What a type change did, so it can be taken back exactly.</summary>
    /// <param name="Shape">An ItemDesc record minted for the conversion, or null when none was.</param>
    public sealed record TypeChange(
        string PrefabPath, int Part, int Volume, byte[] Before, byte[] After, string? Shape);

    /// <summary>
    /// Changes what an existing volume IS — a placed physics shape, glass, or a zone.
    ///
    /// <para>
    /// Not a number to overwrite. The kinds live in different spaces: a type-5 volume is written in its own
    /// part's bone, a self-describing one in the bone of the part it hangs off. Rewriting the type alone
    /// would leave the placement meaning something else and the box would jump. So the world position is
    /// worked out first and written back into whichever space the new kind uses.
    /// </para>
    /// <para>
    /// Going away from type 5 leaves its ItemDesc record behind, inert, exactly as deleting a collision does
    /// — another volume may still name it, and an unnamed record costs a few bytes. Going TO type 5 mints a
    /// box from the volume's own extents, because a shape is what that kind is required to have.
    /// </para>
    /// </summary>
    /// <returns>Null with a reason when it cannot be done; nothing is written then.</returns>
    public static TypeChange? ChangeType(
        string extractedFolder, FrameResource resource, int part, int volume, uint newType,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(resource);
        refusal = null;

        PlacedPhysicsVolume? found = Load(extractedFolder, resource)
            .FirstOrDefault(v => v.Part == part && v.Volume.Index == volume);
        if (found == null) { refusal = "that volume is no longer there"; return null; }
        if (found.Volume.VolumeType == newType) { refusal = "it is already that"; return null; }

        FrameObjectModel? model = resource.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
        if (model == null) { refusal = "this archive has no skinned model"; return null; }

        (PrefabFile Prefab, string Path)? opened = OpenCarPrefab(extractedFolder);
        if (opened == null) { refusal = "this archive has no car prefab"; return null; }
        (PrefabFile prefab, string path) = opened.Value;

        byte[] before = prefab.TakeCarVolume(part, volume) ?? [];
        prefab.PutCarVolume(part, volume, before);
        if (before.Length == 0) { refusal = "that volume could not be read"; return null; }

        // Where it is NOW, in the world — the one thing that has to survive the change.
        //
        // A self-describing box is centred on its own placement, while a shape sits wherever its geometry
        // happens to sit around the placement's origin. So the shape's own centre is folded into the
        // placement on the way out, or a hull that leans forward would come back centred and cover the
        // wrong half of the part.
        Matrix4x4 world = found.World;
        if (found.Volume.NamesShape)
        {
            world.Translation = Vector3.Transform(ShapeCentre(found), world);
        }

        // …and the space the new kind is written in.
        int space = SpaceBoneFor(extractedFolder, model, part, newType == CarPhysicsVolume.ShapeVolumeType);
        Matrix4x4 local = space >= 0
            ? Domain.TransformMath.ComputeLocalTransform(world, model.GetJointWorldTransform(space))
            : world;

        // The size, kept as the box the user can see: a self-describing volume states a full size, while a
        // shape states its own and the volume carries the 1 cm placeholder.
        Vector3 size = found.Volume.NamesShape ? ShapeSize(found) : found.Volume.Size;

        string? mintedShape = null;
        ulong shapeHash = 0;
        if (newType == CarPhysicsVolume.ShapeVolumeType)
        {
            // A type-5 volume must name a shape, so becoming one means minting a box the size of the box that
            // was there. Reuses the builder's hash minting rather than repeating it: two records answering to
            // one data hash is a bug this codebase has already had once.
            if (!CarCollisionBuilder.MintBoxShape(
                    extractedFolder, size * 0.5f, out string? file, out shapeHash, out string? why))
            {
                refusal = why ?? "the shape could not be written";
                return null;
            }
            mintedShape = file;
        }

        if (!prefab.SetCarVolumeType(part, volume, newType, WithoutScale(local),
                newType == CarPhysicsVolume.ShapeVolumeType ? ShapePlaceholderExtents : size, shapeHash))
        {
            refusal = "the prefab would not take the change";
            if (mintedShape != null) CarCollisionBuilder.UnmintShape(extractedFolder, mintedShape);
            return null;
        }

        byte[] after = prefab.TakeCarVolume(part, volume) ?? [];
        prefab.PutCarVolume(part, volume, after);
        AtomicFile.WriteAllBytes(path, prefab.ToBytes());
        return new TypeChange(path, part, volume, before, after, mintedShape);
    }

    /// <summary>Puts a converted volume back the way it was — the undo of <see cref="ChangeType"/>.</summary>
    public static bool RestoreType(TypeChange change, bool toBefore)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Open(change.PrefabPath) is not { } prefab) return false;
        if (prefab.TakeCarVolume(change.Part, change.Volume) == null) return false;
        if (!prefab.PutCarVolume(change.Part, change.Volume, toBefore ? change.Before : change.After))
        {
            return false;
        }
        AtomicFile.WriteAllBytes(change.PrefabPath, prefab.ToBytes());
        return true;
    }

    /// <summary>The bone index whose space a volume of this kind is written in on this part, or -1.</summary>
    private static int SpaceBoneFor(string extracted, FrameObjectModel model, int part, bool namesShape)
    {
        IReadOnlyList<CarDeformPart> parts = Parts(extracted);
        if (part < 0 || part >= parts.Count) return -1;
        ulong wanted = namesShape || parts[part].ParentFrame == 0
            ? parts[part].Frame
            : parts[part].ParentFrame;

        string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray();
        for (int i = 0; i < bones.Length; i++)
        {
            if (Fnv64.Hash(bones[i]) == wanted) return i;
        }
        return -1;
    }

    /// <summary>Where a placed shape's own geometry is centred, relative to the placement's origin. Zero for
    /// a primitive, which is centred by construction; a cooked hull is not.</summary>
    private static Vector3 ShapeCentre(PlacedPhysicsVolume volume)
    {
        if (volume.Shape?.Element is not RigidBodyElement rigid) return Vector3.Zero;
        if (rigid.Shape is not (RigidBodyShape.ConvexPolyhedron or RigidBodyShape.TriangleMesh))
        {
            return Vector3.Zero;
        }
        return CarCollisionShapes.TryReadCookedBounds(rigid.CookedMesh, out Vector3 lo, out Vector3 hi)
            ? (lo + hi) * 0.5f
            : Vector3.Zero;
    }

    /// <summary>
    /// The full size of the box a placed shape occupies — what a self-describing volume would have to say to
    /// cover the same space.
    ///
    /// <para>
    /// A cooked hull answers through its own stored bounds, the same ones the overlay draws it by. Falling
    /// back to a token 20 cm here is what made a converted body hull vanish: the volume was still there,
    /// still in the right place, and a fifth of a metre across on a car five metres long.
    /// </para>
    /// </summary>
    private static Vector3 ShapeSize(PlacedPhysicsVolume volume)
    {
        if (volume.Shape?.Element is not RigidBodyElement rigid) return new Vector3(0.2f);
        switch (rigid.Shape)
        {
            case RigidBodyShape.Box:
                return rigid.BoxDimensions * 2f;
            case RigidBodyShape.Sphere:
                return new Vector3(rigid.Radius * 2f);
            case RigidBodyShape.Capsule or RigidBodyShape.Cylinder:
                return new Vector3(rigid.Radius * 2f, rigid.Radius * 2f, rigid.Height + (rigid.Radius * 2f));
            default:
                return CarCollisionShapes.TryReadCookedBounds(rigid.CookedMesh, out Vector3 lo, out Vector3 hi)
                    ? Vector3.Abs(hi - lo)
                    : new Vector3(0.2f);
        }
    }

    /// <summary>
    /// The frame whose space a self-describing volume on this part is written in — the frame of the part it
    /// hangs off, or its own when it hangs off nothing. Returns a hash, which is how the prefab names frames.
    /// </summary>
    public static ulong SpaceFrameFor(string extractedFolder, string boneName)
    {
        ulong own = Fnv64.Hash(boneName ?? "");
        foreach (CarDeformPart part in Parts(extractedFolder))
        {
            if (part.Frame == own) return part.ParentFrame != 0 ? part.ParentFrame : own;
        }
        return own;
    }

    /// <summary>
    /// Drops the collision volume a stub places, so deleting the stub actually deletes the collision.
    ///
    /// <para>
    /// A stub is not the collision — the prefab volume is, and the game reads only that. Removing the frame
    /// on its own leaves a car that still collides exactly as before while the editor shows nothing there,
    /// which is "I deleted it, it is gone from the tree, and it is still in the game".
    /// </para>
    /// <para>
    /// The SHAPE file is deliberately left behind. An ItemDesc record nothing names is inert, and deleting
    /// it would break any other volume that happens to name the same shape — the archive keeps a few bytes
    /// rather than risking that.
    /// </para>
    /// </summary>
    /// <returns>What was removed, for undo; null when this stub places no volume.</returns>
    public static VolumeChange? TakeForStub(string extractedFolder, FrameObjectCollision stub)
    {
        ArgumentNullException.ThrowIfNull(stub);
        (PrefabFile Prefab, string Path)? opened = OpenCarPrefab(extractedFolder);
        if (opened == null) return null;
        (PrefabFile prefab, string path) = opened.Value;

        if (!DataHashByFileHash(extractedFolder).TryGetValue(stub.Hash, out ulong data)) return null;

        foreach (CarDeformPart part in prefab.CarDeformParts)
        {
            foreach (CarPhysicsVolume volume in part.Volumes)
            {
                if (volume.ShapeHash != data) continue;
                byte[]? taken = prefab.TakeCarVolume(part.Index, volume.Index);
                if (taken == null) return null;
                AtomicFile.WriteAllBytes(path, prefab.ToBytes());
                return new VolumeChange(path, part.Index, volume.Index, taken);
            }
        }
        return null;
    }

    /// <summary>Takes a volume away again — the undo of <see cref="Add"/>.</summary>
    public static bool Remove(VolumeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Open(change.PrefabPath) is not { } prefab) return false;
        if (prefab.TakeCarVolume(change.Part, change.Volume) == null) return false;
        AtomicFile.WriteAllBytes(change.PrefabPath, prefab.ToBytes());
        return true;
    }

    /// <summary>Puts a removed volume back where it was — the redo of <see cref="Remove"/>.</summary>
    public static bool Restore(VolumeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Open(change.PrefabPath) is not { } prefab) return false;
        if (!prefab.PutCarVolume(change.Part, change.Volume, change.Item)) return false;
        AtomicFile.WriteAllBytes(change.PrefabPath, prefab.ToBytes());
        return true;
    }

    /// <summary>Which deformable parts this car has, for a picker: index, kind and the bone each one is.</summary>
    public static IReadOnlyList<CarDeformPart> Parts(string extractedFolder) =>
        OpenCarPrefab(extractedFolder)?.Prefab.CarDeformParts ?? [];

    /// <summary>The bone names that have a deformable part — the only bones a new shape can be given to.</summary>
    public static IReadOnlyList<string> PartBones(string extractedFolder, FrameResource resource) =>
        [.. PartChoices(extractedFolder, resource).Select(c => c.BoneName)];

    /// <summary>
    /// Every deformable part a new shape could be given to: the part, what kind of part it is, and the bone
    /// it is. Which one is chosen is not a detail — it decides what the shape BELONGS to. A shape on the body
    /// is part of the car and everything that touches the car touches it; the same shape on the bonnet
    /// belongs to the bonnet, and only meets the world once that panel is in the way or has come off.
    /// </summary>
    public static IReadOnlyList<CarPartChoice> PartChoices(string extractedFolder, FrameResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        FrameObjectModel? model = resource.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
        if (model == null) return [];
        string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray();

        var byHash = new Dictionary<ulong, int>();
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i].Length > 0) byHash.TryAdd(Fnv64.Hash(bones[i]), i);
        }

        var choices = new List<CarPartChoice>();
        foreach (CarDeformPart part in Parts(extractedFolder))
        {
            if (byHash.TryGetValue(part.Frame, out int bone))
            {
                choices.Add(new CarPartChoice(part.Index, part.Kind, bone, bones[bone]));
            }
        }
        return choices;
    }

    // ── plumbing ──

    private static (PrefabFile Prefab, string Path)? OpenCarPrefab(string extractedFolder)
    {
        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extractedFolder).GetFiles("PREFAB"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return null; }

        foreach (string file in files)
        {
            if (Open(file) is { Car: not null } prefab) return (prefab, file);
        }
        return null;
    }

    private static PrefabFile? Open(string path)
    {
        try { return PrefabFile.Load(path); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return null; }
    }

    private static Dictionary<ulong, (ItemDescFile Shape, string File)> ShapesByDataHash(string extracted)
    {
        var byHash = new Dictionary<ulong, (ItemDescFile, string)>();
        foreach ((ItemDescFile shape, string file) in EachShape(extracted))
        {
            if (shape.Element != null) byHash.TryAdd(shape.Element.DataHash, (shape, file));
        }
        return byHash;
    }

    private static Dictionary<ulong, ulong> DataHashByFileHash(string extracted)
    {
        var map = new Dictionary<ulong, ulong>();
        foreach ((ItemDescFile shape, string _) in EachShape(extracted))
        {
            if (shape.Element != null) map.TryAdd(shape.Hash, shape.Element.DataHash);
        }
        return map;
    }

    private static IEnumerable<(ItemDescFile Shape, string File)> EachShape(string extracted)
    {
        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extracted).GetFiles("ItemDesc"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { yield break; }

        foreach (string file in files)
        {
            ItemDescFile? shape = null;
            try { shape = ItemDescFile.Load(file); }
            catch (Exception) { /* a shape this library cannot read has nothing to contribute */ }
            if (shape != null) yield return (shape, file);
        }
    }

    /// <summary>
    /// The same placement with its basis renormalized. A frame may be scaled; a PhysX shape transform has
    /// nowhere to put a scale, so passing one on would write a matrix the engine cannot honour.
    /// </summary>
    private static Matrix4x4 WithoutScale(Matrix4x4 m)
    {
        var x = new Vector3(m.M11, m.M12, m.M13);
        var y = new Vector3(m.M21, m.M22, m.M23);
        var z = new Vector3(m.M31, m.M32, m.M33);
        x = x.LengthSquared() > 1e-12f ? Vector3.Normalize(x) : Vector3.UnitX;
        y = y.LengthSquared() > 1e-12f ? Vector3.Normalize(y) : Vector3.UnitY;
        z = z.LengthSquared() > 1e-12f ? Vector3.Normalize(z) : Vector3.UnitZ;
        return new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            m.M41, m.M42, m.M43, 1f);
    }
}
