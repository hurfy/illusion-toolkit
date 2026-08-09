using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Bridge;
using Illusion.Assets.Collisions;
using Illusion.Bridge.Payload;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.ItemDesc;
using Illusion.Formats.Native.Model;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// WHAT a car is physically made of — all three candidate layers side by side, measured rather than argued
/// about. Moving an ItemDesc hull was observed to change nothing in game, so "the hulls are the body" is not
/// something this may assume; the point of this probe is to say which layer carries a car's collision and
/// which fields of it place a shape.
/// <para>
/// The three candidates: the ItemDesc shapes named by <c>FrameObjectCollision</c> stubs, the collision
/// volumes the PREFAB hangs off each deformable part, and the hit boxes the skinned model stores per mesh
/// split. Reads only; nothing is written. Output: %TEMP%\illusion_car_physics.txt
/// </para>
/// </summary>
internal static class CarPhysicsProbes
{
    internal static void RunCarPhysicsProbe(string focus, string? reference = null)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_physics.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");

            DumpOneCar(sb, folder, focus, Check);
            Floaters(sb, folder, focus, Check);
            Delivery(sb, folder, focus, Check);
            if (reference != null) CompareWithStock(sb, folder, focus, reference);
            RoundTrip(sb, folder, focus, Check);
            SecondInfluence(sb, folder, focus, Check);
            CapsuleAxis(sb, folder, Check);
            Census(sb, folder, Check, focus);
            LodCoverage(sb, folder, Check);
            Handles(sb, folder, Check);
            Stubless(sb, folder, Check);
            Facing(sb, folder, Check);
            Extents(sb, folder, Check);
            Purpose(sb, folder, Check);
            sb.Insert(0, $"CAR PHYSICS PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "CAR PHYSICS PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // ── one car, in full ──

    private static void DumpOneCar(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var sds = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!sds.Exists) { sb.AppendLine($"no such archive: {focus}"); return; }
        string extracted = MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
        {
            sb.AppendLine($"{focus} is not extracted — open it in the app once"); return;
        }

        FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
        if (fr?.FrameObjects == null) { sb.AppendLine("no frame objects"); return; }
        FrameObjectModel? model = fr.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
        if (model == null) { sb.AppendLine("no skinned model"); return; }

        string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "?").ToArray();
        var boneByHash = new Dictionary<ulong, string>();
        foreach (string bone in bones) boneByHash.TryAdd(Fnv64.Hash(bone), bone);
        foreach (FrameObjectBase any in fr.FrameObjects.Values)
        {
            string? name = any.Name?.ToString();
            if (name != null) boneByHash.TryAdd(Fnv64.Hash(name), name);
        }

        sb.AppendLine($"════ {focus} ════");
        sb.AppendLine($"model \"{model.Name}\", {bones.Length} bones, "
            + $"{fr.FrameObjects.Values.OfType<FrameObjectCollision>().Count()} collision stubs, "
            + $"{model.HitBoxes?.Length ?? 0} hit boxes, {model.BlendMeshSplits?.Length ?? 0} splits");

        // ── A. the ItemDesc layer: which stub, which bone, which shape, and WHERE ──
        Dictionary<ulong, ResolvedCollisionShape> shapes = CarCollisionShapes.Load(
            extracted, fr.FrameObjects.Values.OfType<FrameObjectCollision>());

        sb.AppendLine("\n── A. ItemDesc shapes, as the frame graph places them ──");
        sb.AppendLine($"    {"bone",-14} {"stub",-32} {"stub local T",-24} {"local rot",-9} "
            + $"{"shape",-8} {"local bounds (m)",-34} world centre");

        int stubsOnBones = 0, identityLocal = 0, identityRot = 0, scaled = 0;
        var jointOf = new Dictionary<FrameObjectBase, int>();
        foreach (FrameObjectModel.AttachmentReference r in model.AttachmentReferences ?? [])
        {
            if (r.Attachment != null) jointOf[r.Attachment] = r.JointIndex;
        }

        foreach (FrameObjectCollision stub in fr.FrameObjects.Values.OfType<FrameObjectCollision>()
                     .OrderBy(s => jointOf.TryGetValue(s, out int j) ? j : 999))
        {
            bool onBone = jointOf.TryGetValue(stub, out int joint);
            if (onBone) stubsOnBones++;
            string bone = onBone && joint < bones.Length ? bones[joint] : "(not on a bone)";

            Vector3 t = stub.LocalTransform.Translation;
            bool tZero = t.Length() < 1e-4f;
            bool rotId = IsRotationIdentity(stub.LocalTransform);
            if (tZero && rotId) identityLocal++;
            if (rotId) identityRot++;

            string shapeText = "MISSING", boundsText = "", centreText = "";
            if (shapes.TryGetValue(stub.Hash, out ResolvedCollisionShape? found)
                && found.Shape.Element is RigidBodyElement rigid)
            {
                shapeText = rigid.Shape.ToString();
                if (TryLocalBounds(rigid, out Vector3 lo, out Vector3 hi))
                {
                    boundsText = $"{lo.X,6:F2}{lo.Y,6:F2}{lo.Z,6:F2} ..{hi.X,6:F2}{hi.Y,6:F2}{hi.Z,6:F2}";
                    Vector3 c = Vector3.Transform((lo + hi) * 0.5f, stub.WorldTransform);
                    centreText = $"{c.X,7:F2}{c.Y,7:F2}{c.Z,7:F2}";
                }
            }

            // The SCALE of the stub's own matrix. It is the one part of a placement the prefab cannot carry
            // — a PhysX shape transform has nowhere to put it — so a scaled stub is drawn one size in the
            // editor and used at another in game, which is exactly "I set it exactly and it is not that".
            Matrix4x4 m = stub.LocalTransform;
            var scale = new Vector3(
                new Vector3(m.M11, m.M12, m.M13).Length(),
                new Vector3(m.M21, m.M22, m.M23).Length(),
                new Vector3(m.M31, m.M32, m.M33).Length());
            bool unitScale = MathF.Abs(scale.X - 1f) < 1e-3f && MathF.Abs(scale.Y - 1f) < 1e-3f
                && MathF.Abs(scale.Z - 1f) < 1e-3f;
            if (!unitScale) scaled++;

            sb.AppendLine($"    {bone,-14} {stub.Name,-32} "
                + $"{t.X,7:F3}{t.Y,7:F3}{t.Z,7:F3}  {(rotId ? "identity" : "ROTATED "),-9} "
                + $"{(unitScale ? "1:1" : $"SCALED {scale.X:F2}/{scale.Y:F2}/{scale.Z:F2}"),-22} "
                + $"{shapeText,-8} {boundsText,-34} {centreText}");
        }

        // State of THIS archive, not a fault in the toolkit: a stub scaled before scale-baking existed is
        // still carrying it. Opening and saving the car folds it into the shape and this goes quiet.
        check("no collision stub is still carrying a scale the shape has not absorbed",
            scaled == 0, $"{scaled} stubs are scaled — open and save this car and the scale moves into "
                + "the shape; until then the game uses them UNSCALED");
        check("every collision stub on this car resolves to a shape",
            shapes.Count == fr.FrameObjects.Values.OfType<FrameObjectCollision>().Count(),
            $"{shapes.Count} resolved");

        // The discriminator the "moving a hull did nothing" result turns on. If a stub's own matrix is the
        // identity on every shipped car, then a placement read from the BONE and a placement read from the
        // STUB are indistinguishable in shipped data — and moving the stub would be expected to do nothing.
        sb.AppendLine($"\n    stubs whose local transform is exactly the identity: {identityLocal} of "
            + $"{fr.FrameObjects.Values.OfType<FrameObjectCollision>().Count()} "
            + $"({identityRot} have no rotation)");

        // Which stubs are standing somewhere the game does not put their shape. A moved stub used to be a
        // silent no-op, so an archive edited before this was understood carries the difference.
        IReadOnlyList<PlacedPhysicsVolume> placed = CarPhysicsVolumes.Load(extracted, fr);
        var adrift = placed
            .Where(v => v.Stub != null
                && (v.Volume.Transform.Translation - v.Stub.LocalTransform.Translation).Length() > 1e-3f)
            .ToList();
        sb.AppendLine($"    stubs standing away from the shape the game places: {adrift.Count}");
        foreach (PlacedPhysicsVolume one in adrift)
        {
            sb.AppendLine($"        {one.Stub!.Name,-32} frame {one.Stub.LocalTransform.Translation} "
                + $"vs prefab {one.Volume.Transform.Translation}");
        }

        // ── B. the PREFAB layer: deformable parts and the volumes hung off them ──
        DumpPrefab(sb, extracted, boneByHash, check);

        // ── C. the hit boxes the skinned model carries ──
        DumpHitBoxes(sb, model, bones);

        // ── D. what the visual body actually spans, to compare all of the above against ──
        DumpBodyExtent(sb, fr, model);

