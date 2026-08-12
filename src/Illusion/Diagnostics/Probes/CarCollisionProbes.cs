using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Assets.Collisions;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.ItemDesc;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What a car is actually SHOT AT. The mesh is never what a bullet hits: a car carries a handful of physics
/// shapes hung off its bones, and geometry added to the body has none — so shots pass straight through it.
/// <para>
/// A district keeps that geometry in a <c>.col</c> document; a car archive ships none at all and answers the
/// same stubs out of its own <b>ItemDesc</b> entries instead. This measures that wiring end to end so building
/// on it is not guesswork: which field of a stub names which field of an ItemDesc record, which shapes the
/// game really uses, and which of them need PhysX cooking (the vendored cooker knows exactly one verb,
/// <c>-CookTriangleMesh</c>, so anything convex is out of reach and a primitive is not).
/// </para>
/// <para>Reads the extracted mirror only; nothing is written. Output: %TEMP%\illusion_car_collision.txt</para>
/// </summary>
internal static class CarCollisionProbes
{
    internal static void RunCarCollisionProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_collision.txt");
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
            FileInfo[] archives = new DirectoryInfo(folder).GetFiles("*.sds");
            Array.Sort(archives, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            int cars = 0, carsWithCol = 0, stubsTotal = 0, shapesTotal = 0;
            int resolvedByFileHash = 0, resolvedByDataHash = 0, unresolved = 0;
            int stubsOnBones = 0, stubsNotOnBones = 0;
            int hashIsNameHash = 0, dataHashEqualsHash = 0, identityTransform = 0;
            int cookedTotal = 0, cookedSigned = 0, cookedBounded = 0, cookedContainsVerts = 0;
            float cookedSpan = 0f;
            var shapeCounts = new Dictionary<RigidBodyShape, int>();
            var layerCounts = new Dictionary<int, int>();
            var unresolvedNames = new List<string>();

            foreach (FileInfo sds in archives)
            {
                string extracted = MafiaEnvironment.ExtractedDir(sds);
                if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

                FrameResource? fr;
                List<ItemDescFile> shapes;
                try
                {
                    fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
                    shapes = LoadShapes(extracted);
                }
                catch (Exception) { continue; }
                if (fr?.FrameObjects == null) continue;
                cars++;

                // A car archive is not supposed to carry a .col — the stubs are answered from ItemDesc.
                if (SdsManifest.Load(extracted).GetFiles("Collisions").Count > 0) carsWithCol++;

                var byFileHash = new Dictionary<ulong, ItemDescFile>();
                var byDataHash = new Dictionary<ulong, ItemDescFile>();
                foreach (ItemDescFile shape in shapes)
                {
                    shapesTotal++;
                    byFileHash.TryAdd(shape.Hash, shape);
                    if (shape.Element != null) byDataHash.TryAdd(shape.Element.DataHash, shape);
                    if (shape.Element is RigidBodyElement rigid)
                    {
                        shapeCounts[rigid.Shape] = shapeCounts.GetValueOrDefault(rigid.Shape) + 1;
                        layerCounts[rigid.Layer] = layerCounts.GetValueOrDefault(rigid.Layer) + 1;
                        if (IsIdentity(rigid.Transform)) identityTransform++;
                        if (rigid.Shape is RigidBodyShape.ConvexPolyhedron or RigidBodyShape.TriangleMesh)
                        {
                            cookedTotal++;
                            if (CarCollisionShapes.HasConvexSignature(rigid.CookedMesh)) cookedSigned++;
                            if (CarCollisionShapes.TryReadCookedBounds(rigid.CookedMesh, out Vector3 lo,
                                    out Vector3 hi))
                            {
                                cookedBounded++;
                                // Two independent readings of the same box: the hull's vertices walked, and
                                // the box the blob keeps in its tail. Agreement is what makes either one
                                // believable — neither field is labelled in the format.
                                if (CarCollisionShapes.TryReadTailBounds(rigid.CookedMesh, out Vector3 tlo,
                                        out Vector3 thi)
                                    && (tlo - lo).Length() < 1e-3f && (thi - hi).Length() < 1e-3f)
                                {
                                    cookedContainsVerts++;
                                }
                                cookedSpan = Math.Max(cookedSpan, (hi - lo).Length());
                            }
                        }
                    }
                    if (shape.Element != null && shape.Element.DataHash == shape.Hash) dataHashEqualsHash++;
                }

                // Which bones carry a stub: the attachment list is what puts a hull on a door.
                var attached = new HashSet<FrameObjectBase>();
                foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
                {
                    foreach (FrameObjectModel.AttachmentReference reference in model.AttachmentReferences ?? [])
                    {
                        if (reference.Attachment != null) attached.Add(reference.Attachment);
                    }
                }

                foreach (FrameObjectCollision stub in fr.FrameObjects.Values.OfType<FrameObjectCollision>())
                {
                    stubsTotal++;
                    if (attached.Contains(stub)) stubsOnBones++; else stubsNotOnBones++;
                    // Is the shape's hash simply FNV64 of the stub's name? That is how a new one would be
                    // minted, so it has to be measured rather than assumed.
                    if (Formats.Hashing.Fnv64.Hash(stub.Name.ToString() ?? "") == stub.Hash) hashIsNameHash++;
                    if (byFileHash.ContainsKey(stub.Hash)) resolvedByFileHash++;
                    else if (byDataHash.ContainsKey(stub.Hash)) resolvedByDataHash++;
                    else
                    {
                        unresolved++;
                        if (unresolvedNames.Count < 6) unresolvedNames.Add($"{sds.Name}: {stub.Name}");
                    }
                }
            }

            sb.AppendLine($"CAR COLLISION CENSUS: {cars} car archives, {stubsTotal} collision stubs, "
                + $"{shapesTotal} ItemDesc shapes\n");
            Check("no car archive carries a .col — the shapes live in ItemDesc",
                carsWithCol == 0, $"{carsWithCol} of {cars} carry one");
            Check("every collision stub finds its shape by the ItemDesc's own file hash",
                stubsTotal > 0 && resolvedByFileHash == stubsTotal,
                $"{resolvedByFileHash} by file hash, {resolvedByDataHash} by data hash, {unresolved} unresolved"
                    + (unresolvedNames.Count > 0 ? "; " + string.Join("; ", unresolvedNames) : ""));
            // Not all of them: a stub can also sit in the hierarchy under a plain frame (a trailer, a
            // non-skinned prop in the same archive). Hanging off a BONE is how a hull follows a door, and
            // that is the case this builds on, so it only has to be the rule — not universal.
            Check("collision stubs mostly hang off bones — that is how a hull follows a door",
                stubsTotal > 0 && stubsOnBones > stubsNotOnBones * 4,
                $"{stubsOnBones} on bones, {stubsNotOnBones} elsewhere in the graph");

            // Everything a NEW shape has to get right.
            Check("a shape's own transform is the identity — the FRAME is what places it",
                shapesTotal > 0 && identityTransform == shapesTotal, $"{identityTransform} of {shapesTotal}");
            sb.AppendLine($"  the shape hash is FNV64 of the stub's name on {hashIsNameHash} of {stubsTotal} "
                + $"stubs; the element's data hash equals the file hash on {dataHashEqualsHash} "
                + $"of {shapesTotal} shapes");

            // A cooked hull is the COMMON shape on a car, so the overlay has to draw it at its real size.
            // Nothing here decodes the hull — only the bounding box it carries in its own tail — and that
            // box is believed only because the hull's own vertices sit inside it.
            Check("every cooked shape carries the cooker's convex signature",
                cookedTotal > 0 && cookedSigned == cookedTotal, $"{cookedSigned} of {cookedTotal}");
            Check("…and a readable bounding box in its tail",
                cookedTotal > 0 && cookedBounded == cookedTotal, $"{cookedBounded} of {cookedTotal}");
            Check("…and the same box comes out of walking the hull's own vertices",
                cookedBounded > 0 && cookedContainsVerts == cookedBounded,
                $"{cookedContainsVerts} of {cookedBounded}; widest hull {cookedSpan:F2} m");

            sb.AppendLine("\n  shapes the cars actually use (cooking needed only for the last two):");
            foreach ((RigidBodyShape shape, int count) in shapeCounts.OrderByDescending(p => p.Value))
            {
                bool cooked = shape is RigidBodyShape.TriangleMesh or RigidBodyShape.ConvexPolyhedron;
                sb.AppendLine($"    {shape,-18} {count,5}   {(cooked ? "cooked blob" : "plain numbers")}");
            }
            sb.AppendLine("  collision layer, by how many shapes use it:");
            foreach ((int layer, int count) in layerCounts.OrderByDescending(p => p.Value).Take(8))
                sb.AppendLine($"    layer {layer,3}  {count,5} shapes");

            DumpCar(sb, folder, focus, Check);
            CheckDelivery(sb, folder, focus, Check);
            AddBoxRoundTrip(sb, folder, focus, Check);
            sb.Insert(0, $"CAR COLLISION PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "CAR COLLISION PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    /// <summary>One car's collision wiring in full: every stub, the bone it hangs off, and the shape it names.</summary>
    private static void DumpCar(StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var car = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!car.Exists) { sb.AppendLine($"\nno such archive: {focus}"); return; }

        string extracted = MafiaEnvironment.ExtractedDir(car);
        FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
        if (fr?.FrameObjects == null) return;
        List<ItemDescFile> shapes = LoadShapes(extracted);
        var byHash = new Dictionary<ulong, ItemDescFile>();
        foreach (ItemDescFile shape in shapes) byHash.TryAdd(shape.Hash, shape);

        sb.AppendLine($"\n════ {focus} ════");
        sb.AppendLine($"{shapes.Count} ItemDesc shapes, "
            + $"{fr.FrameObjects.Values.OfType<FrameObjectCollision>().Count()} collision stubs");

        int described = 0, total = 0;
        foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
        {
            string[] bones = (model.GetSkeletonObject().BoneNames ?? [])
                .Select(n => n.ToString() ?? "").ToArray();
            foreach (FrameObjectModel.AttachmentReference reference in model.AttachmentReferences ?? [])
            {
                if (reference.Attachment is not FrameObjectCollision stub) continue;
                total++;
                string bone = reference.JointIndex < bones.Length ? bones[reference.JointIndex] : "?";
                string what = "MISSING";
                if (byHash.TryGetValue(stub.Hash, out ItemDescFile? shape))
                {
                    described++;
                    what = shape.Element is RigidBodyElement rigid
                        ? Describe(rigid)
                        : shape.Element is OpaqueElement ? "opaque" : "?";
                }
                string data = byHash.TryGetValue(stub.Hash, out ItemDescFile? found) && found.Element != null
                    ? $"data 0x{found.Element.DataHash:X16}"
                    : "";
                sb.AppendLine($"    {bone,-14} {stub.Name,-34} 0x{stub.Hash:X16} {data}  {what}");
            }
        }
        check("every stub on a bone names a shape the archive carries", total > 0 && described == total,
            $"{described} of {total}");
    }

    private static string Describe(RigidBodyElement rigid)
    {
        string body = rigid.Shape switch
        {
            RigidBodyShape.Box => $"dims {rigid.BoxDimensions.X:F3} {rigid.BoxDimensions.Y:F3} "
                + $"{rigid.BoxDimensions.Z:F3}",
            RigidBodyShape.Sphere => $"r {rigid.Radius:F3}",
            RigidBodyShape.Capsule or RigidBodyShape.Cylinder => $"r {rigid.Radius:F3} h {rigid.Height:F3}",
            RigidBodyShape.ConvexPolyhedron or RigidBodyShape.TriangleMesh =>
                $"{rigid.CookedMesh?.Length ?? 0} cooked bytes",
            RigidBodyShape.Composite => $"{rigid.Elements.Count} children",
            _ => "?",
        };
        // The 12 floats verbatim: which of them is the translation is exactly what a new shape has to get
        // right, and guessing a row order is how a hull ends up a metre off the part it belongs to.
        string matrix = string.Join(" ", rigid.Transform.Select(f => f.ToString("F2",
            System.Globalization.CultureInfo.InvariantCulture)));
        return $"{rigid.Shape} mat {rigid.MaterialId} layer {rigid.Layer} [{matrix}] {body}";
    }

    /// <summary>
    /// Gives a component a box to be shot at, on a COPY of the car, and checks what the OVERLAY then has to
    /// draw: every stub resolves to a shape, every shape draws as whole line segments, and the new box's
    /// wireframe reaches exactly the half-size it was given. Then puts everything back.
    ///
    /// <para>
    /// The authoring itself goes through the aggregate, because that is the only way a collision is written
    /// now — a role, a shape and a full size, with the volume, the ItemDesc record, the manifest entry and the
    /// mirror stub derived from them. What the record and the stub have to look like is
    /// <c>--probe-collision-role</c>'s question, measured against the shipped corpus; this probe's own
    /// question is the drawing, so that is what it checks beyond the minimum that the box arrived at all.
    /// </para>
    /// <para>
    /// Works on a scratch copy of the extracted folder — the probe must never touch the player's own car.
    /// </para>
    /// </summary>
    private static void AddBoxRoundTrip(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var car = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!car.Exists) return;
        string source = MafiaEnvironment.ExtractedDir(car);
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_carcol_scratch");

        try
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(scratch, Path.GetFileName(file)));

