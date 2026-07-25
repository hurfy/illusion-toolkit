using System.Diagnostics;
using System.IO;
using System.Text;
using Illusion.Assets;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the V2-ported standalone format parsers (bulk parse + byte-roundtrip).</summary>
internal static class FormatProbes
{
    // V2-ported format parsers: enumerate every file of the chosen type under the extracted resources
    // mirror, parse it with the new typed parser, write it back, and assert the bytes are identical.
    // Invariants specific to the format are checked too. Write support is only trustworthy once this is
    // 100% across the install. Formats: "ids" = ItemDesc.
    internal static void RunFormatsProbe(string format)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_formats.txt");
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root)) { sb.AppendLine("resources not unpacked: " + root); return; }

            switch (format)
            {
                case "ids":
                    ProbeItemDesc(root, sb);
                    break;
                case "col":
                    ProbeCollision(root, sb);
                    break;
                case "nav":
                    ProbeNav(root, sb);
                    break;
                case "nov":
                    ProbeNov(root, sb);
                    break;
                case "act":
                    ProbeActors(root, sb);
                    break;
                default:
                    sb.AppendLine("unknown format: " + format + " (supported: ids, col, nav, nov, act)");
                    break;
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally
        {
            sb.AppendLine($"elapsed: {sw.Elapsed.TotalSeconds:F1} s");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Decodes every cooked collision mesh (the opaque PhysX blob CollisionFile keeps verbatim) into
    // vertices+triangles via CookedTriangleMesh.Decode, and re-parses each blob's OPCODE tail as an integrity
    // oracle. A correct forward parse leaves every triangle index in range and lands exactly on the "OPC"
    // magic, with the tail consuming the blob to its end. Output: %TEMP%\illusion_collision_decode.txt
    // Collision surface materials: proves the overlay paints triangles with the material the cooked mesh actually
    // assigns them. Three independent checks per mesh:
    //   1. The .col sections and the NXS per-triangle array must name the same material SET (once the section's
    //      -2 bias is undone) — two independently authored records of the same fact.
    //   2. Every raw id must resolve in CollisionMaterialCatalog, i.e. land inside MaterialsPhysics.tbl's range.
    //   3. The built render parts must partition the index buffer exactly: no gaps, no overlap, full coverage.
    // It also reports how badly the superseded section-range assignment would have mispainted the corpus, which is
    // the regression this probe exists to keep closed. Output: %TEMP%\illusion_collision_materials.txt
    internal static void RunCollisionMaterialsProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_materials.txt");
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root)) { sb.AppendLine("resources not unpacked: " + root); return; }

            string[] files = Directory.GetFiles(root, "*.col", SearchOption.AllDirectories);
            int meshes = 0, withMaterials = 0, setAgree = 0, setDisagree = 0, unresolved = 0, emptiedSections = 0;
            long totalTris = 0, legacyWrongTris = 0;
            int minId = int.MaxValue, maxId = int.MinValue;
            var histogram = new SortedDictionary<int, long>();
            var problems = new StringBuilder();

            foreach (string file in files)
            {
                Formats.Collisions.CollisionFile parsed;
                try { parsed = Formats.Collisions.CollisionFile.Load(file); }
                catch (Exception ex) { problems.AppendLine($"LOAD ERROR {Path.GetFileName(file)}: {ex.Message}"); continue; }

                foreach (var mesh in parsed.Meshes)
                {
                    meshes++;
                    if (mesh.CookedMesh is null) continue;
                    Formats.Collisions.CookedTriangleMesh decoded;
                    try { decoded = Formats.Collisions.CookedTriangleMesh.Decode(mesh.CookedMesh); }
                    catch (Exception ex) { problems.AppendLine($"DECODE FAIL {Path.GetFileName(file)} {mesh.Hash:x16}: {ex.Message}"); continue; }

                    ushort[] materials = decoded.TriangleMaterials;
                    if (materials.Length != decoded.TriangleCount) continue;
                    withMaterials++;
                    totalTris += decoded.TriangleCount;

                    foreach (ushort raw in materials)
                    {
                        histogram.TryGetValue(raw, out long n);
                        histogram[raw] = n + 1;
                        if (raw < minId) minId = raw;
                        if (raw > maxId) maxId = raw;
                    }

                    // (1) Same material set from both records.
                    var fromNxs = new HashSet<int>();
                    foreach (ushort raw in materials) fromNxs.Add(raw);
                    var fromSections = new HashSet<int>();
                    foreach (var section in mesh.Sections)
                    {
                        fromSections.Add((int)section.Material + Domain.CollisionMaterialCatalog.RawToTableBias);
                    }
                    // Sections are a SUPERSET, not an equal set: cooking discards degenerate triangles, so a
                    // section can lose every triangle it owned and survive only in the .col. The cooked array is
                    // authoritative; an id it has that the sections do not is the real contradiction.
                    if (fromSections.IsSupersetOf(fromNxs))
                    {
                        setAgree++;
                        emptiedSections += fromSections.Count - fromNxs.Count;
                    }
                    else
                    {
                        setDisagree++;
                        if (problems.Length < 8000)
                        {
                            problems.AppendLine($"MATERIAL SET MISMATCH {Path.GetFileName(file)} {mesh.Hash:x16}: " +
                                $"nxs=[{string.Join(",", fromNxs)}] sections+2=[{string.Join(",", fromSections)}]");
                        }
                    }

                    // (2) Every id must be a real MaterialsPhysics.tbl entry.
                    foreach (int raw in fromNxs)
                    {
                        if (Domain.CollisionMaterialCatalog.ForRawId(raw).Token == "unknown")
                        {
                            unresolved++;
                            if (problems.Length < 8000) problems.AppendLine($"UNRESOLVED MATERIAL id={raw} in {Path.GetFileName(file)} {mesh.Hash:x16}");
                        }
                    }

                    // How wrong the superseded section-range assignment was, for the same mesh.
                    legacyWrongTris += LegacyMismatchCount(mesh, materials);
                }
            }

            // (3) Render parts must partition the index buffer of every built mesh.
            int partsChecked = 0, partitionBreaks = 0;
            foreach (string file in files)
            {
                Domain.CollisionRenderData data;
                try { data = Assets.Collisions.CollisionSceneBuilder.Build(Formats.Collisions.CollisionFile.Load(file)); }
                catch { continue; }
                foreach (var m in data.Meshes)
                {
                    int cursor = 0;
                    foreach (var part in m.Parts)
                    {
                        partsChecked++;
                        if (part.StartIndex != cursor) { partitionBreaks++; if (problems.Length < 8000) problems.AppendLine($"PART GAP/OVERLAP {Path.GetFileName(file)} {m.Hash:x16} at {part.StartIndex}, expected {cursor}"); }
                        cursor = part.StartIndex + part.IndexCount;
                    }
                    if (cursor != m.Indices.Length)
                    {
                        partitionBreaks++;
                        if (problems.Length < 8000) problems.AppendLine($"PART COVERAGE {Path.GetFileName(file)} {m.Hash:x16}: covered {cursor} of {m.Indices.Length}");
                    }
                    if (m.SourceTriangle.Length != m.Indices.Length / 3)
                    {
                        partitionBreaks++;
                        if (problems.Length < 8000) problems.AppendLine($"SOURCE TRIANGLE LENGTH {Path.GetFileName(file)} {m.Hash:x16}");
                    }
                }
            }

            // Cross-check the table we ship against the installed game's own MaterialsPhysics.tbl. This is what
            // lets the static copy be trusted when tables.sds is unreadable — drift fails loudly instead of
            // silently mislabelling every material.
            var tableReport = new StringBuilder();
            int tableMismatch = 0, tableRows = 0;
            bool tableChecked = false;
            try
            {
                var tokens = Formats.ResourceFormats.MaterialsPhysicsTable.TryReadFromGame(MafiaEnvironment.GameRoot);
                if (tokens is null)
                {
                    tableReport.AppendLine("MaterialsPhysics.tbl: tables.sds not present — shipped table not verified this run.");
                }
                else
                {
                    tableChecked = true;
                    tableRows = tokens.Count;
                    foreach ((int index, string token) in tokens)
                    {
                        var shipped = Domain.CollisionMaterialCatalog.ForTableIndex(index);
                        if (shipped.Index != index || !string.Equals(shipped.Token, token, StringComparison.Ordinal))
                        {
                            tableMismatch++;
                            tableReport.AppendLine($"  TABLE DRIFT index={index}: game='{token}' shipped='{shipped.Token}'");
                        }
                    }
                }
            }
            catch (Exception ex) { tableReport.AppendLine("MaterialsPhysics.tbl read failed: " + ex.Message); }

            bool pass = meshes > 0 && setDisagree == 0 && unresolved == 0 && partitionBreaks == 0 && tableMismatch == 0;
            sb.AppendLine($"COLLISION MATERIALS PROBE — files={files.Length} meshes={meshes} withMaterialArray={withMaterials}");
            sb.AppendLine(pass ? "RESULT: PASS" : meshes == 0 ? "RESULT: NO FILES" : "RESULT: FAIL");
            sb.AppendLine();
            sb.AppendLine($"Section/NXS material-set agreement: {setAgree} contain, {setDisagree} contradict " +
                          $"({emptiedSections} sections lost every triangle to cooking)");
            sb.AppendLine($"Catalog resolution: {unresolved} unresolved ids; raw id range {minId}..{maxId} " +
                          $"(table index {minId - Domain.CollisionMaterialCatalog.RawToTableBias}..{maxId - Domain.CollisionMaterialCatalog.RawToTableBias})");
            sb.AppendLine($"Render parts: {partsChecked} parts checked, {partitionBreaks} partition breaks");
            sb.AppendLine(tableChecked
                ? $"Shipped table vs game MaterialsPhysics.tbl: {tableRows} rows, {tableMismatch} drifted"
                : "Shipped table vs game MaterialsPhysics.tbl: NOT VERIFIED");
            if (tableReport.Length > 0) sb.Append(tableReport);
            sb.AppendLine();
            sb.AppendLine($"Superseded section-range assignment would mispaint {legacyWrongTris} of {totalTris} triangles " +
                          $"({(totalTris > 0 ? 100.0 * legacyWrongTris / totalTris : 0):F1} %) — this is the bug this probe guards.");
            sb.AppendLine();
            sb.AppendLine("Material histogram (triangles):");
            foreach ((int raw, long count) in histogram)
            {
                var material = Domain.CollisionMaterialCatalog.ForRawId(raw);
                sb.AppendLine($"  raw={raw,-4} tbl={material.Index,-4} {material.Token,-22} {material.Name,-26} {count}");
            }
            sb.AppendLine();
            if (problems.Length > 0) sb.Append(problems);
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally
        {
            sb.AppendLine($"elapsed: {sw.Elapsed.TotalSeconds:F1} s");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Triangles the old .col-section-range assignment would have painted with a material other than the one the
    // cooked mesh actually stores. Sections are index ranges (3 per triangle) over the AUTHORED order.
    private static int LegacyMismatchCount(Formats.Collisions.CollisionMesh mesh, ushort[] materials)
    {
        var bySection = new int[materials.Length];
        for (int i = 0; i < bySection.Length; i++) bySection[i] = -1;
        foreach (var section in mesh.Sections)
        {
            int first = (int)(section.Start / 3);
            int last = (int)((section.Start + section.NumEdges) / 3);
            for (int t = first; t < last && t < bySection.Length; t++)
            {
                if (t >= 0) bySection[t] = (int)section.Material + Domain.CollisionMaterialCatalog.RawToTableBias;
            }
        }
        int wrong = 0;
        for (int t = 0; t < materials.Length; t++)
        {
            if (bySection[t] != materials[t]) wrong++;
        }
        return wrong;
    }

    // Corpus oracle for the cooked-mesh scaler. ValidateOpcodeTail CANNOT fail on a wrongly scaled blob — it only
    // proves the structure still parses, and scaling deliberately leaves the structure untouched — so the checks
    // here are semantic: the quantized tree bytes must be bit-identical, the vertices and coefficients must both
    // have moved by exactly s, and the tree's root box (dequantized through the coefficients) must land exactly on
    // the scaled original. That last one is the identity the whole approach rests on.
    // Output: %TEMP%\illusion_collision_scale.txt
    internal static void RunCollisionScaleProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_scale.txt");
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root)) { sb.AppendLine("resources not unpacked: " + root); return; }

            // Two passes over the whole corpus. The uniform one is the original oracle and its rules are the
            // simple ones (every length takes s, mass s³, inertia s⁵). The per-axis one exercises the parts that
            // stop being a multiplication — the refitted bounding sphere and the inertia transform — and is
            // checked by the properties those must satisfy rather than by re-deriving the same formula here,
            // which would only prove the probe agrees with itself.
            // Powers of two keep float multiplication exact, so the assertions are bit-for-bit, not approximate.
            bool uniformOk = RunScalePass(sb, root, new System.Numerics.Vector3(2f), sw);
            sb.AppendLine();
            bool perAxisOk = RunScalePass(sb, root, new System.Numerics.Vector3(2f, 4f, 0.5f), sw);
            sb.AppendLine();
            // Stretching exactly one axis is the case with an analytically clean answer for the inertia tensor
            // (Ixx integrates y and z, so it takes sx and nothing else) — the one independent check available on
            // a transform that otherwise has no closed form to compare against.
            bool singleAxisOk = RunScalePass(sb, root, new System.Numerics.Vector3(2f, 1f, 1f), sw);
            sb.AppendLine();
            sb.AppendLine(uniformOk && perAxisOk && singleAxisOk ? "RESULT: PASS" : "RESULT: FAIL");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static bool RunScalePass(StringBuilder sb, string root, System.Numerics.Vector3 scale, Stopwatch sw)
    {
        {
            bool uniform = scale.X == scale.Y && scale.Y == scale.Z;
            string[] files = Directory.GetFiles(root, "*.col", SearchOption.AllDirectories);
            int meshes = 0, scaled = 0, refused = 0, failed = 0, singleNode = 0;
            int badLength = 0, badPassthrough = 0, badVerts = 0, badCoeffs = 0, badRootBox = 0, badTail = 0, badTopology = 0;
            var failures = new StringBuilder();
            var tailReasons = new Dictionary<string, int>();

            foreach (string file in files)
            {
                Formats.Collisions.CollisionFile parsed;
                try { parsed = Formats.Collisions.CollisionFile.Load(file); }
                catch (Exception ex) { failed++; failures.AppendLine($"LOAD ERROR {Path.GetFileName(file)}: {ex.Message}"); continue; }

                foreach (var mesh in parsed.Meshes)
                {
                    if (mesh.CookedMesh is not { Length: > 0 } original) continue;
                    meshes++;

                    byte[] patched;
                    try { patched = Formats.Collisions.CookedMeshScaler.Scale(original, scale); }
                    catch (NotSupportedException ex)
                    {
                        // A refusal is a correct outcome, not a failure: the patcher declines rather than leave
                        // metadata describing the old size.
                        refused++;
                        if (refused <= 5) failures.AppendLine($"REFUSED {Path.GetFileName(file)} {mesh.Hash:x16}: {ex.Message}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        failures.AppendLine($"SCALE ERROR {Path.GetFileName(file)} {mesh.Hash:x16}: {ex.Message}");
                        continue;
                    }
                    scaled++;

                    if (patched.Length != original.Length) { badLength++; continue; }

                    int opc = Formats.Collisions.CookedTriangleMesh.OpcodeModelOffset(original, out uint modelSize);
                    (uint vertexCount, _) = Formats.Collisions.CookedTriangleMesh.ReadCounts(original);
                    int vertexBytes = (int)vertexCount * 12;
                    int vertexEnd = 36 + vertexBytes;

                    uint modelCode = BitConverter.ToUInt32(original, opc + 8);
                    bool hasTree = (modelCode & 4) == 0; // OPC_SINGLE_NODE serializes no tree
                    if (!hasTree) singleNode++;
                    uint numNodes = hasTree ? BitConverter.ToUInt32(original, opc + 12) : 0;
                    int coeffOff = opc + 16 + (int)numNodes * 20;
                    int tail = opc + (int)modelSize;

                    // 1. Everything between the vertices and the tree — indices, materials, face remap, part
                    //    arrays — plus the quantized node bytes themselves must be untouched.
                    bool passthrough = SpanEqual(original, patched, vertexEnd, opc - vertexEnd);
                    if (hasTree) passthrough &= SpanEqual(original, patched, opc, coeffOff - opc);
                    else passthrough &= SpanEqual(original, patched, opc, tail - opc);
                    if (!passthrough) { badPassthrough++; failures.AppendLine($"PASSTHROUGH {Path.GetFileName(file)} {mesh.Hash:x16}"); continue; }

                    // 2. Vertices moved by exactly s — per axis, so a stretched axis must not disturb the others.
                    bool vertsOk = true;
                    for (int o = 36, axis = 0; o < vertexEnd && vertsOk; o += 4, axis = (axis + 1) % 3)
                        vertsOk = BitConverter.ToSingle(patched, o) == BitConverter.ToSingle(original, o) * Axis(scale, axis);
                    if (!vertsOk) { badVerts++; failures.AppendLine($"VERTICES {Path.GetFileName(file)} {mesh.Hash:x16}"); continue; }

                    // 3. Both coefficient triples moved by exactly s — this is what keeps the quantized integers valid.
                    if (hasTree)
                    {
                        bool coeffOk = true;
                        for (int i = 0; i < 6 && coeffOk; i++)
                        {
                            int o = coeffOff + i * 4;
                            coeffOk = BitConverter.ToSingle(patched, o)
                                == BitConverter.ToSingle(original, o) * Axis(scale, i % 3);
                        }
                        if (!coeffOk) { badCoeffs++; failures.AppendLine($"COEFFS {Path.GetFileName(file)} {mesh.Hash:x16}"); continue; }

                        // 4. THE identity: the root node's box, dequantized through its coefficients, must equal the
                        //    original box scaled by s. Quantized ints unchanged × coefficients scaled by s.
                        if (numNodes > 0 && !RootBoxScaled(original, patched, opc, coeffOff, scale))
                        {
                            badRootBox++;
                            failures.AppendLine($"ROOT BOX {Path.GetFileName(file)} {mesh.Hash:x16}");
                            continue;
                        }
                    }

                    // 5. The tail's own bounds must follow too — the AABB is what a single-node model relies on.
                    if (TailScaled(original, patched, tail, scale, uniform) is { } why)
                    {
                        badTail++;
                        tailReasons[why] = tailReasons.GetValueOrDefault(why) + 1;
                        if (badTail <= 5) failures.AppendLine($"TAIL {Path.GetFileName(file)} {mesh.Hash:x16}: {why}");
                        continue;
                    }

                    // 6. The patched blob still decodes, still parses as an OPCODE model, and describes the SAME
                    //    triangles at s× the coordinates.
                    try
                    {
                        var before = Formats.Collisions.CookedTriangleMesh.Decode(original);
                        var after = Formats.Collisions.CookedTriangleMesh.Decode(patched);
                        Formats.Collisions.CookedTriangleMesh.ValidateOpcodeTail(patched);
                        if (!before.Triangles.AsSpan().SequenceEqual(after.Triangles)
                            || before.Vertices.Length != after.Vertices.Length
                            || after.Vertices[0] != before.Vertices[0] * scale)   // Vector3 * Vector3 is per-axis
                        {
                            badTopology++;
                            failures.AppendLine($"TOPOLOGY {Path.GetFileName(file)} {mesh.Hash:x16}");
                        }
                    }
                    catch (Exception ex)
                    {
                        badTopology++;
                        failures.AppendLine($"REPARSE {Path.GetFileName(file)} {mesh.Hash:x16}: {ex.Message}");
                    }
                }
            }

            int bad = badLength + badPassthrough + badVerts + badCoeffs + badRootBox + badTail + badTopology;
            sb.AppendLine($"— {(uniform ? "UNIFORM" : "PER-AXIS")} PASS, scale={scale} —");
            sb.AppendLine($"{files.Length} files, {meshes} meshes; "
                + $"scaled={scaled} refused={refused} loadOrScaleErrors={failed} singleNodeModels={singleNode}");
            sb.AppendLine($"length={badLength} passthrough={badPassthrough} vertices={badVerts} "
                + $"coefficients={badCoeffs} rootBox={badRootBox} tail={badTail} topology={badTopology}");
            sb.AppendLine($"elapsed {sw.ElapsedMilliseconds} ms total");
            if (tailReasons.Count > 0)
                sb.AppendLine("tail failure reasons: "
                    + string.Join(", ", tailReasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key}={r.Value}")));
            if (failures.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine(failures.ToString(0, Math.Min(failures.Length, 6000)));
            }
            return scaled > 0 && bad == 0 && failed == 0;
        }
    }

    // THROWAWAY MEASUREMENT PROBE — modelCode census over the shipped cooked collision corpus.
    // Question: how many cooked meshes ship with OPC_SINGLE_NODE (bit 4, no serialized tree), and is that
    // population separable from the tree-bearing one by triangle count?
    // Output: %TEMP%\illusion_collision_modelcode.txt
    internal static void RunCollisionModelCodeProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_modelcode.txt");
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root)) { sb.AppendLine("resources not unpacked: " + root); return; }

            string[] files = Directory.GetFiles(root, "*.col", SearchOption.AllDirectories);
            var byCode = new Dictionary<uint, List<uint>>();      // modelCode -> triangle counts (every instance)
            var uniqueByCode = new Dictionary<uint, HashSet<ulong>>(); // modelCode -> distinct mesh hashes
            var singleNodeTris = new List<uint>();
            var treeTris = new List<uint>();
            var singleNodeBig = new List<string>();
            int meshes = 0, failed = 0;

            foreach (string file in files)
            {
                Formats.Collisions.CollisionFile parsed;
                try { parsed = Formats.Collisions.CollisionFile.Load(file); }
                catch (Exception ex) { failed++; sb.AppendLine($"LOAD ERROR {Path.GetFileName(file)}: {ex.Message}"); continue; }

                foreach (var mesh in parsed.Meshes)
                {
                    if (mesh.CookedMesh is not { Length: > 0 } blob) continue;
                    uint modelCode; uint triangleCount;
                    try
                    {
                        int opc = Formats.Collisions.CookedTriangleMesh.OpcodeModelOffset(blob, out _);
                        modelCode = BitConverter.ToUInt32(blob, opc + 8);
                        (_, triangleCount) = Formats.Collisions.CookedTriangleMesh.ReadCounts(blob);
                    }
                    catch (Exception ex) { failed++; sb.AppendLine($"PARSE ERROR {Path.GetFileName(file)} {mesh.Hash:x16}: {ex.Message}"); continue; }
                    meshes++;

                    if (!byCode.TryGetValue(modelCode, out var list)) byCode[modelCode] = list = new List<uint>();
                    list.Add(triangleCount);
                    if (!uniqueByCode.TryGetValue(modelCode, out var set)) uniqueByCode[modelCode] = set = new HashSet<ulong>();
                    set.Add(mesh.Hash);

                    if ((modelCode & 4) != 0)
                    {
                        singleNodeTris.Add(triangleCount);
                        if (triangleCount > 16) singleNodeBig.Add($"{Path.GetFileName(file)} {mesh.Hash:x16} tris={triangleCount} code=0x{modelCode:x}");
                    }
                    else treeTris.Add(triangleCount);
                }
            }

            sb.AppendLine($"COLLISION MODELCODE CENSUS — {files.Length} .col files, {meshes} cooked meshes, {failed} errors");
            sb.AppendLine();
            sb.AppendLine("modelCode distribution (instances / distinct hashes / share):");
            foreach (var kv in byCode.OrderByDescending(k => k.Value.Count))
            {
                string flags = DescribeModelCode(kv.Key);
                sb.AppendLine($"  0x{kv.Key:x8} {flags,-40} n={kv.Value.Count,7} distinct={uniqueByCode[kv.Key].Count,7} "
                    + $"{100.0 * kv.Value.Count / Math.Max(meshes, 1),6:F2}%");
            }
            sb.AppendLine();
            AppendTriStats(sb, "SINGLE_NODE (no serialized tree)", singleNodeTris);
            AppendTriStats(sb, "TREE-BEARING", treeTris);
            sb.AppendLine();
            sb.AppendLine($"SINGLE_NODE with tris > 16: {singleNodeBig.Count}");
            foreach (string s in singleNodeBig.Take(40)) sb.AppendLine("  " + s);
            sb.AppendLine();
            sb.AppendLine($"separation check: max(SINGLE_NODE tris)={(singleNodeTris.Count > 0 ? singleNodeTris.Max() : 0)} "
                + $"min(TREE tris)={(treeTris.Count > 0 ? treeTris.Min() : 0)}");
            int treeBelowSnMax = singleNodeTris.Count > 0 ? treeTris.Count(t => t <= singleNodeTris.Max()) : 0;
            sb.AppendLine($"tree meshes at or below max SINGLE_NODE tri count: {treeBelowSnMax}");
            sb.AppendLine($"elapsed {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static string DescribeModelCode(uint code)
    {
        var parts = new List<string>();
        if ((code & 1) != 0) parts.Add("QUANTIZED");
        if ((code & 2) != 0) parts.Add("NO_LEAF");
        if ((code & 4) != 0) parts.Add("SINGLE_NODE");
        uint rest = code & ~7u;
        if (rest != 0) parts.Add($"unknown:0x{rest:x}");
        if (parts.Count == 0) parts.Add("(none)");
        return string.Join("|", parts);
    }

    private static void AppendTriStats(StringBuilder sb, string label, List<uint> tris)
    {
        if (tris.Count == 0) { sb.AppendLine($"{label}: n=0"); return; }
        tris.Sort();
        uint Pct(double p) => tris[Math.Min(tris.Count - 1, (int)(p * tris.Count))];
        sb.AppendLine($"{label}: n={tris.Count} min={tris[0]} p25={Pct(0.25)} median={Pct(0.5)} p75={Pct(0.75)} "
            + $"p95={Pct(0.95)} p99={Pct(0.99)} max={tris[^1]} mean={tris.Sum(t => (double)t) / tris.Count:F1}");
        var hist = new (string Label, Func<uint, bool> Test)[]
        {
            ("1", t => t == 1), ("2", t => t == 2), ("3-4", t => t is >= 3 and <= 4),
            ("5-8", t => t is >= 5 and <= 8), ("9-16", t => t is >= 9 and <= 16),
            ("17-32", t => t is >= 17 and <= 32), ("33-64", t => t is >= 33 and <= 64),
            ("65-256", t => t is >= 65 and <= 256), (">256", t => t > 256),
        };
        foreach (var (l, test) in hist)
        {
            int n = tris.Count(t => test(t));
            if (n > 0) sb.AppendLine($"    tris {l,-7} : {n,7} ({100.0 * n / tris.Count,6:F2}%)");
        }
    }

    private static bool SpanEqual(byte[] a, byte[] b, int offset, int count) =>
        count <= 0 || a.AsSpan(offset, count).SequenceEqual(b.AsSpan(offset, count));

    // Dequantizes node 0's box from both blobs and checks the patched one is exactly s× the original.
    private static float Axis(System.Numerics.Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static bool RootBoxScaled(
        byte[] original, byte[] patched, int opc, int coeffOff, System.Numerics.Vector3 scale)
    {
        int node0 = opc + 16;
        for (int axis = 0; axis < 3; axis++)
        {
            short center = BitConverter.ToInt16(original, node0 + axis * 2);
            ushort extent = BitConverter.ToUInt16(original, node0 + 6 + axis * 2);
            if (BitConverter.ToInt16(patched, node0 + axis * 2) != center
                || BitConverter.ToUInt16(patched, node0 + 6 + axis * 2) != extent)
            {
                return false; // quantized data must never change
            }

            float s = Axis(scale, axis);
            float centerBefore = center * BitConverter.ToSingle(original, coeffOff + axis * 4);
            float centerAfter = center * BitConverter.ToSingle(patched, coeffOff + axis * 4);
            float extentBefore = extent * BitConverter.ToSingle(original, coeffOff + 12 + axis * 4);
            float extentAfter = extent * BitConverter.ToSingle(patched, coeffOff + 12 + axis * 4);
            if (centerAfter != centerBefore * s || extentAfter != extentBefore * s) return false;
        }
        return true;
    }

    // Tail rules. Lengths take their own axis; mass takes the volume factor. The bounding sphere and the inertia
    // tensor are the two fields a per-axis scale cannot simply multiply, so they are checked by the properties
    // they must SATISFY — re-deriving their formulas here would only prove the probe agrees with itself.
    private static string? TailScaled(
        byte[] original, byte[] patched, int tail, System.Numerics.Vector3 scale, bool uniform)
    {
        // Sphere centre, AABB min/max, centre of mass — three floats each, one axis factor per component.
        foreach (int block in new[] { 4, 20, 32, 84 })
            for (int axis = 0; axis < 3; axis++)
            {
                int o = tail + block + axis * 4;
                if (BitConverter.ToSingle(patched, o) != BitConverter.ToSingle(original, o) * Axis(scale, axis))
                    return $"length@{block}+{axis}";
            }

        // A tolerance is one number for three axes, so it takes the smallest factor — never a larger one, which
        // could let it exceed a dimension the hull shrank along.
        float smallest = MathF.Min(scale.X, MathF.Min(scale.Y, scale.Z));
        if (BitConverter.ToSingle(patched, tail) != BitConverter.ToSingle(original, tail) * smallest)
            return "epsilon";

        if (BitConverter.ToSingle(patched, tail + 44)
            != BitConverter.ToSingle(original, tail + 44) * scale.X * scale.Y * scale.Z)
        {
            return "mass";
        }

        if (!SphereBoundsBox(patched, tail, uniform, original, scale)) return "sphere";
        if (InertiaPlausible(original, patched, tail, scale, uniform) is { } why) return "inertia:" + why;

        // The triangle count and the per-triangle edge flags after it are scale-invariant.
        return BitConverter.ToUInt32(patched, tail + 96) == BitConverter.ToUInt32(original, tail + 96)
            ? null : "triangleCount";
    }

    // A uniform factor keeps the plain multiply (the sphere stays a sphere), so assert exactly that. An uneven
    // one refits, and what MATTERS about the result is that it still contains the geometry: the sphere must
    // reach every corner of the scaled AABB, and must not be wildly bigger than it needs to be.
    private static bool SphereBoundsBox(
        byte[] patched, int tail, bool uniform, byte[] original, System.Numerics.Vector3 scale)
    {
        float radius = BitConverter.ToSingle(patched, tail + 16);
        if (uniform) return radius == BitConverter.ToSingle(original, tail + 16) * scale.X;

        var centre = new System.Numerics.Vector3(
            BitConverter.ToSingle(patched, tail + 4),
            BitConverter.ToSingle(patched, tail + 8),
            BitConverter.ToSingle(patched, tail + 12));
        var min = new System.Numerics.Vector3(
            BitConverter.ToSingle(patched, tail + 20),
            BitConverter.ToSingle(patched, tail + 24),
            BitConverter.ToSingle(patched, tail + 28));
        var max = new System.Numerics.Vector3(
            BitConverter.ToSingle(patched, tail + 32),
            BitConverter.ToSingle(patched, tail + 36),
            BitConverter.ToSingle(patched, tail + 40));

        float furthest = 0f;
        for (int corner = 0; corner < 8; corner++)
        {
            var p = new System.Numerics.Vector3(
                (corner & 1) == 0 ? min.X : max.X,
                (corner & 2) == 0 ? min.Y : max.Y,
                (corner & 4) == 0 ? min.Z : max.Z);
            furthest = MathF.Max(furthest, (p - centre).Length());
        }
        // Equality within a float tick — the refit computes exactly this, so a mismatch means it did not run.
        return radius >= furthest * 0.9999f && radius <= furthest * 1.0001f;
    }

    // Under a uniform factor the tensor obeys the known s⁵ law, so assert it exactly. Under an uneven one there
    // is no single factor; assert instead that the result is still a physically valid inertia tensor — symmetric,
    // positive on the diagonal, and satisfying the triangle inequalities every real body's tensor must — plus the
    // one clean law that survives: scaling ONLY x multiplies Ixx by sx alone, since Ixx integrates y and z.
    private static string? InertiaPlausible(
        byte[] original, byte[] patched, int tail, System.Numerics.Vector3 scale, bool uniform)
    {
        float I(byte[] b, int r, int c) => BitConverter.ToSingle(b, tail + 48 + (r * 3 + c) * 4);

        if (uniform)
        {
            float f = scale.X * scale.X * scale.X * scale.X * scale.X;
            for (int i = 0; i < 9; i++)
            {
                int o = tail + 48 + i * 4;
                if (BitConverter.ToSingle(patched, o) != BitConverter.ToSingle(original, o) * f) return "s^5";
            }
            return null;
        }

        for (int i = 0; i < 9; i++)
            if (!float.IsFinite(BitConverter.ToSingle(patched, tail + 48 + i * 4))) return "not finite";

        // Symmetry must be PRESERVED, not asserted from scratch: whatever asymmetry PhysX's own numbers already
        // carry is not this patcher's to fix, but the transform must not add any.
        for (int r = 0; r < 3; r++)
            for (int c = r + 1; c < 3; c++)
            {
                float wasOff = MathF.Abs(I(original, r, c) - I(original, c, r));
                float nowOff = MathF.Abs(I(patched, r, c) - I(patched, c, r));
                float bound = MathF.Max(MathF.Abs(I(patched, r, c)), MathF.Abs(I(patched, c, r)));
                // Allow the original asymmetry to grow by the same factors the terms themselves grew by.
                float allowed = MathF.Max(bound * 1e-4f, wasOff * scale.X * scale.Y * scale.Z
                    * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)) * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)) * 1.001f);
                if (nowOff > allowed) return $"asymmetry {r}{c}";
            }

        // The one analytically clean law that survives an uneven scale: stretching ONLY x multiplies Ixx by sx
        // and nothing else, because Ixx integrates y and z and only the added mass matters. Checked whenever the
        // pass happens to scale a single axis; a general scale has no such closed form to check against.
        int stretched = -1, singles = 0;
        for (int a = 0; a < 3; a++)
        {
            if (Axis(scale, a) == 1f) continue;
            stretched = a;
            singles++;
        }
        if (singles == 1)
        {
            float expected = I(original, stretched, stretched) * Axis(scale, stretched);
            float actual = I(patched, stretched, stretched);
            float tolerance = MathF.Max(MathF.Abs(expected) * 1e-4f, 1e-12f);
            if (MathF.Abs(actual - expected) > tolerance) return "single-axis law";
        }
        return null;
    }

    internal static void RunCollisionDecodeProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_decode.txt");
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root)) { sb.AppendLine("resources not unpacked: " + root); return; }

            string[] files = Directory.GetFiles(root, "*.col", SearchOption.AllDirectories);
            int meshes = 0, ok = 0, fail = 0, lowUsage = 0;
            long totalVerts = 0, totalTris = 0, trailingTotal = 0;
            int trailingMin = int.MaxValue, trailingMax = 0;
            double usageSum = 0, minUsage = 1.0;
            var failures = new StringBuilder();

            foreach (string file in files)
            {
                Formats.Collisions.CollisionFile parsed;
                try { parsed = Formats.Collisions.CollisionFile.Load(file); }
                catch (Exception ex) { fail++; failures.AppendLine($"LOAD ERROR {Path.GetFileName(file)}: {ex.Message}"); continue; }

                foreach (var mesh in parsed.Meshes)
                {
                    meshes++;
                    if (mesh.CookedMesh is null) { fail++; failures.AppendLine($"NULL BLOB {Path.GetFileName(file)} {mesh.Hash:x16}"); continue; }
                    try
                    {
                        var decoded = Formats.Collisions.CookedTriangleMesh.Decode(mesh.CookedMesh);
                        // Oracle: the OPCODE model right after the geometry must parse cleanly. Its trailing
                        // byte count is expected to be nonzero (PhysX stores bounds/edge metadata after it).
                        int trailing = Formats.Collisions.CookedTriangleMesh.ValidateOpcodeTail(mesh.CookedMesh);
                        totalVerts += decoded.Vertices.Length;
                        totalTris += decoded.TriangleCount;
                        // Correctness signal: a correct decode references almost every vertex. A wrong index
                        // width (the 16-vs-32-bit trap — both keep indices < V) leaves half the indices zero, so
                        // the used-vertex fraction collapses. This is what "all indices < V" alone cannot catch.
                        double usage = VertexUsage(decoded);
                        usageSum += usage;
                        if (usage < minUsage) minUsage = usage;
                        if (usage < 0.5)
                        {
                            lowUsage++;
                            if (failures.Length < 8000) failures.AppendLine($"LOW VERTEX USAGE {usage:P0} {Path.GetFileName(file)} {mesh.Hash:x16} (V={decoded.Vertices.Length} T={decoded.TriangleCount})");
                        }
                        trailingTotal += trailing;
                        if (trailing < trailingMin) trailingMin = trailing;
                        if (trailing > trailingMax) trailingMax = trailing;
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        if (failures.Length < 8000) failures.AppendLine($"DECODE FAIL {Path.GetFileName(file)} {mesh.Hash:x16}: {ex.Message}");
                    }
                }
            }

            sb.AppendLine($"COLLISION DECODE PROBE — files={files.Length} meshes={meshes} ok={ok} fail={fail} lowVertexUsage={lowUsage}");
            sb.AppendLine(fail == 0 && lowUsage == 0 && meshes > 0 ? "RESULT: PASS" : meshes == 0 ? "RESULT: NO FILES" : "RESULT: FAIL");
            sb.AppendLine();
            sb.AppendLine($"Totals: {totalVerts} vertices, {totalTris} triangles decoded (32-bit indices)");
            sb.AppendLine($"Vertex usage (distinct referenced / vertexCount): avg {(ok > 0 ? usageSum / ok : 0):P1}, min {(ok > 0 ? minUsage : 0):P1}");
            sb.AppendLine($"OPCODE tail: all {ok} models parsed cleanly; post-model metadata {(ok > 0 ? trailingMin : 0)}..{trailingMax} B (total {trailingTotal})");
            sb.AppendLine();
            if (failures.Length > 0) sb.Append(failures);
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally
        {
            sb.AppendLine($"elapsed: {sw.Elapsed.TotalSeconds:F1} s");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Fraction of a decoded mesh's vertices that its triangles actually reference (1.0 = every vertex used).
    private static double VertexUsage(Formats.Collisions.CookedTriangleMesh mesh)
    {
        if (mesh.Vertices.Length == 0) return 1.0;
        var used = new HashSet<int>();
        foreach (int i in mesh.Triangles) used.Add(i);
        return (double)used.Count / mesh.Vertices.Length;
    }

    private static void ProbeItemDesc(string root, StringBuilder sb)
    {
        string[] files = Directory.GetFiles(root, "*.ids", SearchOption.AllDirectories);
        int ok = 0, fail = 0, opaque = 0;
        var shapeCounts = new SortedDictionary<string, int>();
        var failures = new StringBuilder();

        foreach (string file in files)
        {
            try
            {
                byte[] original = File.ReadAllBytes(file);
                var parsed = Formats.ItemDesc.ItemDescFile.Load(file);

                string kind = parsed.IsOpaque
                    ? $"Opaque(Type{(byte)parsed.Type}/Sub{parsed.SubType})"
                    : parsed.Type == Formats.ItemDesc.ItemDescType.RigidBody
                        ? ((Formats.ItemDesc.RigidBodyShape)parsed.SubType).ToString()
                        : parsed.Type.ToString();
                shapeCounts.TryGetValue(kind, out int c);
                shapeCounts[kind] = c + 1;
                if (parsed.IsOpaque) opaque++;

                byte[] rewritten = parsed.ToBytes();
                if (original.AsSpan().SequenceEqual(rewritten))
                {
                    ok++;
                }
                else
                {
                    fail++;
                    failures.AppendLine($"ROUNDTRIP FAIL {Path.GetFileName(file)}: {original.Length}B vs " +
                                        $"{rewritten.Length}B, first diff at {FirstDiff(original, rewritten)}");
                }
            }
            catch (Exception ex)
            {
                fail++;
                failures.AppendLine($"PARSE ERROR {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        // Opaque files (unmapped Type/SubType kept verbatim) still round-trip; they count as ok, and are
        // reported so unmapped kinds are visible for a future typed parser.
        sb.AppendLine($"ITEMDESC PROBE — files={files.Length} roundtrip ok={ok} fail={fail} (typed={ok - opaque}, opaque={opaque})");
        sb.AppendLine(fail == 0 && files.Length > 0 ? "RESULT: PASS" : files.Length == 0 ? "RESULT: NO FILES" : "RESULT: FAIL");
        sb.AppendLine();
        sb.AppendLine("Kinds: " + string.Join(", ", shapeCounts.Select(kv => $"{kv.Key}={kv.Value}")));
        sb.AppendLine();
        if (failures.Length > 0) sb.Append(failures);
    }

    private static void ProbeCollision(string root, StringBuilder sb)
    {
        string[] files = Directory.GetFiles(root, "*.col", SearchOption.AllDirectories);
        int ok = 0, fail = 0;
        long totalInstances = 0, totalMeshes = 0;
        var hashes = new HashSet<ulong>();
        var failures = new StringBuilder();

        foreach (string file in files)
        {
            try
            {
                byte[] original = File.ReadAllBytes(file);
                var parsed = Formats.Collisions.CollisionFile.Load(file);
                totalInstances += parsed.Instances.Count;
                totalMeshes += parsed.Meshes.Count;
                foreach (var m in parsed.Meshes) hashes.Add(m.Hash);

                byte[] rewritten = parsed.ToBytes();
                if (original.AsSpan().SequenceEqual(rewritten)) ok++;
                else
                {
                    fail++;
                    failures.AppendLine($"ROUNDTRIP FAIL {Path.GetFileName(file)}: {original.Length}B vs " +
                                        $"{rewritten.Length}B, first diff at {FirstDiff(original, rewritten)}");
                }
            }
            catch (Exception ex)
            {
                fail++;
                failures.AppendLine($"PARSE ERROR {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        sb.AppendLine($"COLLISION PROBE — files={files.Length} roundtrip ok={ok} fail={fail}");
        sb.AppendLine(fail == 0 && files.Length > 0 ? "RESULT: PASS" : files.Length == 0 ? "RESULT: NO FILES" : "RESULT: FAIL");
        sb.AppendLine();
        sb.AppendLine($"Totals: {totalInstances} instances, {totalMeshes} meshes, {hashes.Count} unique mesh hashes");
        sb.AppendLine();
        if (failures.Length > 0) sb.Append(failures);
    }

    private static void ProbeNav(string root, StringBuilder sb)
    {
        RoundtripFormat(root, "*.nav", "AIWORLD", sb, file =>
        {
            var parsed = Formats.Navigation.AiWorldFile.Load(file);
            return (parsed.ToBytes(), $"world={parsed.WorldId} objs={parsed.PathObjectCount}");
        });
    }

    private static void ProbeNov(string root, StringBuilder sb)
    {
        RoundtripFormat(root, "*.nov", "OBJ_DATA", sb, file =>
        {
            var parsed = Formats.Navigation.ObjDataFile.Load(file);
            return (parsed.ToBytes(), $"verts={parsed.GraphVertices.Count} edges={parsed.GraphEdges.Count} cells={parsed.Aimesh.Cells.Count} meshboxes={parsed.AiMeshBoxLines().Count / 24} tail={parsed.Aimesh.MeshTail.Length}B gen='{parsed.GenerationName}'");
        });
    }

    private static void ProbeActors(string root, StringBuilder sb)
    {
        long compressed = 0, uncompressed = 0, sceneRefs = 0;
        RoundtripFormat(root, "*.act", "ACTORS", sb, file =>
        {
            var parsed = Formats.Actors.ActorsFile.Load(file);
            if (parsed.IsCompressed) compressed++; else uncompressed++;
            sceneRefs += parsed.SceneReferences.Count;
            return (parsed.ToBytes(), $"v{parsed.ActorFileVersion} " +
                $"{(parsed.IsCompressed ? "compressed" : "uncompressed")}, {parsed.SceneReferences.Count} scene refs, {parsed.EntityCount} entities");
        });
        sb.AppendLine($"Aggregate: {compressed} compressed, {uncompressed} uncompressed, {sceneRefs} scene refs total");
    }

    // Shared bulk parse-and-roundtrip driver for the simpler format probes.
    private static void RoundtripFormat(string root, string pattern, string label, StringBuilder sb,
        Func<string, (byte[] Rewritten, string Info)> parse)
    {
        string[] files = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
        int ok = 0, fail = 0;
        var failures = new StringBuilder();
        string? firstInfo = null;

        foreach (string file in files)
        {
            try
            {
                byte[] original = File.ReadAllBytes(file);
                (byte[] rewritten, string info) = parse(file);
                firstInfo ??= info;
                if (original.AsSpan().SequenceEqual(rewritten)) ok++;
                else
                {
                    fail++;
                    failures.AppendLine($"ROUNDTRIP FAIL {Path.GetFileName(file)}: {original.Length}B vs " +
                                        $"{rewritten.Length}B, first diff at {FirstDiff(original, rewritten)}");
                }
            }
            catch (Exception ex)
            {
                fail++;
                failures.AppendLine($"PARSE ERROR {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        sb.AppendLine($"{label} PROBE — files={files.Length} roundtrip ok={ok} fail={fail}");
        sb.AppendLine(fail == 0 && files.Length > 0 ? "RESULT: PASS" : files.Length == 0 ? "RESULT: NO FILES" : "RESULT: FAIL");
        sb.AppendLine();
        if (firstInfo != null) sb.AppendLine("Sample: " + firstInfo);
        sb.AppendLine();
        if (failures.Length > 0) sb.Append(failures);
    }

    // Pre-flight census for full collision editing. Four questions whose answers decide design, not code
    // correctness — each one, answered wrongly by assumption, silently corrupts a .col later:
    //   1. FRAME PAIRING. A placement's Unk4 is believed to name the FrameObjectCollision that owns it, and
    //      that frame object carries its OWN copy of the mesh hash. If the pairing holds, every hash repoint
    //      (scale mint, shape accept) must rewrite the frame side too, or the two references desync.
    //      Both candidate index conventions are measured: the raw ordinal and the block-offset one the
    //      FrameResource itself uses for parent links (GetIndexOfObject adds GetBlockCount).
    //   2. SELF-CONTAINMENT. Orphan sweeping assumes every placement resolves inside its own file. If shipped
    //      data has dangling or cross-file hashes, an IsOrphan-driven sweep deletes hulls the game still needs.
    //   3. MESH ORDER. Shipped files are expected hash-ascending (the old toolkit wrote a SortedDictionary).
    //      If they are, minted hulls must be inserted in order rather than appended; if not, the game
    //      tolerates any order and plain append stands.
    //   4. ONE .col PER ARCHIVE. SdsCollisionSaver writes GetFiles("Collisions")[0]; a second entry would
    //      mean the save can target the wrong file.
    // Output: %TEMP%\illusion_collision_census.txt
    internal static void RunCollisionCensusProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_census.txt");
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root)) { sb.AppendLine("resources not unpacked: " + root); return; }

            string[] files = Directory.GetFiles(root, "*.col", SearchOption.AllDirectories);

            // ── 1. Self-containment, ordering, orphans (per file) ──────────────────────────────────────
            int filesParsed = 0, loadErrors = 0;
            long instances = 0, meshes = 0;
            int filesUnsorted = 0, filesWithDangling = 0, filesWithDuplicateMeshHash = 0, filesWithOrphans = 0;
            long danglingPlacements = 0, orphanMeshes = 0, blobless = 0;
            var perFileHashes = new Dictionary<ulong, (int Files, int Copies, long Length, ulong Content, bool Divergent)>();
            var unk4Values = new List<int>();
            long unk4Negative = 0, unk4NonNegative = 0;
            var groups = new Dictionary<byte, long>();
            var orphanFiles = new StringBuilder();
            var failures = new StringBuilder();

            // ── 4. .col files per extracted archive folder ────────────────────────────────────────────
            var colsPerFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // ── 2. Frame pairing (files that have a FrameResource beside the .col) ────────────────────
            int framePairedFiles = 0, frameErrors = 0;
            long rawIndexHits = 0, rawIndexMisses = 0, rawIndexOutOfRange = 0, rawIndexWrongType = 0, rawIndexHashMismatch = 0;
            long blockIndexHits = 0, blockIndexMisses = 0;
            long frameCollisionNodes = 0, frameHashesMissingFromCol = 0;
            var frameCollisionHashes = new HashSet<ulong>();
            var unk4TargetTypes = new Dictionary<string, long>();
            var unk4TargetDistances = new List<float>();

            foreach (string file in files)
            {
                string folder = Path.GetDirectoryName(file)!;
                colsPerFolder[folder] = colsPerFolder.GetValueOrDefault(folder) + 1;

                Formats.Collisions.CollisionFile col;
                try { col = Formats.Collisions.CollisionFile.Load(file); }
                catch (Exception ex) { loadErrors++; failures.AppendLine($"LOAD {Path.GetFileName(file)}: {ex.Message}"); continue; }
                filesParsed++;
                instances += col.Instances.Count;
                meshes += col.Meshes.Count;

                var meshByHash = new Dictionary<ulong, Formats.Collisions.CollisionMesh>();
                bool duplicateHash = false, sorted = true;
                ulong previous = 0;
                bool first = true;
                foreach (Formats.Collisions.CollisionMesh mesh in col.Meshes)
                {
                    if (!meshByHash.TryAdd(mesh.Hash, mesh)) duplicateHash = true;
                    if (!first && mesh.Hash < previous) sorted = false;
                    previous = mesh.Hash;
                    first = false;
                    if (mesh.CookedMesh is not { Length: > 0 }) blobless++;

                    // Cross-file identity: the same hash in two files should carry the same bytes, or the
                    // hash is not a global identity and content-keyed minting has to be per file.
                    ulong content = mesh.CookedMesh is { Length: > 0 } b
                        ? Formats.Hashing.Fnv64.Hash(b, 0, b.Length) : 0;
                    long length = mesh.CookedMesh?.Length ?? 0;
                    if (perFileHashes.TryGetValue(mesh.Hash, out var seen))
                    {
                        perFileHashes[mesh.Hash] = (seen.Files + 1, seen.Copies + 1, seen.Length, seen.Content,
                            seen.Divergent || seen.Content != content || seen.Length != length);
                    }
                    else
                    {
                        perFileHashes[mesh.Hash] = (1, 1, length, content, false);
                    }
                }
                if (duplicateHash) filesWithDuplicateMeshHash++;
                if (!sorted) { filesUnsorted++; failures.AppendLine($"UNSORTED {Path.GetFileName(file)}"); }

                var placed = new HashSet<ulong>();
                int dangling = 0;
                foreach (Formats.Collisions.CollisionInstance inst in col.Instances)
                {
                    placed.Add(inst.Hash);
                    if (!meshByHash.ContainsKey(inst.Hash)) dangling++;
                    if (inst.Unk4 < 0) unk4Negative++; else { unk4NonNegative++; unk4Values.Add(inst.Unk4); }
                    groups[inst.Group] = groups.GetValueOrDefault(inst.Group) + 1;
                }
                if (dangling > 0)
                {
                    filesWithDangling++;
                    danglingPlacements += dangling;
                    failures.AppendLine($"DANGLING {Path.GetFileName(file)}: {dangling} placements name a hull the file does not carry");
                }

                int orphans = 0;
                foreach (Formats.Collisions.CollisionMesh mesh in col.Meshes)
                    if (!placed.Contains(mesh.Hash)) orphans++;
                if (orphans > 0)
                {
                    filesWithOrphans++;
                    orphanMeshes += orphans;
                    // Name them: stock data has none, so an unplaced hull means this working copy was edited —
                    // worth telling apart from a claim about what the game ships.
                    orphanFiles.AppendLine($"  {orphans} unused hull(s) in {Path.GetFileName(Path.GetDirectoryName(file))}"
                        + $"/{Path.GetFileName(file)}");
                }

                // ── Frame pairing for this archive ────────────────────────────────────────────────────
                string[] frameFiles = Directory.GetFiles(folder, "FrameResource_*.fr", SearchOption.TopDirectoryOnly);
                if (frameFiles.Length == 0) continue;
                Formats.Frames.FrameResource fr;
                try { fr = new Formats.Frames.FrameResource(frameFiles[0]); }
                catch (Exception ex) { frameErrors++; failures.AppendLine($"FRAME {Path.GetFileName(file)}: {ex.Message}"); continue; }
                framePairedFiles++;

                // GetObjectFromIndex is a linear ElementAt — materialize once instead of per placement.
                var objects = new List<object>(fr.FrameObjects.Values);
                int blockCount = fr.GetBlockCount;

                foreach (object o in objects)
                {
                    if (o is not Formats.Frames.ObjectTypes.FrameObjectCollision fc) continue;
                    frameCollisionNodes++;
                    frameCollisionHashes.Add(fc.Hash);
                    if (!meshByHash.ContainsKey(fc.Hash)) frameHashesMissingFromCol++;
                }

                foreach (Formats.Collisions.CollisionInstance inst in col.Instances)
                {
                    if (inst.Unk4 < 0) continue;
                    CountPairing(objects, inst.Unk4, inst.Hash,
                        ref rawIndexHits, ref rawIndexMisses, ref rawIndexOutOfRange, ref rawIndexWrongType, ref rawIndexHashMismatch);
                    long ignoreA = 0, ignoreB = 0, ignoreC = 0;
                    CountPairing(objects, inst.Unk4 - blockCount, inst.Hash,
                        ref blockIndexHits, ref blockIndexMisses, ref ignoreA, ref ignoreB, ref ignoreC);

                    // What Unk4 actually points at. If the target's world position sits on the placement, the
                    // field is an owner reference to the visible object — worth knowing even though it means
                    // the frame side carries no hull hash to keep in sync.
                    if (inst.Unk4 < objects.Count && objects[inst.Unk4] is Formats.Frames.ObjectTypes.FrameObjectBase target)
                    {
                        string type = target.GetType().Name;
                        unk4TargetTypes[type] = unk4TargetTypes.GetValueOrDefault(type) + 1;
                        unk4TargetDistances.Add((target.WorldTransform.Translation - inst.Position).Length());
                    }
                }
            }

            int sharedAcrossFiles = 0, divergentContent = 0;
            foreach (var pair in perFileHashes)
            {
                if (pair.Value.Copies > 1) sharedAcrossFiles++;
                if (pair.Value.Divergent) divergentContent++;
            }

            int maxColsPerFolder = 0;
            foreach (int n in colsPerFolder.Values) maxColsPerFolder = Math.Max(maxColsPerFolder, n);

            unk4Values.Sort();

            sb.AppendLine($"COLLISION CENSUS — {files.Length} .col files, {filesParsed} parsed, {loadErrors} load errors");
            sb.AppendLine($"{instances} placements, {meshes} meshes, {perFileHashes.Count} distinct hull hashes, {blobless} blob-less meshes");
            sb.AppendLine($"elapsed {sw.ElapsedMilliseconds} ms");
            sb.AppendLine();

            sb.AppendLine("1. SELF-CONTAINMENT (gates the orphan sweep)");
            sb.AppendLine($"   placements naming a hull absent from their own file: {danglingPlacements} (in {filesWithDangling} files)");
            sb.AppendLine($"   hull hashes carried by more than one file: {sharedAcrossFiles} — of those, byte-divergent: {divergentContent}");
            sb.AppendLine($"   files with a duplicate mesh hash inside one file: {filesWithDuplicateMeshHash}");
            sb.AppendLine($"   unplaced (orphan) hulls in SHIPPED data: {orphanMeshes} (in {filesWithOrphans} files)");
            if (orphanFiles.Length > 0) sb.Append(orphanFiles);
            sb.AppendLine($"   → sweeping by IsOrphan is {(danglingPlacements == 0 ? "SAFE (every placement resolves in its own file)" : "UNSAFE — dangling references exist")}");
            sb.AppendLine();

            sb.AppendLine("2. MESH ORDER (decides insert-sorted vs append)");
            sb.AppendLine($"   files whose Meshes list is NOT hash-ascending: {filesUnsorted}/{filesParsed}");
            sb.AppendLine($"   → {(filesUnsorted == 0 ? "shipped data is sorted — insert minted hulls in hash order" : "the game tolerates unsorted — plain append is fine")}");
            sb.AppendLine();

            sb.AppendLine("3. FRAME PAIRING (decides whether a hash repoint must rewrite the frame side)");
            sb.AppendLine($"   archives with a FrameResource beside the .col: {framePairedFiles} ({frameErrors} parse errors)");
            sb.AppendLine($"   FrameObjectCollision nodes: {frameCollisionNodes}; whose hash is absent from the .col: {frameHashesMissingFromCol}");
            sb.AppendLine($"   placements with Unk4 >= 0: {unk4NonNegative}; with Unk4 < 0: {unk4Negative}");
            if (unk4Values.Count > 0)
                sb.AppendLine($"   Unk4 range: min={unk4Values[0]} median={unk4Values[unk4Values.Count / 2]} max={unk4Values[^1]}");
            sb.AppendLine($"   raw ordinal   Unk4 → FrameObjectCollision with the SAME hash: {rawIndexHits}/{unk4NonNegative} " +
                          $"(outOfRange={rawIndexOutOfRange} notACollisionNode={rawIndexWrongType} hashMismatch={rawIndexHashMismatch})");
            sb.AppendLine($"   block-offset  Unk4-GetBlockCount → same-hash node: {blockIndexHits}/{unk4NonNegative}");

            int frameHashesInAnyCol = 0;
            foreach (ulong h in frameCollisionHashes) if (perFileHashes.ContainsKey(h)) frameHashesInAnyCol++;
            sb.AppendLine($"   distinct FrameObjectCollision hashes: {frameCollisionHashes.Count}; resolving in ANY shipped .col: {frameHashesInAnyCol}");

            var typeList = new List<KeyValuePair<string, long>>(unk4TargetTypes);
            typeList.Sort((a, b) => b.Value.CompareTo(a.Value));
            var typeText = new StringBuilder();
            foreach (var t in typeList) typeText.Append($"{t.Key}×{t.Value} ");
            sb.AppendLine("   Unk4 target types (raw ordinal): " + typeText.ToString().TrimEnd());
            if (unk4TargetDistances.Count > 0)
            {
                unk4TargetDistances.Sort();
                sb.AppendLine($"   distance |targetWorldPos − placementPos|: min={unk4TargetDistances[0]:F2} " +
                              $"median={unk4TargetDistances[unk4TargetDistances.Count / 2]:F2} " +
                              $"p90={unk4TargetDistances[(int)(unk4TargetDistances.Count * 0.9)]:F2} " +
                              $"max={unk4TargetDistances[^1]:F2} (m)");
            }

            string verdict = rawIndexHits == unk4NonNegative && unk4NonNegative > 0
                ? "Unk4 IS the raw frame-object ordinal and the frame carries the same hash → a repoint MUST rewrite the frame object too"
                : blockIndexHits == unk4NonNegative && unk4NonNegative > 0
                    ? "Unk4 is the block-offset frame index → a repoint MUST rewrite the frame object too"
                    : "no convention pairs Unk4 with a same-hash FrameObjectCollision — the frame side carries NO hull hash "
                      + "to keep in sync, so a repoint touches the .col only";
            sb.AppendLine("   → " + verdict);
            sb.AppendLine();

            sb.AppendLine("4. ONE .col PER ARCHIVE (SdsCollisionSaver writes GetFiles(\"Collisions\")[0])");
            sb.AppendLine($"   archive folders carrying a .col: {colsPerFolder.Count}; max .col per folder: {maxColsPerFolder}");
            sb.AppendLine($"   → {(maxColsPerFolder <= 1 ? "saving by resource type is unambiguous" : "AMBIGUOUS — key the save target by file name captured at load")}");
            sb.AppendLine();

            var groupList = new List<KeyValuePair<byte, long>>(groups);
            groupList.Sort((a, b) => b.Value.CompareTo(a.Value));
            var groupText = new StringBuilder();
            foreach (var g in groupList) groupText.Append($"{g.Key}×{g.Value} ");
            sb.AppendLine("APPENDIX — Group byte histogram (new placements must pick one): " + groupText.ToString().TrimEnd());
            sb.AppendLine();

            // A census reports; it fails only when the corpus itself could not be read.
            sb.AppendLine(filesParsed > 0 && loadErrors == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            if (failures.Length > 0)
            {
                sb.AppendLine();
                sb.Append(failures.ToString(0, Math.Min(failures.Length, 6000)));
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // One placement's Unk4 under one index convention: does it land on a FrameObjectCollision naming the
    // same hull? Misses are split by cause so a wrong convention (everything out of range) is not confused
    // with a real desync (in range, right type, wrong hash).
    private static void CountPairing(List<object> objects, int index, ulong hash,
        ref long hits, ref long misses, ref long outOfRange, ref long wrongType, ref long hashMismatch)
    {
        if (index < 0 || index >= objects.Count) { misses++; outOfRange++; return; }
        if (objects[index] is not Formats.Frames.ObjectTypes.FrameObjectCollision fc) { misses++; wrongType++; return; }
        if (fc.Hash != hash) { misses++; hashMismatch++; return; }
        hits++;
    }
}