        // ── E. every frame in the archive, so a part someone added can be found by eye ──
        sb.AppendLine("\n── E. the archive's frames ──");
        foreach (FrameObjectBase frame in fr.FrameObjects.Values)
        {
            string kind = frame.GetType().Name.Replace("FrameObject", "", StringComparison.Ordinal);
            string joint = jointOf.TryGetValue(frame, out int j)
                ? $"on bone {(j < bones.Length ? bones[j] : j.ToString())}" : "";
            Vector3 t = frame.WorldTransform.Translation;
            string bounds = frame is FrameObjectSingleMesh mesh
                ? $"  bounds {mesh.Boundings.Min.X,6:F2}{mesh.Boundings.Min.Y,6:F2}{mesh.Boundings.Min.Z,6:F2}"
                    + $" ..{mesh.Boundings.Max.X,6:F2}{mesh.Boundings.Max.Y,6:F2}{mesh.Boundings.Max.Z,6:F2}"
                : "";
            sb.AppendLine($"    {kind,-12} {frame.Name,-32} world{t.X,7:F2}{t.Y,7:F2}{t.Z,7:F2} "
                + $"{joint,-22}{bounds}");
        }
        // The rig as a TREE. A vertex group only tells half the story: weighting something to deform_bumperFL
        // still carries it with bumperF, because that bone hangs off it. "I unassigned the bumper and it
        // still moves with the bumper" is that, and the bone list alone cannot show it.
        sb.AppendLine("\n── F. the rig, parent by parent ──");
        byte[] parents = model.GetSkeletonHierarchyObject().ParentIndices ?? [];
        for (int i = 0; i < bones.Length; i++)
        {
            int parent = i < parents.Length ? parents[i] : -1;
            string owner = parent >= 0 && parent < bones.Length && parent != i ? bones[parent] : "—";
            sb.AppendLine($"    {i,3} {bones[i],-24} under {owner}");
        }
    }

    private static void DumpPrefab(
        StringBuilder sb, string extracted, Dictionary<ulong, string> names, Action<string, bool, string> check)
    {
        string? prf = null;
        try { prf = SdsManifest.Load(extracted).GetFiles("PREFAB").FirstOrDefault(); }
        catch (Exception) { /* no manifest, no prefab */ }
        sb.AppendLine("\n── B. PREFAB deformable parts and their collision volumes ──");
        if (prf == null) { sb.AppendLine("    no PREFAB in this archive"); return; }

        PrefabFile file = PrefabFile.Load(prf);

        // Every entry the container holds. A car's own init data is not the only thing that can carry
        // deformable parts — the same block sits inside the phys-thing and actor-deform variants — so which
        // entry is being edited has to be a measured fact, not an assumption.
        sb.AppendLine($"    {Path.GetFileName(prf)} holds {file.PrefabCount} entries:");
        for (int i = 0; i < file.PrefabCount; i++)
        {
            (int type, int size) = file.Entries[i];
            PrefabEntryW raw = file.Wire.Prefabs[i];
            int parts = raw.CarInit.Count > 0 && raw.CarInit[0].Deformation.Count > 0
                ? raw.CarInit[0].Deformation[0].DeformParts.Count
                : 0;
            sb.AppendLine($"        {i,3}  type {type,2}  {size,7} B  decoded as {raw.TypedKind}  "
                + $"hash 0x{raw.Hash:X16}  deform parts {parts}");
        }

        PrefabEntryW? entry = file.Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0);
        if (entry == null) { sb.AppendLine("    no car entry"); return; }
        PrefabCarInitW car = entry.CarInit[0];
        if (car.Deformation.Count == 0) { sb.AppendLine("    no deformation block"); return; }
        PrefabDeformationInitW def = car.Deformation[0];

        string Name(ulong hash) => hash == 0 ? "-"
            : names.TryGetValue(hash, out string? n) ? n : $"0x{hash:X16}";

        sb.AppendLine($"    {def.DeformParts.Count} deform parts, {def.Joints.Count} joints, "
            + $"root frame {Name(def.RootFrameName)}, scale bone {Name(def.ScaleBoneFrameName)}");

        // Every other table the deformation block carries, resolved against BOTH namespaces a hash could be
        // in: a frame name, or a physics shape's data hash. A new volume that the game never walks has to be
        // missing from a list somewhere, and these are the lists.
        Dictionary<ulong, string> shapes = ShapeDataHashes(extracted);
        string Any(ulong hash) => hash == 0 ? "-"
            : names.TryGetValue(hash, out string? n) ? "frame " + n
            : shapes.TryGetValue(hash, out string? s) ? "SHAPE " + s
            : $"0x{hash:X16}";

        sb.AppendLine($"    hash→index pairs ({def.Unk1Pairs.Count}):");
        foreach (PrefabHashIndexW pair in def.Unk1Pairs)
        {
            sb.AppendLine($"        {Any(pair.Hash),-40} → {pair.Index}");
        }
        sb.AppendLine($"    owner deforms ({def.OwnerDeforms.Count}):");
        foreach (PrefabOwnerDeformW owner in def.OwnerDeforms)
        {
            sb.AppendLine($"        {Any(owner.Unk0),-30} {Any(owner.Unk1),-30} "
                + $"u16 lists {owner.Unk4.Count}/{owner.Unk6.Count}, "
                + $"part transforms {owner.PartTransforms.Count}");
        }
        for (int j = 0; j < def.Joints.Count; j++)
        {
            PrefabJointW joint = def.Joints[j];
            sb.AppendLine($"    joint {j}: {joint.Unk0} {joint.Unk1} {joint.Unk2} {joint.Unk3}  "
                + $"sets {joint.JointSets.Count}  names [{string.Join(", ", joint.Unk6)}]  "
                + $"break energies [{string.Join(", ",
                    joint.PartBreakEnergy.Select(b => $"part {b.PartId}={b.BreakEnergy:F1}"))}]");
        }

        int volumes = 0, withHashes = 0;
        var typeCounts = new Dictionary<uint, int>();
        for (int i = 0; i < def.DeformParts.Count; i++)
        {
            PrefabDeformPartW part = def.DeformParts[i];
            string frames = string.Join(", ", part.Unk3.Select(Name));
            sb.AppendLine($"\n    part {i,2}  type {part.PartType,3} flags 0x{part.Flags:X8}  "
                + $"parent {Name(part.ParentDeformPartName),-14} frames [{frames}]");
            sb.AppendLine($"            centre of mass {part.CentreOfMass.X,7:F3}{part.CentreOfMass.Y,7:F3}"
                + $"{part.CentreOfMass.Z,7:F3}   part T {part.PartTransform.Translation.X,7:F3}"
                + $"{part.PartTransform.Translation.Y,7:F3}{part.PartTransform.Translation.Z,7:F3}"
                + $"   deform bones {part.SmDeformBones.Count}");
            sb.AppendLine($"            unk2 {part.Unk2}  unk4/5/6 {part.Unk4:F2}/{part.Unk5:F2}/{part.Unk6:F2}"
                + $"  impulses {part.InternalImpulses.Count}  drops {part.DropParts.Count}"
                + $"  drains {part.DrainEnergy.Count}  unk14 [{string.Join(",", part.Unk14)}]"
                + $"  unk17 {part.Unk17} unk18 {part.Unk18} unk19 {part.Unk19}"
                + $"  unk20 [{string.Join(",", part.Unk20)}]  unk23/24 {part.Unk23}/{part.Unk24}"
                + $"  orig {part.Unk21Data.Count} rel {part.Unk22RelData.Count} common {part.Common.Count}");

            foreach (PrefabCollVolumeCollectionW collection in part.CollisionVolumes)
            {
                foreach (PrefabCollVolumeW v in collection.Volumes)
                {
                    volumes++;
                    typeCounts[v.VolumeType] = typeCounts.GetValueOrDefault(v.VolumeType) + 1;
                    if (v.Unk4Hashes.Count > 0) withHashes++;
                    string hashes = v.Unk4Hashes.Count == 0 ? "" :
                        "  → " + string.Join(" + ", v.Unk4Hashes.Select(Name));
                    sb.AppendLine($"            volume type {v.VolumeType}  "
                        + $"T {v.Transform.Translation.X,7:F3}{v.Transform.Translation.Y,7:F3}"
                        + $"{v.Transform.Translation.Z,7:F3}  extents {v.Extents.X,6:F3}{v.Extents.Y,6:F3}"
                        + $"{v.Extents.Z,6:F3}  extra {v.Unk2Transform.Count}/{v.Unk6.Count}{hashes}");
                }
            }
        }

        sb.AppendLine($"\n    {volumes} collision volumes over {def.DeformParts.Count} parts; "
            + $"{withHashes} name a pair of frames");
        sb.AppendLine("    volume types: " + string.Join(", ",
            typeCounts.OrderByDescending(p => p.Value).Select(p => $"{p.Key}×{p.Value}")));
        check("the prefab's deform parts carry collision volumes", volumes > 0, $"{volumes} volumes");
    }

    /// <summary>Every physics shape in the archive, keyed by the hash a prefab volume names it with.</summary>
    private static Dictionary<ulong, string> ShapeDataHashes(string extracted)
    {
        var found = new Dictionary<ulong, string>();
        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extracted).GetFiles("ItemDesc"); }
        catch (Exception) { return found; }
        foreach (string file in files)
        {
            try
            {
                ItemDescFile shape = ItemDescFile.Load(file);
                if (shape.Element is RigidBodyElement rigid)
                {
                    found[rigid.DataHash] = $"{rigid.Shape} ({Path.GetFileName(file)})";
                }
            }
            catch (Exception) { /* unreadable shapes have no name to give */ }
        }
        return found;
    }

    private static void DumpHitBoxes(StringBuilder sb, FrameObjectModel model, string[] bones)
    {
        sb.AppendLine("\n── C. hit boxes stored on the skinned model (one per mesh split piece) ──");
        FrameObjectModel.HitBoxInfo[] boxes = model.HitBoxes ?? [];
        if (boxes.Length == 0) { sb.AppendLine("    none"); return; }

        // The reading --probe-car-collision settled on: signed int16 × 10/32768 for the centre, the same
        // scale unsigned for the size.
        const float Scale = 10f / 32768f;
        int piece = 0;
        foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
        {
            string bone = split.BlendIndex < bones.Length ? bones[split.BlendIndex] : $"#{split.BlendIndex}";
            for (int p = 0; p < (split.Data?.Length ?? 0) && piece < boxes.Length; p++, piece++)
            {
                FrameObjectModel.HitBoxInfo box = boxes[piece];
                var centre = new Vector3(box.Position.S1, box.Position.S2, box.Position.S3) * Scale;
                var size = new Vector3(
                    (ushort)box.Size.S1, (ushort)box.Size.S2, (ushort)box.Size.S3) * Scale;
                sb.AppendLine($"    piece {piece,3}  split \"{bone}\"  unk 0x{box.Unk:X8}  "
                    + $"centre {centre.X,7:F3}{centre.Y,7:F3}{centre.Z,7:F3}  "
                    + $"size {size.X,7:F3}{size.Y,7:F3}{size.Z,7:F3}");
            }
        }
        if (piece < boxes.Length) sb.AppendLine($"    (+{boxes.Length - piece} boxes past the split table)");
    }

    private static void DumpBodyExtent(StringBuilder sb, FrameResource fr, FrameObjectModel model)
    {
        sb.AppendLine("\n── D. what the visible body spans, for scale ──");
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (FrameObjectSingleMesh mesh in fr.FrameObjects.Values.OfType<FrameObjectSingleMesh>())
        {
            lo = Vector3.Min(lo, mesh.Boundings.Min);
            hi = Vector3.Max(hi, mesh.Boundings.Max);
        }
        sb.AppendLine($"    every mesh bounding box together: {lo.X,7:F2}{lo.Y,7:F2}{lo.Z,7:F2}"
            + $" ..{hi.X,7:F2}{hi.Y,7:F2}{hi.Z,7:F2}   (model bounds "
            + $"{model.Boundings.Min.X,6:F2}{model.Boundings.Min.Y,6:F2}"
            + $"{model.Boundings.Min.Z,6:F2} ..{model.Boundings.Max.X,6:F2}"
            + $"{model.Boundings.Max.Y,6:F2}{model.Boundings.Max.Z,6:F2})");
    }

    /// <summary>
    /// Does an edit REACH the game? Unpacks the live .sds and compares what is inside it against the extracted
    /// folder the editor writes to — prefab volumes first, then the physics shapes they name.
    /// <para>
    /// A car that behaves the same after a Build either never received the change or received it and does not
    /// use it, and only this tells the two apart without starting the game.
    /// </para>
    /// </summary>
    private static void Delivery(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var car = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!car.Exists) return;
        string extracted = MafiaEnvironment.ExtractedDir(car);
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_carphys_packed");

        sb.AppendLine("\n── G. what the PACKED archive carries ──");
        try
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);
            SdsArchive.Open(car.FullName).Extract(scratch);

            // The .sds unpacks into subfolders; the manifest reader wants everything beside SDSContent.xml.
            foreach (string file in Directory.GetFiles(scratch, "*", SearchOption.AllDirectories))
            {
                string flat = Path.Combine(scratch, Path.GetFileName(file));
                if (!File.Exists(flat)) File.Copy(file, flat);
            }

            IReadOnlyList<CarDeformPart> onDisk = CarPhysicsVolumes.Parts(extracted);
            IReadOnlyList<CarDeformPart> packed = CarPhysicsVolumes.Parts(scratch);
            check("the packed archive carries the same collision volumes as the working copy",
                packed.Count == onDisk.Count
                && packed.Sum(p => p.Volumes.Count) == onDisk.Sum(p => p.Volumes.Count),
                $"{packed.Sum(p => p.Volumes.Count)} volumes packed, "
                    + $"{onDisk.Sum(p => p.Volumes.Count)} on disk, {packed.Count} vs {onDisk.Count} parts");

            var packedShapes = new Dictionary<ulong, string>();
            foreach (string ids in Directory.GetFiles(scratch, "*.ids", SearchOption.AllDirectories))
            {
                try
                {
                    ItemDescFile shape = ItemDescFile.Load(ids);
                    if (shape.Element is RigidBodyElement rigid)
                    {
                        packedShapes[rigid.DataHash] = rigid.Shape.ToString();
                    }
                }
                catch (Exception) { /* unreadable here means unreadable everywhere */ }
            }

            int named = 0, missing = 0;
            var absent = new List<string>();
            foreach (CarDeformPart part in packed)
            {
                foreach (CarPhysicsVolume volume in part.Volumes.Where(v => v.NamesShape))
                {
                    if (packedShapes.ContainsKey(volume.ShapeHash)) named++;
                    else
                    {
                        missing++;
                        absent.Add($"part {part.Index} volume {volume.Index} → 0x{volume.ShapeHash:X16}");
                    }
                }
            }
            check("every packed volume finds its physics shape inside the packed archive",
                missing == 0, $"{named} found, {missing} missing" + (absent.Count > 0
                    ? "; " + string.Join("; ", absent.Take(4)) : ""));

            // Every field of every volume of the parts that gained one — so a new volume can be read against
            // its shipped neighbours line by line.
            foreach (CarDeformPart part in packed.Where(p => p.Volumes.Count > 1))
            {
                sb.AppendLine($"    part {part.Index} ({part.Kind}) — {part.Volumes.Count} volumes");
                foreach (CarPhysicsVolume volume in part.Volumes)
                {
                    sb.AppendLine($"        type {volume.VolumeType}  T {volume.Transform.Translation}  "
                        + $"size {volume.Size}  shape 0x{volume.ShapeHash:X16}  "
                        + (packedShapes.TryGetValue(volume.ShapeHash, out string? kind) ? kind : "—"));
                }
            }
        }
        catch (Exception ex) { check("the packed archive can be read back", false, ex.Message); }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch leftovers are not a failure */ }
        }
    }

    /// <summary>
    /// What this car has that a stock copy of it does not: bones, frames and deformable parts. Answers the one
    /// question a report of "my new part has no collision" cannot answer on its own — WHICH part is new, and
    /// whether the bone it hangs off is one that can carry collision at all.
    /// </summary>
    /// <param name="reference">Path to an untouched copy of the same .sds.</param>
    private static void CompareWithStock(StringBuilder sb, string folder, string focus, string reference)
    {
        sb.AppendLine($"\n── F. against the stock archive ({reference}) ──");
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_carphys_stock");
        try
        {
            if (!File.Exists(reference)) { sb.AppendLine("    no such file"); return; }
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);
            SdsArchive.Open(reference).Extract(scratch);

            FrameResource? stock = SdsMeshLoader.OpenScene(scratch).FrameResource;
            FrameResource? mine = SdsMeshLoader.OpenScene(
                MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, focus + ".sds")))).FrameResource;
            if (stock?.FrameObjects == null || mine?.FrameObjects == null) { sb.AppendLine("    unreadable"); return; }

            sb.AppendLine("    bones added:  " + string.Join(", ", Added(Bones(mine), Bones(stock))));
            sb.AppendLine("    bones removed:" + string.Join(", ", Added(Bones(stock), Bones(mine))));
            sb.AppendLine("    frames added: " + string.Join(", ", Added(Names(mine), Names(stock))));
            sb.AppendLine("    frames removed:" + string.Join(", ", Added(Names(stock), Names(mine))));

            var stockParts = CarPhysicsVolumes.Parts(scratch).Select(p => p.Frame).ToHashSet();
            var mineParts = CarPhysicsVolumes.Parts(
                MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, focus + ".sds"))))
                .Select(p => p.Frame).ToHashSet();
            sb.AppendLine($"    deformable parts: {stockParts.Count} stock, {mineParts.Count} here");

            // Which bones carry geometry: a split is what makes a bone something a bullet could meet.
            sb.AppendLine("    mesh splits added: " + string.Join(", ", Added(Splits(mine), Splits(stock))));

            // Geometry added to an EXISTING bone shows up nowhere above — no new frame, no new bone, no new
            // split — so the mesh itself is what has to be measured. Which BONE it was weighted to cannot be
            // read off the split table: its index is not a bone index (the shipped ones skip half the rig),
            // and claiming otherwise would send someone to the wrong part.
            // The hit boxes the skinned model carries, one per mesh-split piece. If geometry was added and
            // these did not move, then whatever reads them is aiming at the OLD shape of the part — which is
            // what "bullets go straight through my new piece" looks like.
            (int Count, string Digest) stockBoxes = HitBoxDigest(stock);
            (int Count, string Digest) mineBoxes = HitBoxDigest(mine);
            sb.AppendLine($"    hit boxes: {stockBoxes.Count} stock, {mineBoxes.Count} here — "
                + (stockBoxes.Digest == mineBoxes.Digest
                    ? "IDENTICAL, so nothing recomputed them when the mesh changed"
                    : "different"));
            sb.AppendLine($"    split pieces: {Pieces(stock)} stock, {Pieces(mine)} here");

            // Does the geometry still fit inside those boxes? A vertex outside every one of them is a piece
            // of the car nothing is aiming at. Axis-aligned in model space, which is only a proxy — the boxes'
            // orientation is not solved — but the comparison against the stock model is what carries the
            // conclusion, and both are read the same way.
            foreach ((string label, FrameResource fr) in new[] { ("stock", stock), ("here ", mine) })
            {
                (int outside, int total, Vector3 lo, Vector3 hi) = OutsideHitBoxes(fr);
                sb.AppendLine($"    {label}: {outside} of {total} vertices outside every hit box"
                    + (outside > 0
                        ? $", spanning {lo.X,6:F2}{lo.Y,6:F2}{lo.Z,6:F2} ..{hi.X,6:F2}{hi.Y,6:F2}{hi.Z,6:F2}"
                        : ""));
            }

            // Which BONE the added geometry actually rides. A vertex whose position is in this archive and
            // not in the stock one is geometry somebody modelled; the bone it is weighted to is the whole
            // answer to "why does my part move with the door". Read out of the vertex buffer through the
            // remap pools, which is the reading the game itself uses.
            DumpAddedVertexBones(sb, stock, mine);

            foreach (string mesh in MeshSizes(stock).Keys.Union(MeshSizes(mine).Keys).OrderBy(n => n))
            {
                MeshSizes(stock).TryGetValue(mesh, out (int V, int I) was);
                MeshSizes(mine).TryGetValue(mesh, out (int V, int I) now);
                if (was == now) continue;
                sb.AppendLine($"        mesh {mesh,-24} {was.V,7} -> {now.V,7} vertices, "
                    + $"{was.I / 3,7} -> {now.I / 3,7} faces");
            }
        }
        catch (Exception ex) { sb.AppendLine("    " + ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch leftovers are not a failure */ }
        }
    }

    private static HashSet<string> Bones(FrameResource fr) =>
        [.. fr.FrameObjects!.Values.OfType<FrameObjectModel>()
            .SelectMany(m => (m.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? ""))];

    private static HashSet<string> Names(FrameResource fr) =>
        [.. fr.FrameObjects!.Values.OfType<FrameObjectBase>().Select(f => f.Name.ToString() ?? "")];

    private static HashSet<string> Splits(FrameResource fr)
    {
        var found = new HashSet<string>();
        foreach (FrameObjectModel model in fr.FrameObjects!.Values.OfType<FrameObjectModel>())
        {
            string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray();
            foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
            {
                found.Add(split.BlendIndex < bones.Length
                    ? bones[split.BlendIndex]
                    : $"#{split.BlendIndex}");
            }
        }
        return found;
    }

    /// <summary>
    /// Which bones hold the geometry this archive has and a stock copy does not.
    ///
    /// <para>
    /// Bone ids in the vertex buffer are POOL-LOCAL — an index into the remap pool of whichever face group
    /// draws the vertex — so reading them straight out of the buffer names the wrong bone. They go through
    /// <c>ResolveBoneRemap</c> first, which is the same resolution the renderer and the skin rebuild use.
    /// </para>
    /// </summary>
    private static void DumpAddedVertexBones(StringBuilder sb, FrameResource stock, FrameResource mine)
    {
        // The stock positions, to measure against. An exact match is no good: a rebuild re-quantizes the
        // whole buffer, so every vertex moves a fraction of a millimetre and the entire car reads as new.
        // What marks geometry somebody MODELLED is being far from anything stock — 5 mm, which no
        // re-quantization reaches and no modelling stays under.
        var stockAt = new List<Vector3>();
        foreach (FrameObjectModel model in stock.FrameObjects!.Values.OfType<FrameObjectModel>())
        {
            stockAt.AddRange(SdsMeshLoader.DecodeLod0(model)?.Positions ?? []);
        }
        Vector3[] stockPositions = [.. stockAt];
        const float Modelled = 0.005f;

        foreach (FrameObjectModel model in stock.FrameObjects.Values.OfType<FrameObjectModel>())
        {
            sb.AppendLine($"    skin of stock \"{model.Name}\": {SdsMeshLoader.DescribeBoneRemap(model)}");
        }

        foreach (FrameObjectModel model in mine.FrameObjects!.Values.OfType<FrameObjectModel>())
        {
            Illusion.Assets.Sds.DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(model);
            if (decoded == null) continue;
            sb.AppendLine($"    skin of \"{model.Name}\": {SdsMeshLoader.DescribeBoneRemap(model)}");
            byte[]? global = SdsMeshLoader.GlobalBoneIds(model);
            if (global == null) continue;

            string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "?").ToArray();
            // All FOUR influences, not just the heaviest. A part can look "attached to something else"
            // because a slot nobody meant to fill is still naming an old bone — reporting only slot 0 hides
            // exactly that.
            var byBinding = new Dictionary<string, (int Count, Vector3 Lo, Vector3 Hi)>(StringComparer.Ordinal);
            int added = 0;
            for (int v = 0; v < decoded.NumVerts; v++)
            {
                Vector3 at = decoded.Positions[v];
                float nearest = float.MaxValue;
                foreach (Vector3 s in stockPositions)
                {
                    float d = Vector3.DistanceSquared(s, at);
                    if (d < nearest) nearest = d;
                    if (nearest <= Modelled * Modelled) break;
                }
                if (nearest <= Modelled * Modelled) continue;

                added++;
                var influences = new List<string>();
                for (int k = 0; k < 4; k++)
                {
                    float weight = decoded.BoneWeights is { } w && (v * 4) + k < w.Length ? w[(v * 4) + k] : 0f;
                    if (weight <= 0f) continue;
                    int id = (v * 4) + k < global.Length ? global[(v * 4) + k] : -1;
                    string bone = id >= 0 && id < bones.Length ? bones[id] : $"#{id}";
                    influences.Add($"{bone} {weight:F2}");
                }
                string binding = influences.Count == 0 ? "(no influence at all)" : string.Join(" + ", influences);
                (int count, Vector3 lo, Vector3 hi) = byBinding.TryGetValue(binding, out var seen)
                    ? seen
                    : (0, new Vector3(float.MaxValue), new Vector3(float.MinValue));
                byBinding[binding] = (count + 1, Vector3.Min(lo, at), Vector3.Max(hi, at));
            }

            sb.AppendLine($"    \"{model.Name}\": {added} vertices further than 5 mm from anything stock — "
                + "geometry that was modelled, and everything each piece of it is bound to:");
            foreach ((string binding, (int count, Vector3 lo, Vector3 hi)) in
                     byBinding.OrderByDescending(p => p.Value.Count))
            {
                sb.AppendLine($"        {count,5} vertices  "
                    + $"{lo.X,6:F2}{lo.Y,6:F2}{lo.Z,6:F2} ..{hi.X,6:F2}{hi.Y,6:F2}{hi.Z,6:F2}   {binding}");
            }

            // The question underneath all of it: does a vertex that EXISTED before still ride the same bone?
            // If a rebuild scrambled the skin, every vertex group in Blender is mislabelled, added geometry
            // inherits a bone at random, and neither symptom points at the cause.
            CompareSkinAgainstStock(sb, stock, decoded, global, bones, Modelled);
        }
    }

    /// <summary>
    /// For every vertex this model shares with a stock copy, whether it is weighted to the same bone. The
    /// comparison is by POSITION — a rebuild renumbers vertices, so index-to-index means nothing — and only
    /// vertices with exactly one stock vertex within <paramref name="tolerance"/> are judged, so a seam where
    /// several split vertices sit on one point cannot vote.
    /// </summary>
    private static void CompareSkinAgainstStock(
        StringBuilder sb, FrameResource stock, Illusion.Assets.Sds.DecodedMesh mine,
        byte[] mineGlobal, string[] bones, float tolerance)
    {
        FrameObjectModel? stockModel = stock.FrameObjects!.Values.OfType<FrameObjectModel>().FirstOrDefault();
        Illusion.Assets.Sds.DecodedMesh? stockMesh = stockModel == null ? null : SdsMeshLoader.DecodeLod0(stockModel);
        byte[]? stockGlobal = stockModel == null ? null : SdsMeshLoader.GlobalBoneIds(stockModel);
        if (stockMesh == null || stockGlobal == null)
        {
            sb.AppendLine("        (the stock model's skin cannot be resolved — nothing to compare against)");
            return;
        }
        string[] stockBones = (stockModel!.GetSkeletonObject().BoneNames ?? [])
            .Select(n => n.ToString() ?? "?").ToArray();

        int judged = 0, same = 0;
        var moved = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int v = 0; v < mine.NumVerts; v++)
        {
            int hit = -1;
            bool ambiguous = false;
            for (int s = 0; s < stockMesh.NumVerts; s++)
            {
                if (Vector3.DistanceSquared(stockMesh.Positions[s], mine.Positions[v]) > tolerance * tolerance)
                {
                    continue;
                }
                if (hit >= 0 && stockGlobal[s * 4] != stockGlobal[hit * 4]) { ambiguous = true; break; }
                hit = s;
            }
            if (hit < 0 || ambiguous) continue;

            judged++;
            string was = stockGlobal[hit * 4] < stockBones.Length
                ? stockBones[stockGlobal[hit * 4]] : $"#{stockGlobal[hit * 4]}";
            string now = mineGlobal[v * 4] < bones.Length ? bones[mineGlobal[v * 4]] : $"#{mineGlobal[v * 4]}";
            if (string.Equals(was, now, StringComparison.Ordinal)) same++;
            else moved[$"{was} → {now}"] = moved.GetValueOrDefault($"{was} → {now}") + 1;
        }

        sb.AppendLine($"        shared vertices judged {judged}, still on the same bone {same}, moved "
            + $"{judged - same}");
        foreach ((string change, int count) in moved.OrderByDescending(p => p.Value).Take(10))
        {
            sb.AppendLine($"            {change,-40} {count,5} vertices");
        }
    }

    /// <summary>How many hit boxes the archive's skinned models carry, and what they say — as one string, so
    /// two archives can be told apart without printing 187 rows twice.</summary>
    private static (int Count, string Digest) HitBoxDigest(FrameResource fr)
    {
        var text = new StringBuilder();
        int count = 0;
        foreach (FrameObjectModel model in fr.FrameObjects!.Values.OfType<FrameObjectModel>())
        {
            foreach (FrameObjectModel.HitBoxInfo box in model.HitBoxes ?? [])
            {
                count++;
                text.Append(box.Unk).Append(' ')
                    .Append(box.Position.S1).Append(',').Append(box.Position.S2).Append(',')
                    .Append(box.Position.S3).Append(' ')
                    .Append(box.Size.S1).Append(',').Append(box.Size.S2).Append(',')
                    .Append(box.Size.S3).Append(';');
            }
        }
        return (count, text.ToString());
    }

    /// <summary>
    /// How much of a model's geometry no hit box covers. The boxes are read the way <c>--probe-car-collision</c>
    /// settled them — signed centre and unsigned FULL size, both int16 × 10/32768 — and taken as axis-aligned
    /// in model space, which is the reading their orientation is still unknown against.
    /// </summary>
    private static (int Outside, int Total, Vector3 Lo, Vector3 Hi) OutsideHitBoxes(FrameResource fr)
    {
        const float Scale = 10f / 32768f;
        var boxes = new List<(Vector3 Lo, Vector3 Hi)>();
        foreach (FrameObjectModel model in fr.FrameObjects!.Values.OfType<FrameObjectModel>())
        {
            foreach (FrameObjectModel.HitBoxInfo box in model.HitBoxes ?? [])
            {
                var centre = new Vector3(box.Position.S1, box.Position.S2, box.Position.S3) * Scale;
                var half = new Vector3(
                    (ushort)box.Size.S1, (ushort)box.Size.S2, (ushort)box.Size.S3) * (Scale * 0.5f);
                boxes.Add((centre - half, centre + half));
            }
        }

        int outside = 0, total = 0;
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
        {
            Illusion.Assets.Sds.DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(model);
            foreach (Vector3 v in decoded?.Positions ?? [])
            {
                total++;
                bool covered = false;
                foreach ((Vector3 boxLo, Vector3 boxHi) in boxes)
                {
                    if (v.X >= boxLo.X - 1e-3f && v.X <= boxHi.X + 1e-3f
                        && v.Y >= boxLo.Y - 1e-3f && v.Y <= boxHi.Y + 1e-3f
                        && v.Z >= boxLo.Z - 1e-3f && v.Z <= boxHi.Z + 1e-3f)
                    {
                        covered = true;
                        break;
                    }
                }
                if (covered) continue;
                outside++;
                lo = Vector3.Min(lo, v);
                hi = Vector3.Max(hi, v);
            }
        }
        return (outside, total, lo, hi);
    }

    private static int Pieces(FrameResource fr) =>
        fr.FrameObjects!.Values.OfType<FrameObjectModel>()
            .SelectMany(m => m.BlendMeshSplits ?? [])
            .Sum(s => s.Data?.Length ?? 0);

    /// <summary>Each mesh's vertex and index count — how much geometry the archive carries, by name.</summary>
    private static Dictionary<string, (int Vertices, int Indices)> MeshSizes(FrameResource fr)
    {
        var found = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (FrameObjectSingleMesh mesh in fr.FrameObjects!.Values.OfType<FrameObjectSingleMesh>())
        {
            int vertices = mesh.GetVertexBuffer(0)?.Data?.Length ?? 0;
            int indices = mesh.GetIndexBuffer(0)?.GetData()?.Length ?? 0;
            found[mesh.Name.ToString() ?? "?"] = (vertices, indices);
        }
        return found;
    }

    private static IEnumerable<string> Added(HashSet<string> now, HashSet<string> before) =>
        now.Except(before).OrderBy(n => n, StringComparer.Ordinal);

    // ── giving a part something to be shot at, end to end, on a copy ──

    /// <summary>
    /// The whole chain the reported bug turns on, run on a scratch copy: a bone gets a box, and the box has to
    /// exist in ALL THREE places — as an ItemDesc record the manifest announces, as a stub in the frame graph,
    /// and as a collision volume in the prefab. The third is the one that was missing, and the one the game
    /// reads; a box with the first two is exactly the "it is just a mesh" the user described.
    /// </summary>
    private static void RoundTrip(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var car = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!car.Exists) return;
        string source = MafiaEnvironment.ExtractedDir(car);
        if (!File.Exists(Path.Combine(source, "SDSContent.xml"))) return;
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_carphys_scratch");

        sb.AppendLine("\n\n════ adding collision to a bone, on a copy ════");
        try
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(scratch, Path.GetFileName(file)));

            // Rewriting a prefab must be a no-op when nothing was asked for. Everything below writes this
            // file, so a writer that does not reproduce its input would corrupt a car on the first edit.
            string? prf = SdsManifest.Load(scratch).GetFiles("PREFAB").FirstOrDefault();
            if (prf != null)
            {
                byte[] before = File.ReadAllBytes(prf);
                byte[] after = PrefabFile.Load(prf).ToBytes();
                check("a prefab read and written back is byte-identical",
                    before.AsSpan().SequenceEqual(after),
                    $"{before.Length} vs {after.Length} bytes, first difference at {FirstDiff(before, after)}");
            }

            FrameResource? fr = SdsMeshLoader.OpenScene(scratch).FrameResource;
            FrameObjectModel? model = fr?.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (fr == null || model == null) { sb.AppendLine("no skinned model"); return; }

            string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray();
            IReadOnlyList<string> partBones = CarPhysicsVolumes.PartBones(scratch, fr);
            sb.AppendLine($"    bones that are a deformable part, so can carry collision ({partBones.Count}): "
                + string.Join(", ", partBones));
            check("a car has bones that can carry collision", partBones.Count > 0, "");
            if (partBones.Count == 0) return;

            string target = partBones.Contains("coverF", StringComparer.Ordinal) ? "coverF" : partBones[0];
            int bone = Array.FindIndex(bones, n => string.Equals(n, target, StringComparison.Ordinal));
            int volumesBefore = CarPhysicsVolumes.Load(scratch, fr).Count;

            var size = new Vector3(0.30f, 0.20f, 0.05f);
            Matrix4x4 place = Matrix4x4.CreateTranslation(0.11f, 0.22f, 0.33f);
            AddedCollisionBox? added = CarCollisionBuilder.AddShape(
                model, bone, "illusion_physics_probe_Collision", RigidBodyShape.Box, size, place, scratch,
                out string? refusal);
            check($"a box can be added to \"{target}\"", added != null, refusal ?? "");
            if (added == null) return;

            IReadOnlyList<PlacedPhysicsVolume> now = CarPhysicsVolumes.Load(scratch, fr);
            PlacedPhysicsVolume? mine = now.FirstOrDefault(v => ReferenceEquals(v.Stub, added.Frame));
            check("…and the prefab gained a collision volume for it — the half the game reads",
                now.Count == volumesBefore + 1 && mine != null,
                $"{volumesBefore} -> {now.Count} volumes");
            if (mine == null) return;

            // Adding the SAME NAME twice. Deleting a collision leaves its ItemDesc record behind, so the
            // second add has to mint clear of BOTH of the orphan's hashes — the file one and the data one.
            // When it did not, two records answered to one data hash, the new volume resolved to the orphan
            // and lost its stub: the shape drew at the prefab placement, ignoring the scale and standing
            // still while the gizmo moved.
            AddedCollisionBox? twin = CarCollisionBuilder.AddShape(
                model, bone, "illusion_physics_probe_Collision_2", RigidBodyShape.Box, size, place, scratch,
                out string? twinRefusal);
            check("a second shape can be added", twin != null, twinRefusal ?? "");
            if (twin != null)
            {
                ulong twinData = (twin.Shape.Element as RigidBodyElement)?.DataHash ?? 0;
                ulong mineData = (added.Shape.Element as RigidBodyElement)?.DataHash ?? 0;
                check("…and its hashes collide with nothing already in the archive",
                    twin.Shape.Hash != added.Shape.Hash && twinData != mineData
                    && twinData != added.Shape.Hash && twin.Shape.Hash != mineData
                    && twinData != 0 && mineData != 0,
                    $"file 0x{twin.Shape.Hash:X16}/0x{added.Shape.Hash:X16}, "
                        + $"data 0x{twinData:X16}/0x{mineData:X16}");
                check("…and both volumes resolve to their OWN stub, which is what the overlay draws by",
                    CarPhysicsVolumes.Load(scratch, fr)
                        .Count(v => ReferenceEquals(v.Stub, added.Frame) || ReferenceEquals(v.Stub, twin.Frame))
                        == 2, "");
                CarCollisionBuilder.Remove(model, twin);
            }

            check("the volume names the shape by its DATA hash, the way every shipped one does",
                mine.Volume.ShapeHash == added.Shape.Element!.DataHash && mine.Shape != null,
                $"0x{mine.Volume.ShapeHash:X16} vs 0x{added.Shape.Element!.DataHash:X16}");
            check("the volume sits on the bone that was chosen",
                mine.Bone == bone, $"{mine.BoneName} (part {mine.Part}, {mine.PartKind})");
            check("the placement survives the axis conversion into the file and back",
                (mine.Volume.Transform.Translation - place.Translation).Length() < 1e-4f,
                $"{mine.Volume.Transform.Translation} vs {place.Translation}");

            // Where a shape LANDS when the thing that was selected is not the part it is given to. The dialog
            // answers "which part" with the body whenever the selection is not itself a deformable part — and
            // no Dummy ever is — so asking for a shape while pointing at a climb box used to put it at the
            // body bone, which on a car is its centre. Reported as "the transform is strange in places".
            FrameObjectDummy? pointed = fr.FrameObjects.Values.OfType<FrameObjectDummy>()
                .FirstOrDefault(d => d.WorldTransform.Translation.Length() > 0.2f);
            if (pointed != null)
            {
                Matrix4x4 landing = TransformMath.ComputeLocalTransform(
                    pointed.WorldTransform, model.GetJointWorldTransform(bone));
                TransformMath.TryDecompose(landing, out _, out Quaternion rotation, out Vector3 position);
                AddedCollisionBox? atDummy = CarCollisionBuilder.AddShape(
                    model, bone, "illusion_landing_probe_Collision", RigidBodyShape.Box, size,
                    TransformMath.Compose(rotation, Vector3.One, position), scratch, out string? landRefusal);
                check($"a shape asked for while pointing at \"{pointed.Name}\" lands THERE, not at the "
                    + $"\"{target}\" bone it belongs to", atDummy != null, landRefusal ?? "");
                if (atDummy != null)
                {
                    PlacedPhysicsVolume? landed = CarPhysicsVolumes.Load(scratch, fr)
                        .FirstOrDefault(v => ReferenceEquals(v.Stub, atDummy.Frame));
                    float off = landed == null
                        ? float.NaN
                        : (landed.World.Translation - pointed.WorldTransform.Translation).Length();
                    check("…and the prefab agrees, so the game puts it where the editor drew it",
                        landed != null && off < 1e-3f,
                        landed == null ? "no volume" : $"{off:F4} m away from the dummy");
                    CarCollisionBuilder.Remove(model, atDummy);
                }
            }

            // The SELF-DESCRIBING volume — what the second report was about: "I change the Z of windowFR2 and
            // it moves along Y, and undo does not put it back". Two separate questions, so two checks.
            PlacedPhysicsVolume? loose = CarPhysicsVolumes.Load(scratch, fr)
                .FirstOrDefault(v => v.Stub == null && v.Bone >= 0
                    && v.BoneName.Contains("window", StringComparison.OrdinalIgnoreCase))
                ?? CarPhysicsVolumes.Load(scratch, fr).FirstOrDefault(v => v.Stub == null && v.Bone >= 0);
            if (loose != null && FlatIndexOf(scratch, loose) is var slot && slot >= 0)
            {
                float was = loose.Volume.Transform.Translation.Z;
                Vector3 before = loose.World.Translation;
                Illusion.Assets.Prefabs.PrefabEditing.ValueChange? edit =
                    Illusion.Assets.Prefabs.PrefabEditing.SetValueIn(
                        scratch, CarValueSlot.CollisionVolumePosition, slot, 2, was + 0.25f, "probe");

                PlacedPhysicsVolume? after = CarPhysicsVolumes.Load(scratch, fr)
                    .FirstOrDefault(v => v.Part == loose.Part && v.Volume.Index == loose.Volume.Index);
                Vector3 shifted = after == null ? Vector3.Zero : after.World.Translation - before;
                sb.AppendLine($"    \"{loose.BoneName}\" is written in \"{ParentBoneName(scratch, fr, loose)}\" "
                    + $"space: +0.25 on the field's Z moves it {shifted:F3} in the world — its local axes point "
                    + $"X{Row(after?.World ?? Matrix4x4.Identity, 0):F2} Y{Row(after?.World ?? Matrix4x4.Identity, 1):F2} "
                    + $"Z{Row(after?.World ?? Matrix4x4.Identity, 2):F2}");
                check("a self-describing volume's position field writes the axis it says it does",
                    edit != null && after != null
                    && MathF.Abs(after.Volume.Transform.Translation.Z - (was + 0.25f)) < 1e-4f
                    && MathF.Abs(after.Volume.Transform.Translation.X - loose.Volume.Transform.Translation.X) < 1e-5f
                    && MathF.Abs(after.Volume.Transform.Translation.Y - loose.Volume.Transform.Translation.Y) < 1e-5f,
                    after == null ? "the volume is gone" : $"{after.Volume.Transform.Translation:F4}");

                // …and the undo of it, which is what Ctrl+Z runs. A negative starting number is the case the
                // report singled out, so the assertion is on exact equality rather than on a tolerance.
                if (edit != null)
                {
                    Illusion.Assets.Prefabs.PrefabEditing.RestoreValue(edit, edit.Before);
                    PlacedPhysicsVolume? back = CarPhysicsVolumes.Load(scratch, fr)
                        .FirstOrDefault(v => v.Part == loose.Part && v.Volume.Index == loose.Volume.Index);
                    check($"…and undoing it puts back exactly what was there (was {was:F4})",
                        back != null && back.Volume.Transform.Translation == loose.Volume.Transform.Translation,
                        back == null ? "the volume is gone" : $"{back.Volume.Transform.Translation:F4} vs "
                            + $"{loose.Volume.Transform.Translation:F4}");
                }
            }

            // The PANEL's own arithmetic: the position rows are shown in the car's axes, not the bone's, so
            // "+0.25 on Y" has to move the box a quarter of a metre along the car and nowhere else. This is
            // the fix for "Y moves it along Z and Z along Y" — the field was reading the raw bone-space
            // numbers, and most car bones are turned.
            if (loose != null)
            {
                var worlds = new Dictionary<ulong, Matrix4x4>();
                string[] rig = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray();
                for (int i = 0; i < rig.Length; i++) worlds.TryAdd(Fnv64.Hash(rig[i]), model.GetJointWorldTransform(i));

                Illusion.Assets.Prefabs.PrefabAssembly? shown =
                    Illusion.Assets.Prefabs.PrefabAssembly.ReadFrom(scratch, null, worlds);
                Illusion.Assets.Prefabs.PrefabRefView? posRow = shown?.Entries
                    .SelectMany(e => e.Groups).Where(g => g.Title == "Collision").SelectMany(g => g.Rows)
                    .FirstOrDefault(r => r.ValueSlot == CarValueSlot.CollisionVolumePosition
                        && r.Index == FlatIndexOf(scratch, loose));

                PlacedPhysicsVolume? nowAt = CarPhysicsVolumes.Load(scratch, fr)
                    .FirstOrDefault(v => v.Part == loose.Part && v.Volume.Index == loose.Volume.Index);
                check("the panel shows a volume's position in the CAR's axes, where the viewport draws it",
                    posRow is { Space: not null } && nowAt != null
                    && (new Vector3(posRow.X, posRow.Y, posRow.Z) - nowAt.World.Translation).Length() < 1e-3f,
                    posRow == null ? "no position row"
                        : $"panel {new Vector3(posRow.X, posRow.Y, posRow.Z):F3} vs world {nowAt?.World.Translation:F3}");

                // …and writing one of those axes back moves it along THAT axis of the car, which is the whole
                // point: the conversion has to run in both directions or the field is worse than raw.
                if (posRow is { Space: { } space } && nowAt != null
                    && Matrix4x4.Invert(space, out Matrix4x4 back))
                {
                    var wanted = new Vector3(posRow.X, posRow.Y + 0.25f, posRow.Z);
                    Vector3 asStored = Vector3.Transform(wanted, back);
                    for (int at = 0; at < 3; at++)
                    {
                        Illusion.Assets.Prefabs.PrefabEditing.SetValueIn(
                            scratch, CarValueSlot.CollisionVolumePosition, posRow.Index, at,
                            at == 0 ? asStored.X : at == 1 ? asStored.Y : asStored.Z, "probe");
                    }
                    PlacedPhysicsVolume? ended = CarPhysicsVolumes.Load(scratch, fr)
                        .FirstOrDefault(v => v.Part == loose.Part && v.Volume.Index == loose.Volume.Index);
                    Vector3 went = (ended?.World.Translation ?? Vector3.Zero) - nowAt.World.Translation;
                    check("…and typing +0.25 into that Y moves it a quarter-metre along the car, nowhere else",
                        ended != null && (went - new Vector3(0f, 0.25f, 0f)).Length() < 2e-3f,
                        $"it went {went:F4}");
                }
            }

            // A SELF-DESCRIBING volume, which the toolkit could not make at all until now. Every box it added
            // was type 5 — a placed physics shape, i.e. body collision — so hanging one on a window part
            // changed nothing, and that is why the in-game test came back empty twice. A window is type 0 on
            // all 527 shipped ones; this is the path that can finally write one.
            IReadOnlyList<string> zoneBones = CarPhysicsVolumes.PartBones(scratch, fr);
            string zoneOn = zoneBones.FirstOrDefault(b => b.Contains("window", StringComparison.OrdinalIgnoreCase))
                ?? target;
            int zonesBefore = CarPhysicsVolumes.Load(scratch, fr).Count;
            var zoneSize = new Vector3(0.60f, 0.02f, 0.40f);
            CarPhysicsVolumes.VolumeChange? zone = CarPhysicsVolumes.AddZone(
                scratch, zoneOn, Matrix4x4.CreateTranslation(0.1f, 0.2f, 0.3f), zoneSize, 0);
            check($"a plain GLASS volume can be added to \"{zoneOn}\"", zone != null, "");
            if (zone != null)
            {
                PlacedPhysicsVolume? made = CarPhysicsVolumes.Load(scratch, fr)
                    .FirstOrDefault(v => v.Volume.VolumeType == 0 && !v.Volume.NamesShape
                        && (v.Volume.Size - zoneSize).Length() < 1e-4f);
                check("…and it is type 0, names no shape, and states the size that was asked for",
                    made != null && made.Volume.ShapeHash == 0,
                    made == null ? "not found" : $"type {made.Volume.VolumeType}, size {made.Volume.Size:F3}");
                check("…and it added exactly one volume",
                    CarPhysicsVolumes.Load(scratch, fr).Count == zonesBefore + 1,
                    $"{zonesBefore} -> {CarPhysicsVolumes.Load(scratch, fr).Count}");

                CarPhysicsVolumes.Remove(zone);
                check("…and undoing it takes that one volume away again",
                    CarPhysicsVolumes.Load(scratch, fr).Count == zonesBefore, "");
                CarPhysicsVolumes.Restore(zone);
                check("…and redo puts it back, not a second one",
                    CarPhysicsVolumes.Load(scratch, fr).Count == zonesBefore + 1, "");
                CarPhysicsVolumes.Remove(zone);
            }

            // CHANGING what an existing volume is. The catch is that the kinds live in different spaces — a
            // type-5 volume in its own part's bone, a self-describing one in the bone of the part it hangs
            // off — so rewriting the type alone would leave the placement meaning something else and the box
            // would jump. What must survive a conversion is where it IS.
            // A shipped HULL, not one of this probe's own boxes: a hull is the case that broke — its size
            // lives in the cooked blob, and reading a token 0.2 m instead turned a car body into a speck.
            List<PlacedPhysicsVolume> convertible = [.. CarPhysicsVolumes.Load(scratch, fr)
                .Where(v => v.Volume.NamesShape && v.Bone >= 0 && v.Shape != null)];
            PlacedPhysicsVolume? toConvert =
                convertible.FirstOrDefault(v => (v.Shape!.Element as RigidBodyElement)?.Shape
                    == RigidBodyShape.ConvexPolyhedron)
                ?? convertible.FirstOrDefault();
            if (toConvert != null)
            {
                // Where the SPACE it covers is centred, which is what has to survive — not the placement's
                // origin. A cooked hull sits wherever its geometry sits around that origin, while a plain box
                // is centred on it, so the two are only the same thing for a primitive.
                RigidBodyElement? rb = toConvert.Shape?.Element as RigidBodyElement;
                Vector3 middle = Vector3.Zero;
                if (rb?.Shape is RigidBodyShape.ConvexPolyhedron or RigidBodyShape.TriangleMesh
                    && Illusion.Assets.Collisions.CarCollisionShapes.TryReadCookedBounds(
                        rb.CookedMesh, out Vector3 hLo, out Vector3 hHi))
                {
                    middle = (hLo + hHi) * 0.5f;
                }
                Vector3 wasAt = Vector3.Transform(middle, toConvert.World);
                sb.AppendLine($"    converting {rb?.Shape.ToString() ?? "?"} on \"{toConvert.BoneName}\" "
                    + $"({toConvert.PartKind}), covering a space centred on {wasAt:F3}");
                CarPhysicsVolumes.TypeChange? turned = CarPhysicsVolumes.ChangeType(
                    scratch, fr, toConvert.Part, toConvert.Volume.Index, 0, out string? noTurn);
                check("a placed shape can be turned into glass", turned != null, noTurn ?? "");
                if (turned != null)
                {
                    PlacedPhysicsVolume? asGlass = CarPhysicsVolumes.Load(scratch, fr)
                        .FirstOrDefault(v => v.Part == toConvert.Part && v.Volume.Index == toConvert.Volume.Index);
                    check("…and it stays exactly where it was, though the space it is written in changed",
                        asGlass != null && (asGlass.World.Translation - wasAt).Length() < 1e-3f,
                        asGlass == null ? "gone" : $"{asGlass.World.Translation:F3} vs {wasAt:F3}");
                    check("…and it now names no shape and states its own size",
                        asGlass is { Volume.VolumeType: 0, Volume.ShapeHash: 0 } && asGlass.Volume.Size.Length() > 0.01f,
                        asGlass == null ? "gone" : $"type {asGlass.Volume.VolumeType}, size {asGlass.Volume.Size:F3}");
                    // A hull turned into a box has to keep its SIZE too, or it survives the conversion as a
                    // speck: the body hull of a five-metre car came back a fifth of a metre across and read
                    // as having vanished from the viewport.
                    check("…and it is the size of the shape it replaced, not a token box",
                        asGlass != null && asGlass.Volume.Size.Length() > 0.4f,
                        asGlass == null ? "gone" : $"{asGlass.Volume.Size:F3}");

                    // …and back again, which has to mint a shape because that kind is required to have one.
                    CarPhysicsVolumes.TypeChange? back = CarPhysicsVolumes.ChangeType(
                        scratch, fr, toConvert.Part, toConvert.Volume.Index,
                        Illusion.Formats.Prefab.CarPhysicsVolume.ShapeVolumeType, out string? noBack);
                    PlacedPhysicsVolume? again = back == null ? null : CarPhysicsVolumes.Load(scratch, fr)
                        .FirstOrDefault(v => v.Part == toConvert.Part && v.Volume.Index == toConvert.Volume.Index);
                    check("glass can be turned back into a placed shape, and a shape is minted for it",
                        back != null && again is { Volume.NamesShape: true } && again.Shape != null,
                        noBack ?? (again == null ? "gone" : $"shape {again.Volume.ShapeHash:X16}"));
                    check("…and it is still in the same place after the round trip",
                        again != null && (again.World.Translation - wasAt).Length() < 1e-3f,
                        again == null ? "gone" : $"{again.World.Translation:F3} vs {wasAt:F3}");

                    if (back != null) CarPhysicsVolumes.RestoreType(back, toBefore: true);
                    CarPhysicsVolumes.RestoreType(turned, toBefore: true);
                    PlacedPhysicsVolume? undone = CarPhysicsVolumes.Load(scratch, fr)
                        .FirstOrDefault(v => v.Part == toConvert.Part && v.Volume.Index == toConvert.Volume.Index);
                    check("…and undoing both conversions puts the original volume back",
                        undone is { Volume.NamesShape: true }
                        && undone.Volume.ShapeHash == toConvert.Volume.ShapeHash,
                        undone == null ? "gone" : $"type {undone.Volume.VolumeType}");
                }
            }

            // DELETING SEVERAL STUBS AT ONCE. Reported: "the prefab says 19 collisions, I delete TWO, and it
            // says 18". Each stub is supposed to take its own volume with it, so two should leave 17. This
            // walks every stub the car has and reports which volume each one claims — a stub that claims the
            // same volume as another, or none, is the arithmetic.
            var claims = new Dictionary<int, List<string>>();
            var orphans = new List<string>();
            foreach (PlacedPhysicsVolume any in CarPhysicsVolumes.Load(scratch, fr))
            {
                if (any.Stub == null) continue;
                if (!claims.TryGetValue(any.Volume.Index + (any.Part * 1000), out List<string>? who))
                {
                    claims[any.Volume.Index + (any.Part * 1000)] = who = [];
                }
                who.Add(any.Stub.Name.ToString() ?? "?");
            }
            foreach (FrameObjectCollision stub in fr.FrameObjects!.Values.OfType<FrameObjectCollision>())
            {
                bool named = CarPhysicsVolumes.Load(scratch, fr).Any(v => ReferenceEquals(v.Stub, stub));
                if (!named) orphans.Add(stub.Name.ToString() ?? "?");
            }
            int shared = claims.Count(p => p.Value.Count > 1);
            sb.AppendLine($"    {fr.FrameObjects!.Values.OfType<FrameObjectCollision>().Count()} collision "
                + $"stubs, {claims.Count} volumes claimed, {shared} claimed by more than one stub, "
                + $"{orphans.Count} stubs claiming no volume at all"
                + (orphans.Count > 0 ? " — " + string.Join(", ", orphans.Take(6)) : ""));
            foreach ((int at, List<string> who) in claims.Where(p => p.Value.Count > 1).Take(6))
            {
                sb.AppendLine($"      part {at / 1000} volume {at % 1000} is claimed by "
                    + string.Join(" and ", who));
            }

            // A stub that shares its volume with another means deleting both takes ONE volume away, and a
            // stub that claims none means deleting it takes nothing. Either way the count the panel shows
            // stops matching what was removed, which is exactly the report.
            check("every collision stub claims a volume of its own",
                shared == 0 && orphans.Count == 0,
                $"{shared} volumes shared, {orphans.Count} stubs with none");

            // What the reported bug actually was: moving the stub used to change only the frame graph.
            var moved = Matrix4x4.CreateTranslation(-0.4f, 0.7f, 1.25f);
            added.Frame.LocalTransform = moved;
            int synced = CarPhysicsVolumes.SyncStubs(scratch, [added.Frame]);
            PlacedPhysicsVolume? after2 = CarPhysicsVolumes.Load(scratch, fr)
                .FirstOrDefault(v => v.Volume.ShapeHash == added.Shape.Element!.DataHash);
            check("moving the stub carries through to the prefab volume",
                synced == 1 && after2 != null
                && (after2.Volume.Transform.Translation - moved.Translation).Length() < 1e-4f,
                after2 == null ? "the volume is gone" : $"{after2.Volume.Transform.Translation} vs {moved.Translation}");

            // A shipped stub, moved and synced, must land where the shipped volume already was — the two
            // copies have to mean the same thing or the conversion is wrong in a way no probe would notice.
            PlacedPhysicsVolume? shipped = CarPhysicsVolumes.Load(scratch, fr)
                .FirstOrDefault(v => v.Stub != null && !ReferenceEquals(v.Stub, added.Frame));
            if (shipped?.Stub != null)
            {
                Matrix4x4 asShipped = shipped.Volume.Transform;
                Matrix4x4 asFrame = shipped.Stub.LocalTransform;
                check("a shipped volume and its own stub already agree, under the conversion this writes",
                    (asShipped.Translation - asFrame.Translation).Length() < 1e-3f,
                    $"{shipped.Stub.Name}: prefab {asShipped.Translation} vs frame {asFrame.Translation}");
            }

            // Scaling the stub with the gizmo. The placement cannot carry a scale, so it has to end up in
            // the SHAPE — dropping it is what drew a box 0.41 m thick in the editor and gave the game one
            // 0.10 m thick, which a character's arm goes straight through.
            Matrix4x4 grown = added.Frame.LocalTransform;
            grown.M31 *= 4f;
            grown.M32 *= 4f;
            grown.M33 *= 4f;
            added.Frame.LocalTransform = grown;
            int bakedCount = CarPhysicsVolumes.BakeScales(scratch, [added.Frame]);
            ItemDescFile grownShape = ItemDescFile.Load(added.ShapeFile);
            float thickness = (grownShape.Element as RigidBodyElement)?.BoxDimensions.Z ?? 0f;
            check("scaling a box stub folds the scale into the box's own size",
                bakedCount == 1 && MathF.Abs(thickness - (0.05f * 4f)) < 1e-3f,
                $"half-thickness {thickness:F3}, wanted {0.05f * 4f:F3}");
            Matrix4x4 afterBake = added.Frame.LocalTransform;
            check("…and the stub itself comes back unscaled, so nothing is counted twice",
                MathF.Abs(new Vector3(afterBake.M31, afterBake.M32, afterBake.M33).Length() - 1f) < 1e-3f,
                $"{new Vector3(afterBake.M31, afterBake.M32, afterBake.M33).Length():F3}");

            // …and the repair the loader performs: a stub left standing somewhere else is snapped back onto
            // the placement the game uses, so the handle never lies about what it holds.
            added.Frame.LocalTransform = Matrix4x4.CreateTranslation(9f, 9f, 9f);
            int aligned = CarPhysicsVolumes.AlignStubsToPrefab(scratch, fr);
            check("a stub standing away from its volume is snapped back on load",
                aligned >= 1
                && (added.Frame.LocalTransform.Translation - moved.Translation).Length() < 1e-4f,
                $"{aligned} stubs moved; the probe's own is at {added.Frame.LocalTransform.Translation}");
            check("…and a car whose copies already agree needs no repair at all",
                CarPhysicsVolumes.AlignStubsToPrefab(scratch, fr) == 0, "");

            // The route the PREFAB TAB takes: a number typed into a field, straight into the file, with the
            // scene never touched. Reported as "I change the position and nothing happens in the scene" —
            // the prefab moved and the overlay went on drawing the stub, which had not. What makes it visible
            // is the repair above, run after the edit rather than only at load.
            PlacedPhysicsVolume? typed = CarPhysicsVolumes.Load(scratch, fr)
                .FirstOrDefault(v => ReferenceEquals(v.Stub, added.Frame));
            if (typed != null)
            {
                const float wanted = 1.75f;
                bool written = Illusion.Assets.Prefabs.PrefabEditing.SetValueIn(
                    scratch, CarValueSlot.CollisionVolumePosition,
                    typed.Part >= 0 ? FlatIndexOf(scratch, typed) : -1, 1, wanted, "probe") != null;
                CarPhysicsVolumes.AlignStubsToPrefab(scratch, fr);
                check("a position typed into the Prefab tab moves the stub the overlay draws by",
                    written && MathF.Abs(added.Frame.LocalTransform.Translation.Y - wanted) < 1e-3f,
                    written
                        ? $"stub is at Y={added.Frame.LocalTransform.Translation.Y:F3}, wanted {wanted:F3}"
                        : "the prefab would not take the value");
            }

            // The route the property panel takes: every volume addressed by ONE flat number, read, written,
            // and put back. A slot that reads and does not write is a field that looks editable and is not.
            PrefabValueRoundTrip(scratch, check);

            // Deleting a frame that hangs off a BONE, the way the scene tree does it. The writer resolves
            // each attachment with IndexOfValue over the frame objects, so a frame removed from the resource
            // while the model still lists it is written with an index of -1 relative to the block — and the
            // game crashes on load. Nothing else in the toolkit tests this, and it bit a real car.
            var deleteDoc = new Illusion.Assets.Adapters.SceneDocumentAdapter(fr, car);
            var doomed = new List<Illusion.Domain.IFrameNode> { deleteDoc.Node(added.Frame) };
            Illusion.Assets.Frames.DetachedFrames? detached =
                Illusion.Assets.Frames.DetachedFrames.Capture(deleteDoc, doomed, scratch);
            check("a frame delete captures the frame", detached != null, "");
            if (detached != null)
            {
                int before = (model.AttachmentReferences ?? []).Length;
                int volumesBeforeDelete = CarPhysicsVolumes.Load(scratch, fr).Count;
                detached.Detach();
                check("deleting a collision stub takes its VOLUME with it — the half the game reads",
                    CarPhysicsVolumes.Load(scratch, fr).Count == volumesBeforeDelete - 1,
                    $"{CarPhysicsVolumes.Load(scratch, fr).Count} volumes, was {volumesBeforeDelete}");
                bool dangling = (model.AttachmentReferences ?? [])
                    .Any(r => r.Attachment != null
                        && !model.Resource.FrameObjects.ContainsKey(r.Attachment.RefID));
                check("deleting an attached frame takes its attachment with it — no dangling reference",
                    !dangling && (model.AttachmentReferences ?? []).Length == before - 1,
                    $"{(model.AttachmentReferences ?? []).Length} references, was {before}, "
                        + $"dangling={dangling}");

                // …and the whole thing still serializes, which is the step that used to write the bad index.
                try
                {
                    var reread = new FrameResource();
                    using var stream = new MemoryStream(fr.WriteToStream());
                    reread.ReadFromFile(stream);
                    check("…and the resource still writes and reads back", true, "");
                }
                catch (Exception ex) { check("…and the resource still writes and reads back", false, ex.Message); }

                detached.Reattach();
                check("undoing the delete puts the attachment back on its bone",
                    (model.AttachmentReferences ?? []).Length == before
                    && added.Frame.AttachedTo == model,
                    $"{(model.AttachmentReferences ?? []).Length} references, wanted {before}");
                check("…and puts the collision volume back too",
                    CarPhysicsVolumes.Load(scratch, fr).Count == volumesBeforeDelete,
                    $"{CarPhysicsVolumes.Load(scratch, fr).Count} volumes, wanted {volumesBeforeDelete}");
            }

            CarCollisionBuilder.Remove(model, added);
            check("undo takes the volume away with the shape and the stub",
                CarPhysicsVolumes.Load(scratch, fr).Count == volumesBefore
                && !File.Exists(added.ShapeFile),
                $"{CarPhysicsVolumes.Load(scratch, fr).Count} volumes, was {volumesBefore}");

            CarCollisionBuilder.Restore(added, added.Shape.ToBytes());
            check("redo puts all three back, not two of them",
                CarPhysicsVolumes.Load(scratch, fr).Count == volumesBefore + 1
                && File.Exists(added.ShapeFile)
                && SdsManifest.Load(scratch).HasFile(Path.GetFileName(added.ShapeFile)),
                $"{CarPhysicsVolumes.Load(scratch, fr).Count} volumes");
        }
        catch (Exception ex)
        {
            check("the round trip runs", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch leftovers are not a failure */ }
        }
    }

    /// <summary>
    /// Every collision volume reached the way the Prefab tab reaches it — one flat index, one axis at a time —
    /// written, read back, and restored. Also drops a volume and puts it back, which is what the remove button
    /// and its undo do.
    /// </summary>
    private static void PrefabValueRoundTrip(string extracted, Action<string, bool, string> check)
    {
        string? path = SdsManifest.Load(extracted).GetFiles("PREFAB").FirstOrDefault();
        if (path == null) return;
        PrefabFile prefab = PrefabFile.Load(path);

        int count = prefab.CarVolumeCount();
        int read = 0, written = 0, restored = 0;
        for (int flat = 0; flat < count; flat++)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                foreach (CarValueSlot slot in new[]
                         { CarValueSlot.CollisionVolumePosition, CarValueSlot.CollisionVolumeSize })
                {
                    float before = prefab.GetCarValue(slot, flat, axis);
                    if (float.IsNaN(before)) continue;
                    read++;
                    float wanted = before + 0.125f;
                    if (prefab.SetCarValue(slot, flat, axis, wanted)
                        && Math.Abs(prefab.GetCarValue(slot, flat, axis) - wanted) < 1e-5f)
                    {
                        written++;
                    }
                    prefab.SetCarValue(slot, flat, axis, before);
                    if (Math.Abs(prefab.GetCarValue(slot, flat, axis) - before) < 1e-5f) restored++;
                }
            }
        }
        check("every collision volume's numbers read, write and read back the same",
            read > 0 && written == read && restored == read,
            $"{read} numbers, {written} written, {restored} restored");

        // Dropping the LAST volume and putting it back is the case a flat index gets wrong: after the take,
        // the index it came from is past the end of everything.
        byte[]? taken = prefab.TakeCarItem(CarItemKind.CollisionVolume, count - 1);
        bool put = taken != null && prefab.PutCarItem(CarItemKind.CollisionVolume, count - 1, taken);
        check("a volume can be dropped and put back at the same index — including the last one",
            taken != null && put && prefab.CarVolumeCount() == count,
            $"{prefab.CarVolumeCount()} volumes, was {count}");

        // …and the file that comes out is the file that went in, once everything has been put back.
        check("a prefab put back exactly is byte-identical to what was read",
            File.ReadAllBytes(path).AsSpan().SequenceEqual(prefab.ToBytes()), "");
    }

    /// <summary>
    /// What a SECOND influence on a vertex actually writes. Weighting a part to one bone works; weighting it
    /// to two — the panel and its deform bone, which is what a modeller reaches for — was reported to send it
    /// flying. Both bones are in the same remap pool, so the palette is not the answer; this reproduces the
    /// edit on a scratch copy and prints every slot of the result beside a SHIPPED multi-influence vertex,
    /// which is the only way to see which of the four bytes we get wrong.
    /// </summary>
    private static void SecondInfluence(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var car = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!car.Exists) return;
        string source = MafiaEnvironment.ExtractedDir(car);
        if (!File.Exists(Path.Combine(source, "SDSContent.xml"))) return;
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_carphys_skin");

        sb.AppendLine("\n\n════ giving a vertex a second influence, on a copy ════");
        try
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(scratch, Path.GetFileName(file)));

            FrameResource? fr = SdsMeshLoader.OpenScene(scratch).FrameResource;
            FrameObjectModel? model = fr?.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (fr == null || model == null) { sb.AppendLine("no skinned model"); return; }

            var document = new Illusion.Assets.Adapters.SceneDocumentAdapter(fr, car);
            Illusion.Assets.Adapters.FrameNodeAdapter node = document.Node(model);
            MeshObjectPayload? payload = BridgeMeshExporter.TryExport(node, document, out string? why);
            check("the car exports to the bridge", payload != null, why ?? "");
            if (payload == null) return;

            // What the SHIPPED data puts in a slot that carries no weight. If the game only ever blends the
            // influences a material declares, the id there means nothing and anything may sit in it — but if
            // the last weight is derived rather than stored, a leftover id is a bone that gets a share of the
            // vertex. The convention the shipped car follows is the one to copy either way.
            Illusion.Assets.Sds.DecodedMesh? before = SdsMeshLoader.DecodeLod0(model);
            if (before?.BoneWeights is { } bw && before.BoneIndices is { } bi)
            {
                int empty = 0, emptyNonZero = 0;
                for (int v = 0; v < before.NumVerts; v++)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        if (bw[(v * 4) + k] > 0f) continue;
                        empty++;
                        if (bi[(v * 4) + k] != 0) emptyNonZero++;
                    }
                }
                // Measured, and it settles the question the other way: 770 of 16765 shipped empty slots on
                // this car still name a bone. The game ignores a slot whose weight is zero, so leaving a
                // leftover id in one is the shipped convention rather than a fault — and a writer that
                // "cleaned" them would be changing bytes to chase a bug that is not there.
                sb.AppendLine($"    shipped: {empty} slots carry no weight, {emptyNonZero} of them still "
                    + "name a bone — so a leftover id in an unused slot is normal, not a fault");
            }

            string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray();

            // What the VANILLA bumper is weighted to, in full. "Copy what the shipped car does" is the only
            // reliable rule for a format nobody documented, and the shipped answer here is not a guess.
            byte[]? stockGlobal = SdsMeshLoader.GlobalBoneIds(model);
            if (before?.BoneWeights is { } sw && stockGlobal != null)
            {
                var sets = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int v = 0; v < before.NumVerts; v++)
                {
                    var names = new List<string>();
                    bool touchesBumper = false;
                    for (int k = 0; k < 4; k++)
                    {
                        if (sw[(v * 4) + k] <= 0f) continue;
                        byte id = stockGlobal[(v * 4) + k];
                        string bone = id < bones.Length ? bones[id] : $"#{id}";
                        if (bone.Contains("umperF", StringComparison.Ordinal)) touchesBumper = true;
                        names.Add($"{bone} {sw[(v * 4) + k]:F2}");
                    }
                    if (!touchesBumper) continue;
                    string key = string.Join(" + ", names);
                    sets[key] = sets.GetValueOrDefault(key) + 1;
                }
                sb.AppendLine($"    what the VANILLA front bumper is weighted to ({sets.Count} distinct "
                    + "combinations, most common first):");
                foreach ((string key, int count) in sets.OrderByDescending(p => p.Value).Take(12))
                {
                    sb.AppendLine($"        {count,5} vertices   {key}");
                }
            }

            int panel = Array.FindIndex(bones, n => string.Equals(n, "bumperF", StringComparison.Ordinal));
            check("the rig has the panel bone this reproduces against", panel >= 0, "bumperF");
            if (panel < 0) return;

            // Every welded vertex that already rides a deform bone of that panel gets the panel itself as a
            // SECOND influence, half and half — exactly the edit that was reported.
            var deformOf = new HashSet<byte>();
            for (int b = 0; b < bones.Length; b++)
            {
                if (bones[b].StartsWith("deform_bumperF", StringComparison.Ordinal)) deformOf.Add((byte)b);
            }
            int changed = 0;
            for (int v = 0; v < payload.Positions.Length; v++)
            {
                if (payload.BoneWeights.Length < (v * 4) + 4) break;
                if (payload.BoneWeights[(v * 4) + 1] > 0f) continue;   // already blended — leave it alone
                if (!deformOf.Contains(payload.BoneIndices[v * 4])) continue;
                payload.BoneWeights[(v * 4) + 0] = 0.5f;
                payload.BoneWeights[(v * 4) + 1] = 0.5f;
                payload.BoneIndices[(v * 4) + 1] = (byte)panel;
                changed++;
            }
            sb.AppendLine($"    {changed} vertices given a second influence (panel + its deform bone)");
            if (changed == 0)
            {
                // Nothing single-influence left on those bones — this car has already been re-weighted by
                // hand. That is a fact about the archive, not a fault in the toolkit, so it is reported and
                // not failed: the run simply has nothing to reproduce with.
                sb.AppendLine("    (every vertex on those bones already blends more than one — nothing to do)");
                return;
            }

            BridgeMeshApplier.ApplyResult? applied =
                BridgeMeshApplier.TryApply(node, payload, out string? applyWhy);
            check("a two-influence re-weight is accepted", applied != null, applyWhy ?? "");
            if (applied == null) return;
            applied.ApplyNew();

            sb.AppendLine($"    after applying: {SdsMeshLoader.DescribeBoneRemap(model)}");
            check("…and the skin still resolves afterwards", SdsMeshLoader.GlobalBoneIds(model) != null, "");

            // Every slot of a vertex we wrote, beside a slot-for-slot reading of a SHIPPED blended vertex.
            Illusion.Assets.Sds.DecodedMesh? after = SdsMeshLoader.DecodeLod0(model);
            byte[]? global = SdsMeshLoader.GlobalBoneIds(model);
            if (after?.BoneWeights is { } w && after.BoneIndices is { } local && global != null)
            {
                sb.AppendLine("    written vertices (id shown pool-local → resolved, with its weight):");
                int shown = 0;
                for (int v = 0; v < after.NumVerts && shown < 3; v++)
                {
                    if (w[(v * 4) + 1] <= 0f || !deformOf.Contains(global[v * 4])) continue;
                    sb.AppendLine("        " + SlotText(local, w, global, bones, v));
                    shown++;
                }
                sb.AppendLine("    shipped vertices that already blend three bones:");
                shown = 0;
                for (int v = 0; v < after.NumVerts && shown < 3; v++)
                {
                    if (w[(v * 4) + 2] <= 0f) continue;
                    sb.AppendLine("        " + SlotText(local, w, global, bones, v));
                    shown++;
                }
            }
            applied.RestoreOriginal();
        }
        catch (Exception ex) { check("the second-influence run completes", false, ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch leftovers are not a failure */ }
        }
    }

    private static string SlotText(byte[] local, float[] weights, byte[] global, string[] bones, int v)
    {
        var text = new StringBuilder();
        for (int k = 0; k < 4; k++)
        {
            byte id = global[(v * 4) + k];
            text.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"[{local[(v * 4) + k],3} → {(id < bones.Length ? bones[id] : "#" + id),-18} "
                + $"{weights[(v * 4) + k]:F2}]");
        }
        return text.ToString();
    }

    /// <summary>
    /// Which local axis a CAPSULE lies along. The overlay has been drawing one as a box stretched along
    /// local Y on the strength of the PhysX convention alone, and a capsule drawn along the wrong axis comes
    /// out as a diagonal slab across the part it is supposed to hug.
    /// <para>
    /// The oracle is the car's own symmetry, the same one <c>--probe-cars</c> used to settle how attachments
    /// are placed: capsules come in named left/right pairs, and under the right axis the pair's world
    /// directions must be mirror images about the car's centre plane. A wrong axis breaks that on every pair.
    /// </para>
    /// </summary>
    private static void CapsuleAxis(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ which axis a capsule lies along ════");
        FileInfo[] archives = new DirectoryInfo(folder).GetFiles("*.sds");
        Array.Sort(archives, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        double[] error = new double[3];
        double[] fit = new double[3];
        int pairs = 0, capsules = 0;
        foreach (FileInfo sds in archives)
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            if (fr?.FrameObjects == null) continue;

            Dictionary<ulong, ResolvedCollisionShape> shapes = CarCollisionShapes.Load(
                extracted, fr.FrameObjects.Values.OfType<FrameObjectCollision>());

            // The geometry the capsules sit in, once per car.
            Vector3[] mesh = [.. fr.FrameObjects.Values.OfType<FrameObjectModel>()
                .SelectMany(m => SdsMeshLoader.DecodeLod0(m)?.Positions ?? [])];
            if (mesh.Length == 0) continue;

            foreach (FrameObjectCollision stub in fr.FrameObjects.Values.OfType<FrameObjectCollision>())
            {
                if (!shapes.TryGetValue(stub.Hash, out ResolvedCollisionShape? found)
                    || found.Shape.Element is not RigidBodyElement
                        { Shape: RigidBodyShape.Capsule } capsule) continue;
                Matrix4x4 world = stub.WorldTransform;
                if (!Matrix4x4.Invert(world, out Matrix4x4 toLocal)) continue;
                capsules++;

                // The geometry this capsule is wrapped around, read in the SHAPE's own frame. A capsule is
                // long along one axis and 2r across the other two, so whichever axis the nearby geometry is
                // longest along is the axis the capsule lies along — provided it hugs anything at all.
                float reach = (capsule.Height * 0.5f) + (capsule.Radius * 3f);
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                int near = 0;
                foreach (Vector3 v in mesh)
                {
                    Vector3 local = Vector3.Transform(v, toLocal);
                    if (local.Length() > reach) continue;
                    near++;
                    lo = Vector3.Min(lo, local);
                    hi = Vector3.Max(hi, local);
                }
                if (near < 20) continue;   // nothing to hug — this capsule says nothing about the question

                pairs++;
                Vector3 span = hi - lo;
                float[] spans = [span.X, span.Y, span.Z];
                int longest = Array.IndexOf(spans, spans.Max());
                error[longest] += 1;       // reused as a tally: how often each axis is the longest

                // The stronger reading: not just WHICH axis is longest, but whether the geometry's own
                // extents match the capsule's numbers — 2r across, height + 2r along. Direction alone could
                // be an accident of what happens to sit nearby; matching all three cannot.
                for (int a = 0; a < 3; a++)
                {
                    float[] want = [capsule.Radius * 2f, capsule.Radius * 2f, capsule.Radius * 2f];
                    want[a] = capsule.Height + (capsule.Radius * 2f);
                    fit[a] += Math.Abs(spans[0] - want[0]) + Math.Abs(spans[1] - want[1])
                        + Math.Abs(spans[2] - want[2]);
                }
            }
        }

        string[] names = ["local X", "local Y", "local Z"];
        sb.AppendLine($"    {capsules} capsules, {pairs} of them wrapped around geometry; which local axis "
            + "that geometry is longest along:");
        for (int a = 0; a < 3; a++)
        {
            sb.AppendLine($"        {names[a]}  {error[a],5:F0} capsules");
        }
        sb.AppendLine("    …and how far the geometry's own extents are from the capsule's numbers "
            + "(2r across, height + 2r along) under each choice — lower is better:");
        for (int a = 0; a < 3; a++)
        {
            sb.AppendLine($"        {names[a]}  mean miss {fit[a] / Math.Max(1, pairs),7:F3} m");
        }
        int closest = Array.IndexOf(fit, fit.Min());
        sb.AppendLine($"    longest-axis says {names[Array.IndexOf(error, error.Max())]}, "
            + $"extent-fit says {names[closest]}");

        int best = Array.IndexOf(error, error.Max());
        check("a capsule lies along ONE axis, and the geometry it wraps says which",
            pairs > 0 && error[best] > error.Where((_, i) => i != best).Sum() * 2,
            $"{names[best]} on {error[best]:F0} of {pairs}");
    }

    // ── every car, counted ──

    private static void Census(StringBuilder sb, string folder, Action<string, bool, string> check,
        string focus = "berkley_kingfisher_pha")
    {
        sb.AppendLine("\n\n════ every extracted car ════");
        FileInfo[] archives = new DirectoryInfo(folder).GetFiles("*.sds");
        Array.Sort(archives, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        int cars = 0, stubs = 0, stubIdentity = 0, stubNoRotation = 0;
        int prefabCars = 0, parts = 0, volumes = 0, partsWithVolumes = 0, hashedVolumes = 0;
        var volumeTypes = new Dictionary<uint, int>();
        var partTypes = new Dictionary<uint, int>();
        int volumeHashResolved = 0, volumeHashTotal = 0;
        int partFrameResolved = 0, partFrameTotal = 0;

        // What a type-5 volume's hash actually names, and how the two copies of the same placement — the
        // prefab's and the frame stub's — line up. Both are the questions a writer has to answer.
        int hash5Total = 0, hash5IsDataHash = 0, hash5IsFileHash = 0, hash5First = 0;
        var shapeBehind = new Dictionary<RigidBodyShape, int>();
        int[] permHit = new int[48];
        int pairTotal = 0, rotationHit = 0, swapRotationHit = 0;
        var collectionCounts = new Dictionary<int, int>();
        var disagreeing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var shapeByPart = new Dictionary<(uint Part, RigidBodyShape Shape), int>();
        int indexListsMatchVolumes = 0, indexListsAny = 0, partsCounted = 0;
        var indexSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var indexMax = new Dictionary<string, int>(StringComparer.Ordinal);
        var indexLen = new Dictionary<(string List, string Of), int>();
        var indexFits = new Dictionary<(string List, string Of), int>();
        var indexSamples = new List<string>();
        int effectParts = 0, effectCars = 0, effectVariesWithinCar = 0, partsWithString = 0;
        var partStrings = new Dictionary<string, int>(StringComparer.Ordinal);
        var partSmall = new Dictionary<string, Dictionary<short, int>>(StringComparer.Ordinal)
        {
            ["Unk17"] = [], ["Unk18"] = [], ["Unk19"] = [],
        };
        var smallByKind = new Dictionary<(uint Kind, short Value), int>();
        var focusParts = new List<string>();
        var effectFields = new Dictionary<string, Dictionary<short, int>>(StringComparer.Ordinal)
        {
            ["ParticleBreakID"] = [],
            ["ParticleHingeVersionID"] = [],
            ["SnowParticleID_0"] = [],
            ["SnowParticleID_1"] = [],
        };
        var unk14Counts = new Dictionary<int, int>();
        var unk20Counts = new Dictionary<int, int>();
        int withUnk2Transform = 0, withUnk6 = 0;
        var typeByPart = new Dictionary<(uint Part, uint Volume), int>();
        int inlineTotal = 0, fitsAsFull = 0, fitsAsHalf = 0;
        int stubsClaimed = 0, shapesUnclaimed = 0, shapesTotal = 0;

        // A part's own Unk3 list — length distribution, and whether an entry that does resolve lands on a
        // BONE specifically (not just any frame of the archive), broken down by which bone name it is.
        var unk3CountHist = new Dictionary<int, int>();
        int unk3Entries = 0, unk3Resolved = 0, unk3ResolvedBone = 0;
        var unk3BoneByPartType = new Dictionary<(uint PartType, string Bone), int>();

        // A part's SmDeformBones list — length distribution, and whether its SmJointName hash resolves to a
        // frame, and specifically to one following the "deform_*" naming the reference toolkit's type suggests.
        var smDeformCountHist = new Dictionary<int, int>();
        int smDeformEntries = 0, smDeformResolved = 0, smDeformResolvedDeformPrefix = 0;

        // ParentDeformPartName: does it name a FRAME like every other reference in the prefab, or something
        // else (an ItemDesc shape, or nothing at all) — and does Unk17, documented as the parent part's INDEX,
        // point at a part whose own Unk3[0] is that same hash?
        int parentNameTotal = 0, parentNameResolvesFrame = 0, parentNameResolvesShape = 0, parentNameUnresolved = 0;
        int parentIndexTotal = 0, parentIndexAgreesWithName = 0, parentIndexOutOfRange = 0;

        // Follow-up: can a part's own bone (Unk3[0]) serve as a stable, unique identity for it? Is it unique
        // within its own car, does every bone of the rig belong to some part or is it an orphan, do the
        // deform-bone joints (SmJointName) ever double as a part's own bone, and where do the helper-frame
        // markers (seats, climb boxes, fuel tanks, exhaust emitters, wipers, lights) land relative to a part.
        int carsWithPrefabAndFrames = 0, carsWithNoUnk3Collision = 0, carsWithUnk3Collision = 0;
        var unk3CollisionExamples = new List<string>();
        int bonesTotal = 0, bonesOwnedByAPart = 0, bonesOrphan = 0;
        var orphanCountHist = new Dictionary<int, int>();
        var orphanBoneNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int smDeformOverlapsPartBone = 0;
        var markerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var markerOwned = new Dictionary<string, int>(StringComparer.Ordinal);
        var markerUnownedBone = new Dictionary<string, int>(StringComparer.Ordinal);
        var markerNoBone = new Dictionary<string, int>(StringComparer.Ordinal);

        // Follow-up: for a marker landing on a bone no part owns, does walking UP FrameSkeletonHierarchy's
        // ParentIndices reach a bone some part DOES own — and which component is that?
        int ancestorChecked = 0, ancestorResolved = 0, ancestorHitRoot = 0, ancestorCouldNotWalk = 0;
        var ancestorHopHist = new Dictionary<int, int>();
        var ancestorLandsOnBone = new Dictionary<(string Label, string Bone), int>();
        var ancestorLandsOnKind = new Dictionary<(string Label, uint PartType), int>();

        // Sanity check: for a marker ALREADY on an owned bone, the same walk must find it at 0 hops.
        int sanityChecked = 0, sanityHopZero = 0, sanityMismatch = 0;

        // The other direction: of the orphan bones (not a part's Unk3[0]), how many are claimed instead
        // through SmDeformBones' SmJointName, and how many are claimed by NEITHER link.
        int orphanClaimedBySmDeform = 0, orphanClaimedByNeither = 0;
        var neitherClaimedNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        // Follow-up: of the bones claimed by NEITHER link, are they real things — do they carry geometry,
        // hit boxes, a reference from some other prefab collection, or a helper frame hung off them?
        int neitherWithGeometry = 0, neitherGeomWithNonZeroBox = 0;
        var neitherPiecesHist = new Dictionary<int, int>();
        var neitherWithGeomNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var neitherNoGeomNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int neitherClaimedByOtherRef = 0, neitherClaimedByNothingAtAll = 0;
        var neitherRefCollectionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int neitherWithAttachment = 0;

        foreach (FileInfo sds in archives)
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            if (fr?.FrameObjects == null) continue;
            cars++;

            // How big this car is, longest side first — the yardstick the "are extents half or full" test
            // measures a box against.
            var carLo = new Vector3(float.MaxValue);
            var carHi = new Vector3(float.MinValue);
            foreach (FrameObjectSingleMesh mesh in fr.FrameObjects.Values.OfType<FrameObjectSingleMesh>())
            {
                carLo = Vector3.Min(carLo, mesh.Boundings.Min);
                carHi = Vector3.Max(carHi, mesh.Boundings.Max);
            }
            float[] carSpan = carHi.X < carLo.X ? [0f, 0f, 0f]
                : [.. new[] { carHi.X - carLo.X, carHi.Y - carLo.Y, carHi.Z - carLo.Z }
                    .OrderByDescending(v => v)];

            var names = new Dictionary<ulong, string>();
            var boneHashes = new Dictionary<ulong, string>();
            foreach (FrameObjectBase any in fr.FrameObjects.Values)
            {
                string? n = any.Name?.ToString();
                if (n != null) names.TryAdd(Fnv64.Hash(n), n);
            }
            foreach (FrameObjectModel m in fr.FrameObjects.Values.OfType<FrameObjectModel>())
            {
                foreach (Illusion.Formats.Hashing.HashName bone in m.GetSkeletonObject().BoneNames ?? [])
                {
                    string n = bone.ToString() ?? "";
                    if (n.Length > 0)
                    {
                        names.TryAdd(Fnv64.Hash(n), n);
                        boneHashes.TryAdd(Fnv64.Hash(n), n);
                    }
                }
            }

            // For the marker-ownership question: which joint each named (non-bone) frame hangs off, and the
            // rig's bone names in JOINT order (needed to turn a joint index back into the hash Unk3 would use).
            FrameObjectModel? carModel = fr.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
            var jointOf = new Dictionary<FrameObjectBase, int>();
            foreach (FrameObjectModel.AttachmentReference r in carModel?.AttachmentReferences ?? [])
            {
                if (r.Attachment != null) jointOf[r.Attachment] = r.JointIndex;
            }
            string[] carBonesByJoint = carModel != null
                ? (carModel.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray()
                : [];
            var frameByHash = new Dictionary<ulong, FrameObjectBase>();
            foreach (FrameObjectBase f in fr.FrameObjects.Values)
            {
                string? n = f.Name?.ToString();
                if (n != null) frameByHash.TryAdd(Fnv64.Hash(n), f);
            }
            // Joint index of each bone, by its OWN name hash — the starting point for walking up
            // FrameSkeletonHierarchy.ParentIndices from a marker's attachment bone.
            var boneIndexByHash = new Dictionary<ulong, int>();
            for (int bi = 0; bi < carBonesByJoint.Length; bi++)
            {
                boneIndexByHash.TryAdd(Fnv64.Hash(carBonesByJoint[bi]), bi);
            }
            byte[] carParentIndices = carModel?.GetSkeletonHierarchyObject().ParentIndices ?? [];

            var stubByShapeHash = new Dictionary<ulong, FrameObjectCollision>();
            foreach (FrameObjectCollision stub in fr.FrameObjects.Values.OfType<FrameObjectCollision>())
            {
                stubs++;
                stubByShapeHash.TryAdd(stub.Hash, stub);
                if (stub.LocalTransform.Translation.Length() < 1e-4f
                    && IsRotationIdentity(stub.LocalTransform)) stubIdentity++;
                if (IsRotationIdentity(stub.LocalTransform)) stubNoRotation++;
            }

            // The archive's own physics shapes, under BOTH keys they can be named by: the record's file hash
            // (which is what a frame stub uses) and the element's data hash.
            var shapeByFileHash = new Dictionary<ulong, RigidBodyElement>();
            var shapeByDataHash = new Dictionary<ulong, (RigidBodyElement Rigid, ulong FileHash)>();
            try
            {
                foreach (string ids in SdsManifest.Load(extracted).GetFiles("ItemDesc"))
                {
                    try
                    {
                        ItemDescFile shape = ItemDescFile.Load(ids);
                        if (shape.Element is not RigidBodyElement rigid) continue;
                        shapesTotal++;
                        shapeByFileHash.TryAdd(shape.Hash, rigid);
                        shapeByDataHash.TryAdd(rigid.DataHash, (rigid, shape.Hash));
                    }
                    catch (Exception) { /* a shape this library cannot read is not this census's subject */ }
                }
            }
            catch (Exception) { /* no manifest, no shapes */ }
            var claimed = new HashSet<ulong>();

            string? prf = null;
            try { prf = SdsManifest.Load(extracted).GetFiles("PREFAB").FirstOrDefault(); }
            catch (Exception) { /* ditto */ }
            if (prf == null) continue;

            PrefabFile file;
            try { file = PrefabFile.Load(prf); }
            catch (Exception) { continue; }
            PrefabEntryW? entry = file.Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0);
            if (entry == null || entry.CarInit[0].Deformation.Count == 0) continue;
            prefabCars++;
            PrefabCarInitW carInit = entry.CarInit[0];
            List<PrefabDeformPartW> deformParts = carInit.Deformation[0].DeformParts;
            PrefabOtherInitW? other = carInit.Other.Count > 0 ? carInit.Other[0] : null;

            // ── injectivity: is Unk3[0] unique across the parts of THIS car? ──
            carsWithPrefabAndFrames++;
            var partsByFrame = new Dictionary<ulong, List<PrefabDeformPartW>>();
            foreach (PrefabDeformPartW p in deformParts)
            {
                if (p.Unk3.Count == 0) continue;
                ulong key = p.Unk3[0];
                if (!partsByFrame.TryGetValue(key, out List<PrefabDeformPartW>? list))
                {
                    list = [];
                    partsByFrame[key] = list;
                }
                list.Add(p);
            }
            List<KeyValuePair<ulong, List<PrefabDeformPartW>>> collisionsHere =
                [.. partsByFrame.Where(kv => kv.Value.Count > 1)];
            if (collisionsHere.Count == 0) carsWithNoUnk3Collision++;
            else
            {
                carsWithUnk3Collision++;
                foreach ((ulong hash, List<PrefabDeformPartW> list) in collisionsHere)
                {
                    string boneName = names.GetValueOrDefault(hash, $"0x{hash:X16}");
                    string kindsText = string.Join("+",
                        list.Select(p => PartTypeNames.GetValueOrDefault(p.PartType, p.PartType.ToString())));
                    unk3CollisionExamples.Add(
                        $"{Path.GetFileNameWithoutExtension(sds.Name)}: {boneName} <- {kindsText}");
                }
            }
            // The set of bones some deform part of THIS car claims as its own — used for the coverage check
            // below, for the deform-bone overlap check inside the part loop, and for the marker question.
            var ownedBoneHashesHere = new HashSet<ulong>(partsByFrame.Keys);

            // The set of bones claimed through the OTHER link — a part's SmDeformBones' SmJointName — used
            // only for the "claimed by neither link" follow-up below.
            var smOwnedBoneHashesHere = new HashSet<ulong>();
            foreach (PrefabDeformPartW p in deformParts)
            {
                foreach (PrefabSmDeformBoneW sm in p.SmDeformBones) smOwnedBoneHashesHere.Add(sm.SmJointName);
            }

            // Are the bones claimed by NEITHER link real things? First, geometry: flatten this car's split
            // table into (bone hash → absolute piece indices), using the MEASURED reading — a split's bone is
            // BoneRemapIDs[BlendIndex] (LOD0's flat remap table), right 98.89% of the time; the raw BlendIndex
            // read as a bone id is right only 2.2% of the time (docs/car-anatomy.md, "Splits and pieces").
            // Piece index is a running counter across ALL splits in order, which is what HitBoxes is indexed
            // by — the same reading BulletProbes.PiecesOf uses.
            byte[] flatRemap = [];
            try
            {
                Illusion.Formats.Frames.Resources.FrameBlendInfo.BoneIndexInfo[] lods =
                    carModel?.GetBlendInfoObject().BoneIndexInfos ?? [];
                if (lods.Length > 0) flatRemap = lods[0].BoneRemapIDs ?? [];
            }
            catch (Exception) { /* a model with no blend info has no geometry to attribute to a bone */ }
            var pieceIndicesByBoneHash = new Dictionary<ulong, List<int>>();
            int pieceIndex = 0;
            foreach (FrameObjectModel.WeightedByMeshSplit split in carModel?.BlendMeshSplits ?? [])
            {
                int boneId = split.BlendIndex < flatRemap.Length ? flatRemap[split.BlendIndex] : -1;
                ulong splitBoneHash = boneId >= 0 && boneId < carBonesByJoint.Length
                    ? Fnv64.Hash(carBonesByJoint[boneId]) : 0;
                int pieces = split.Data?.Length ?? 0;
                if (splitBoneHash != 0)
                {
                    if (!pieceIndicesByBoneHash.TryGetValue(splitBoneHash, out List<int>? list))
                    {
                        list = [];
                        pieceIndicesByBoneHash[splitBoneHash] = list;
                    }
                    for (int k = 0; k < pieces; k++) list.Add(pieceIndex + k);
                }
                pieceIndex += pieces;
            }
            FrameObjectModel.HitBoxInfo[] carHitBoxes = carModel?.HitBoxes ?? [];

            // Attachments: which joints have a helper frame (Dummy/Point) hung off them via
            // AttachmentReferences — reusing jointOf, which already carries every attached frame of this car.
            var attachedJoints = new HashSet<int>();
            foreach ((FrameObjectBase attached, int atJoint) in jointOf)
            {
                if (attached is FrameObjectDummy or FrameObjectPoint) attachedJoints.Add(atJoint);
            }

            // Referenced anywhere else in the prefab — every sibling collection that names a frame besides
            // a deform part's own Unk3[0] and SmDeformBones.
            var otherReferenceHashes = new Dictionary<string, HashSet<ulong>>(StringComparer.Ordinal);
            void AddRef(string collection, ulong hash)
            {
                if (hash == 0) return;
                if (!otherReferenceHashes.TryGetValue(collection, out HashSet<ulong>? set))
                {
                    set = [];
                    otherReferenceHashes[collection] = set;
                }
                set.Add(hash);
            }
            if (other != null)
            {
                AddRef("headlight", other.HeadlightModelName);
                AddRef("backlight", other.BacklightModelName);
                AddRef("toplight", other.ToplightModelName);
                AddRef("snow rest", other.SnowRestName);
                foreach (ulong h in other.DrivingWheels) AddRef("driving wheel", h);
                foreach (ulong h in other.FuelTanks) AddRef("fuel tank", h);
                foreach (ulong h in other.ExhaustEmitters) AddRef("exhaust emitter", h);
                foreach (PrefabWindowDataW w in other.WindowData) AddRef("window", w.WindowFrameName);
            }
            foreach (ulong h in carInit.WipersFrameName) AddRef("wiper", h);
            foreach (PrefabAxleW axle in carInit.Axles)
            {
                AddRef("axle", axle.AxleName);
                AddRef("axle brake drum", axle.BrakeDrumName);
                AddRef("axle rot wing", axle.RotWingName);
            }
            foreach (PrefabDoorPointsW d in carInit.DoorPoints) AddRef("door points", d.DoorFrameName);
            foreach (PrefabSeatW s in carInit.Seats)
            {
                AddRef("seat", s.FrameName);
                AddRef("seat's door", s.DoorIndexFrameName);
            }
            foreach (PrefabClimbBoxW c in carInit.ClimbBoxes)
            {
                AddRef("climb box dummy", c.DummyFrameName);
                AddRef("climb box bone", c.BoneFrameName);
            }

            // ── coverage the other way: of the rig's own bones, how many does a deform part actually name? ──
            bonesTotal += boneHashes.Count;
            int ownedHere = 0;
            foreach ((ulong boneHash, string boneName) in boneHashes)
            {
                if (ownedBoneHashesHere.Contains(boneHash)) { ownedHere++; continue; }
                orphanBoneNameCounts[boneName] = orphanBoneNameCounts.GetValueOrDefault(boneName) + 1;

                // Of this orphan (not claimed via Unk3[0]): is it claimed instead via SmDeformBones? If
                // neither link claims it, note the name so the "true orphan" list can be read off by eye.
                if (smOwnedBoneHashesHere.Contains(boneHash)) orphanClaimedBySmDeform++;
                else
                {
                    orphanClaimedByNeither++;
                    neitherClaimedNameCounts[boneName] = neitherClaimedNameCounts.GetValueOrDefault(boneName) + 1;

                    // Is this "claimed by neither link" bone a real thing? Geometry first: is it named as a
                    // split's bone (BoneRemapIDs[BlendIndex]), and if so, do any of its pieces carry a
                    // non-placeholder hit box?
                    if (pieceIndicesByBoneHash.TryGetValue(boneHash, out List<int>? pieceIdx)
                        && pieceIdx.Count > 0)
                    {
                        neitherWithGeometry++;
                        neitherPiecesHist[pieceIdx.Count] =
                            neitherPiecesHist.GetValueOrDefault(pieceIdx.Count) + 1;
                        neitherWithGeomNameCounts[boneName] =
                            neitherWithGeomNameCounts.GetValueOrDefault(boneName) + 1;
                        if (pieceIdx.Any(pi => pi < carHitBoxes.Length && IsNonZeroBox(carHitBoxes[pi])))
                        {
                            neitherGeomWithNonZeroBox++;
                        }
                    }
                    else
                    {
                        neitherNoGeomNameCounts[boneName] =
                            neitherNoGeomNameCounts.GetValueOrDefault(boneName) + 1;
                    }

                    // Referenced by some OTHER prefab collection we were not checking?
                    bool claimedElsewhere = false;
                    foreach ((string collection, HashSet<ulong> set) in otherReferenceHashes)
                    {
                        if (!set.Contains(boneHash)) continue;
                        neitherRefCollectionCounts[collection] =
                            neitherRefCollectionCounts.GetValueOrDefault(collection) + 1;
                        claimedElsewhere = true;
                    }
                    if (claimedElsewhere) neitherClaimedByOtherRef++; else neitherClaimedByNothingAtAll++;

                    // Does a helper frame (Dummy/Point) hang off it via AttachmentReferences?
                    if (boneIndexByHash.TryGetValue(boneHash, out int neitherJoint)
                        && attachedJoints.Contains(neitherJoint))
                    {
                        neitherWithAttachment++;
                    }
                }
            }
            bonesOwnedByAPart += ownedHere;
            int orphanHere = boneHashes.Count - ownedHere;
            bonesOrphan += orphanHere;
            orphanCountHist[orphanHere] = orphanCountHist.GetValueOrDefault(orphanHere) + 1;

            // ── markers: seats, climb boxes, fuel tanks, exhaust emitters, wipers, lights ──
            // Each names a frame directly (sometimes a bone itself, sometimes a Dummy/Point hung off one via
            // AttachmentReferences); resolved here to the bone it actually lands on, so it can be asked
            // whether that bone is one a deform part owns.
            // Bone-and-Found logic is UNCHANGED from before (so Owned/Unowned/NoBone totals stay identical);
            // Joint is best-effort, -1 when a starting joint cannot be pinned down, in which case the
            // ancestor walk below is skipped and counted separately as "could not walk".
            (ulong Bone, bool Found, int Joint) ResolveToBone(ulong hash)
            {
                if (hash == 0) return (0, false, -1);
                if (boneHashes.ContainsKey(hash))
                {
                    int idx = boneIndexByHash.TryGetValue(hash, out int i) ? i : -1;
                    return (hash, true, idx);
                }
                if (frameByHash.TryGetValue(hash, out FrameObjectBase? f)
                    && jointOf.TryGetValue(f, out int joint) && joint >= 0 && joint < carBonesByJoint.Length)
                {
                    return (Fnv64.Hash(carBonesByJoint[joint]), true, joint);
                }
                return (0, false, -1);
            }

            // Walk up FrameSkeletonHierarchy.ParentIndices from startJoint (checked FIRST, so hop 0 means
            // "the starting bone itself is owned") until an owned bone is found, the rig root is reached
            // (root is self-parented, the same convention DumpOneCar's own rig dump uses), or the hierarchy
            // proves unusable (a cycle, or an index outside the bone table).
            (bool Found, int Hops, ulong Bone, bool HitRoot, bool Malformed) WalkToOwnedAncestor(int startJoint)
            {
                if (startJoint < 0 || startJoint >= carBonesByJoint.Length || carParentIndices.Length == 0)
                {
                    return (false, 0, 0, false, true);
                }
                var visited = new HashSet<int>();
                int joint = startJoint;
                int hops = 0;
                while (true)
                {
                    if (!visited.Add(joint)) return (false, hops, 0, false, true);
                    ulong hash = Fnv64.Hash(carBonesByJoint[joint]);
                    if (ownedBoneHashesHere.Contains(hash)) return (true, hops, hash, false, false);
                    if (joint >= carParentIndices.Length) return (false, hops, 0, false, true);
                    int parent = carParentIndices[joint];
                    if (parent < 0 || parent >= carBonesByJoint.Length || parent == joint)
                    {
                        return (false, hops, 0, true, false);
                    }
                    joint = parent;
                    hops++;
                }
            }

            (string Label, IEnumerable<ulong> Hashes)[] markerSources =
            [
                ("seat", carInit.Seats.Select(s => s.FrameName)),
                ("climb box", carInit.ClimbBoxes.Select(b => b.DummyFrameName)),
                ("fuel tank", other?.FuelTanks ?? []),
                ("exhaust emitter", other?.ExhaustEmitters ?? []),
                ("wiper", carInit.WipersFrameName),
                ("light", other == null
                    ? Array.Empty<ulong>()
                    : new[] { other.HeadlightModelName, other.BacklightModelName, other.ToplightModelName }),
            ];
            foreach ((string label, IEnumerable<ulong> hashes) in markerSources)
            {
                foreach (ulong hash in hashes)
                {
                    if (hash == 0) continue;
                    markerCounts[label] = markerCounts.GetValueOrDefault(label) + 1;
                    (ulong boneHash, bool found, int joint) = ResolveToBone(hash);
                    if (!found) { markerNoBone[label] = markerNoBone.GetValueOrDefault(label) + 1; continue; }

                    bool owned = ownedBoneHashesHere.Contains(boneHash);
                    if (owned) markerOwned[label] = markerOwned.GetValueOrDefault(label) + 1;
                    else markerUnownedBone[label] = markerUnownedBone.GetValueOrDefault(label) + 1;

                    if (owned)
                    {
                        // Sanity check: the SAME walk, from a marker that is already on an owned bone, must
                        // land on that owned bone at 0 hops — it should not need to move at all.
                        sanityChecked++;
                        if (joint < 0)
                        {
                            // No joint to start from, but ownership was already decided directly off the
                            // bone hash — that IS the owned bone, trivially 0 hops.
                            sanityHopZero++;
                        }
                        else
                        {
                            (bool sFound, int sHops, _, _, _) = WalkToOwnedAncestor(joint);
                            if (sFound && sHops == 0) sanityHopZero++; else sanityMismatch++;
                        }
                    }
                    else
                    {
                        // Ancestor fallback: walking up from an unowned marker's attachment bone, does it
                        // reach a bone some part DOES own — and through which component?
                        ancestorChecked++;
                        if (joint < 0) { ancestorCouldNotWalk++; continue; }

                        (bool aFound, int aHops, ulong aBone, bool aHitRoot, bool aMalformed) =
                            WalkToOwnedAncestor(joint);
                        if (aMalformed) ancestorCouldNotWalk++;
                        else if (aFound)
                        {
                            ancestorResolved++;
                            ancestorHopHist[aHops] = ancestorHopHist.GetValueOrDefault(aHops) + 1;
                            string ownerBone = names.GetValueOrDefault(aBone, $"0x{aBone:X16}");
                            ancestorLandsOnBone[(label, ownerBone)] =
                                ancestorLandsOnBone.GetValueOrDefault((label, ownerBone)) + 1;
                            if (partsByFrame.TryGetValue(aBone, out List<PrefabDeformPartW>? owners)
                                && owners.Count > 0)
                            {
                                ancestorLandsOnKind[(label, owners[0].PartType)] =
                                    ancestorLandsOnKind.GetValueOrDefault((label, owners[0].PartType)) + 1;
                            }
                        }
                        else if (aHitRoot) ancestorHitRoot++;
                        else ancestorCouldNotWalk++;
                    }
                }
            }

            var breakIdsHere = new HashSet<short>();
            foreach (PrefabDeformPartW part in deformParts)
            {
                parts++;
                partTypes[part.PartType] = partTypes.GetValueOrDefault(part.PartType) + 1;
                foreach (ulong h in part.Unk3)
                {
                    partFrameTotal++;
                    if (names.ContainsKey(h)) partFrameResolved++;
                }

                // How many frames a part's Unk3 names, and — for the ones that resolve — which BONE, so a
                // part of kind "door" naming a bone called doorFL can be read straight off the tally.
                unk3CountHist[part.Unk3.Count] = unk3CountHist.GetValueOrDefault(part.Unk3.Count) + 1;
                foreach (ulong h in part.Unk3)
                {
                    unk3Entries++;
                    if (names.ContainsKey(h)) unk3Resolved++;
                    if (boneHashes.TryGetValue(h, out string? boneName))
                    {
                        unk3ResolvedBone++;
                        unk3BoneByPartType[(part.PartType, boneName)] =
                            unk3BoneByPartType.GetValueOrDefault((part.PartType, boneName)) + 1;
                    }
                }

                // How many SmDeformBones a part carries, and whether SmJointName resolves to a frame — and
                // specifically to one named deform_*.
                smDeformCountHist[part.SmDeformBones.Count] =
                    smDeformCountHist.GetValueOrDefault(part.SmDeformBones.Count) + 1;
                foreach (PrefabSmDeformBoneW smBone in part.SmDeformBones)
                {
                    smDeformEntries++;
                    if (names.TryGetValue(smBone.SmJointName, out string? jointName))
                    {
                        smDeformResolved++;
                        if (jointName.StartsWith("deform_", StringComparison.OrdinalIgnoreCase))
                        {
                            smDeformResolvedDeformPrefix++;
                        }
                    }
                    // Does a deform-bone joint ever double as a PART's own bone (Unk3[0]) in the same car?
                    if (ownedBoneHashesHere.Contains(smBone.SmJointName)) smDeformOverlapsPartBone++;
                }

                // ParentDeformPartName against Unk17. The name should be a frame hash like every other
                // reference in the prefab; the index should point at the part whose OWN Unk3[0] is that hash.
                if (part.ParentDeformPartName != 0)
                {
                    parentNameTotal++;
                    if (names.ContainsKey(part.ParentDeformPartName)) parentNameResolvesFrame++;
                    else if (shapeByDataHash.ContainsKey(part.ParentDeformPartName)) parentNameResolvesShape++;
                    else parentNameUnresolved++;
                }
                if (part.Unk17 != 65535)
                {
                    parentIndexTotal++;
                    List<PrefabDeformPartW> allParts = deformParts;
                    if (part.Unk17 >= allParts.Count)
                    {
                        parentIndexOutOfRange++;
                    }
                    else
                    {
                        ulong parentOwnFrame = allParts[part.Unk17].Unk3.Count > 0
                            ? allParts[part.Unk17].Unk3[0] : 0;
                        if (parentOwnFrame != 0 && parentOwnFrame == part.ParentDeformPartName)
                        {
                            parentIndexAgreesWithName++;
                        }
                    }
                }

                int here = part.CollisionVolumes.Sum(c => c.Volumes.Count);
                if (here > 0) partsWithVolumes++;
                volumes += here;
                collectionCounts[part.CollisionVolumes.Count] =
                    collectionCounts.GetValueOrDefault(part.CollisionVolumes.Count) + 1;

                // Two unnamed lists of ushorts sit on every deformable part. If either of them is an index
                // INTO the volume list, a volume added without one would be a volume the game never walks —
                // which is exactly the symptom to explain.
                // The per-part STRING. materials_shots.tbl is keyed by a NAME (plech, kov, sklo…), and this is
                // the only string a deformable part carries — so if a part names its surface anywhere, here.
                foreach (PrefabDeformPartCommonW named in part.Common)
                {
                    foreach (string s in named.Unk6Value)
                    {
                        partStrings[s] = partStrings.GetValueOrDefault(s) + 1;
                    }
                    if (named.Unk6Value.Count > 0) partsWithString++;
                }
                // Three small unmeasured numbers on the part itself. A surface index would fit any of them,
                // and materials_shots has 190 rows.
                Bump(partSmall["Unk17"], (short)part.Unk17);
                Bump(partSmall["Unk18"], part.Unk18);
                Bump(partSmall["Unk19"], part.Unk19);
                smallByKind[(part.PartType, (short)part.Unk19)] =
                    smallByKind.GetValueOrDefault((part.PartType, (short)part.Unk19)) + 1;
                if (string.Equals(Path.GetFileNameWithoutExtension(sds.Name), focus, StringComparison.OrdinalIgnoreCase))
                {
                    focusParts.Add($"{(names.TryGetValue(part.Unk3.FirstOrDefault(), out string? pn) ? pn : "?"),-16}"
                        + $" kind {PartTypeNames.GetValueOrDefault(part.PartType, "?"),-8}"
                        + $" Unk17 {(short)part.Unk17,4}  Unk19 {part.Unk19,3}  Unk23 {part.Unk23,3}  Unk24 {part.Unk24,3}");
                }

                // The effects block, one per part, inside the part.s common tail.
                foreach (PrefabDeformPartCommonW common in part.Common)
                {
                    foreach (PrefabDeformPartEffectsW fx in common.PartEffects)
                    {
                        effectParts++;
                        Bump(effectFields["ParticleBreakID"], fx.ParticleBreakId);
                        Bump(effectFields["ParticleHingeVersionID"], fx.ParticleHingeVersionId);
                        Bump(effectFields["SnowParticleID_0"], fx.SnowParticleId0);
                        Bump(effectFields["SnowParticleID_1"], fx.SnowParticleId1);
                        breakIdsHere.Add(fx.ParticleBreakId);
                    }
                }

                partsCounted++;
                unk14Counts[part.Unk14.Count] = unk14Counts.GetValueOrDefault(part.Unk14.Count) + 1;
                unk20Counts[part.Unk20.Count] = unk20Counts.GetValueOrDefault(part.Unk20.Count) + 1;
                if (part.Unk14.Count > 0 || part.Unk20.Count > 0) indexListsAny++;
                if (part.Unk14.Count == here || part.Unk20.Count == here) indexListsMatchVolumes++;

                // WHAT they are lists OF. "As long as the volume list" is a weak reading: a part with one
                // volume and one deform bone matches both. So every sibling list of the part is offered the
                // same question, and the values are range-checked against each candidate — an index list
                // cannot hold a number past the end of what it indexes.
                (string Name, int Count)[] siblings =
                [
                    ("volumes", here),
                    ("deform bones", part.SmDeformBones.Count),
                    ("drop parts", part.DropParts.Count),
                    ("drain energy", part.DrainEnergy.Count),
                    ("impulses", part.InternalImpulses.Count),
                    ("frames", part.Unk3.Count),
                    ("parts in the car", deformParts.Count),
                ];
                foreach ((string listName, IReadOnlyList<ushort> list) in
                         new (string, IReadOnlyList<ushort>)[] { ("Unk14", part.Unk14), ("Unk20", part.Unk20) })
                {
                    if (list.Count == 0) continue;
                    indexSeen[listName] = indexSeen.GetValueOrDefault(listName) + 1;
                    indexMax[listName] = Math.Max(indexMax.GetValueOrDefault(listName), list.Max());
                    foreach ((string sibling, int count) in siblings)
                    {
                        if (list.Count == count) indexLen[(listName, sibling)] =
                            indexLen.GetValueOrDefault((listName, sibling)) + 1;
                        // A list of indices INTO something never names a slot that thing does not have.
                        if (count > 0 && list.All(x => x < count)) indexFits[(listName, sibling)] =
                            indexFits.GetValueOrDefault((listName, sibling)) + 1;
                    }
                    if (indexSamples.Count < 12 && list.Count > 1)
                    {
                        indexSamples.Add($"{Path.GetFileNameWithoutExtension(sds.Name),-24} part {parts - 1,2} "
                            + $"{PartTypeNames.GetValueOrDefault(part.PartType, "?"),-8} {listName} "
                            + $"[{string.Join(",", list)}]  vols {here} bones {part.SmDeformBones.Count} "
                            + $"drops {part.DropParts.Count} frames {part.Unk3.Count}");
                    }
                }
                foreach (PrefabCollVolumeCollectionW c in part.CollisionVolumes)
                {
                    foreach (PrefabCollVolumeW v in c.Volumes)
                    {
                        volumeTypes[v.VolumeType] = volumeTypes.GetValueOrDefault(v.VolumeType) + 1;
                        if (v.Unk2Transform.Count > 0) withUnk2Transform++;
                        if (v.Unk6.Count > 0) withUnk6++;
                        typeByPart[(part.PartType, v.VolumeType)] =
                            typeByPart.GetValueOrDefault((part.PartType, v.VolumeType)) + 1;
                        if (v.Unk4Hashes.Count > 0) hashedVolumes++;
                        foreach (ulong h in v.Unk4Hashes)
                        {
                            volumeHashTotal++;
                            if (names.ContainsKey(h)) volumeHashResolved++;
                        }

                        if (v.Unk4Hashes.Count == 2)
                        {
                            // The pair is (something, shape). Which slot carries the shape is measured, not
                            // assumed — a writer that fills the wrong one produces a car with no collision.
                            if (v.Unk4Hashes[0] != 0) hash5First++;
                            hash5Total++;
                            ulong key = v.Unk4Hashes[1];
                            if (shapeByDataHash.TryGetValue(key, out (RigidBodyElement Rigid, ulong FileHash) hit))
                            {
                                hash5IsDataHash++;
                                claimed.Add(hit.FileHash);
                                shapeBehind[hit.Rigid.Shape] = shapeBehind.GetValueOrDefault(hit.Rigid.Shape) + 1;
                                shapeByPart[(part.PartType, hit.Rigid.Shape)] =
                                    shapeByPart.GetValueOrDefault((part.PartType, hit.Rigid.Shape)) + 1;

                                // The same placement, written down twice: once here and once as the frame
                                // stub's own matrix. Which axis order relates them is what a writer needs.
                                if (stubByShapeHash.TryGetValue(hit.FileHash, out FrameObjectCollision? stub))
                                {
                                    pairTotal++;
                                    Vector3 raw = v.Transform.Translation;
                                    Vector3 want = stub.LocalTransform.Translation;
                                    for (int k = 0; k < 48; k++)
                                    {
                                        if ((Permute(raw, k) - want).Length() < 1e-3f) permHit[k]++;
                                    }
                                    if ((new Vector3(raw.Z, raw.Y, raw.X) - want).Length() >= 1e-3f)
                                    {
                                        disagreeing[sds.Name] = disagreeing.GetValueOrDefault(sds.Name) + 1;
                                    }
                                    if (RotationMatches(v, stub.LocalTransform)) rotationHit++;
                                    if (SwapRotationMatches(v, stub.LocalTransform)) swapRotationHit++;
                                }
                            }
                            else if (shapeByFileHash.ContainsKey(key)) hash5IsFileHash++;
                        }
                        else
                        {
                            // Volumes that carry no hash have to describe themselves — the extents are the
                            // shape. Whether those are half-sizes or full sizes decides every box written.
                            inlineTotal++;
                            float[] e = [.. new[] { v.Extents.X, v.Extents.Y, v.Extents.Z }
                                .OrderByDescending(x => x)];
                            if (carSpan[0] > 0f)
                            {
                                if (e[0] <= carSpan[0] && e[1] <= carSpan[1] && e[2] <= carSpan[2]) fitsAsFull++;
                                if (e[0] * 2f <= carSpan[0] && e[1] * 2f <= carSpan[1]
                                    && e[2] * 2f <= carSpan[2]) fitsAsHalf++;
                            }
                        }
                    }
                }
            }

            // Does THIS car use more than one break-effect id across its own parts? A field that is one
            // value per car would be a car property, not a part property, and could not explain a roof
            // drawing something the bonnet does not.
            if (breakIdsHere.Count > 0)
            {
                effectCars++;
                if (breakIdsHere.Count > 1) effectVariesWithinCar++;
            }

            stubsClaimed += claimed.Count;
            shapesUnclaimed += shapeByFileHash.Count - claimed.Count;
        }

        sb.AppendLine($"{cars} extracted car archives, {stubs} collision stubs, {prefabCars} with a car prefab");
        sb.AppendLine($"    stub local transform is the identity  {stubIdentity} of {stubs}"
            + $"   (no rotation: {stubNoRotation})");
        sb.AppendLine($"    deform parts {parts}, of which {partsWithVolumes} carry collision volumes; "
            + $"{volumes} volumes, {hashedVolumes} name frames");
        sb.AppendLine("    volume types: " + string.Join(", ",
            volumeTypes.OrderByDescending(p => p.Value).Select(p => $"{p.Key}×{p.Value}")));
        sb.AppendLine("    part types:   " + string.Join(", ",
            partTypes.OrderByDescending(p => p.Value).Select(p => $"{p.Key}×{p.Value}")));
        sb.AppendLine($"    a deform part's own hashes resolve to a frame of the same archive: "
            + $"{partFrameResolved} of {partFrameTotal}");
        sb.AppendLine($"    a collision volume's hashes resolve to a frame of the same archive: "
            + $"{volumeHashResolved} of {volumeHashTotal}");

        check("a collision volume's hashes are NOT frame names",
            volumeHashTotal > 0 && volumeHashResolved == 0, $"{volumeHashResolved} of {volumeHashTotal}");
        check("a deform part names frames of its own model",
            partFrameTotal > 0 && partFrameResolved * 2 > partFrameTotal,
            $"{partFrameResolved} of {partFrameTotal}");

        // ── Unk3: how many frames a part names, and which bone it is when it resolves to one ──
        int unk3Zero = unk3CountHist.GetValueOrDefault(0);
        int unk3One = unk3CountHist.GetValueOrDefault(1);
        int unk3TwoPlus = unk3CountHist.Where(p => p.Key >= 2).Sum(p => p.Value);
        sb.AppendLine($"\n  a part's own Unk3 list, length distribution over {parts} parts:");
        sb.AppendLine("    " + string.Join(", ", unk3CountHist.OrderBy(p => p.Key)
            .Select(p => $"{p.Key}×{p.Value}")));
        sb.AppendLine($"    0 entries: {unk3Zero} of {parts}   1 entry: {unk3One} of {parts}   "
            + $"2+ entries: {unk3TwoPlus} of {parts}");
        check("a part's Unk3 is overwhelmingly a single frame reference",
            parts > 0 && unk3One * 2 > parts, $"{unk3One} of {parts} parts carry exactly one");
        sb.AppendLine($"    of {unk3Entries} Unk3 hashes: {unk3Resolved} resolve to ANY frame of this car, "
            + $"{unk3ResolvedBone} resolve specifically to a BONE");

        sb.AppendLine("\n  which bone a part's Unk3 names, by part kind (top names when it resolves to a bone):");
        foreach (uint partType in unk3BoneByPartType.Keys.Select(k => k.PartType).Distinct().OrderBy(t => t))
        {
            var row = unk3BoneByPartType.Where(p => p.Key.PartType == partType)
                .OrderByDescending(p => p.Value).Take(6);
            sb.AppendLine($"    {partType,2} {PartTypeNames.GetValueOrDefault(partType, "?"),-8}  "
                + string.Join("  ", row.Select(p => $"{p.Key.Bone}×{p.Value}")));
        }

        // ── SmDeformBones: how many a part carries, and whether SmJointName is a deform_* frame ──
        int smZero = smDeformCountHist.GetValueOrDefault(0);
        int smOne = smDeformCountHist.GetValueOrDefault(1);
        int smTwoPlus = smDeformCountHist.Where(p => p.Key >= 2).Sum(p => p.Value);
        sb.AppendLine($"\n  a part's SmDeformBones list, length distribution over {parts} parts:");
        sb.AppendLine("    " + string.Join(", ", smDeformCountHist.OrderBy(p => p.Key)
            .Select(p => $"{p.Key}×{p.Value}")));
        sb.AppendLine($"    0 entries: {smZero} of {parts}   1 entry: {smOne} of {parts}   "
            + $"2+ entries: {smTwoPlus} of {parts}");
        sb.AppendLine($"    of {smDeformEntries} SmJointName hashes: {smDeformResolved} resolve to a frame "
            + $"of this car, {smDeformResolvedDeformPrefix} of those are named deform_*");

        // ── ParentDeformPartName vs Unk17 ──
        sb.AppendLine($"\n  ParentDeformPartName, over {parentNameTotal} parts that carry a non-zero one:");
        sb.AppendLine($"    resolves to a FRAME name        {parentNameResolvesFrame}");
        sb.AppendLine($"    resolves to a SHAPE's data hash {parentNameResolvesShape}");
        sb.AppendLine($"    resolves to neither              {parentNameUnresolved}");
        check("ParentDeformPartName names a FRAME, like every other reference in the prefab",
            parentNameTotal > 0 && parentNameResolvesFrame == parentNameTotal,
            $"{parentNameResolvesFrame} of {parentNameTotal}");
        sb.AppendLine($"    Unk17 (index of the parent part) is set (not 65535) on {parentIndexTotal} parts "
            + $"({parentIndexOutOfRange} had an out-of-range index); of those, the indexed part's own "
            + $"Unk3[0] equals THIS part's ParentDeformPartName on {parentIndexAgreesWithName}");
        check("Unk17's indexed part and ParentDeformPartName's hash agree on who the parent is",
            parentIndexTotal > 0 && parentIndexAgreesWithName * 20 > parentIndexTotal * 19,
            $"{parentIndexAgreesWithName} of {parentIndexTotal}");

        // ── follow-up: can a part's own bone (Unk3[0]) serve as a stable, unique component identity? ──
        sb.AppendLine($"\n  Unk3[0] uniqueness within a car ({carsWithPrefabAndFrames} cars with a prefab):");
        sb.AppendLine($"    cars where no two parts share a frame        {carsWithNoUnk3Collision}");
        sb.AppendLine($"    cars where two or more parts DO share one    {carsWithUnk3Collision}");
        foreach (string e in unk3CollisionExamples) sb.AppendLine("      " + e);
        check("a part's Unk3[0] is unique within its own car — a stable per-component key",
            carsWithPrefabAndFrames > 0 && carsWithUnk3Collision == 0,
            $"{carsWithUnk3Collision} of {carsWithPrefabAndFrames} cars have a collision");

        // ── coverage the other way: of the rig's own bones, how many does a deform part actually name? ──
        sb.AppendLine($"\n  bone coverage the other way ({bonesTotal} bones over {carsWithPrefabAndFrames} cars):");
        sb.AppendLine($"    named by a deform part (Unk3[0])   {bonesOwnedByAPart} of {bonesTotal}");
        sb.AppendLine($"    named by nothing (orphan bone)     {bonesOrphan} of {bonesTotal}");
        sb.AppendLine("    orphan bones per car: " + string.Join(", ",
            orphanCountHist.OrderBy(p => p.Key).Select(p => $"{p.Key}×{p.Value} cars")));
        sb.AppendLine("    most common orphan bone names: " + string.Join(", ",
            orphanBoneNameCounts.OrderByDescending(p => p.Value).Take(15)
                .Select(p => $"{p.Key}×{p.Value}")));

        // ── do deform bones (SmJointName) ever double as a part's own bone, in the same car? ──
        sb.AppendLine($"\n  a deform bone's SmJointName is ALSO some part's own Unk3[0] in the same car: "
            + $"{smDeformOverlapsPartBone} of {smDeformEntries}");

        // ── markers: seats, climb boxes, fuel tanks, exhaust emitters, wipers, lights ──
        sb.AppendLine("\n  marker frames, resolved through AttachmentReferences to a bone, against whether a "
            + "deform part owns that bone:");
        sb.AppendLine($"    {"",-16} {"markers",8} {"owned bone",11} {"unowned bone",13} {"no bone at all",15}");
        foreach (string label in markerCounts.Keys)
        {
            int total = markerCounts[label];
            int owned = markerOwned.GetValueOrDefault(label);
            int unowned = markerUnownedBone.GetValueOrDefault(label);
            int noBone = markerNoBone.GetValueOrDefault(label);
            sb.AppendLine($"    {label,-16} {total,8} {owned,11} {unowned,13} {noBone,15}");
        }
        int markersTotal = markerCounts.Values.Sum();
        int markersOwned = markerOwned.Values.Sum();
        int markersUnowned = markerUnownedBone.Values.Sum();
        int markersNoBone = markerNoBone.Values.Sum();
        sb.AppendLine($"    total: {markersOwned} of {markersTotal} land on a bone some part owns, "
            + $"{markersUnowned} of {markersTotal} on a bone no part owns, "
            + $"{markersNoBone} of {markersTotal} resolve to no bone at all");

        // ── follow-up: walking UP from an unowned marker's bone, does it reach an owned ancestor? ──
        sb.AppendLine($"\n  ancestor fallback, for the {ancestorChecked} markers whose own bone is NOT owned:");
        sb.AppendLine($"    reaches an owned ancestor        {ancestorResolved} of {ancestorChecked}");
        sb.AppendLine($"    hits the rig root, none found    {ancestorHitRoot} of {ancestorChecked}");
        sb.AppendLine($"    could not be walked at all       {ancestorCouldNotWalk} of {ancestorChecked}");
        sb.AppendLine("    hops taken, raw: " + string.Join(", ",
            ancestorHopHist.OrderBy(p => p.Key).Select(p => $"{p.Key}×{p.Value}")));
        int hop1 = ancestorHopHist.GetValueOrDefault(1);
        int hop2 = ancestorHopHist.GetValueOrDefault(2);
        int hop3Plus = ancestorHopHist.Where(p => p.Key >= 3).Sum(p => p.Value);
        sb.AppendLine($"    bucketed: 1 hop {hop1} of {ancestorResolved}   2 hops {hop2} of {ancestorResolved}   "
            + $"3+ hops {hop3Plus} of {ancestorResolved}");
        check("an unowned marker's own bone is never itself owned at 0 hops (definitional)",
            !ancestorHopHist.ContainsKey(0), string.Join(",", ancestorHopHist.Keys));

        sb.AppendLine("\n  which component the ancestor walk lands on, by marker label — bone name:");
        foreach (string label in ancestorLandsOnBone.Keys.Select(k => k.Label).Distinct())
        {
            var row = ancestorLandsOnBone.Where(p => p.Key.Label == label)
                .OrderByDescending(p => p.Value).Take(8);
            sb.AppendLine($"    {label,-16} " + string.Join("  ", row.Select(p => $"{p.Key.Bone}×{p.Value}")));
        }
        sb.AppendLine("\n  which component the ancestor walk lands on, by marker label — part kind:");
        foreach (string label in ancestorLandsOnKind.Keys.Select(k => k.Label).Distinct())
        {
            var row = ancestorLandsOnKind.Where(p => p.Key.Label == label).OrderByDescending(p => p.Value);
            sb.AppendLine($"    {label,-16} " + string.Join("  ",
                row.Select(p => $"{PartTypeNames.GetValueOrDefault(p.Key.PartType, p.Key.PartType.ToString())}"
                    + $"×{p.Value}")));
        }

        // ── sanity check: the walk must not change any answer that was already "owned" ──
        sb.AppendLine($"\n  sanity check — an already-owned marker's own bone resolves at 0 hops: "
            + $"{sanityHopZero} of {sanityChecked} ({sanityMismatch} mismatched)");
        check("the ancestor walk is a strict generalisation — it never changes an existing owned answer",
            sanityChecked > 0 && sanityMismatch == 0, $"{sanityHopZero} of {sanityChecked}, {sanityMismatch} mismatched");

        // ── the other direction: of the orphan bones, how many does SmDeformBones claim instead? ──
        int orphanTotalChecked = orphanClaimedBySmDeform + orphanClaimedByNeither;
        sb.AppendLine($"\n  of the {orphanTotalChecked} orphan bones (not a part's Unk3[0]):");
        sb.AppendLine($"    claimed instead via SmDeformBones' SmJointName   {orphanClaimedBySmDeform} "
            + $"of {orphanTotalChecked}");
        sb.AppendLine($"    claimed by NEITHER link                          {orphanClaimedByNeither} "
            + $"of {orphanTotalChecked}");
        sb.AppendLine("    top 15 names claimed by neither link: " + string.Join(", ",
            neitherClaimedNameCounts.OrderByDescending(p => p.Value).Take(15)
                .Select(p => $"{p.Key}×{p.Value}")));

        // ── follow-up: are the "claimed by neither link" bones real things, or plumbing? ──
        int neitherTotal = orphanClaimedByNeither;
        sb.AppendLine($"\n  of the {neitherTotal} bones claimed by NEITHER link, are they real things:");
        sb.AppendLine($"\n  1. geometry — named as a split's bone via BoneRemapIDs[BlendIndex]:");
        sb.AppendLine($"    with at least one split piece      {neitherWithGeometry} of {neitherTotal}");
        sb.AppendLine($"    with no split at all                {neitherTotal - neitherWithGeometry} "
            + $"of {neitherTotal}");
        sb.AppendLine("    pieces-per-bone, of the ones with geometry: " + string.Join(", ",
            neitherPiecesHist.OrderBy(p => p.Key).Select(p => $"{p.Key}×{p.Value}")));
        sb.AppendLine("    top 15 names WITH geometry: " + string.Join(", ",
            neitherWithGeomNameCounts.OrderByDescending(p => p.Value).Take(15)
                .Select(p => $"{p.Key}×{p.Value}")));
        sb.AppendLine("    top 15 names WITHOUT geometry: " + string.Join(", ",
            neitherNoGeomNameCounts.OrderByDescending(p => p.Value).Take(15)
                .Select(p => $"{p.Key}×{p.Value}")));

        sb.AppendLine($"\n  2. hit boxes, of the {neitherWithGeometry} bones WITH geometry:");
        sb.AppendLine($"    at least one non-placeholder hit box   {neitherGeomWithNonZeroBox} "
            + $"of {neitherWithGeometry}");
        sb.AppendLine($"    every piece's hit box is all-zero      "
            + $"{neitherWithGeometry - neitherGeomWithNonZeroBox} of {neitherWithGeometry}");

        sb.AppendLine($"\n  3. referenced by some OTHER prefab collection (headlight/backlight/toplight, "
            + "snow rest, wipers, driving wheels, fuel tanks, exhaust emitters, axle name/brake drum/rot "
            + "wing, window, door points, seat (+its door), climb box dummy/bone):");
        sb.AppendLine($"    claimed by at least one such reference   {neitherClaimedByOtherRef} "
            + $"of {neitherTotal}");
        sb.AppendLine($"    claimed by NOTHING in the whole prefab   {neitherClaimedByNothingAtAll} "
            + $"of {neitherTotal}");
        sb.AppendLine("    per collection: " + string.Join(", ",
            neitherRefCollectionCounts.OrderByDescending(p => p.Value).Select(p => $"{p.Key}×{p.Value}")));

        sb.AppendLine($"\n  4. attachments — a helper frame (Dummy/Point) hung off it via AttachmentReferences:");
        sb.AppendLine($"    with at least one attachment   {neitherWithAttachment} of {neitherTotal}");
        sb.AppendLine($"    with none                      {neitherTotal - neitherWithAttachment} "
            + $"of {neitherTotal}");

        // ── what a hashed volume actually names ──
        sb.AppendLine($"\n  a volume that carries a pair of hashes ({hash5Total} of them):");
        sb.AppendLine($"    second hash is an ItemDesc's DATA hash   {hash5IsDataHash}");
        sb.AppendLine($"    second hash is an ItemDesc's FILE hash   {hash5IsFileHash}");
        sb.AppendLine($"    first hash is not zero                   {hash5First}");
        sb.AppendLine("    the shapes behind them: " + string.Join(", ",
            shapeBehind.OrderByDescending(p => p.Value).Select(p => $"{p.Key}×{p.Value}")));
        check("a hashed collision volume names a physics shape of its own archive, by DATA hash",
            hash5Total > 0 && hash5IsDataHash == hash5Total,
            $"{hash5IsDataHash} of {hash5Total} (as a file hash: {hash5IsFileHash})");
        sb.AppendLine($"    physics shapes in these archives {shapesTotal}; named by a volume "
            + $"{stubsClaimed}, named by none {shapesUnclaimed}");

        // ── the same placement, written twice ──
        int best = Array.IndexOf(permHit, permHit.Max());
        sb.AppendLine($"\n  a hashed volume's transform against the frame stub's own, over {pairTotal} pairs:");
        foreach (int k in Enumerable.Range(0, 48).OrderByDescending(k => permHit[k]).Take(4))
        {
            sb.AppendLine($"    {PermName(k),-14} agrees on {permHit[k],5} of {pairTotal}");
        }
        sb.AppendLine("    archives where the two copies disagree: " + (disagreeing.Count == 0 ? "none"
            : string.Join(", ", disagreeing.OrderByDescending(p => p.Value).Select(p => $"{p.Key} ×{p.Value}"))));
        sb.AppendLine($"    rotation is the same basis at all       {rotationHit} of {pairTotal}");
        sb.AppendLine($"    …and exactly stub[i][j] = prefab[2-i][2-j]  {swapRotationHit} of {pairTotal}");
        check("the rotation converts by the same X↔Z swap as the translation",
            pairTotal > 0 && swapRotationHit * 20 > pairTotal * 19,
            $"{swapRotationHit} of {pairTotal}");
        sb.AppendLine($"\n  a part's volumes are grouped into collections: " + string.Join(", ",
            collectionCounts.OrderBy(p => p.Key).Select(p => $"{p.Key} collection(s) ×{p.Value} parts")));
        sb.AppendLine($"    volumes carrying the optional second transform {withUnk2Transform}, "
            + $"the optional nested block {withUnk6}");

        // Is a physics shape's KIND tied to what the part is? If every shipped cover carries a convex hull and
        // never a box, then writing a box onto one is writing something the game may not take.
        sb.AppendLine("\n  which shape kinds a part type's volumes actually name:");
        foreach (uint partType in shapeByPart.Keys.Select(k => k.Part).Distinct().OrderBy(t => t))
        {
            sb.AppendLine($"    {partType,2} {PartTypeNames.GetValueOrDefault(partType, "?"),-8}  "
                + string.Join("  ", shapeByPart.Where(p => p.Key.Part == partType)
                    .OrderByDescending(p => p.Value).Select(p => $"{p.Key.Shape}×{p.Value}")));
        }

        sb.AppendLine($"\n  the two unnamed index lists on a deformable part ({partsCounted} parts):");
        sb.AppendLine("    Unk14 length: " + string.Join(", ",
            unk14Counts.OrderBy(p => p.Key).Select(p => $"{p.Key}×{p.Value}")));
        sb.AppendLine("    Unk20 length: " + string.Join(", ",
            unk20Counts.OrderBy(p => p.Key).Select(p => $"{p.Key}×{p.Value}")));
        sb.AppendLine($"    parts where one of them is as long as the volume list: {indexListsMatchVolumes} "
            + $"of {partsCounted}; parts carrying either at all: {indexListsAny}");
        sb.AppendLine($"    {"",-8} {"biggest value",13}  " + string.Join("  ",
            new[] { "volumes", "deform bones", "drop parts", "drain energy", "impulses", "frames",
                    "parts in the car" }.Select(s => $"{s,-16}")));
        foreach (string listName in new[] { "Unk14", "Unk20" })
        {
            int carried = indexSeen.GetValueOrDefault(listName);
            if (carried == 0) continue;
            sb.AppendLine($"    {listName,-8} {indexMax.GetValueOrDefault(listName),13}  " + string.Join("  ",
                new[] { "volumes", "deform bones", "drop parts", "drain energy", "impulses", "frames",
                        "parts in the car" }
                    .Select(s => $"{$"len {indexLen.GetValueOrDefault((listName, s))} " +
                                    $"fits {indexFits.GetValueOrDefault((listName, s))}",-16}")));
            sb.AppendLine($"    {"",-8} …of {carried} parts that carry a {listName}");
        }
        sb.AppendLine("    what they actually hold:");
        foreach (string sample in indexSamples) sb.AppendLine("      " + sample);

        // ── WHICH EFFECT A SHOT DRAWS ──
        //
        // Established in game (2026-08-05): geometry rebound from the hood's vertex group to the ROOF's,
        // with its material untouched, draws a DIFFERENT impact effect. So the effect is chosen by the
        // deformable PART the geometry hangs on, not by the triangle's material — and the part carries an
        // effects block the toolkit had never opened, whose legacy names are ParticleBreakID,
        // ParticleHingeVersionID, SnowParticleID_0..3 and ParticleScale.
        //
        // This asks the first question that has to be true for that story: do those ids actually DIFFER
        // between parts of one car? A field that is one constant everywhere cannot be selecting anything.
        // The one STRING a deformable part carries. materials_shots.tbl — the game's own catalogue of what a
        // shot draws — is keyed by a NAME (silnice, plech, kov, sklo…), so if a part names its surface
        // anywhere, this is the only field in it that could.
        sb.AppendLine($"\n  the string a deformable part carries ({partsWithString} of {parts} carry one):");
        foreach ((string text, int count) in partStrings.OrderByDescending(p => p.Value).Take(24))
        {
            sb.AppendLine($"    \"{text}\" ×{count}");
        }
        foreach ((string field, Dictionary<short, int> values) in partSmall)
        {
            sb.AppendLine($"    {field,-8} {values.Count,4} distinct   " + string.Join("  ",
                values.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key}×{p.Value}")));
        }

        // Does the field vary WITHIN a kind? That is the whole question. If every "cover" carried the same
        // value it would only be restating the kind, and the bonnet and the boot lid — both covers — could
        // not draw different effects. In game they do.
        sb.AppendLine("\n  Unk19 by part kind (a kind with more than one value is telling parts apart):");
        foreach (uint kind in smallByKind.Keys.Select(k => k.Kind).Distinct().OrderBy(k => k))
        {
            List<KeyValuePair<(uint Kind, short Value), int>> row =
                [.. smallByKind.Where(p => p.Key.Kind == kind).OrderByDescending(p => p.Value)];
            sb.AppendLine($"    {kind,2} {PartTypeNames.GetValueOrDefault(kind, "?"),-8} {row.Count,3} values   "
                + string.Join("  ", row.Take(10).Select(p => $"{p.Key.Value}×{p.Value}")));
        }

        // Confirmed in game: parts sharing this number share the effect a shot on them draws. For that to be
        // able to tell a bonnet from a boot lid, it has to vary WITHIN a kind — and it does, everywhere except
        // the body and the engine bay, which are one group each.
        int kindsThatVary = smallByKind.Keys.Select(k => k.Kind).Distinct()
            .Count(kind => smallByKind.Count(p => p.Key.Kind == kind) > 1);
        int kindsTotal = smallByKind.Keys.Select(k => k.Kind).Distinct().Count();
        check("a part's effect group varies within its kind, so it can tell one panel from another",
            kindsThatVary >= kindsTotal - 3, $"{kindsThatVary} of {kindsTotal} kinds carry more than one value");

        sb.AppendLine($"\n  every part of {focus}:");
        foreach (string line in focusParts) sb.AppendLine("    " + line);

        sb.AppendLine($"\n  the effects block on a deformable part ({effectParts} parts carrying one):");
        foreach ((string name, Dictionary<short, int> values) in effectFields)
        {
            List<KeyValuePair<short, int>> top = [.. values.OrderByDescending(p => p.Value).Take(6)];
            sb.AppendLine($"    {name,-24} {values.Count,4} distinct   "
                + string.Join("  ", top.Select(p => $"{p.Key}×{p.Value}")));
        }
        sb.AppendLine($"    cars where one car's parts disagree about ParticleBreakID: "
            + $"{effectVariesWithinCar} of {effectCars}");
        // MEASURED, and it closes the candidate rather than opening it. The block's two impact-effect ids are
        // -1 on every part of every shipped car, and the only fields in it that vary at all are the SNOW ones,
        // on snow parts. So whatever picks the effect a shot draws, it is not written here — even though the
        // effect demonstrably follows the part (rebinding geometry from the bonnet's vertex group to the
        // roof's, with the material untouched, changes it).
        check("the part effects block does not choose the impact effect — its ids are unset on every car",
            effectParts > 0 && effectFields["ParticleBreakID"].Count == 1
            && effectFields["ParticleBreakID"].ContainsKey(-1),
            $"{effectFields["ParticleBreakID"].Count} distinct ParticleBreakID over {effectParts} parts");
        check("…and no car's parts disagree about it, so it cannot be telling one part from another",
            effectVariesWithinCar == 0, $"{effectVariesWithinCar} of {effectCars} cars vary");
        check("the only effect ids that DO vary are the snow ones, and only on snow parts",
            effectFields["SnowParticleID_0"].Count > 1,
            string.Join(" ", effectFields["SnowParticleID_0"].OrderByDescending(p => p.Value)
                .Select(p => $"{p.Key}×{p.Value}")));
        // Both lists hold PART NUMBERS — every value lands inside the car's own part list, and nothing else
        // they could be indexing takes all of them. That makes a part's POSITION load-bearing: renumber the
        // list and these silently point at the wrong panels. Nothing in the toolkit can renumber it today
        // (see CarItemKind — there is no DeformPart), and this assertion is what will notice if that changes.
        int carriers = indexSeen.GetValueOrDefault("Unk14") + indexSeen.GetValueOrDefault("Unk20");
        int within = indexFits.GetValueOrDefault(("Unk14", "parts in the car"))
            + indexFits.GetValueOrDefault(("Unk20", "parts in the car"));
        check("a deformable part's two index lists name other PARTS of the same car",
            carriers > 0 && within == carriers,
            $"{within} of {carriers} lists stay inside the part list; next best reading is "
                + $"{indexFits.GetValueOrDefault(("Unk14", "deform bones"))
                    + indexFits.GetValueOrDefault(("Unk20", "deform bones"))} as bone indices");
        check("the prefab volume and the frame stub carry the SAME placement, under one fixed axis order",
            pairTotal > 0 && permHit[best] * 20 > pairTotal * 19,
            $"{PermName(best)} on {permHit[best]} of {pairTotal} — the rest are archives already edited");

        // ── half-extents or full ──
        sb.AppendLine($"\n  volumes that describe themselves by extents ({inlineTotal}):");
        sb.AppendLine($"    the box fits inside its own car read as FULL sizes  {fitsAsFull}");
        sb.AppendLine($"    …read as HALF sizes (so twice as big)               {fitsAsHalf}");
        check("an inline volume's extents are FULL sizes, not half",
            inlineTotal > 0 && fitsAsFull > fitsAsHalf,
            $"{fitsAsFull} vs {fitsAsHalf} of {inlineTotal}");

        // ── which kind of part carries which kind of volume ──
        sb.AppendLine("\n  volume type by part type (part type names are the reference toolkit's):");
        foreach ((uint partType, string name) in PartTypeNames.OrderBy(p => p.Key))
        {
            var row = typeByPart.Where(p => p.Key.Part == partType).OrderBy(p => p.Key.Volume).ToList();
            if (row.Count == 0) continue;
            sb.AppendLine($"    {partType,2} {name,-8}  "
                + string.Join("  ", row.Select(p => $"type {p.Key.Volume}×{p.Value}")));
        }
        foreach ((uint partType, uint volumeType) in typeByPart.Keys
                     .Where(k => !PartTypeNames.ContainsKey(k.Part)).Distinct().OrderBy(k => k.Part))
        {
            sb.AppendLine($"    {partType,2} (unnamed) type {volumeType}×{typeByPart[(partType, volumeType)]}");
        }
    }

    /// <summary>
    /// Whether a bone's geometry survives to the far LOD, or drops out — the fact that decides whether a
    /// "component" is one geometry or a ragged list of them.
    /// <para>
    /// The split/hit-box table (<c>BlendMeshSplits</c>) is ONE block shared across LODs (docs/car-anatomy.md:
    /// quantization, bounds and BlendMeshSplits all break LOD0 if edited through LOD1), so a piece cannot
    /// "not be there" by having a different table — the only way a piece is absent at a LOD is if its face
    /// range does not fit inside THAT LOD's own (shorter) index buffer. A split's bone is
    /// <c>BoneRemapIDs[BlendIndex]</c> (measured: right 98.89% of the time, docs/car-anatomy.md), and the flat
    /// remap table is PER LOD (<c>FrameBlendInfo.BoneIndexInfos[lod]</c>) — resolved here through each LOD's
    /// own table, never LOD0's reused, per the explicit ask.
    /// </para>
    /// <para>
    /// <c>FrameSkeleton.BoneLODUsage</c> / <c>MappingForBlendingInfo.RefToUsageArray</c>/<c>UsageArray</c> look
    /// like a more direct answer to "which bones does this LOD use", but their own doc comments carry
    /// unresolved TODOs ("my suspicion is...") — not measured elsewhere in this codebase, so they are not
    /// trusted here. The split/index-buffer reading above is the one already established and tested
    /// (<c>--probe-bullets</c>, <c>BulletProbes.PiecesOf</c>) and is used unchanged.
    /// </para>
    /// </summary>
    private static void LodCoverage(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ LOD coverage: does a bone's geometry survive to the far level? ════");

        FileInfo[] archives = new DirectoryInfo(folder).GetFiles("*.sds");
        Array.Sort(archives, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        // Q1: LOD count, over every extracted archive AND over just the ones with a car prefab.
        var lodCountAll = new Dictionary<int, int>();
        var lodCountPrefab = new Dictionary<int, int>();
        int allCars = 0, prefabCars = 0;

        // Q2-Q4: per-bone LOD presence, split three ways.
        const string BucketOwned = "Unk3[0] owned";
        const string BucketNeither = "claimed by neither link";
        const string BucketOther = "everything else";
        var lodsPresentHist = new Dictionary<(string Bucket, int Lods), int>();
        var lod0ByBucket = new Dictionary<string, int>();
        var vanishByBucket = new Dictionary<string, int>();
        var bothByBucket = new Dictionary<string, int>();
        var lod1OnlyByBucket = new Dictionary<string, int>();
        var vanishNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var reverseNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int twoLodCars = 0, otherLodCars = 0, noRemapForLod = 0;

        // Q5: bone-palette size per LOD.
        int remapMismatch = 0, remapPairs = 0, lod1Smaller = 0, lod1Equal = 0, lod1Bigger = 0;
        long sumLod0 = 0, sumLod1 = 0;

        foreach (FileInfo sds in archives)
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            if (fr?.FrameObjects == null) continue;

            FrameObjectModel? carModel = fr.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (carModel == null) continue;
            allCars++;

            int totalLods = carModel.Geometry?.LOD?.Length ?? 0;
            lodCountAll[totalLods] = lodCountAll.GetValueOrDefault(totalLods) + 1;

            // Is this one of the 85 cars with a car prefab? Same gate Census uses.
            string? prf = null;
            try { prf = SdsManifest.Load(extracted).GetFiles("PREFAB").FirstOrDefault(); }
            catch (Exception) { /* no manifest, no prefab */ }
            PrefabEntryW? entry = null;
            if (prf != null)
            {
                try
                {
                    PrefabFile file = PrefabFile.Load(prf);
                    entry = file.Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0);
                }
                catch (Exception) { entry = null; }
            }
            if (entry == null || entry.CarInit[0].Deformation.Count == 0) continue;
            prefabCars++;
            lodCountPrefab[totalLods] = lodCountPrefab.GetValueOrDefault(totalLods) + 1;
            List<PrefabDeformPartW> deformParts = entry.CarInit[0].Deformation[0].DeformParts;

            // The bone table and the two ownership sets, rebuilt exactly as the main census does.
            string[] carBonesByJoint = (carModel.GetSkeletonObject().BoneNames ?? [])
                .Select(n => n.ToString() ?? "").ToArray();
            var boneHashes = new Dictionary<ulong, string>();
            foreach (string n in carBonesByJoint)
            {
                if (n.Length > 0) boneHashes.TryAdd(Fnv64.Hash(n), n);
            }
            var ownedSet = new HashSet<ulong>();
            var smSet = new HashSet<ulong>();
            foreach (PrefabDeformPartW p in deformParts)
            {
                if (p.Unk3.Count > 0) ownedSet.Add(p.Unk3[0]);
                foreach (PrefabSmDeformBoneW sm in p.SmDeformBones) smSet.Add(sm.SmJointName);
            }

            // ── per-LOD split presence: for each LOD, which bone hashes have >= 1 piece whose face range
            // fits inside THAT LOD's own index buffer, resolved through THAT LOD's own remap table ──
            Illusion.Formats.Frames.Resources.FrameBlendInfo.BoneIndexInfo[] blendLods = [];
            try { blendLods = carModel.GetBlendInfoObject().BoneIndexInfos ?? []; }
            catch (Exception) { /* no blend info: nothing can be attributed to a bone at any LOD */ }

            var presentAt = new List<HashSet<ulong>>();
            for (int lod = 0; lod < totalLods; lod++)
            {
                var here = new HashSet<ulong>();
                if (lod >= blendLods.Length)
                {
                    noRemapForLod++;
                    presentAt.Add(here);
                    continue;
                }
                byte[] remap = blendLods[lod].BoneRemapIDs ?? [];
                int indexLen = carModel.GetIndexBuffer(lod)?.GetData()?.Length ?? 0;
                foreach (FrameObjectModel.WeightedByMeshSplit split in carModel.BlendMeshSplits ?? [])
                {
                    int boneId = split.BlendIndex < remap.Length ? remap[split.BlendIndex] : -1;
                    if (boneId < 0 || boneId >= carBonesByJoint.Length) continue;
                    ulong hash = Fnv64.Hash(carBonesByJoint[boneId]);
                    if (here.Contains(hash)) continue;

                    bool anyRangeFits = false;
                    foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
                    {
                        foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                        {
                            foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                            {
                                if (range.StartIndex + (range.NumFaces * 3) <= indexLen) { anyRangeFits = true; break; }
                            }
                            if (anyRangeFits) break;
                        }
                        if (anyRangeFits) break;
                    }
                    if (anyRangeFits) here.Add(hash);
                }
                presentAt.Add(here);
            }

            if (totalLods == 2) twoLodCars++; else if (totalLods > 0) otherLodCars++;

            // ── classify every bone that has geometry ANYWHERE into the 3 buckets, and measure coverage ──
            foreach ((ulong boneHash, string boneName) in boneHashes)
            {
                bool hasAnyGeometry = presentAt.Any(set => set.Contains(boneHash));
                if (!hasAnyGeometry) continue;

                string bucket = ownedSet.Contains(boneHash) ? BucketOwned
                    : !smSet.Contains(boneHash) ? BucketNeither
                    : BucketOther;

                int lodsPresent = presentAt.Count(set => set.Contains(boneHash));
                lodsPresentHist[(bucket, lodsPresent)] =
                    lodsPresentHist.GetValueOrDefault((bucket, lodsPresent)) + 1;

                if (totalLods == 2)
                {
                    bool inLod0 = presentAt[0].Contains(boneHash);
                    bool inLod1 = presentAt[1].Contains(boneHash);
                    if (inLod0)
                    {
                        lod0ByBucket[bucket] = lod0ByBucket.GetValueOrDefault(bucket) + 1;
                        if (inLod1) bothByBucket[bucket] = bothByBucket.GetValueOrDefault(bucket) + 1;
                        else
                        {
                            vanishByBucket[bucket] = vanishByBucket.GetValueOrDefault(bucket) + 1;
                            vanishNameCounts[boneName] = vanishNameCounts.GetValueOrDefault(boneName) + 1;
                        }
                    }
                    if (inLod1 && !inLod0)
                    {
                        lod1OnlyByBucket[bucket] = lod1OnlyByBucket.GetValueOrDefault(bucket) + 1;
                        reverseNameCounts[boneName] = reverseNameCounts.GetValueOrDefault(boneName) + 1;
                    }
                }
            }

            // ── Q5: bone-palette size per LOD, cross-checked two ways ──
            int[] lodRemapIdCount = carModel.GetSkeletonObject().LodRemapIDCount ?? [];
            for (int lod = 0; lod < totalLods && lod < blendLods.Length; lod++)
            {
                int fromSkeleton = lod < lodRemapIdCount.Length ? lodRemapIdCount[lod] : -1;
                int fromBlend = blendLods[lod].BoneRemapIDs?.Length ?? -1;
                if (fromSkeleton >= 0 && fromBlend >= 0 && fromSkeleton != fromBlend) remapMismatch++;
            }
            if (totalLods == 2 && blendLods.Length >= 2)
            {
                int p0 = blendLods[0].BoneRemapIDs?.Length ?? 0;
                int p1 = blendLods[1].BoneRemapIDs?.Length ?? 0;
                remapPairs++;
                sumLod0 += p0;
                sumLod1 += p1;
                if (p1 < p0) lod1Smaller++; else if (p1 > p0) lod1Bigger++; else lod1Equal++;
            }
        }

        // ── report ──
        sb.AppendLine($"  the reading applied: BoneRemapIDs[BlendIndex] resolved through EACH LOD's OWN flat "
            + "remap table (FrameBlendInfo.BoneIndexInfos[lod]) — LOD1 is never resolved against LOD0's table.");
        sb.AppendLine($"  a piece counts as present at a LOD when at least one face range fits inside THAT "
            + "LOD's own index buffer length; the split/hit-box table itself is one block shared by both LODs.");

        sb.AppendLine($"\n  1. LOD count per model:");
        sb.AppendLine($"    over all {allCars} extracted archives:      " + string.Join(", ",
            lodCountAll.OrderBy(p => p.Key).Select(p => $"{p.Key} LODs×{p.Value}")));
        sb.AppendLine($"    over the {prefabCars} with a car prefab:    " + string.Join(", ",
            lodCountPrefab.OrderBy(p => p.Key).Select(p => $"{p.Key} LODs×{p.Value}")));
        check("every car with a prefab carries exactly 2 LODs",
            prefabCars > 0 && lodCountPrefab.Count == 1 && lodCountPrefab.ContainsKey(2),
            string.Join(", ", lodCountPrefab.Select(p => $"{p.Key}×{p.Value}")));
        check("the LOD count is the same on the wider 106-archive set as on the 85 with a prefab",
            lodCountAll.Count == lodCountPrefab.Count
            && lodCountAll.Keys.All(k => lodCountPrefab.ContainsKey(k)),
            $"all: {string.Join(",", lodCountAll.Keys.OrderBy(k => k))}  "
                + $"prefab: {string.Join(",", lodCountPrefab.Keys.OrderBy(k => k))}");
        sb.AppendLine($"    cars with exactly 2 LODs: {twoLodCars}; other LOD counts: {otherLodCars}; "
            + $"LODs with no remap table at all: {noRemapForLod}");

        sb.AppendLine($"\n  2. per-bone LOD coverage, of bones that have geometry SOMEWHERE, split three ways:");
        foreach (string bucket in new[] { BucketOwned, BucketNeither, BucketOther })
        {
            var row = lodsPresentHist.Where(p => p.Key.Bucket == bucket).OrderBy(p => p.Key.Lods).ToList();
            int total = row.Sum(p => p.Value);
            sb.AppendLine($"    {bucket,-24} {total,5} bones   " + string.Join(", ",
                row.Select(p => $"{p.Key.Lods} LOD(s)×{p.Value}")));
        }

        sb.AppendLine($"\n  bones present in LOD0 but ABSENT from LOD1 (\"vanish\"), among cars with exactly "
            + "2 LODs, by bucket:");
        int lod0TotalAll = 0, vanishTotalAll = 0, bothTotalAll = 0, lod1OnlyTotalAll = 0;
        foreach (string bucket in new[] { BucketOwned, BucketNeither, BucketOther })
        {
            int lod0 = lod0ByBucket.GetValueOrDefault(bucket);
            int vanish = vanishByBucket.GetValueOrDefault(bucket);
            int both = bothByBucket.GetValueOrDefault(bucket);
            lod0TotalAll += lod0; vanishTotalAll += vanish; bothTotalAll += both;
            sb.AppendLine($"    {bucket,-24} vanish {vanish,5} of {lod0,5} in LOD0  (survive: {both})");
        }
        sb.AppendLine($"    TOTAL                    vanish {vanishTotalAll,5} of {lod0TotalAll,5} in LOD0  "
            + $"(survive: {bothTotalAll})");
        check("some LOD0 geometry does not survive to LOD1",
            lod0TotalAll > 0 && vanishTotalAll > 0, $"{vanishTotalAll} of {lod0TotalAll}");

        sb.AppendLine("\n  3. top 20 names of bones present in LOD0 but absent from LOD1:");
        sb.AppendLine("    " + string.Join(", ", vanishNameCounts.OrderByDescending(p => p.Value).Take(20)
            .Select(p => $"{p.Key}×{p.Value}")));

        sb.AppendLine("\n  4. the reverse — bones present in LOD1 but NOT LOD0:");
        foreach (string bucket in new[] { BucketOwned, BucketNeither, BucketOther })
        {
            lod1OnlyTotalAll += lod1OnlyByBucket.GetValueOrDefault(bucket);
        }
        sb.AppendLine($"    total: {lod1OnlyTotalAll} (by bucket: " + string.Join(", ",
            new[] { BucketOwned, BucketNeither, BucketOther }
                .Select(b => $"{b}={lod1OnlyByBucket.GetValueOrDefault(b)}")) + ")");
        if (lod1OnlyTotalAll > 0)
        {
            sb.AppendLine("    names: " + string.Join(", ", reverseNameCounts.OrderByDescending(p => p.Value)
                .Take(20).Select(p => $"{p.Key}×{p.Value}")));
        }
        check("nothing has geometry in LOD1 that is missing from LOD0",
            lod1OnlyTotalAll == 0, $"{lod1OnlyTotalAll} counterexamples");

        sb.AppendLine($"\n  5. bone-palette size per LOD (FrameSkeleton.LodRemapIDCount / "
            + $"FrameBlendInfo.BoneIndexInfos[lod].BoneRemapIDs.Length — {remapMismatch} disagreements "
            + "between the two, over every LOD of every car with a prefab):");
        if (remapPairs > 0)
        {
            double avgLod0 = (double)sumLod0 / remapPairs;
            double avgLod1 = (double)sumLod1 / remapPairs;
            sb.AppendLine($"    average palette size — LOD0: {avgLod0:F1}   LOD1: {avgLod1:F1}   "
                + $"(LOD1 is smaller on average by {avgLod0 - avgLod1:F1})");
            sb.AppendLine($"    LOD1 smaller: {lod1Smaller} of {remapPairs}   equal: {lod1Equal} of {remapPairs}   "
                + $"LOD1 bigger: {lod1Bigger} of {remapPairs}");
            check("the far LOD uses a smaller (or equal) bone palette than LOD0, never a bigger one",
                remapPairs > 0 && lod1Bigger == 0, $"{lod1Bigger} of {remapPairs} had a BIGGER LOD1 palette");
        }
        else
        {
            sb.AppendLine("    no 2-LOD car with a resolvable remap table on both levels");
        }
    }

    /// <summary>
    /// EVERY volume of the focus car as the overlay draws it, next to how big the car actually is.
    ///
    /// <para>
    /// The overlay draws a wireframe per volume and nothing else — no name, no tree row for a volume that has
    /// no stub — so a box standing off in the sky reads as "something is in the scene, it is not in the
    /// hierarchy, and it is not clear what it even is". This says what each one is and how far outside the
    /// car's own mesh bounds it reaches, which is the difference between a drawing bug and a volume the game
    /// really does put there.
    /// </para>
    /// </summary>
    private static void Floaters(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var sds = new FileInfo(Path.Combine(folder, focus + ".sds"));
        string extracted = MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) return;

        FrameResource? fr;
        try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
        catch (Exception) { return; }
        if (fr?.FrameObjects == null) return;

        if (!CarBounds(fr, out Vector3 lo, out Vector3 hi)) return;
        Vector3 centre = (lo + hi) * 0.5f;
        Vector3 half = (hi - lo) * 0.5f;

        IReadOnlyList<PlacedPhysicsVolume> volumes;
        try { volumes = CarPhysicsVolumes.Load(extracted, fr); }
        catch (Exception) { return; }

        sb.AppendLine($"\n\n════ every volume the overlay draws ({focus}) ════");
        sb.AppendLine($"  car mesh bounds {lo:F2} … {hi:F2}  (centre {centre:F2}, half {half:F2})");
        sb.AppendLine($"    {"part",-4} {"kind",-8} {"bone",-16} {"ty",-3} {"stub",-5} "
            + $"{"size (file)",-21} {"bone world",-23} {"local (read)",-23} {"PartTransform T",-23} "
            + $"{"parent part",-16} {"world",-23} outside");

        FrameObjectModel? car = fr.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
        IReadOnlyList<CarDeformPart> deformParts = CarPhysicsVolumes.Parts(extracted);
        var partNames = new Dictionary<ulong, string>();
        foreach (CarDeformPart p in deformParts)
        {
            PlacedPhysicsVolume? sample = volumes.FirstOrDefault(v => v.Part == p.Index);
            if (sample != null && sample.BoneName != "") partNames.TryAdd(p.Frame, sample.BoneName);
        }
        int outside = 0, stubless = 0;
        foreach (PlacedPhysicsVolume v in volumes.OrderByDescending(v => Outside(v, centre, half)))
        {
            float over = Outside(v, centre, half);
            if (over > 0.25f) outside++;
            if (v.Stub == null) stubless++;
            string shape = v.Shape?.Element is RigidBodyElement rigid ? rigid.Shape.ToString() : "—";
            string boneWorld = car != null && v.Bone >= 0
                ? car.GetJointWorldTransform(v.Bone).Translation.ToString("F3")
                : "—";
            CarDeformPart? own = v.Part >= 0 && v.Part < deformParts.Count ? deformParts[v.Part] : null;
            string partT = own != null ? own.PartTransform.Translation.ToString("F3") : "—";
            string parent = own == null || own.ParentFrame == 0
                ? "(none)"
                : partNames.TryGetValue(own.ParentFrame, out string? found) ? found : "0x…";
            sb.AppendLine($"    {v.Part,-4} {v.PartKind,-8} {(v.BoneName == "" ? "(none)" : v.BoneName),-16} "
                + $"{v.Volume.VolumeType,-3} {(v.Stub != null ? "yes" : "no"),-5} "
                + $"{v.Volume.Size,-21:F3} {boneWorld,-23} {v.Volume.Transform.Translation,-23:F3} "
                + $"{partT,-23} {parent,-16} {v.World.Translation,-23:F3} {over,6:F2} m  {shape}");
        }

        sb.AppendLine($"  {volumes.Count} volumes: {stubless} with no stub (and so no tree row at all), "
            + $"{outside} reaching more than 0.25 m outside the car's own mesh bounds");

        check("every volume the overlay draws sits within a car-length of the car",
            volumes.Count == 0 || volumes.All(v => Outside(v, centre, half) < half.Length()),
            $"{outside} of {volumes.Count} reach outside the mesh bounds");
    }

    /// <summary>How far the volume's own centre reaches outside the car's mesh bounds, in metres.</summary>
    private static float Outside(PlacedPhysicsVolume v, Vector3 centre, Vector3 half) =>
        Outside(v.World.Translation, centre, half);

    private static float Outside(Vector3 point, Vector3 centre, Vector3 half)
    {
        Vector3 d = Vector3.Abs(point - centre) - half;
        return MathF.Max(0f, MathF.Max(d.X, MathF.Max(d.Y, d.Z)));
    }

    /// <summary>
    /// The car's own extent in model space, from all eight corners of every mesh's box.
    /// <para>
    /// Eight corners and not just min/max: a mesh box is stated in the mesh's own space, and a rotated mesh's
    /// min and max corners do not map to the min and max of the result. Transforming only those two read a
    /// symmetric car as spanning -0.98 … 1.92 across, which would have made every symmetry test meaningless.
    /// </para>
    /// </summary>
    private static bool CarBounds(FrameResource fr, out Vector3 lo, out Vector3 hi)
    {
        lo = new Vector3(float.MaxValue);
        hi = new Vector3(float.MinValue);
        bool any = false;
        foreach (FrameObjectSingleMesh mesh in fr.FrameObjects.Values.OfType<FrameObjectSingleMesh>())
        {
            Matrix4x4 world = mesh.WorldTransform;
            Vector3 min = mesh.Boundings.Min, max = mesh.Boundings.Max;
            for (int corner = 0; corner < 8; corner++)
            {
                var point = new Vector3(
                    (corner & 1) != 0 ? max.X : min.X,
                    (corner & 2) != 0 ? max.Y : min.Y,
                    (corner & 4) != 0 ? max.Z : min.Z);
                Vector3 at = Vector3.Transform(point, world);
                lo = Vector3.Min(lo, at);
                hi = Vector3.Max(hi, at);
                any = true;
            }
        }
        return any;
    }

    /// <summary>
    /// WHERE a volume that has no stub actually goes — the one placement in this file nothing has ever
    /// checked.
    ///
    /// <para>
    /// The axis conversion between the prefab and the frame graph was measured against 1097 stub/volume
    /// pairs, and every one of those is a type-5 volume. A volume with no stub — every window, the engine
    /// bay, the roof — has no second copy to be checked against, so its reading was inherited on faith. A
    /// car is symmetric, and that is the check the data can still answer: the left window volume and the
    /// right one have to be mirror images. Whichever reading makes them mirror is the right one.
    /// </para>
    /// </summary>
    private static void Stubless(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ volumes with no stub: which reading makes the car symmetric? ════");

        // Six readings of the same bytes, scored two ways. Whether the prefab's axis reversal applies to a
        // self-describing volume, whether its transform is relative to the part's bone or to the model, and
        // whether the bone contributes its rotation or only its position, are three independent questions —
        // so the combinations are enumerated rather than argued about.
        string[] names = Readings;
        int count = names.Length;
        float[] error = new float[count];
        int[] scored = new int[count];
        int[] inside = new int[count];
        int placed = 0;
        // The same scores for the volumes that DO have a stub. Those were already believed to be in their own
        // bone's space, and the belief has to be re-tested against the same yardstick or the two halves of
        // this answer are not comparable.
        float[] stubError = new float[count];
        int[] stubScored = new int[count];
        int[] stubInside = new int[count];
        int stubPlaced = 0, stubPairs = 0;
        var examples = new List<string>();
        int cars = 0, pairs = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            if (fr?.FrameObjects == null) continue;
            FrameObjectModel? model = fr.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (model == null) continue;

            IReadOnlyList<PlacedPhysicsVolume> volumes;
            try { volumes = CarPhysicsVolumes.Load(extracted, fr); }
            catch (Exception) { continue; }
            List<PlacedPhysicsVolume> loose = [.. volumes.Where(v => v.Stub == null && v.Bone >= 0)];
            List<PlacedPhysicsVolume> held = [.. volumes.Where(v => v.Stub != null && v.Bone >= 0)];
            if (loose.Count == 0 && held.Count == 0) continue;
            IReadOnlyList<CarDeformPart> parts = CarPhysicsVolumes.Parts(extracted);
            cars++;

            // Second score, and the one that needs no pairing: a shipped volume describes a part of the car,
            // so it has to BE on the car. A reading that puts a window box three metres off the side is
            // wrong however symmetric it manages to look.
            if (CarBounds(fr, out Vector3 lo, out Vector3 hi))
            {
                Vector3 mid = (lo + hi) * 0.5f, ext = (hi - lo) * 0.5f;
                foreach (PlacedPhysicsVolume v in loose)
                {
                    placed++;
                    for (int reading = 0; reading < count; reading++)
                    {
                        if (Outside(Read(v, model, parts, reading), mid, ext) < 0.05f) inside[reading]++;
                    }
                }
                foreach (PlacedPhysicsVolume v in held)
                {
                    stubPlaced++;
                    for (int reading = 0; reading < count; reading++)
                    {
                        if (Outside(Read(v, model, parts, reading), mid, ext) < 0.05f) stubInside[reading]++;
                    }
                }
            }

            // The same mirror score for the volumes that have a stub, so the two populations are judged by
            // one yardstick rather than one being taken on trust.
            foreach (PlacedPhysicsVolume left in held)
            {
                if (Partner(left, held) is not { } right) continue;
                stubPairs++;
                for (int reading = 0; reading < count; reading++)
                {
                    Vector3 l = Read(left, model, parts, reading);
                    Vector3 r = Read(right, model, parts, reading);
                    stubError[reading] += MathF.Abs(l.X + r.X) + MathF.Abs(l.Y - r.Y) + MathF.Abs(l.Z - r.Z);
                    stubScored[reading]++;
                }
            }

            foreach (PlacedPhysicsVolume left in loose)
            {
                if (Partner(left, loose) is not { } right) continue;
                pairs++;

                for (int reading = 0; reading < count; reading++)
                {
                    Vector3 l = Read(left, model, parts, reading);
                    Vector3 r = Read(right, model, parts, reading);
                    // Mirror across the car's own X: the left one's X is the right one's negated, and the
                    // other two axes agree. Nothing here assumes where the car's centre is.
                    float miss = MathF.Abs(l.X + r.X) + MathF.Abs(l.Y - r.Y) + MathF.Abs(l.Z - r.Z);
                    error[reading] += miss;
                    scored[reading]++;
                }

                if (examples.Count < 8)
                {
                    examples.Add($"{sds.Name}  {left.BoneName} / {right.BoneName} (type "
                        + $"{left.Volume.VolumeType}): today {Read(left, model, parts, 0):F2} vs {Read(right, model, parts, 0):F2}"
                        + $"  |  as-written {Read(left, model, parts, 2):F2} vs {Read(right, model, parts, 2):F2}");
                }
            }
        }

        sb.AppendLine($"  {cars} cars — {placed} volumes with NO stub ({pairs} left/right pairs), "
            + $"{stubPlaced} WITH a stub ({stubPairs} pairs)");
        sb.AppendLine($"    {"reading",-44} {"self-describing: mirror  on the car",-34} places a shape: mirror  on the car");
        for (int reading = 0; reading < count; reading++)
        {
            float mean = scored[reading] > 0 ? error[reading] / scored[reading] : float.NaN;
            float stubMean = stubScored[reading] > 0 ? stubError[reading] / stubScored[reading] : float.NaN;
            sb.AppendLine($"    {names[reading],-44} {mean,7:F3} m   {inside[reading],4}/{placed} "
                + $"({(placed > 0 ? inside[reading] * 100.0 / placed : 0),5:F1}%)      "
                + $"{stubMean,7:F3} m   {stubInside[reading],4}/{stubPlaced} "
                + $"({(stubPlaced > 0 ? stubInside[reading] * 100.0 / stubPlaced : 0),5:F1}%)");
        }
        foreach (string example in examples) sb.AppendLine("      " + example);

        int best = Best(error, scored, inside, placed);
        int stubBest = Best(stubError, stubScored, stubInside, stubPlaced);
        sb.AppendLine($"  best for a volume with NO stub: {names[best]}");
        sb.AppendLine($"  best for a volume WITH a stub:  {names[stubBest]}");

        // A shipped volume describes a part of a symmetric car, so its left and right twins belong at
        // mirrored places and both belong ON the car. The two populations turn out to answer differently,
        // and that difference is the finding: a volume that places a shape is written in its own part's bone
        // space, while a volume that describes itself is written in the space of the part it HANGS OFF.
        check("a volume that places a shape is written in its own part's bone space",
            stubPlaced > 0 && stubBest == 0, $"best is \"{names[stubBest]}\"");
        check("a volume with no stub is written in the space of the part it hangs off, not its own",
            placed > 0 && best == 10, $"best is \"{names[best]}\"");
    }

    /// <summary>The candidate readings of a stubless volume's placement, in the order <see cref="Read"/>
    /// numbers them.</summary>
    private static readonly string[] Readings =
    [
        "its OWN part's bone, axes reversed",
        "model space, axes reversed",
        "bone space, axes as written",
        "model space, axes as written",
        "bone POSITION only, axes reversed",
        "bone POSITION only, axes as written",
        "the part's own PartTransform",
        "the chain of PartTransforms up ParentFrame",
        "the PARENT part's chain, axes reversed",
        "the PARENT part's chain, axes as written",
        "the part it HANGS OFF, axes reversed",
        "the part it HANGS OFF, axes as written",
    ];

    /// <summary>One of the candidate readings of a volume's placement, as a world position.</summary>
    private static Vector3 Read(
        PlacedPhysicsVolume v, FrameObjectModel model, IReadOnlyList<CarDeformPart> parts, int reading)
    {
        Matrix4x4 written = reading is 2 or 3 or 5 or 9 or 11
            ? SwapPrefabAxes(v.Volume.Transform)
            : v.Volume.Transform;
        switch (reading)
        {
            case 1 or 3:
                return written.Translation;                  // model space: the matrix stands on its own
            // The bone carries the volume but does not turn it — the reading that would explain a local
            // translation which already looks like a world position.
            case 4 or 5:
                return model.GetJointWorldTransform(v.Bone).Translation + written.Translation;
            // The part's OWN transform, which the reference toolkit reads (S_InitDeformPart.PartTransform)
            // and this toolkit has never used. A part also names a parent part, so the chain is worth a
            // separate reading from the single hop.
            case 6 or 7:
            {
                Matrix4x4 at = written;
                CarDeformPart? part = v.Part >= 0 && v.Part < parts.Count ? parts[v.Part] : null;
                for (int hop = 0; part != null && hop < 16; hop++)
                {
                    at *= part.PartTransform;
                    if (reading == 6 || part.ParentFrame == 0) break;
                    part = parts.FirstOrDefault(p => p.Frame == part.ParentFrame);
                }
                return at.Translation;
            }
            // The volume written in the PARENT part's space — which is what a part's own PartTransform
            // turns out to be measured in, and the volume tracks it almost exactly.
            case 8 or 9:
            {
                Matrix4x4 at = written;
                CarDeformPart? own = v.Part >= 0 && v.Part < parts.Count ? parts[v.Part] : null;
                CarDeformPart? up = own == null || own.ParentFrame == 0
                    ? null
                    : parts.FirstOrDefault(p => p.Frame == own.ParentFrame);
                for (int hop = 0; up != null && hop < 16; hop++)
                {
                    at *= up.PartTransform;
                    if (up.ParentFrame == 0) break;
                    up = parts.FirstOrDefault(p => p.Frame == up.ParentFrame);
                }
                return at.Translation;
            }
            // The space of the part this part HANGS OFF. A door's window is written relative to the door;
            // a window in the body is written relative to the body, whose bone is the identity — which is
            // why plain model space already explains most of them.
            case 10 or 11:
            {
                CarDeformPart? own = v.Part >= 0 && v.Part < parts.Count ? parts[v.Part] : null;
                CarDeformPart? up = own == null || own.ParentFrame == 0
                    ? null
                    : parts.FirstOrDefault(p => p.Frame == own.ParentFrame);
                int bone = up == null ? -1 : BoneOf(model, up.Frame);
                return bone < 0 ? written.Translation : model.PlaceOnJoint(written, bone).Translation;
            }
            default:
                return model.PlaceOnJoint(written, v.Bone).Translation;
        }
    }

    /// <summary>
    /// Which way a self-describing volume FACES — the half of its placement that nothing had checked.
    ///
    /// <para>
    /// The space question was settled by where the boxes land, and "where" is a translation: every score
    /// there read <c>.Translation</c> and none of them touched the 3×3. A reading can put a window pane on
    /// the right door and still stand it on edge, which is exactly what was reported.
    /// </para>
    /// <para>
    /// The oracle is the pane itself. A window volume is thin on exactly one axis, and that thin axis IS the
    /// pane's normal — so a window in a door must face ACROSS the car, and a windscreen or a rear window must
    /// not. The names say which is which (<c>windowBL</c> is a side, <c>windowF</c> is not), and that is
    /// independent of any matrix, so it can judge them.
    /// </para>
    /// </summary>
    private static void Facing(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ which way a self-describing volume faces ════");

        string[] names = FacingReadings;
        int count = names.Length;
        int[] right = new int[count];
        int panes = 0, cars = 0;
        var examples = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            FrameObjectModel? model = fr?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (model == null) continue;

            IReadOnlyList<PlacedPhysicsVolume> volumes;
            try { volumes = CarPhysicsVolumes.Load(extracted, fr!); }
            catch (Exception) { continue; }
            IReadOnlyList<CarDeformPart> parts = CarPhysicsVolumes.Parts(extracted);
            bool counted = false;

            foreach (PlacedPhysicsVolume v in volumes)
            {
                if (v.Stub != null || v.Bone < 0) continue;
                if (ThinAxis(v.Volume.Size) is not int thin) continue;
                // Only a volume whose name says which way it OUGHT to face can judge a reading.
                bool side = MirrorName(v.BoneName) != null;
                bool front = v.BoneName.EndsWith('F') || v.BoneName.EndsWith('B');
                if (!side && !front) continue;
                panes++;
                if (!counted) { cars++; counted = true; }

                for (int reading = 0; reading < count; reading++)
                {
                    Matrix4x4 world = Faces(v, model, parts, reading);
                    Vector3 normal = Vector3.Normalize(Row(world, thin));
                    float across = MathF.Abs(normal.X);
                    if (side ? across > 0.7f : across < 0.5f) right[reading]++;
                }

                if (examples.Count < 8)
                {
                    Vector3 now = Vector3.Normalize(Row(Faces(v, model, parts, 0), thin));
                    examples.Add($"{sds.Name}  {v.BoneName} ({(side ? "side" : "front/back")}), thin axis "
                        + $"{"XYZ"[thin]}: today it faces {now:F2}");
                }
            }
        }

        sb.AppendLine($"  {cars} cars, {panes} panes whose name says which way they should face");
        for (int reading = 0; reading < count; reading++)
        {
            sb.AppendLine($"    {names[reading],-46} facing right {right[reading],4}/{panes} "
                + $"({(panes > 0 ? right[reading] * 100.0 / panes : 0),5:F1}%)");
        }
        foreach (string example in examples) sb.AppendLine("      " + example);

        int best = 0;
        for (int reading = 1; reading < count; reading++)
        {
            if (right[reading] > right[best]) best = reading;
        }
        sb.AppendLine($"  best: {names[best]}");

        check("a window pane faces the way its own name says it must",
            panes > 0 && best == 0, $"best is \"{names[best]}\" with {right[best]} of {panes}, "
                + $"today {right[0]}");
    }

    /// <summary>
    /// WHAT a self-describing volume is for, as far as the shipped data can say.
    ///
    /// <para>
    /// The honest answer today is "a zone, and which zone follows from the part it hangs off" — but that is
    /// an inference from names, and names are the weakest evidence there is. Three things in the file are
    /// stronger: the part's KIND (the engine's own enum), the volume's TYPE, and the part's FLAGS, which the
    /// reference toolkit annotates (2 always-dynamic, 16 kill-part, 0x400 snow, 0x2000 AI box, 0x40000 fade
    /// off). If a volume type lines up with one part kind and one flag across 85 cars, that is what it is.
    /// </para>
    /// </summary>
    private static void Purpose(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ what a self-describing volume belongs to ════");

        var byType = new Dictionary<(uint Volume, string Kind), int>();
        var flagsByType = new Dictionary<uint, Dictionary<uint, int>>();
        var namesByType = new Dictionary<uint, Dictionary<string, int>>();
        int total = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            if (fr?.FrameObjects == null) continue;

            IReadOnlyList<PlacedPhysicsVolume> volumes;
            try { volumes = CarPhysicsVolumes.Load(extracted, fr); }
            catch (Exception) { continue; }
            IReadOnlyList<CarDeformPart> parts = CarPhysicsVolumes.Parts(extracted);

            foreach (PlacedPhysicsVolume v in volumes)
            {
                if (v.Volume.NamesShape) continue;
                total++;
                uint type = v.Volume.VolumeType;
                byType[(type, v.PartKind)] = byType.GetValueOrDefault((type, v.PartKind)) + 1;

                CarDeformPart? own = v.Part >= 0 && v.Part < parts.Count ? parts[v.Part] : null;
                if (own != null)
                {
                    Dictionary<uint, int> flags = flagsByType.TryGetValue(type, out Dictionary<uint, int>? f)
                        ? f : flagsByType[type] = [];
                    flags[own.Flags] = flags.GetValueOrDefault(own.Flags) + 1;
                }

                Dictionary<string, int> stems = namesByType.TryGetValue(type, out Dictionary<string, int>? n)
                    ? n : namesByType[type] = [];
                string stem = Stem(v.BoneName);
                stems[stem] = stems.GetValueOrDefault(stem) + 1;
            }
        }

        sb.AppendLine($"  {total} self-describing volumes across every extracted car");
        foreach (uint type in byType.Keys.Select(k => k.Volume).Distinct().OrderBy(t => t))
        {
            int count = byType.Where(p => p.Key.Volume == type).Sum(p => p.Value);
            sb.AppendLine($"\n  ── volume type {type} — {count} of them ──");
            sb.AppendLine("    the part it hangs off is a: " + string.Join(", ",
                byType.Where(p => p.Key.Volume == type).OrderByDescending(p => p.Value)
                    .Select(p => $"{p.Key.Kind} x{p.Value}")));
            if (flagsByType.TryGetValue(type, out Dictionary<uint, int>? flags))
            {
                sb.AppendLine("    that part's flags: " + string.Join(", ",
                    flags.OrderByDescending(p => p.Value).Take(6)
                        .Select(p => $"{FlagNames(p.Key)} x{p.Value}")));
            }
            if (namesByType.TryGetValue(type, out Dictionary<string, int>? stems))
            {
                sb.AppendLine("    what it is called: " + string.Join(", ",
                    stems.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key} x{p.Value}")));
            }
        }

        // The two kinds never trade places, and THAT is the finding — not the majority. Type 0 is glass: it
        // is on a part the engine types as a window 527 times out of 636, and the rest are doors and covers
        // that carry glass (one is literally "Dvere shrnovaci", Czech for a folding door). Type 6 is a zone
        // on the body: snow, the engine bay, patches and boards. Neither ever appears where the other lives.
        int glassOnBodyZone = byType.Where(p => p.Key.Volume == 0 && p.Key.Kind is "snow" or "motor")
            .Sum(p => p.Value);
        int zoneOnGlass = byType.Where(p => p.Key is { Volume: 6, Kind: "window" }).Sum(p => p.Value);
        check("type 0 is glass and type 6 is a body zone — neither ever turns up as the other",
            total > 0 && glassOnBodyZone == 0 && zoneOnGlass == 0,
            $"{glassOnBodyZone} type-0 on snow/motor, {zoneOnGlass} type-6 on a window");
    }

    /// <summary>A frame name with its side and its digits taken off — so windowBL and windowFR2 count as one
    /// thing when asking what a kind of volume is called.</summary>
    private static string Stem(string bone)
    {
        string stem = bone.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        foreach (string side in new[] { "FL", "FR", "BL", "BR" })
        {
            int at = stem.IndexOf(side, StringComparison.Ordinal);
            if (at >= 0) stem = stem.Remove(at, 2);
        }
        if (stem.Length > 1 && (stem.EndsWith('L') || stem.EndsWith('R'))) stem = stem[..^1];
        return stem.Length == 0 ? "(unnamed)" : stem;
    }

    /// <summary>A deform part's flag word in the reference toolkit's own words (S_InitDeformPart.Unk1).</summary>
    private static string FlagNames(uint flags)
    {
        if (flags == 0) return "none";
        var named = new List<string>();
        if ((flags & 0x2) != 0) named.Add("always-dynamic");
        if ((flags & 0x10) != 0) named.Add("kill-part");
        if ((flags & 0x400) != 0) named.Add("snow");
        if ((flags & 0x2000) != 0) named.Add("AI-box");
        if ((flags & 0x40000) != 0) named.Add("fade-off");
        uint rest = flags & ~0x42412u;
        if (rest != 0) named.Add("0x" + rest.ToString("X", CultureInfo.InvariantCulture));
        return string.Join("+", named);
    }

    /// <summary>
    /// Whether a pane's two IN-PLANE extents are the right way round — the last unchecked corner of this
    /// placement.
    ///
    /// <para>
    /// The axis reversal was measured on the shipped stub/volume pairs, and every one of those is a type-5
    /// volume whose extents are the placeholder 0.01 on all three axes. So the reversal was verified for the
    /// matrix and never once for the numbers, and only a self-describing volume has numbers worth reversing.
    /// </para>
    /// <para>
    /// The oracle is that a car window is WIDER THAN IT IS TALL — every side window, every windscreen, every
    /// rear window on every car ever made. So of a pane's two in-plane axes, the more horizontal one has to
    /// carry the bigger number. Reported as "the rotation axis of the blue boxes looks wrong, you can see it
    /// on the windows": the panes stood on edge, tall and narrow, where the glass is long and low.
    /// </para>
    /// </summary>
    private static void Extents(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ are a pane's in-plane extents the right way round? ════");

        string[] names = ["as the reader gives them  (today)", "with the axes reversed too"];
        int[] right = new int[2];
        int panes = 0, cars = 0;
        var examples = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            FrameObjectModel? model = fr?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (model == null) continue;

            IReadOnlyList<PlacedPhysicsVolume> volumes;
            try { volumes = CarPhysicsVolumes.Load(extracted, fr!); }
            catch (Exception) { continue; }
            IReadOnlyList<CarDeformPart> parts = CarPhysicsVolumes.Parts(extracted);
            bool counted = false;

            foreach (PlacedPhysicsVolume v in volumes)
            {
                if (v.Stub != null || v.Bone < 0) continue;
                if (!v.BoneName.Contains("window", StringComparison.OrdinalIgnoreCase)) continue;
                if (ThinAxis(v.Volume.Size) is not int thin) continue;
                panes++;
                if (!counted) { cars++; counted = true; }

                Matrix4x4 world = Faces(v, model, parts, 0);
                int a = (thin + 1) % 3, b = (thin + 2) % 3;
                // Which of the two in-plane axes lies more nearly flat — that is the one along the glass.
                float upA = MathF.Abs(Vector3.Normalize(Row(world, a)).Z);
                float upB = MathF.Abs(Vector3.Normalize(Row(world, b)).Z);
                int flat = upA < upB ? a : b, upright = upA < upB ? b : a;

                Vector3[] readings = [v.Volume.Size, new Vector3(v.Volume.Size.Z, v.Volume.Size.Y, v.Volume.Size.X)];
                for (int reading = 0; reading < 2; reading++)
                {
                    if (Axis(readings[reading], flat) > Axis(readings[reading], upright)) right[reading]++;
                }

                if (examples.Count < 8)
                {
                    examples.Add($"{sds.Name}  {v.BoneName}: today {Axis(v.Volume.Size, flat):F2} m along the "
                        + $"glass x {Axis(v.Volume.Size, upright):F2} m tall  |  reversed "
                        + $"{Axis(readings[1], flat):F2} x {Axis(readings[1], upright):F2}");
                }
            }
        }

        sb.AppendLine($"  {cars} cars, {panes} window panes");
        for (int reading = 0; reading < 2; reading++)
        {
            sb.AppendLine($"    {names[reading],-36} wider than tall {right[reading],4}/{panes} "
                + $"({(panes > 0 ? right[reading] * 100.0 / panes : 0),5:F1}%)");
        }
        foreach (string example in examples) sb.AppendLine("      " + example);

        check("a window pane comes out wider than it is tall, the way glass is",
            panes > 0 && right[0] >= right[1],
            $"as read {right[0]}/{panes}, as written {right[1]}/{panes}");
    }

    private static float Axis(Vector3 v, int index) => index switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    /// <summary>The candidate ways of composing a self-describing volume's ROTATION.</summary>
    private static readonly string[] FacingReadings =
    [
        "parentRot x volumeRot, axes reversed  (today)",
        "plain multiply: volume x parent",
        "plain multiply: parent x volume",
        "the volume's own rotation, unturned",
        "parentRot x volumeRot, axes as written",
        "its OWN part's bone instead of the parent's",
    ];

    private static Matrix4x4 Faces(
        PlacedPhysicsVolume v, FrameObjectModel model, IReadOnlyList<CarDeformPart> parts, int reading)
    {
        Matrix4x4 written = reading == 4 ? SwapPrefabAxes(v.Volume.Transform) : v.Volume.Transform;
        CarDeformPart? own = v.Part >= 0 && v.Part < parts.Count ? parts[v.Part] : null;
        CarDeformPart? up = own == null || own.ParentFrame == 0
            ? null
            : parts.FirstOrDefault(p => p.Frame == own.ParentFrame);
        int bone = up == null ? -1 : BoneOf(model, up.Frame);
        Matrix4x4 parent = bone < 0 ? Matrix4x4.Identity : model.GetJointWorldTransform(bone);

        return reading switch
        {
            1 => written * parent,
            2 => parent * written,
            3 => written,
            5 => v.Bone >= 0 ? model.PlaceOnJoint(written, v.Bone) : written,
            _ => bone < 0 ? written : model.PlaceOnJoint(written, bone),
        };
    }

    /// <summary>The axis a box is CLEARLY thinnest on — a pane's normal — or null when it is not a pane.</summary>
    private static int? ThinAxis(Vector3 size)
    {
        float[] axes = [MathF.Abs(size.X), MathF.Abs(size.Y), MathF.Abs(size.Z)];
        int thin = 0;
        for (int i = 1; i < 3; i++)
        {
            if (axes[i] < axes[thin]) thin = i;
        }
        float next = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            if (i != thin) next = MathF.Min(next, axes[i]);
        }
        // Three times thinner than anything else, or it is a block and has no normal worth speaking of.
        return axes[thin] > 1e-5f && next > axes[thin] * 3f ? thin : null;
    }

    private static Vector3 Row(Matrix4x4 m, int index) => index switch
    {
        0 => new Vector3(m.M11, m.M12, m.M13),
        1 => new Vector3(m.M21, m.M22, m.M23),
        _ => new Vector3(m.M31, m.M32, m.M33),
    };

    /// <summary>The one flat number the Prefab tab addresses a volume by — part and volume folded together,
    /// which is how the panel's rows are keyed.</summary>
    private static int FlatIndexOf(string extracted, PlacedPhysicsVolume volume)
    {
        Illusion.Formats.Prefab.PrefabFile? prefab =
            Illusion.Assets.Prefabs.PrefabEditing.OpenFirst(extracted);
        return prefab?.CarVolumeIndex(volume.Part, volume.Volume.Index) ?? -1;
    }

    /// <summary>The bone whose space a self-describing volume is written in — the part it hangs off.</summary>
    private static string ParentBoneName(string extracted, FrameResource fr, PlacedPhysicsVolume volume)
    {
        FrameObjectModel? model = fr.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
        IReadOnlyList<CarDeformPart> parts = CarPhysicsVolumes.Parts(extracted);
        CarDeformPart? own = volume.Part >= 0 && volume.Part < parts.Count ? parts[volume.Part] : null;
        if (model == null || own == null || own.ParentFrame == 0) return volume.BoneName;
        int bone = BoneOf(model, own.ParentFrame);
        string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray();
        return bone >= 0 && bone < bones.Length ? bones[bone] : volume.BoneName;
    }

    /// <summary>The joint index a frame hash names on this model, or -1.</summary>
    private static int BoneOf(FrameObjectModel model, ulong frame)
    {
        string[] bones = (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "").ToArray();
        for (int i = 0; i < bones.Length; i++)
        {
            if (Fnv64.Hash(bones[i]) == frame) return i;
        }
        return -1;
    }

    /// <summary>The prefab's own axis reversal, which is its own inverse — applying it undoes the reader's.</summary>
    private static Matrix4x4 SwapPrefabAxes(Matrix4x4 m) => new(
        m.M33, m.M32, m.M31, 0f,
        m.M23, m.M22, m.M21, 0f,
        m.M13, m.M12, m.M11, 0f,
        m.M43, m.M42, m.M41, 1f);

    /// <summary>
    /// The reading that wins: fewest mirror misses, with "is it on the car at all" as the tie-break. Symmetry
    /// is the sharper instrument — it separates the answers by a factor of forty, while "on the car" is
    /// generous enough that several readings pass it.
    /// </summary>
    private static int Best(float[] error, int[] scored, int[] inside, int placed)
    {
        int best = 0;
        for (int reading = 1; reading < error.Length; reading++)
        {
            float mine = scored[reading] > 0 ? error[reading] / scored[reading] : float.MaxValue;
            float his = scored[best] > 0 ? error[best] / scored[best] : float.MaxValue;
            if (mine < his - 1e-4f || (MathF.Abs(mine - his) <= 1e-4f && inside[reading] > inside[best]))
            {
                best = reading;
            }
        }
        return best;
    }

    /// <summary>The same volume on the other side of the car, or null when there is no such twin. Only the
    /// left-hand direction answers, so a pair is never counted twice.</summary>
    private static PlacedPhysicsVolume? Partner(
        PlacedPhysicsVolume left, IReadOnlyList<PlacedPhysicsVolume> among)
    {
        string? mirrored = MirrorName(left.BoneName);
        if (mirrored == null) return null;
        PlacedPhysicsVolume? right = among.FirstOrDefault(
            v => string.Equals(v.BoneName, mirrored, StringComparison.Ordinal)
                && v.Volume.VolumeType == left.Volume.VolumeType
                && (v.Volume.Size - left.Volume.Size).Length() < 1e-3f);
        return right != null && string.CompareOrdinal(left.BoneName, right.BoneName) < 0 ? right : null;
    }

    /// <summary>The name of the same thing on the other side of the car, or null when it names no side.</summary>
    private static string? MirrorName(string bone)
    {
        (string From, string To)[] sides =
        [
            ("FL", "FR"), ("FR", "FL"), ("BL", "BR"), ("BR", "BL"),
        ];
        foreach ((string from, string to) in sides)
        {
            int at = bone.IndexOf(from, StringComparison.Ordinal);
            if (at >= 0) return string.Concat(bone.AsSpan(0, at), to, bone.AsSpan(at + 2));
        }
        if (bone.EndsWith('L')) return string.Concat(bone.AsSpan(0, bone.Length - 1), "R");
        if (bone.EndsWith('R')) return string.Concat(bone.AsSpan(0, bone.Length - 1), "L");
        return null;
    }

    /// <summary>
    /// Whether a stub is a HANDLE for the volume it mirrors — measured, because the whole editing path assumes
    /// it is.
    ///
    /// <para>
    /// The prefab writes a volume's placement in the space of the part's BONE. The editor drags the stub frame
    /// and writes the frame's LOCAL matrix straight into that slot, which is only the same space when the stub
    /// hangs off that very joint: a frame attached to joint J is placed by J and by nothing else, while an
    /// unattached one is placed by its ParentIndex1 chain. A stub on the wrong joint — or on none — is a handle
    /// that moves the box somewhere other than where the game will put it, and that is exactly the "the
    /// transform is strange in places" report this section exists to answer.
    /// </para>
    /// </summary>
    private static void Handles(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ is a stub a handle? (stub joint vs the part's bone) ════");

        int cars = 0, volumesWithStub = 0, onPartBone = 0, onOtherBone = 0, onNoBone = 0;
        var strays = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            if (fr?.FrameObjects == null) continue;
            FrameObjectModel? model = fr.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (model == null) continue;

            IReadOnlyList<PlacedPhysicsVolume> placed;
            try { placed = CarPhysicsVolumes.Load(extracted, fr); }
            catch (Exception) { continue; }
            if (placed.Count == 0) continue;
            cars++;

            var jointOf = new Dictionary<FrameObjectBase, int>();
            foreach (FrameObjectModel.AttachmentReference r in model.AttachmentReferences ?? [])
            {
                if (r.Attachment != null) jointOf[r.Attachment] = r.JointIndex;
            }

            foreach (PlacedPhysicsVolume volume in placed)
            {
                if (volume.Stub is not { } stub) continue;
                volumesWithStub++;
                if (!jointOf.TryGetValue(stub, out int joint))
                {
                    onNoBone++;
                    if (strays.Count < 12) strays.Add($"{sds.Name}: {stub.Name} places {volume.PartKind} "
                        + $"\"{volume.BoneName}\" but hangs off no joint at all");
                }
                else if (joint == volume.Bone) { onPartBone++; }
                else
                {
                    onOtherBone++;
                    if (strays.Count < 12) strays.Add($"{sds.Name}: {stub.Name} places {volume.PartKind} "
                        + $"\"{volume.BoneName}\" (bone {volume.Bone}) but hangs off joint {joint}");
                }
            }
        }

        sb.AppendLine($"  {cars} cars, {volumesWithStub} volumes that have a stub: "
            + $"{onPartBone} on the part's own bone, {onOtherBone} on a different bone, {onNoBone} on none");
        foreach (string stray in strays) sb.AppendLine("    " + stray);

        // The editor writes stub.LocalTransform into a slot the game reads in the part's bone space, so the two
        // are only the same space when the stub hangs off that bone. Measured: 1096 of 1097 do, and the one
        // that does not is the game's own left/right slip in shubert_armoured — not a second convention. If
        // this ever starts failing wider, dragging a stub moves the box somewhere the game will not put it,
        // and the fix is to re-space the write rather than to re-measure.
        check("a stub is a handle — it hangs off the very bone of the part its volume belongs to, "
            + "bar one shipped left/right slip",
            volumesWithStub > 0 && onNoBone == 0 && onOtherBone <= 1,
            $"{onOtherBone} on another bone, {onNoBone} on none, out of {volumesWithStub}");
    }

    /// <summary>The deformable-part kinds, as the reference toolkit names them (S_InitDeformPart.Unk0).</summary>
    private static readonly Dictionary<uint, string> PartTypeNames = new()
    {
        [0] = "normal", [1] = "body", [2] = "wheel", [3] = "lid", [4] = "door", [5] = "window",
        [6] = "cover", [7] = "bumper", [12] = "exhaust", [13] = "motor", [14] = "tyre", [15] = "snow",
        [16] = "plow",
    };

    /// <summary>One of the 48 signed axis orders, applied to a vector — the search space for "how are these
    /// two copies of the same placement related".</summary>
    private static Vector3 Permute(Vector3 v, int code)
    {
        ReadOnlySpan<int> orders =
        [
            0, 1, 2,  0, 2, 1,  1, 0, 2,  1, 2, 0,  2, 0, 1,  2, 1, 0,
        ];
        int order = code / 8, signs = code % 8;
        float[] value = [v.X, v.Y, v.Z];
        return new Vector3(
            value[orders[(order * 3) + 0]] * ((signs & 1) != 0 ? -1f : 1f),
            value[orders[(order * 3) + 1]] * ((signs & 2) != 0 ? -1f : 1f),
            value[orders[(order * 3) + 2]] * ((signs & 4) != 0 ? -1f : 1f));
    }

    private static string PermName(int code)
    {
        ReadOnlySpan<char> axis = ['X', 'Y', 'Z'];
        ReadOnlySpan<int> orders =
        [
            0, 1, 2,  0, 2, 1,  1, 0, 2,  1, 2, 0,  2, 0, 1,  2, 1, 0,
        ];
        int order = code / 8, signs = code % 8;
        var text = new StringBuilder("(");
        for (int i = 0; i < 3; i++)
        {
            text.Append((signs & (1 << i)) != 0 ? '-' : '+').Append(axis[orders[(order * 3) + i]]);
            if (i < 2) text.Append(' ');
        }
        return text.Append(')').ToString();
    }

    /// <summary>
    /// Whether the volume's own 3×3 says the same thing as the stub's, once both are read through the axis
    /// order the translations agree on. Written as a basis comparison rather than a matrix identity because
    /// the two sides store their rows in different orders and only the resulting frame has to match.
    /// </summary>
    private static bool RotationMatches(PrefabCollVolumeW v, Matrix4x4 stub)
    {
        Vector3[] mine = [v.Transform.Row0, v.Transform.Row1, v.Transform.Row2];
        Vector3[] theirs =
        [
            new(stub.M11, stub.M12, stub.M13),
            new(stub.M21, stub.M22, stub.M23),
            new(stub.M31, stub.M32, stub.M33),
        ];
        // Each of the volume's rows has to be one of the stub's rows, up to sign and axis order.
        foreach (Vector3 row in mine)
        {
            bool found = false;
            foreach (Vector3 other in theirs)
            {
                for (int k = 0; k < 48 && !found; k++)
                {
                    if ((Permute(row, k) - other).Length() < 1e-3f) found = true;
                }
            }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>
    /// The one conversion a writer can actually use: the same X↔Z swap that relates the two translations,
    /// applied to both indices of the 3×3. If this holds, a placement authored in the frame graph can be
    /// written into the prefab (and back) with no guessing left in it.
    /// </summary>
    private static bool SwapRotationMatches(PrefabCollVolumeW v, Matrix4x4 stub)
    {
        Vector3[] rows = [v.Transform.Row0, v.Transform.Row1, v.Transform.Row2];
        Vector3[] want =
        [
            new(stub.M11, stub.M12, stub.M13),
            new(stub.M21, stub.M22, stub.M23),
            new(stub.M31, stub.M32, stub.M33),
        ];
        for (int i = 0; i < 3; i++)
        {
            Vector3 source = rows[2 - i];
            var swapped = new Vector3(source.Z, source.Y, source.X);
            if ((swapped - want[i]).Length() > 1e-3f) return false;
        }
        return true;
    }

    // ── helpers ──

    /// <summary>Tallies one value of a field whose vocabulary is being surveyed.</summary>
    private static void Bump(Dictionary<short, int> into, short value) =>
        into[value] = into.GetValueOrDefault(value) + 1;

    /// <summary>A hit box that is not just a zeroed placeholder — position or size carries something.</summary>
    private static bool IsNonZeroBox(FrameObjectModel.HitBoxInfo b) =>
        b.Position.S1 != 0 || b.Position.S2 != 0 || b.Position.S3 != 0
        || b.Size.S1 != 0 || b.Size.S2 != 0 || b.Size.S3 != 0;

    private static bool IsRotationIdentity(Matrix4x4 m) =>
        MathF.Abs(m.M11 - 1f) < 1e-4f && MathF.Abs(m.M22 - 1f) < 1e-4f && MathF.Abs(m.M33 - 1f) < 1e-4f
        && MathF.Abs(m.M12) < 1e-4f && MathF.Abs(m.M13) < 1e-4f && MathF.Abs(m.M21) < 1e-4f
        && MathF.Abs(m.M23) < 1e-4f && MathF.Abs(m.M31) < 1e-4f && MathF.Abs(m.M32) < 1e-4f;

    private static bool TryLocalBounds(RigidBodyElement rigid, out Vector3 lo, out Vector3 hi)
    {
        lo = default;
        hi = default;
        switch (rigid.Shape)
        {
            case RigidBodyShape.ConvexPolyhedron:
            case RigidBodyShape.TriangleMesh:
                return CarCollisionShapes.TryReadCookedBounds(rigid.CookedMesh, out lo, out hi);
            case RigidBodyShape.Box:
                lo = -rigid.BoxDimensions; hi = rigid.BoxDimensions; return true;
            case RigidBodyShape.Sphere:
                lo = new Vector3(-rigid.Radius); hi = new Vector3(rigid.Radius); return true;
            case RigidBodyShape.Capsule:
            case RigidBodyShape.Cylinder:
                // Along local Z — measured, see CarCollisionShapes.AppendCapsule.
                hi = new Vector3(rigid.Radius, rigid.Radius, (rigid.Height * 0.5f) + rigid.Radius);
                lo = -hi; return true;
            default:
                return false;
        }
    }
}