            Car? stitched = Car.ReadFrom(scratch);
            FrameResource? fr = stitched?.Frames;
            FrameObjectModel? model = fr?.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (stitched == null || fr == null || model == null)
            {
                sb.AppendLine("\nno car with a skinned model to attach to");
                return;
            }

            // The bonnet where the car has one, the body otherwise — a part, because a collision hangs off a
            // deform part and a bare bone has nothing to hang one on.
            CarComponent? part = stitched.Components.FirstOrDefault(
                    c => !c.IsBare && string.Equals(c.Name, "coverF", StringComparison.OrdinalIgnoreCase))
                ?? stitched.Body;
            if (part == null || part.IsBare) { sb.AppendLine("\nthis car has no deformable part"); return; }

            int stubsBefore = fr.FrameObjects.Values.OfType<FrameObjectCollision>().Count();
            int shapesBefore = SdsManifest.Load(scratch).GetFiles("ItemDesc").Count;
            int volumesBefore = part.Collisions.Count;

            sb.AppendLine($"\n════ adding a box to \"{part.Name}\" ({part.Kind}) ════");
            CarEdit? edit = stitched.AddCollision(
                part, CarCollisionRole.Body, CarCollisionShape.Box,
                new Vector3(0.60f, 0.40f, 0.10f), new Vector3(0f, 0.10f, 0.02f), out string? refusal);
            check("a solid collision can be added to a component", edit != null, refusal ?? "");
            if (edit == null) return;

