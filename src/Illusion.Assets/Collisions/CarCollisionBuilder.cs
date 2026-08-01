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
public sealed record AddedCollisionBox(
    FrameObjectCollision Frame, ItemDescFile Shape, string ShapeFile, int Bone);

/// <summary>
/// Gives a car part something to be shot at.
///
/// <para>
/// A car's mesh is never what a bullet hits. Its collision is a handful of physics shapes hung off its bones:
/// a <see cref="FrameObjectCollision"/> stub says WHERE, and an <b>ItemDesc</b> record says WHAT. Geometry added
/// to the body has neither, so shots pass straight through it — which is the whole reason this exists.
/// </para>
/// <para>
/// Measured on the shipped cars (<c>--probe-car-collision</c>), which is what makes the shape below the right
/// one to write: 106 car archives carry no <c>.col</c> at all and answer 1174 of 1174 stubs out of their own
/// ItemDesc entries, matched on the record's FILE hash; every one of those 1174 shapes has the IDENTITY as its
/// own transform, so the stub's frame is the only thing that places it. Boxes are not a workaround either —
/// the cars ship 307 of them next to 599 convex hulls and 253 capsules.
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
    /// Adds a box-shaped collision shape to <paramref name="model"/>'s bone and wires it up: a new ItemDesc
    /// record written into the extracted folder AND announced in its manifest, plus a stub frame naming it,
    /// attached to the bone so it moves with the part.
    /// </summary>
    /// <param name="dimensions">Box size, in the same units as the frame transforms.</param>
    /// <param name="localInBoneSpace">Where the box sits relative to the bone.</param>
    /// <returns>Null with a <paramref name="refusal"/> when it cannot be done; nothing is written then.</returns>
    public static AddedCollisionBox? AddBox(
        FrameObjectModel model, int bone, string name, Vector3 dimensions,
        Matrix4x4 localInBoneSpace, string extractedFolder, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(model);
        refusal = null;

        if (string.IsNullOrWhiteSpace(name)) { refusal = "the shape needs a name"; return null; }
        if (dimensions.X <= 0f || dimensions.Y <= 0f || dimensions.Z <= 0f)
        {
            refusal = "a box needs a positive size on every axis";
            return null;
        }

        int boneCount;
        try { boneCount = model.GetSkeletonObject().BoneNames?.Length ?? 0; }
        catch (Exception) { refusal = "the model's rig cannot be read"; return null; }
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

        IReadOnlyList<string> existing = manifest.GetFiles("ItemDesc");
        var takenHashes = new HashSet<ulong>();
        foreach (string file in existing)
        {
            try { takenHashes.Add(ItemDescFile.Load(file).Hash); }
            catch (Exception) { /* a shape we cannot read still must not have its hash reused */ }
        }

        ulong hash = MintHash(name, takenHashes);
        var shape = new ItemDescFile
        {
            Hash = hash,
            Type = ItemDescType.RigidBody,
            SubType = (byte)RigidBodyShape.Box,
            Element = new RigidBodyElement
            {
                // Every shipped shape carries a data hash of its own, distinct from the file hash and from
                // anything derivable — the lookup that matters is the FILE hash (1174 of 1174), so this only
                // has to be unique.
                DataHash = MintHash(name + "#data", takenHashes),
                Shape = RigidBodyShape.Box,
                MaterialId = 0,
                Layer = -1,          // every shape on every shipped car
                Transform = Identity3x4(),
                BoxDimensions = dimensions,
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

        return new AddedCollisionBox(stub, shape, path, bone);
    }

    /// <summary>
    /// Undoes <see cref="AddBox"/>: the stub leaves the graph and the bone, the shape file goes, and the
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

        model.DetachFromJoints(added.Frame);
        added.Frame.SetParent(ParentInfo.ParentType.ParentIndex1, null);
        added.Frame.SetParent(ParentInfo.ParentType.ParentIndex2, null);
        model.Resource.FrameObjects.Remove(added.Frame.RefID);
        try { if (File.Exists(added.ShapeFile)) File.Delete(added.ShapeFile); }
        catch (IOException) { /* the shape is orphaned, not fatal */ }
    }

    /// <summary>
    /// Puts a removed shape back on disk and back in the manifest — what a REDO needs. Re-running
    /// <see cref="AddBox"/> would mint a second hash and a second file, so an undo/redo pair would leave the
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SdsFormatException)
        {
            // Reported by the next Build rather than thrown into an undo/redo step.
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
