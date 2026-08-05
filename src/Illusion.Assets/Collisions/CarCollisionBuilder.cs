using System.Numerics;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.ItemDesc;

namespace Illusion.Assets.Collisions;

/// <summary>What an added box collision is made of, so the caller can undo it as one thing.</summary>
/// <param name="Frame">The stub that was added to the frame graph.</param>
/// <param name="Shape">The physics shape it names.</param>
/// <param name="ShapeFile">Full path of the .ids written into the extracted folder.</param>
/// <param name="Bone">Index of the bone the stub hangs off.</param>
/// <param name="Volume">The prefab collision volume that makes the shape real — without it the archive
/// carries a shape nothing uses.</param>
public sealed record AddedCollisionBox(
    FrameObjectCollision Frame, ItemDescFile Shape, string ShapeFile, int Bone,
    CarPhysicsVolumes.VolumeChange Volume);

/// <summary>
/// Gives a car part something to be shot at.
///
/// <para>
/// A car's mesh is never what a bullet hits. Its collision is a handful of physics shapes hung off its bones:
/// a <see cref="FrameObjectCollision"/> stub says WHERE, and an <b>ItemDesc</b> record says WHAT. Geometry added
/// to the body has neither, so shots pass straight through it — which is the whole reason this exists.
/// </para>
/// <para>
/// THREE things, not two, and the third is the one that took a wrong turn to find. The shape and the stub are
/// not enough: the game reads a car's physics out of the PREFAB, where every deformable part carries the
/// collision volumes that place its shapes (<see cref="CarPhysicsVolumes"/>). A shape and a stub with no
/// volume produce exactly what was reported — geometry a player walks through and bullets pass through — and
/// that is why a bone without a deformable part is refused here rather than served.
/// </para>
/// <para>
/// Measured on the shipped cars (<c>--probe-car-collision</c>, <c>--probe-car-physics</c>): 106 car archives
/// carry no <c>.col</c> at all and answer 1174 of 1174 stubs out of their own ItemDesc entries, matched on the
/// record's FILE hash, while 1097 of 1097 prefab volumes name the same records by their DATA hash. Every one
/// of those shapes has the IDENTITY as its own transform, so the placement is entirely in the two matrices.
/// Boxes are not a workaround either — 232 of the volumes place one.
/// </para>
/// <para>
/// A box is deliberate: it is described by three numbers, while a convex hull is a PhysX-cooked blob and the
/// vendored cooker knows exactly one verb, <c>-CookTriangleMesh</c>. Convex cooking is not available at all,
/// so a hull cannot be minted from geometry today.
/// </para>
/// </summary>
public static class CarCollisionBuilder
{
    /// <summary>Manifest version the shipped ItemDesc entries carry.</summary>
    private const int ItemDescVersion = 3;