            CarSave saved = stitched.Save();
            check("…and the save delivers it whole", saved.Ok, string.Join("; ", saved.Lost));
            if (!saved.Ok) return;

            // The manifest is what packing reads — a shape written and not announced is silently dropped.
            SdsManifest after = SdsManifest.Load(scratch);
            check("the shape record was written and announced in the manifest, which is what packing reads",
                after.GetFiles("ItemDesc").Count == shapesBefore + 1
                    && after.GetFiles("ItemDesc").All(File.Exists),
                $"{shapesBefore} -> {after.GetFiles("ItemDesc").Count} shapes");
            check("the frame graph gained exactly one mirror stub",
                fr.FrameObjects.Values.OfType<FrameObjectCollision>().Count() == stubsBefore + 1,
                $"{stubsBefore} -> {fr.FrameObjects.Values.OfType<FrameObjectCollision>().Count()}");

            // The writer is the real judge: a frame the resource cannot serialize takes the whole car with it.
            var reread = new FrameResource();
            using (var stream = new MemoryStream(fr.WriteToStream())) reread.ReadFromFile(stream);
            check("the new stub survives the writer and comes back on a bone",
                reread.FrameObjects?.Values.OfType<FrameObjectCollision>().Count() == stubsBefore + 1
                    && reread.FrameObjects.Values.OfType<FrameObjectCollision>()
                        .All(c => c.AttachedTo != null),
                $"{reread.FrameObjects?.Values.OfType<FrameObjectCollision>().Count() ?? -1} stubs came back");

