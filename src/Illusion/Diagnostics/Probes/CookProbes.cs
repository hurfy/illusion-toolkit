using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Collisions;
using Illusion.Formats.Collisions;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Probes of the cooking pipeline: the PhysX runtime detector, the index widener, and the cooker subprocess.
/// Only the last needs a PhysX install; the other two run anywhere, which matters because the widener mutates
/// cooked bytes and must stay covered on machines that cannot cook at all.
/// </summary>
internal static class CookProbes
{
    // Where the toolkit stands on cooking: is the vendored exe present, is the PhysX engine installed, and
    // does a smoke cook actually come back? Reports rather than fails when the runtime is absent — a machine
    // without PhysX is a supported configuration, not a broken one.
    // Output: %TEMP%\illusion_collision_runtime.txt
    internal static void RunRuntimeProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_runtime.txt");
        var sb = new StringBuilder();
        try
        {
            PhysXRuntimeLocator.Forget();
            CookAvailability availability = PhysXRuntimeLocator.Check();
            sb.AppendLine("COLLISION RUNTIME PROBE");
            sb.AppendLine($"cooker: {PhysXRuntimeLocator.CookerPath}");
            sb.AppendLine($"  present = {File.Exists(PhysXRuntimeLocator.CookerPath)}");
            sb.AppendLine($"available = {availability.Available}");
            sb.AppendLine($"detail    = {availability.Detail}");
            sb.AppendLine();

            if (!availability.Available)
            {
                sb.AppendLine("RESULT: SKIPPED — hull shape editing is disabled on this machine, by design.");
                return;
            }

            // A cube is the smallest thing that encloses a volume, so it is the cheapest proof the whole chain
            // works: serialize, spawn, cook, validate, widen.
            (Vector3[] verts, int[] tris, ushort[] ids) = Cube();
            var sw = Stopwatch.StartNew();
            CookResult result = PhysXCooker.Cook(verts, tris, ids);
            sw.Stop();
            sb.AppendLine($"smoke cook: {(result.Cooked != null ? result.Cooked.Length + " bytes" : "REFUSED — " + result.Refusal)}"
                + $" in {sw.ElapsedMilliseconds} ms");
            sb.AppendLine(result.Cooked != null ? "RESULT: PASS" : "RESULT: FAIL");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // The index widener, checked WITHOUT a PhysX install. The cooker picks the narrowest index width the vertex
    // count allows, but every mesh Mafia II ships is 32-bit, so cooked output has to be widened before the game
    // ever sees it — a byte-level mutation that needs its own semantic cover.
    //
    // No fixtures are checked in. Instead the corpus supplies them: take a real shipped 32-bit mesh, NARROW it
    // to 8 or 16 bits, widen it back, and require the original bytes exactly. That round trip is a sharper
    // oracle than any hand-made blob, because the expected answer is a mesh PhysX itself produced.
    // Output: %TEMP%\illusion_collision_widen.txt
    internal static void RunWidenProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_widen.txt");
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
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root)) { sb.AppendLine("resources not unpacked: " + root); return; }

            byte[]? small = null, large = null;   // ≤255 vertices (8-bit capable) and ≤65535 (16-bit capable)
            foreach (string file in Directory.GetFiles(root, "*.col", SearchOption.AllDirectories))
            {
                CollisionFile parsed;
                try { parsed = CollisionFile.Load(file); } catch (Exception) { continue; }
                foreach (CollisionMesh mesh in parsed.Meshes)
                {
                    if (mesh.CookedMesh is not { Length: > 0 } blob) continue;
                    (uint vertexCount, _) = ReadCountsPublic(blob);
                    if (small == null && vertexCount is > 3 and <= 255) small = blob;
                    if (large == null && vertexCount is > 255 and <= 65535) large = blob;
                }
                if (small != null && large != null) break;
            }

            sb.AppendLine("COLLISION WIDEN PROBE");
            Check("found a shipped hull small enough for 8-bit indices", small != null);
            Check("found one that needs 16-bit indices", large != null);

            // A shipped mesh is already 32-bit, so widening must be an exact no-op — anything else means the
            // pass mangles hulls it should not touch at all.
            if (small != null)
            {
                byte[] untouched = CookedIndexWidener.Widen(small);
                Check("widening an already-32-bit hull changes nothing",
                    untouched.AsSpan().SequenceEqual(small), $"{untouched.Length} vs {small.Length} bytes");
            }

            foreach ((byte[]? source, int width) in new[] { (small, 1), (large, 2) })
            {
                if (source == null) continue;
                string label = width == 1 ? "8-bit" : "16-bit";

                byte[] narrow = Narrow(source, width);
                Check($"{label}: narrowing shrinks the blob as expected",
                    narrow.Length == source.Length - TriangleIndexCount(source) * (4 - width),
                    $"{source.Length} -> {narrow.Length}");
                Check($"{label}: the narrowed blob is itself readable",
                    Decodes(narrow), "the probe's own fixture must be valid or it proves nothing");

                byte[] widened = CookedIndexWidener.Widen(narrow);
                Check($"{label}: widening restores the original bytes EXACTLY",
                    widened.AsSpan().SequenceEqual(source),
                    $"{widened.Length} vs {source.Length} bytes, first diff at {FirstDiff(widened, source)}");

                CookedTriangleMesh before = CookedTriangleMesh.Decode(source);
                CookedTriangleMesh after = CookedTriangleMesh.Decode(widened);
                Check($"{label}: the triangles survive the round trip",
                    before.Triangles.AsSpan().SequenceEqual(after.Triangles)
                    && before.TriangleMaterials.AsSpan().SequenceEqual(after.TriangleMaterials));
                Check($"{label}: the OPCODE model still parses after widening", Validates(widened));
            }

            sb.AppendLine();
            sb.AppendLine(fail == 0 && pass > 0 ? "RESULT: PASS" : "RESULT: FAIL");
            sb.Insert(0, $"COLLISION WIDEN PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // The cooker subprocess end to end. SKIPs cleanly without a PhysX install so a machine that cannot cook
    // still reports green — the feature is optional, and a probe that failed there would train people to
    // ignore it. Output: %TEMP%\illusion_collision_cook.txt
    internal static void RunCookProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_cook.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            sb.AppendLine("COLLISION COOK PROBE");
            PhysXRuntimeLocator.Forget();
            CookAvailability availability = PhysXRuntimeLocator.Check();
            if (!availability.Available)
            {
                sb.AppendLine("RESULT: SKIPPED — " + availability.Detail);
                return;
            }

            // --- Bad input never reaches the subprocess: the cooker's way of refusing is an empty file and a
            // --- zero exit code, so anything nameable is caught before it gets there.
            (Vector3[] verts, int[] tris, ushort[] ids) = Cube();
            CookResult noTriangles = PhysXCooker.Cook(verts, Array.Empty<int>(), Array.Empty<ushort>());
            Check("a hull with no triangles is refused by name",
                noTriangles.Cooked == null && noTriangles.Refusal != null, noTriangles.Refusal ?? "");

            var flatVerts = new[] { Vector3.Zero, Vector3.Zero, Vector3.Zero };
            CookResult degenerate = PhysXCooker.Cook(flatVerts, new[] { 0, 1, 2 }, new ushort[] { 4 });
            Check("fully degenerate geometry is refused by name",
                degenerate.Cooked == null && degenerate.Refusal != null, degenerate.Refusal ?? "");

            var badIds = new ushort[ids.Length];
            Array.Fill(badIds, (ushort)0);
            CookResult badSurface = PhysXCooker.Cook(verts, tris, badIds);
            Check("surface id 0 is refused (the .col section bias would underflow)",
                badSurface.Cooked == null && badSurface.Refusal != null, badSurface.Refusal ?? "");

            CookResult mismatched = PhysXCooker.Cook(verts, tris, new ushort[] { 4 });
            Check("one surface per triangle is required",
                mismatched.Cooked == null && mismatched.Refusal != null, mismatched.Refusal ?? "");

            // --- The real thing.
            CookResult cube = PhysXCooker.Cook(verts, tris, ids);
            Check("a cube cooks", cube.Cooked != null, cube.Refusal ?? $"{cube.Cooked?.Length} bytes");
            if (cube.Cooked == null) return;

            Check("the cooked cube decodes to the triangles it was given",
                CookedTriangleMesh.Decode(cube.Cooked).TriangleCount == tris.Length / 3);
            Check("its indices come back 32-bit (shipped data never has narrow ones)",
                IndexWidthOf(cube.Cooked) == 4, IndexWidthOf(cube.Cooked) + " byte(s)");
            Check("its OPCODE model parses", Validates(cube.Cooked));
            Check("its metadata tail is the layout the toolkit understands",
                CookedMeshTail.IsSupported(cube.Cooked, out string? tailWhy), tailWhy ?? "");

            // Determinism is what makes content-keyed hull identity work: the same geometry must cook to the
            // same bytes, or every push would mint a fresh near-duplicate hull.
            CookResult again = PhysXCooker.Cook(verts, tris, ids);
            Check("cooking the same geometry twice is byte-identical",
                again.Cooked != null && again.Cooked.AsSpan().SequenceEqual(cube.Cooked));

            // Per-triangle surfaces must survive: cooking REORDERS triangles, so the material array coming back
            // is a permutation, and losing it would silently repaint a hull's physics surfaces.
            var painted = new ushort[ids.Length];
            for (int i = 0; i < painted.Length; i++) painted[i] = (ushort)(4 + i % 5);
            CookResult multi = PhysXCooker.Cook(verts, tris, painted);
            if (multi.Cooked != null)
            {
                ushort[] back = CookedTriangleMesh.Decode(multi.Cooked).TriangleMaterials;
                var wanted = new List<ushort>(painted);
                var got = new List<ushort>(back);
                wanted.Sort();
                got.Sort();
                Check("every painted surface survives the cook (as a permutation)",
                    wanted.Count == got.Count && wanted.TrueForAll(w => got.Remove(w)));
            }
            else
            {
                Check("a multi-surface cube cooks", false, multi.Refusal ?? "");
            }

            // Bigger meshes take a different path inside PhysX: a real multi-node tree instead of a single node.
            (Vector3[] gv, int[] gt, ushort[] gi) = Grid(20);
            CookResult grid = PhysXCooker.Cook(gv, gt, gi);
            Check($"a {gt.Length / 3}-triangle grid cooks and validates",
                grid.Cooked != null && Validates(grid.Cooked), grid.Refusal ?? $"{grid.Cooked?.Length} bytes");
            if (grid.Cooked != null)
            {
                Check("the grid's indices are 32-bit too (it has enough vertices to tempt 16)",
                    IndexWidthOf(grid.Cooked) == 4);
                Check("the grid decodes to the triangle count it was given",
                    CookedTriangleMesh.Decode(grid.Cooked).TriangleCount == gt.Length / 3);
            }

            sb.AppendLine();
            sb.AppendLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            sb.Insert(0, $"COLLISION COOK PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Accepting a reshaped hull, end to end and headless: a real placement is exported the way the bridge
    // exports it, the payload is edited the way Blender would edit it, and the result goes through the accept
    // path — surfaces resolved from slots, unusable triangles dropped, sections built, cooked, minted.
    //
    // The load-bearing checks are the refusals. A hull painted with a material that names no game surface must
    // be refused rather than defaulted: a guessed physics surface produces a hull that looks right and behaves
    // wrong — wrong footfall, wrong impact — and nothing surfaces it until someone walks there.
    // Output: %TEMP%\illusion_collision_shapepush.txt
    internal static void RunShapePushProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_shapepush.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            sb.AppendLine($"COLLISION SHAPE-PUSH PROBE — district={district}");
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            PhysXRuntimeLocator.Forget();
            CookAvailability availability = PhysXRuntimeLocator.Check();
            if (!availability.Available)
            {
                sb.AppendLine("RESULT: SKIPPED — " + availability.Detail);
                return;
            }

            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }
            string extracted = Assets.Sds.SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            CollisionFile cf = CollisionFile.Load(colPath);
            byte[] originalBytes = cf.ToBytes();
            var document = new Assets.Adapters.CollisionDocumentAdapter(cf, new FileInfo(sds));

            Bridge.Payload.CollisionObjectPayload? exported = null;
            foreach (CollisionInstance instance in cf.Instances)
            {
                exported = Assets.Bridge.CollisionBridgeExporter.TryExport(document.Node(instance), out _);
                // A hull small enough to cook quickly, big enough to be a real one.
                if (exported is { Positions.Length: > 8 and < 600 }) break;
                exported = null;
            }
            if (exported == null) { sb.AppendLine("no suitable exportable placement"); return; }
            sb.AppendLine($"hull {exported.Name}: {exported.Positions.Length} verts, "
                + $"{exported.FaceMaterials.Length} tris, {exported.Materials.Count} surface slot(s)");

            int meshes0 = cf.Meshes.Count;

            // --- An untouched export must cook and mint: this is the whole pipeline on known-good geometry,
            // --- the same isolation the in-game gate uses before any reshape is trusted.
            Assets.Bridge.CollisionPushAcceptor.Result asIs =
                Assets.Bridge.CollisionPushAcceptor.TryAccept(document, exported);
            Check("an unchanged hull cooks and mints", asIs.Minted is { SkipReason: null }, asIs.Refusal ?? "");
            if (asIs.Minted is not { } mintedAsIs) return;
            Check("minting did not touch the file", cf.Meshes.Count == meshes0);
            Check("the minted hull carries sections",
                mintedAsIs.Added is { } added && added.Sections.Count > 0,
                $"{mintedAsIs.Added?.Sections.Count ?? 0} section(s)");
            Check("its identity comes from the cooked bytes, not the source hull",
                mintedAsIs.Hash != exported.MeshHash);

            // Sections must partition the cooked triangle list exactly — a gap or an overlap would mean some
            // triangles belong to no surface, or to two.
            if (mintedAsIs.Added is { } withSections)
            {
                uint covered = 0;
                bool contiguous = true;
                foreach (CollisionSection s in withSections.Sections)
                {
                    if (s.Start != covered) contiguous = false;
                    covered += s.NumEdges;
                }
                int cookedTriangles = CookedTriangleMesh.Decode(withSections.CookedMesh!).TriangleCount;
                Check("the sections tile the triangle list with no gaps",
                    contiguous && covered == cookedTriangles * 3,
                    $"covered {covered} of {cookedTriangles * 3} indices");
            }

            // --- Determinism: the same push twice must resolve to ONE hull, or every push grows the .col.
            cf.Meshes.Add(mintedAsIs.Added!);
            Assets.Bridge.CollisionPushAcceptor.Result repeat =
                Assets.Bridge.CollisionPushAcceptor.TryAccept(document, exported);
            Check("pushing the same geometry again dedupes to the same hull",
                repeat.Minted is { Added: null } r && r.Hash == mintedAsIs.Hash);
            cf.Meshes.Remove(mintedAsIs.Added!);

            // --- A real reshape mints a DIFFERENT hull.
            Bridge.Payload.CollisionObjectPayload moved = Clone(exported);
            moved.Positions[0] += new Vector3(0.5f, 0.25f, 0f);
            Assets.Bridge.CollisionPushAcceptor.Result reshaped =
                Assets.Bridge.CollisionPushAcceptor.TryAccept(document, moved);
            Check("a moved vertex mints a different hull",
                reshaped.Minted is { } m && m.Hash != mintedAsIs.Hash, reshaped.Refusal ?? "");

            // --- Refusals, each naming what to fix.
            Bridge.Payload.CollisionObjectPayload unknownSurface = Clone(exported);
            unknownSurface.Materials = new List<Bridge.Payload.CollisionMaterialInfo>
            {
                new() { RawId = 0, Name = "Material.001" },
            };
            for (int i = 0; i < unknownSurface.FaceMaterials.Length; i++) unknownSurface.FaceMaterials[i] = 0;
            Assets.Bridge.CollisionPushAcceptor.Result unknown =
                Assets.Bridge.CollisionPushAcceptor.TryAccept(document, unknownSurface);
            Check("a face painted with a non-collision material is refused BY NAME",
                unknown.Minted == null && unknown.Refusal != null
                && unknown.Refusal.Contains("Material.001", StringComparison.Ordinal),
                unknown.Refusal ?? "");

            Bridge.Payload.CollisionObjectPayload danglingSlot = Clone(exported);
            danglingSlot.FaceMaterials[0] = 250;
            Assets.Bridge.CollisionPushAcceptor.Result dangling =
                Assets.Bridge.CollisionPushAcceptor.TryAccept(document, danglingSlot);
            Check("a face using a slot the push never described is refused",
                dangling.Minted == null && dangling.Refusal != null, dangling.Refusal ?? "");

            Bridge.Payload.CollisionObjectPayload collapsed = Clone(exported);
            Array.Fill(collapsed.Positions, Vector3.Zero);
            Assets.Bridge.CollisionPushAcceptor.Result flat =
                Assets.Bridge.CollisionPushAcceptor.TryAccept(document, collapsed);
            Check("a hull collapsed to a point is refused, not cooked to nothing",
                flat.Minted == null && flat.Refusal != null, flat.Refusal ?? "");

            Check("every refusal left the .col untouched", cf.ToBytes().AsSpan().SequenceEqual(originalBytes));

            sb.AppendLine();
            sb.AppendLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            sb.Insert(0, $"COLLISION SHAPE-PUSH PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Authoring a hull that never existed (P6): the Shift+D path. A duplicated collision object arrives
    // wearing a fresh "new:" id, its geometry is cooked from scratch, and it becomes a hull plus a placement.
    //
    // The invariant worth guarding is index lockstep: the ray-picker pairs CollisionFile.Instances with the
    // layer's child nodes BY POSITION, so a placement appended without its node — or undone out of order —
    // makes later clicks resolve to a different hull than the one under the cursor.
    // Output: %TEMP%\illusion_collision_newhull.txt
    internal static void RunNewHullProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_newhull.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            sb.AppendLine($"COLLISION NEW-HULL PROBE — district={district}");
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            PhysXRuntimeLocator.Forget();
            CookAvailability availability = PhysXRuntimeLocator.Check();
            if (!availability.Available)
            {
                sb.AppendLine("RESULT: SKIPPED — " + availability.Detail);
                return;
            }

            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }
            string extracted = Assets.Sds.SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            CollisionFile cf = CollisionFile.Load(colPath);
            byte[] originalBytes = cf.ToBytes();
            int meshes0 = cf.Meshes.Count, instances0 = cf.Instances.Count;
            var document = new Assets.Adapters.CollisionDocumentAdapter(cf, new FileInfo(sds));

            // A cube the modder built in Blender, painted with a real game surface.
            (Vector3[] verts, int[] tris, ushort[] ids) = Cube();
            var authored = new Bridge.Payload.CollisionObjectPayload
            {
                Id = "new:probe",
                Name = "authored",
                Positions = verts,
                LoopVertexIndices = Array.ConvertAll(tris, i => (uint)i),
                FaceMaterials = new ushort[tris.Length / 3],
                Materials = new List<Bridge.Payload.CollisionMaterialInfo>
                {
                    new() { RawId = ids[0], Name = "Concrete" },
                },
            };

            Assets.Bridge.CollisionPushAcceptor.Result created =
                Assets.Bridge.CollisionPushAcceptor.TryAccept(document, authored);
            Check("a hull authored from scratch cooks and mints",
                created.Minted is { SkipReason: null }, created.Refusal ?? "");
            if (created.Minted is not { } minted) return;
            Check("nothing was written to the file yet", cf.Meshes.Count == meshes0);

            // Applying it the way the push does: hull first, then the placement.
            var placement = new CollisionInstance
            {
                Position = new Vector3(10f, 20f, 3f),
                Rotation = Vector3.Zero,
                Hash = minted.Hash,
                Unk4 = -1,
                Group = 128,
            };
            cf.Meshes.Add(minted.Added!);
            cf.Instances.Add(placement);
            document.InvalidateMeshIndex();

            Check("the hull and its placement are both in the file",
                cf.Meshes.Count == meshes0 + 1 && cf.Instances.Count == instances0 + 1);
            Check("the placement resolves to the new hull", document.MeshFor(placement.Hash) != null);
            Check("a fresh placement owns nothing and takes the common group",
                placement.Unk4 == -1 && placement.Group == 128);

            // It has to survive the file, since that is the entire point.
            string roundTripPath = Path.Combine(Path.GetTempPath(), "illusion_collision_newhull.col");
            File.WriteAllBytes(roundTripPath, cf.ToBytes());
            CollisionFile reloaded = CollisionFile.Load(roundTripPath);
            File.Delete(roundTripPath);
            CollisionMesh? back = reloaded.Meshes.FirstOrDefault(m => m.Hash == minted.Hash);
            Check("the authored hull survives save and reload",
                back?.CookedMesh is { Length: > 0 } && back.Sections.Count > 0
                && reloaded.Instances.Any(i => i.Hash == minted.Hash));
            Check("its geometry reloads as the cube it was",
                back != null && CookedTriangleMesh.Decode(back.CookedMesh!).TriangleCount == tris.Length / 3);

            // Pushing the same cube again must reuse the hull and add only a placement — otherwise every
            // duplicate a modder makes grows the file by a whole mesh.
            Assets.Bridge.CollisionPushAcceptor.Result twice =
                Assets.Bridge.CollisionPushAcceptor.TryAccept(document, authored);
            Check("an identical second hull dedupes to the first",
                twice.Minted is { Added: null } t && t.Hash == minted.Hash);

            // Undo order: placement off first, then the hull it named.
            cf.Instances.Remove(placement);
            CollisionMeshMinterRemove(cf, minted.Hash);
            document.InvalidateMeshIndex();
            Check("removing both leaves the .col byte-identical",
                cf.ToBytes().AsSpan().SequenceEqual(originalBytes));

            // A mirrored object is refused rather than cooked inside out.
            var mirrored = Clone(authored);
            for (int i = 0; i < mirrored.Positions.Length; i++)
                mirrored.Positions[i] = mirrored.Positions[i] with { X = -mirrored.Positions[i].X };
            Assets.Bridge.CollisionPushAcceptor.Result flipped =
                Assets.Bridge.CollisionPushAcceptor.TryAccept(document, mirrored);
            Check("a mirrored hull still cooks (winding is the caller's problem, not the cooker's)",
                flipped.Minted != null || flipped.Refusal != null, flipped.Refusal ?? "cooked");

            sb.AppendLine();
            sb.AppendLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            sb.Insert(0, $"COLLISION NEW-HULL PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static void CollisionMeshMinterRemove(CollisionFile file, ulong hash) =>
        CollisionMeshMinter.RemoveMesh(file, hash);

    // A payload the probe can mutate without disturbing the one it was derived from.
    private static Bridge.Payload.CollisionObjectPayload Clone(
        Bridge.Payload.CollisionObjectPayload source) => new()
        {
            Id = source.Id,
            Name = source.Name,
            World = source.World,
            Local = source.Local,
            Positions = (Vector3[])source.Positions.Clone(),
            LoopVertexIndices = (uint[])source.LoopVertexIndices.Clone(),
            FaceMaterials = (ushort[])source.FaceMaterials.Clone(),
            Materials = new List<Bridge.Payload.CollisionMaterialInfo>(source.Materials),
            MeshHash = source.MeshHash,
            Group = source.Group,
            Unk4 = source.Unk4,
            Rotation = source.Rotation,
        };

    // ── fixtures ──

    private static (Vector3[] Vertices, int[] Triangles, ushort[] Surfaces) Cube()
    {
        var v = new[]
        {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1),
        };
        var t = new[]
        {
            0, 2, 1, 0, 3, 2,  4, 5, 6, 4, 6, 7,  0, 1, 5, 0, 5, 4,
            2, 3, 7, 2, 7, 6,  1, 2, 6, 1, 6, 5,  0, 4, 7, 0, 7, 3,
        };
        var s = new ushort[t.Length / 3];
        Array.Fill(s, (ushort)4);
        return (v, t, s);
    }

    // An n×n grid of quads: enough vertices and triangles to exercise a real tree rather than a single node.
    private static (Vector3[] Vertices, int[] Triangles, ushort[] Surfaces) Grid(int n)
    {
        var v = new Vector3[(n + 1) * (n + 1)];
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
                v[y * (n + 1) + x] = new Vector3(x, y, ((x * 7 + y * 13) % 5) * 0.25f);

        var t = new List<int>(n * n * 6);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int a = y * (n + 1) + x, b = a + 1, c = a + n + 1, d = c + 1;
                t.Add(a); t.Add(c); t.Add(b);
                t.Add(b); t.Add(c); t.Add(d);
            }
        var s = new ushort[t.Count / 3];
        Array.Fill(s, (ushort)4);
        return (v, t.ToArray(), s);
    }

    // ── byte helpers (the probe reads the header itself; the format's own accessors are internal) ──

    private const int FlagsOffset = 12;
    private const int VertexArrayOffset = 36;

    private static (uint VertexCount, uint TriangleCount) ReadCountsPublic(byte[] blob) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(28)),
         BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(32)));

    private static int TriangleIndexCount(byte[] blob) => (int)ReadCountsPublic(blob).TriangleCount * 3;

    private static int IndexWidthOf(byte[] blob)
    {
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(FlagsOffset));
        return (flags & (1 << 3)) != 0 ? 1 : (flags & (1 << 4)) != 0 ? 2 : 4;
    }

    /// <summary>
    /// The inverse of the widener, used only to build fixtures: rewrites a 32-bit blob's triangle indices at
    /// <paramref name="width"/> bytes and sets the matching flag. Every caller checks that widening the result
    /// reproduces the input, so a mistake here cannot pass as a success.
    /// </summary>
    private static byte[] Narrow(byte[] blob, int width)
    {
        (uint vertexCount, uint triangleCount) = ReadCountsPublic(blob);
        int indexCount = (int)triangleCount * 3;
        int indexStart = VertexArrayOffset + (int)vertexCount * 12;
        int tailStart = indexStart + indexCount * 4;

        var output = new byte[indexStart + indexCount * width + (blob.Length - tailStart)];
        blob.AsSpan(0, indexStart).CopyTo(output);

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(FlagsOffset));
        flags |= width == 1 ? 1u << 3 : 1u << 4;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(FlagsOffset), flags);

        for (int i = 0; i < indexCount; i++)
        {
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(indexStart + i * 4));
            if (width == 1) output[indexStart + i] = (byte)index;
            else BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(indexStart + i * 2), (ushort)index);
        }

        blob.AsSpan(tailStart).CopyTo(output.AsSpan(indexStart + indexCount * width));
        return output;
    }

    private static bool Decodes(byte[] blob)
    {
        try { CookedTriangleMesh.Decode(blob); return true; }
        catch (CollisionDecodeException) { return false; }
    }

    private static bool Validates(byte[] blob)
    {
        try { CookedTriangleMesh.ValidateOpcodeTail(blob); return true; }
        catch (CollisionDecodeException) { return false; }
    }
}