    /// <summary>
    /// Adds a primitive collision shape to <paramref name="model"/>'s bone and wires it up: a new ItemDesc
    /// record written into the extracted folder AND announced in its manifest, a collision volume in the
    /// car's prefab (the half the game reads), plus a stub frame naming it, attached to the bone so it moves
    /// with the part.
    /// </summary>
    /// <param name="kind">Box, Sphere or Capsule. A convex hull is not on the list and cannot be: it is a
    /// PhysX-cooked blob and the vendored cooker knows exactly one verb, <c>-CookTriangleMesh</c>. The
    /// primitives are pure numbers, so they need no cooker at all.</param>
    /// <param name="size">Box: half-extents. Sphere: X is the radius. Capsule: X is the radius and Y the
    /// length of the straight section, which lies along the shape's local Z.</param>
    /// <param name="localInBoneSpace">Where the shape sits relative to the bone.</param>
    /// <param name="surface">
    /// Which physics surface the shape names, as a <c>MaterialsPhysics.tbl</c> INDEX — or null for "as the
    /// game ships them", which is the raw 0 every one of the 1174 shipped car shapes carries.
    /// <para>
    /// A table index and not a raw value, deliberately. The field on disk is a PhysX slot id, offset from the
    /// table by <see cref="Domain.CollisionMaterialCatalog.RawToTableBias"/> — the same offset the world's
    /// collision uses. Writing the table index straight in is what made the first in-game test meaningless:
    /// asking for 28 (breakable glass) wrote a 28 the game read as 30, bulletproof glass, and the result
    /// looked like "the field does nothing". Taking the index and applying the bias here means a caller
    /// cannot get it wrong.
    /// </para>
    /// </param>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is written then.</returns>
    public static AddedCollisionBox? AddShape(
        FrameObjectModel model, int bone, string name, RigidBodyShape kind, Vector3 size,
        Matrix4x4 localInBoneSpace, string extractedFolder, out string? refusal, int? surface = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        refusal = null;

        if (string.IsNullOrWhiteSpace(name)) { refusal = "the shape needs a name"; return null; }
        if (!Describes(kind, size, out string? sizeRefusal)) { refusal = sizeRefusal; return null; }

        HashName[] boneNames;
        try { boneNames = model.GetSkeletonObject().BoneNames ?? []; }
        catch (Exception) { refusal = "the model's rig cannot be read"; return null; }
        int boneCount = boneNames.Length;
        if (bone < 0 || bone >= boneCount) { refusal = "that bone is not part of this model's rig"; return null; }
        // The joint index rides as a single byte, so a rig may be longer than a stub can point into.
        if (bone > byte.MaxValue) { refusal = "that bone is past the 255th, which an attachment cannot name"; return null; }

        FrameResource resource = model.Resource;
        if (resource.FrameObjects.Values.OfType<FrameObjectBase>()
            .Any(f => string.Equals(f.Name.ToString(), name, StringComparison.Ordinal)))
        {
            refusal = $"the archive already has a frame called '{name}'";
            return null;
        }

        SdsManifest manifest;
        try { manifest = SdsManifest.Load(extractedFolder); }
        catch (Exception ex) when (ex is IOException or SdsFormatException)
        {
            refusal = "the archive's manifest cannot be read: " + ex.Message;
            return null;
        }

        // A stub the archive already has, to copy the wiring from rather than invent it: how a collision frame
        // hangs in the graph is the archive's own convention, and a car always ships several.
        FrameObjectCollision? donor = FindDonorStub(model, resource);
        if (donor == null)
        {
            refusal = "this archive has no collision frame to copy the wiring from";
            return null;
        }

        // The bone has to BE a deformable part, because that is what the prefab hangs a collision volume off
        // and the volume is what the game reads. Refusing here is the point: writing the shape and the stub
        // alone produces geometry that looks armoured and is not.
        string boneName = boneNames[bone].ToString() ?? "";
        IReadOnlyList<string> partBones = CarPhysicsVolumes.PartBones(extractedFolder, resource);
        if (!partBones.Contains(boneName, StringComparer.Ordinal))
        {
            refusal = partBones.Count == 0
                ? "this archive has no car prefab, so there is nowhere to declare a collision volume"
                : $"\"{boneName}\" is not a deformable part of this car, and only a deformable part can carry "
                    + "collision. Bones that can: " + string.Join(", ", partBones);
            return null;
        }

        // BOTH hashes of every shape already here, not just the file hash.
        //
        // A shape carries two ids and they are looked up by different readers — the stub finds it by the FILE
        // hash, the prefab volume by the DATA hash — and both are minted from the frame's NAME. Deleting a
        // collision leaves its ItemDesc record behind on purpose (an unnamed record is inert, and another
        // volume may still name it), so adding a shape with the same name again used to salt the file hash
        // clear of the orphan and hand out the orphan's DATA hash unchanged. Two records then answered to one
        // data hash, the new volume resolved to the ORPHAN, and its stub could not be found from it: the
        // shape drew at the prefab placement instead of at its stub, so it ignored the scale and stood still
        // while the gizmo moved.
        IReadOnlyList<string> existing = manifest.GetFiles("ItemDesc");
        var takenHashes = new HashSet<ulong>();
        foreach (string file in existing)
        {
            try
            {
                ItemDescFile already = ItemDescFile.Load(file);
                takenHashes.Add(already.Hash);
                if (already.Element != null) takenHashes.Add(already.Element.DataHash);
            }
            catch (Exception) { /* a shape we cannot read still must not have its hash reused */ }
        }

        ulong hash = MintHash(name, takenHashes);
        ulong dataHash = MintHash(name + "#data", takenHashes);
        var shape = new ItemDescFile
        {
            Hash = hash,
            Type = ItemDescType.RigidBody,
            SubType = (byte)kind,
            Element = new RigidBodyElement
            {
                // Two hashes, two readers. The stub finds the record by the FILE hash (1174 of 1174); the
                // prefab volume that actually places it names this one (1097 of 1097). Both only have to be
                // unique inside the archive.
                DataHash = dataHash,
                Shape = kind,
                // Raw slot id, not the table index — see the parameter's own note.
                MaterialId = surface is { } table
                    ? (ushort)(table + Domain.CollisionMaterialCatalog.RawToTableBias)
                    : (ushort)0,
                Layer = -1,          // every shape on every shipped car
                Transform = Identity3x4(),
                BoxDimensions = kind == RigidBodyShape.Box ? size : default,
                Radius = kind == RigidBodyShape.Box ? 0f : size.X,
                Height = kind == RigidBodyShape.Capsule ? size.Y : 0f,
            },
        };

        string fileName = NextShapeFileName(manifest, extractedFolder);
        string path = Path.Combine(extractedFolder, fileName);
        try
        {
            File.WriteAllBytes(path, shape.ToBytes());
            // Packing builds the archive from the MANIFEST, never from the folder: a file written and not
            // announced is silently dropped, and the archive then names a resource nothing carries.
            manifest.AddEntry("ItemDesc", fileName, ItemDescVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            refusal = "the shape could not be written: " + ex.Message;
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* best effort */ }
            return null;
        }

        // The half the game reads. Written before the frame graph is touched so a refusal here leaves nothing
        // half-wired: without a volume the shape is inert, and an inert shape is the bug this exists to fix.
        CarPhysicsVolumes.VolumeChange? volume =
            CarPhysicsVolumes.Add(extractedFolder, boneName, localInBoneSpace, dataHash);
        if (volume == null)
        {
            refusal = "the car's prefab would not take a collision volume for this bone";
            RollBackShape(extractedFolder, path);
            return null;
        }

        var stub = new FrameObjectCollision(resource)
        {
            Name = new HashName(name),
            Hash = hash,
            LocalTransform = localInBoneSpace,
        };
        // The graph wiring, taken from the donor: how a collision frame hangs is the archive's own convention
        // (ParentIndex1 cascades the transform, ParentIndex2 anchors it to a scene), and getting the two
        // backwards is what makes an attachment fly off to the model's origin.
        stub.SetParent(ParentInfo.ParentType.ParentIndex1, donor.Parent);
        stub.SetParent(ParentInfo.ParentType.ParentIndex2, donor.Root);
        stub.IsOnFrameTable = donor.IsOnFrameTable;
        stub.FrameNameTableFlags = donor.FrameNameTableFlags;
        resource.FrameObjects.Add(stub.RefID, stub);

        // Hanging it off the bone is what makes it move with the part.
        model.AttachToJoint(stub, (byte)bone);

        return new AddedCollisionBox(stub, shape, path, bone, volume);
    }