            // ── What the overlay draws ──
            Dictionary<ulong, ResolvedCollisionShape> resolvedAll = CarCollisionShapes.Load(
                scratch, fr.FrameObjects.Values.OfType<FrameObjectCollision>());
            int stubs = fr.FrameObjects.Values.OfType<FrameObjectCollision>().Count();
            check("every stub in the archive resolves to a shape the overlay can draw",
                resolvedAll.Count == stubs, $"{resolvedAll.Count} of {stubs}");

            // Every shape draws as SOMETHING, in line pairs. Not "24 vertices each": a box is still twelve
            // edges, but a capsule is drawn as a capsule and a sphere as three circles, because a box around a
            // capsule stands √2·r off the axis and reads as half again too big.
            var lines = new List<Vector3>();
            int drawn = 0;
            foreach (ResolvedCollisionShape one in resolvedAll.Values)
            {
                int before = lines.Count;
                CarCollisionShapes.AppendWireframe(lines, one, Matrix4x4.Identity);
                if (lines.Count > before) drawn++;
            }
            check("every shape draws, as whole line segments",
                drawn == resolvedAll.Count && lines.Count % 2 == 0,
                $"{drawn} of {resolvedAll.Count} shapes, {lines.Count} vertices");

            // The box just added, drawn: its corners sit at HALF the full size that was asked for, which is
            // the whole of what "size is always the whole thing" means once it reaches a shape record.
            //
            // Read off a FRESH stitch, not off the car that wrote it: a component's collision list is built
            // when the car is stitched, so the one that made the edit still lists what it was read with.
            CarCollision? minted = Car.ReadFrom(scratch)?.Components
                .FirstOrDefault(c => string.Equals(c.Name, part.Name, StringComparison.Ordinal))
                ?.Collisions.Skip(volumesBefore).FirstOrDefault();
            ResolvedCollisionShape? mine = resolvedAll.Values.FirstOrDefault(
                r => r.Shape.Element is RigidBodyElement { Shape: RigidBodyShape.Box } b
                    && Math.Abs(b.BoxDimensions.X - 0.30f) < 1e-4f
                    && Math.Abs(b.BoxDimensions.Y - 0.20f) < 1e-4f);
            var boxLines = new List<Vector3>();
            if (mine != null) CarCollisionShapes.AppendWireframe(boxLines, mine, Matrix4x4.Identity);
            float reach = boxLines.Count > 0 ? boxLines.Max(v => Math.Abs(v.X)) : 0f;
            check("the wireframe reaches half the full size the box was given",
                Math.Abs(reach - 0.30f) < 1e-4f, $"{reach:F3} vs 0.300");
            check("…and the aggregate reads that same box back as the FULL size that was typed",
                minted != null && Math.Abs(minted.Size.X - 0.60f) < 1e-4f,
                minted == null ? "the new collision is not on the component when the car is read again"
                    : $"{minted.Size.X:F3} vs 0.600");

