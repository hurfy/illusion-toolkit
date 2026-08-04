using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Bridge;
using Illusion.Assets.Collisions;
using Illusion.Bridge.Payload;
using Illusion.Assets.Sds;
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
            Delivery(sb, folder, focus, Check);
            if (reference != null) CompareWithStock(sb, folder, focus, reference);
            RoundTrip(sb, folder, focus, Check);
            SecondInfluence(sb, folder, focus, Check);
            CapsuleAxis(sb, folder, Check);
            Census(sb, folder, Check);
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

    private static void Census(StringBuilder sb, string folder, Action<string, bool, string> check)
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
        var unk14Counts = new Dictionary<int, int>();
        var unk20Counts = new Dictionary<int, int>();
        int withUnk2Transform = 0, withUnk6 = 0;
        var typeByPart = new Dictionary<(uint Part, uint Volume), int>();
        int inlineTotal = 0, fitsAsFull = 0, fitsAsHalf = 0;
        int stubsClaimed = 0, shapesUnclaimed = 0, shapesTotal = 0;

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
                    if (n.Length > 0) names.TryAdd(Fnv64.Hash(n), n);
                }
            }

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

            foreach (PrefabDeformPartW part in entry.CarInit[0].Deformation[0].DeformParts)
            {
                parts++;
                partTypes[part.PartType] = partTypes.GetValueOrDefault(part.PartType) + 1;
                foreach (ulong h in part.Unk3)
                {
                    partFrameTotal++;
                    if (names.ContainsKey(h)) partFrameResolved++;
                }
                int here = part.CollisionVolumes.Sum(c => c.Volumes.Count);
                if (here > 0) partsWithVolumes++;
                volumes += here;
                collectionCounts[part.CollisionVolumes.Count] =
                    collectionCounts.GetValueOrDefault(part.CollisionVolumes.Count) + 1;

                // Two unnamed lists of ushorts sit on every deformable part. If either of them is an index
                // INTO the volume list, a volume added without one would be a volume the game never walks —
                // which is exactly the symptom to explain.
                partsCounted++;
                unk14Counts[part.Unk14.Count] = unk14Counts.GetValueOrDefault(part.Unk14.Count) + 1;
                unk20Counts[part.Unk20.Count] = unk20Counts.GetValueOrDefault(part.Unk20.Count) + 1;
                if (part.Unk14.Count > 0 || part.Unk20.Count > 0) indexListsAny++;
                if (part.Unk14.Count == here || part.Unk20.Count == here) indexListsMatchVolumes++;
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