    /// <summary>
    /// Writes a BOX shape record into the archive and announces it, without any of the wiring — for a caller
    /// that already has a volume and only needs something for it to name (converting a self-describing volume
    /// into a placed one).
    /// </summary>
    /// <param name="halfSize">Half-extents, the way a box record states itself.</param>
    /// <param name="dataHash">The record's DATA hash, which is what a prefab volume names it by.</param>
    internal static bool MintBoxShape(
        string extractedFolder, Vector3 halfSize, out string? file, out ulong dataHash, out string? refusal)
    {
        file = null;
        dataHash = 0;
        refusal = null;

        if (!Describes(RigidBodyShape.Box, halfSize, out refusal)) return false;

        SdsManifest manifest;
        try { manifest = SdsManifest.Load(extractedFolder); }
        catch (Exception ex) when (ex is IOException or SdsFormatException)
        {
            refusal = "the archive's manifest cannot be read: " + ex.Message;
            return false;
        }

        var taken = new HashSet<ulong>();
        foreach (string existing in manifest.GetFiles("ItemDesc"))
        {
            try
            {
                ItemDescFile already = ItemDescFile.Load(existing);
                taken.Add(already.Hash);
                if (already.Element != null) taken.Add(already.Element.DataHash);
            }
            catch (Exception) { /* a shape we cannot read still must not have its hash reused */ }
        }

        string stem = "converted_" + taken.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ulong hash = MintHash(stem, taken);
        dataHash = MintHash(stem + "#data", taken);
        var shape = new ItemDescFile
        {
            Hash = hash,
            Type = ItemDescType.RigidBody,
            SubType = (byte)RigidBodyShape.Box,
            Element = new RigidBodyElement
            {
                DataHash = dataHash,
                Shape = RigidBodyShape.Box,
                MaterialId = 0,
                Layer = -1,
                Transform = Identity3x4(),
                BoxDimensions = halfSize,
            },
        };

        string name = NextShapeFileName(manifest, extractedFolder);
        string path = Path.Combine(extractedFolder, name);
        try
        {
            File.WriteAllBytes(path, shape.ToBytes());
            manifest.AddEntry("ItemDesc", name, ItemDescVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            refusal = "the shape could not be written: " + ex.Message;
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* best effort */ }
            return false;
        }

        file = path;
        return true;
    }

    /// <summary>Unwrites a shape minted by <see cref="MintBoxShape"/> that could not be used after all.</summary>
    internal static void UnmintShape(string extractedFolder, string path) =>
        RollBackShape(extractedFolder, path);

    /// <summary>Unwrites a shape that was written and then could not be used.</summary>
    private static void RollBackShape(string extractedFolder, string path)
    {
        try { SdsManifest.Load(extractedFolder).RemoveEntry(Path.GetFileName(path)); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { /* reported by the next Build */ }
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* best effort */ }
    }