            // …and taking it back puts the archive back the way it was — the record, the manifest line and
            // the stub together, because half of them is an archive that cannot be packed.
            stitched.Restore(edit.Before);
            CarSave undone = stitched.Save();
            check("undo delivers whole too", undone.Ok, string.Join("; ", undone.Lost));

            SdsManifest afterUndo = SdsManifest.Load(scratch);
            check("undo takes the stub and the shape away again",
                fr.FrameObjects.Values.OfType<FrameObjectCollision>().Count() == stubsBefore
                    && afterUndo.GetFiles("ItemDesc").Count == shapesBefore,
                $"{fr.FrameObjects.Values.OfType<FrameObjectCollision>().Count()} stubs, "
                    + $"{afterUndo.GetFiles("ItemDesc").Count} shapes");
            // The failure this check exists for: the file went and the manifest kept naming it, so the next
            // Build died with "Could not find file …ItemDesc_0.ids" and the archive could not be packed at
            // all. Packing reads the manifest, so removing a file means unsaying it there too.
            check("every shape the manifest still names is really on disk",
                afterUndo.GetFiles("ItemDesc").All(File.Exists), "");
        }
        catch (Exception ex)
        {
            check("the add-box round trip runs", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch leftovers are not a failure */ }
        }
    }

    /// <summary>
    /// Does an edited shape actually REACH the game? Unpacks the live .sds into scratch and compares its
    /// ItemDesc payloads against the extracted folder the editor writes to.
    /// <para>
    /// This is the question a moved hull cannot answer on its own: a car that drives the same afterwards
    /// either never received the change, or received it and does not use these shapes for driving. Comparing
    /// the two copies separates the two without starting the game.
    /// </para>
    /// </summary>
    private static void CheckDelivery(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var car = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!car.Exists) return;
        string extracted = MafiaEnvironment.ExtractedDir(car);
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_carcol_packed");

        try
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);
            SdsArchive.Open(car.FullName).Extract(scratch);

            var onDisk = new Dictionary<ulong, byte[]>();
            foreach (string file in SdsManifest.Load(extracted).GetFiles("ItemDesc"))
            {
                try { onDisk[ItemDescFile.Load(file).Hash] = File.ReadAllBytes(file); }
                catch (Exception) { /* unreadable here means unreadable everywhere */ }
            }

            var inArchive = new Dictionary<ulong, byte[]>();
            foreach (string file in Directory.GetFiles(scratch, "*.ids", SearchOption.AllDirectories))
            {
                try { inArchive[ItemDescFile.Load(file).Hash] = File.ReadAllBytes(file); }
                catch (Exception) { /* ditto */ }
            }

            int matched = 0, differing = 0, missing = 0;
            foreach ((ulong hash, byte[] bytes) in onDisk)
            {
                if (!inArchive.TryGetValue(hash, out byte[]? packed)) missing++;
                else if (packed.AsSpan().SequenceEqual(bytes)) matched++;
                else differing++;
            }

            sb.AppendLine($"\n════ does an edit reach the archive? ════");
            sb.AppendLine($"    {onDisk.Count} shapes in the extracted folder, {inArchive.Count} in the "
                + $"packed .sds — {matched} identical, {differing} differing, {missing} not packed at all");
            check("every shape the editor holds is in the packed archive, byte for byte",
                onDisk.Count > 0 && differing == 0 && missing == 0,
                $"{differing} differ, {missing} missing — a differing one means the last Build did not carry "
                    + "the edit; all identical means the game HAS the shape and does not use it for this");
        }
        catch (Exception ex)
        {
            check("the packed archive can be read back", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* scratch leftovers are not a failure */ }
        }
    }

    private static bool IsIdentity(float[] m) =>
        m.Length == 12
        && Math.Abs(m[0] - 1f) < 1e-6f && Math.Abs(m[5] - 1f) < 1e-6f && Math.Abs(m[10] - 1f) < 1e-6f
        && Math.Abs(m[1]) < 1e-6f && Math.Abs(m[2]) < 1e-6f && Math.Abs(m[3]) < 1e-6f
        && Math.Abs(m[4]) < 1e-6f && Math.Abs(m[6]) < 1e-6f && Math.Abs(m[7]) < 1e-6f
        && Math.Abs(m[8]) < 1e-6f && Math.Abs(m[9]) < 1e-6f && Math.Abs(m[11]) < 1e-6f;

    private static List<ItemDescFile> LoadShapes(string extracted)
    {
        var shapes = new List<ItemDescFile>();
        foreach (string file in SdsManifest.Load(extracted).GetFiles("ItemDesc"))
        {
            try { shapes.Add(ItemDescFile.Load(file)); }
            catch (Exception) { /* a shape this library cannot read is not this probe's subject */ }
        }
        return shapes;
    }

}
