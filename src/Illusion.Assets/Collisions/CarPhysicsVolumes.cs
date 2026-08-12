using System.Numerics;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.ItemDesc;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Collisions;

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
/// A car's real physics, READ where the game actually reads it: the collision volumes the PREFAB hangs off
/// each deformable part.
///
/// <para>
/// A reader, and only a reader. It used to write these volumes too, and that made it one of four modules
/// that reached the prefab on their own — the seam ticket 13 closes. Everything that changes a car's
/// collision now goes through <see cref="Cars.Car"/>, which is the one path from an intent to bytes; what is
/// left here is what the overlay, the space conversion and the pickers need to SHOW one.
/// </para>
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

    /// <summary>Which deformable parts this car has — index, kind and the bone each one is.</summary>
    public static IReadOnlyList<CarDeformPart> Parts(string extractedFolder) =>
        OpenCarPrefab(extractedFolder)?.Prefab.CarDeformParts ?? [];

    // ── plumbing ──

    /// <summary>
    /// The archive's first prefab that holds a car, for a caller that only wants to READ it — what a probe
    /// consults to check what the aggregate wrote. There is no writing counterpart on purpose.
    /// </summary>
    public static PrefabFile? OpenFirst(string extractedFolder)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedFolder);
        return OpenCarPrefab(extractedFolder)?.Prefab;
    }

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
}