    /// <summary>
    /// Undoes <see cref="AddShape"/>: the stub leaves the graph and the bone, the shape file goes, and the
    /// manifest stops naming it.
    /// <para>
    /// All three, because they are one thing. Leaving the manifest entry behind was assumed harmless — the
    /// packer would surely skip a file that is not there — and it is not: Build fails outright with "Could
    /// not find file …ItemDesc_0.ids", and the archive cannot be packed again until the entry goes.
    /// </para>
    /// </summary>
    public static void Remove(FrameObjectModel model, AddedCollisionBox added)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(added);

        try
        {
            string? folder = Path.GetDirectoryName(added.ShapeFile);
            if (folder != null)
            {
                SdsManifest.Load(folder).RemoveEntry(Path.GetFileName(added.ShapeFile));
            }
        }
        catch (Exception ex) when (ex is IOException or SdsFormatException)
        {
            // The file still goes below; a manifest that cannot be rewritten is reported by the next Build.
        }

        // The prefab volume goes with it: a volume left naming a shape that no longer exists is a car whose
        // physics the game builds out of a missing record.
        CarPhysicsVolumes.Remove(added.Volume);

        model.DetachFromJoints(added.Frame);
        added.Frame.SetParent(ParentInfo.ParentType.ParentIndex1, null);
        added.Frame.SetParent(ParentInfo.ParentType.ParentIndex2, null);
        model.Resource.FrameObjects.Remove(added.Frame.RefID);
        try { if (File.Exists(added.ShapeFile)) File.Delete(added.ShapeFile); }
        catch (IOException) { /* the shape is orphaned, not fatal */ }
    }

    /// <summary>
    /// Puts a removed shape back on disk and back in the manifest — what a REDO needs. Re-running
    /// <see cref="AddShape"/> would mint a second hash and a second file, so an undo/redo pair would leave the
    /// archive carrying two shapes where the user made one.
    /// </summary>
    public static void Restore(AddedCollisionBox added, byte[] shapeBytes)
    {
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(shapeBytes);
        try
        {
            if (!File.Exists(added.ShapeFile)) File.WriteAllBytes(added.ShapeFile, shapeBytes);
            string? folder = Path.GetDirectoryName(added.ShapeFile);
            if (folder != null)
            {
                SdsManifest.Load(folder)
                    .AddEntry("ItemDesc", Path.GetFileName(added.ShapeFile), ItemDescVersion);
            }
            CarPhysicsVolumes.Restore(added.Volume);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SdsFormatException)
        {
            // Reported by the next Build rather than thrown into an undo/redo step.
        }
    }

    /// <summary>Whether the numbers make sense for the kind of shape asked for.</summary>
    private static bool Describes(RigidBodyShape kind, Vector3 size, out string? refusal)
    {
        refusal = null;
        switch (kind)
        {
            case RigidBodyShape.Box when size.X > 0f && size.Y > 0f && size.Z > 0f:
                return true;
            case RigidBodyShape.Box:
                refusal = "a box needs a positive size on every axis";
                return false;
            case RigidBodyShape.Sphere when size.X > 0f:
                return true;
            case RigidBodyShape.Sphere:
                refusal = "a sphere needs a positive radius";
                return false;
            case RigidBodyShape.Capsule when size.X > 0f && size.Y > 0f:
                return true;
            case RigidBodyShape.Capsule:
                refusal = "a capsule needs a positive radius and length";
                return false;
            default:
                refusal = $"{kind} cannot be created — only a box, a sphere or a capsule is pure numbers";
                return false;
        }
    }

    /// <summary>A collision stub already on this model, preferring one hung off a bone.</summary>
    private static FrameObjectCollision? FindDonorStub(FrameObjectModel model, FrameResource resource)
    {
        foreach (FrameObjectModel.AttachmentReference reference in model.AttachmentReferences ?? [])
        {
            if (reference.Attachment is FrameObjectCollision onBone) return onBone;
        }
        return resource.FrameObjects.Values.OfType<FrameObjectCollision>().FirstOrDefault();
    }

    /// <summary>A 64-bit id nothing in the archive is using yet.</summary>
    private static ulong MintHash(string seed, HashSet<ulong> taken)
    {
        for (int salt = 0; ; salt++)
        {
            ulong candidate = Fnv64.Hash(salt == 0
                ? seed
                : seed + "#" + salt.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (candidate != 0 && taken.Add(candidate)) return candidate;
        }
    }

    /// <summary>A shape file name the manifest is not already using.</summary>
    private static string NextShapeFileName(SdsManifest manifest, string folder)
    {
        for (int index = 0; ; index++)
        {
            string name = $"ItemDesc_{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}.ids";
            if (!manifest.HasFile(name) && !File.Exists(Path.Combine(folder, name))) return name;
        }
    }

    /// <summary>The 3x4 identity — what every shipped shape carries, because the frame is what places it.</summary>
    private static float[] Identity3x4() =>
        [1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f];
}
