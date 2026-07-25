using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the SDS archive layer: single-archive read chain, write-idempotence and extraction parity.</summary>
internal static class ArchiveProbes
{
    // Read chain of a single SDS: meshes, vertices, world-AABB, part textures.
    internal static void RunSdsProbe(string? sdsArg)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_probe.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }

            sb.AppendLine("PcFolder:   " + MafiaEnvironment.PcFolder);
            sb.AppendLine("CityFolder: " + MafiaEnvironment.CityFolder);

            string sds = sdsArg ?? Directory.GetFiles(MafiaEnvironment.CityFolder, "*.sds")[0];
            sb.AppendLine("Loading:    " + sds);

            var sw = Stopwatch.StartNew();
            var meshes = SdsMeshLoader.LoadSds(new FileInfo(sds));
            sw.Stop();

            long verts = 0, tris = 0;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var m in meshes)
            {
                verts += m.VertexCount;
                tris += m.TriangleCount;
                foreach (var p in m.Positions)
                {
                    Vector3 w = Vector3.Transform(p, m.World);
                    min = Vector3.Min(min, w);
                    max = Vector3.Max(max, w);
                }
            }

            sb.AppendLine($"OK: {meshes.Count} meshes, {verts} verts, {tris} tris in {sw.ElapsedMilliseconds} ms");
            sb.AppendLine($"World bounds min: {min}");
            sb.AppendLine($"World bounds max: {max}");

            int totalParts = 0, texturedParts = 0;
            var uniqueTex = new HashSet<string>();
            foreach (var m in meshes)
            {
                foreach (var p in m.Parts)
                {
                    totalParts++;
                    if (!string.IsNullOrEmpty(p.DiffuseTexture)) { texturedParts++; uniqueTex.Add(p.DiffuseTexture); }
                }
            }
            sb.AppendLine($"Parts: {totalParts}, with texture: {texturedParts}, unique textures: {uniqueTex.Count}");
            foreach (string t in uniqueTex.Take(6)) sb.AppendLine("  tex: " + t);
            for (int i = 0; i < Math.Min(5, meshes.Count); i++)
            {
                var m = meshes[i];
                sb.AppendLine($"  [{i}] {m.Name} — {m.VertexCount} v, {m.TriangleCount} t");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Ground-truth regression net for the Formats refactor. Two invariants per archive:
    //   1) Archive write-idempotence: deserialize → serialize to memory → deserialize; header fields,
    //      resource-type table and every entry (TypeId/Version/FileHash/ram-vram/decompressed data bytes)
    //      must survive unchanged. Compressed bytes are NOT compared — the zlib codec may legitimately differ.
    //   2) FrameResource generation stability: load the extracted working copy, write (A), parse A, write
    //      again (B); A must equal B byte-exact. Pass 1 may differ from the on-disk file because WriteToStream
    //      runs UpdateFrameData/SanitizeFrameData — the test asserts that sanitize is a fixpoint.
    // Also censuses block storage (zlib/oodle/uncompressed) so we know where Oodle actually occurs in this
    // install before the compression rewrite. The report is deterministic except the trailing "elapsed" line,
    // so phase-over-phase diffs against the archived baseline stay clean.
    internal static void RunRoundtripProbe(string? filter)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_roundtrip.txt");
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            // Skip third-party launcher storage (the MafiaHub/M2O mod keeps its own archive backups under
            // .mafiahub\ — not game data, and its copies may be stale or corrupt).
            List<FileInfo> archives = ResourceUnpacker.EnumerateGameSds()
                .Where(f => !f.FullName.Contains(@"\.mafiahub\", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!string.IsNullOrEmpty(filter))
                archives = archives.Where(f => f.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (archives.Count == 0)
            {
                sb.AppendLine("no .sds archives found" + (filter is null ? "" : $" for filter '{filter}'"));
                return;
            }

            int archOk = 0, archFail = 0, frOk = 0, frFail = 0, frMissing = 0;
            long zlibBlocks = 0, oodleBlocks = 0, rawBlocks = 0;
            var failures = new StringBuilder();

            foreach (FileInfo sds in archives)
            {
                string rel = Path.GetRelativePath(MafiaEnvironment.GameRoot, sds.FullName);

                // 1) Archive round-trip + block census.
                try
                {
                    using MemoryStream raw = ReadUnwrapped(sds);
                    (int zl, int oo, int un) = CensusBlocks(raw);
                    zlibBlocks += zl; oodleBlocks += oo; rawBlocks += un;

                    raw.Position = 0;
                    SdsArchive a = SdsArchive.Load(raw);

                    using var repacked = new MemoryStream();
                    a.Save(repacked, new SdsWriteOptions());
                    repacked.Position = 0;
                    SdsArchive b = SdsArchive.Load(repacked);

                    string? diff = CompareArchives(a, b);
                    if (diff is null)
                    {
                        archOk++;
                        sb.AppendLine($"OK   {rel} — {a.Entries.Count} entries, blocks z={zl} o={oo} u={un}");
                    }
                    else { archFail++; failures.AppendLine($"ARCHIVE FAIL {rel}: {diff}"); }
                }
                catch (Exception ex) { archFail++; failures.AppendLine($"ARCHIVE ERROR {rel}: {ex.Message}"); }

                // 2) FrameResource generation stability (needs the extracted working copy).
                try
                {
                    string extractedDir = MafiaEnvironment.ExtractedDir(sds);
                    if (!File.Exists(Path.Combine(extractedDir, "SDSContent.xml"))) { frMissing++; continue; }
                    foreach (string file in SdsManifest.Load(extractedDir).GetFiles("FrameResource"))
                    {
                        var fr1 = new FrameResource(file);
                        byte[] genA = fr1.WriteToStream();
                        var fr2 = new FrameResource();
                        using (var ms = new MemoryStream(genA, false)) fr2.ReadFromFile(ms);
                        byte[] genB = fr2.WriteToStream();
                        if (genA.AsSpan().SequenceEqual(genB))
                        {
                            frOk++;
                            sb.AppendLine($"FRAME OK {rel} — {Path.GetFileName(file)} gen {genA.Length} B");
                        }
                        else
                        {
                            frFail++;
                            failures.AppendLine($"FRAME FAIL {rel}: {Path.GetFileName(file)} " +
                                                $"gen1={genA.Length}B gen2={genB.Length}B first diff at {FirstDiff(genA, genB)}");
                        }
                    }
                }
                catch (Exception ex) { frFail++; failures.AppendLine($"FRAME ERROR {rel}: {ex}"); }
            }

            var head = new StringBuilder();
            head.AppendLine($"ROUNDTRIP PROBE — archives={archives.Count} ok={archOk} fail={archFail} | " +
                            $"FrameResource gen ok={frOk} fail={frFail} not-extracted={frMissing} | " +
                            $"blocks zlib={zlibBlocks} oodle={oodleBlocks} uncompressed={rawBlocks}" +
                            (filter is null ? "" : $" | filter='{filter}'"));
            head.AppendLine(archFail == 0 && frFail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            head.AppendLine();
            if (failures.Length > 0) { head.Append(failures); head.AppendLine(); }
            sb.Insert(0, head.ToString());
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally
        {
            sb.AppendLine($"elapsed: {sw.Elapsed.TotalSeconds:F1} s");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Extraction parity vs the previous extractor: every archive that already has a folder in the
    // /resources mirror is re-extracted with the current SdsArchive.Extract into TEMP; the two trees must
    // match file-for-file, byte-for-byte (including SDSContent.xml). Proves the registry-based extraction
    // reproduces what all existing tooling (scene loaders, repack) reads.
    internal static void RunExtractProbe(string? filter)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_extract.txt");
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        string tempRoot = Path.Combine(Path.GetTempPath(), "illusion_extract_probe");
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            List<FileInfo> archives = ResourceUnpacker.EnumerateGameSds()
                .Where(f => !f.FullName.Contains(@"\.mafiahub\", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!string.IsNullOrEmpty(filter))
                archives = archives.Where(f => f.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            int ok = 0, fail = 0, skipped = 0;
            var failures = new StringBuilder();

            foreach (FileInfo sds in archives)
            {
                string rel = Path.GetRelativePath(MafiaEnvironment.GameRoot, sds.FullName);
                string reference = MafiaEnvironment.ExtractedDir(sds);
                if (!File.Exists(Path.Combine(reference, "SDSContent.xml"))) { skipped++; continue; }

                try
                {
                    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
                    SdsArchive.Open(sds.FullName).Extract(tempRoot);

                    string? diff = CompareTrees(reference, tempRoot);
                    if (diff is null) { ok++; sb.AppendLine($"OK   {rel}"); }
                    else
                    {
                        fail++;
                        failures.AppendLine($"EXTRACT FAIL {rel}: {diff}");
                        // Preserve the differing output for inspection (one folder per failed archive).
                        string keep = Path.Combine(Path.GetTempPath(), "illusion_extract_failed", sds.Name);
                        if (Directory.Exists(keep)) Directory.Delete(keep, true);
                        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                        Directory.Move(tempRoot, keep);
                    }
                }
                catch (Exception ex) { fail++; failures.AppendLine($"EXTRACT ERROR {rel}: {ex.Message}"); }
            }

            var head = new StringBuilder();
            head.AppendLine($"EXTRACT PROBE — archives={archives.Count} ok={ok} fail={fail} not-extracted={skipped}" +
                            (filter is null ? "" : $" | filter='{filter}'"));
            head.AppendLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            head.AppendLine();
            if (failures.Length > 0) { head.Append(failures); head.AppendLine(); }
            sb.Insert(0, head.ToString());
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
            sb.AppendLine($"elapsed: {sw.Elapsed.TotalSeconds:F1} s");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // FrameNameTable rebuild fidelity: for every archive with a name table, rebuild it from the loaded
    // FrameResource (fixed BuildDataFromResource), reload the rebuilt bytes, relink, and assert per-object
    // membership + flags + names match the original load. Also counts how many rebuild byte-identically. Proves
    // the name-table rewrite is a semantic fixpoint (so wiring it into Save preserves game visibility). No game
    // file is written. Output: %TEMP%\illusion_nametable.txt
    internal static void RunNameTableProbe(string? filter)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_nametable.txt");
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            List<FileInfo> archives = ResourceUnpacker.EnumerateGameSds()
                .Where(f => !f.FullName.Contains(@"\.mafiahub\", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (!string.IsNullOrEmpty(filter))
                archives = archives.Where(f => f.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            int tables = 0, semanticOk = 0, semanticFail = 0, byteIdentical = 0;
            bool editRan = false, editOk = false;
            var failures = new StringBuilder();

            foreach (FileInfo sds in archives)
            {
                string rel = Path.GetRelativePath(MafiaEnvironment.GameRoot, sds.FullName);
                try
                {
                    string extracted = MafiaEnvironment.ExtractedDir(sds);
                    if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;
                    IReadOnlyList<string> tableFiles = SdsManifest.Load(extracted).GetFiles("FrameNameTable");
                    if (tableFiles.Count == 0) continue;

                    ExtractedSds ex = ExtractedSds.Load(extracted);
                    FrameResource? fr = ex.FrameResource;
                    if (fr?.FrameObjects == null || ex.FrameNameTable == null) continue;
                    tables++;

                    var objects = new List<FrameObjectBase?>(fr.FrameObjects.Count);
                    foreach (object v in fr.FrameObjects.Values) objects.Add(v as FrameObjectBase);

                    // Baseline: original membership + flags (LinkNameTableFlags already applied on load).
                    var baseOnTable = new bool[objects.Count];
                    var baseFlags = new NameTableFlags[objects.Count];
                    for (int i = 0; i < objects.Count; i++)
                        if (objects[i] is { } o) { baseOnTable[i] = o.IsOnFrameTable; baseFlags[i] = o.FrameNameTableFlags; }

                    // Rebuild from the resource, serialize, reload.
                    var rebuilt = new FrameNameTable();
                    rebuilt.BuildDataFromResource(fr);
                    byte[] rebuiltBytes = SerializeTable(rebuilt);
                    var reloaded = new FrameNameTable();
                    using (var ms = new MemoryStream(rebuiltBytes, false)) reloaded.ReadFromFile(ms);

                    // Reset objects, then relink from the reloaded rebuilt table (replicates LinkNameTableFlags).
                    for (int i = 0; i < objects.Count; i++)
                        if (objects[i] is { } o) { o.IsOnFrameTable = false; o.FrameNameTableFlags = 0; }
                    foreach (FrameNameTable.Data d in reloaded.FrameData!)
                        if (d.FrameIndex >= 0 && d.FrameIndex < objects.Count && objects[d.FrameIndex] is { } o)
                        { o.IsOnFrameTable = true; o.FrameNameTableFlags = d.Flags; }

                    // Membership + flags must match the baseline for every object.
                    bool ok = true;
                    string detail = "";
                    for (int i = 0; i < objects.Count && ok; i++)
                    {
                        if (objects[i] is not { } o) continue;
                        if (o.IsOnFrameTable != baseOnTable[i] || o.FrameNameTableFlags != baseFlags[i])
                        { ok = false; detail = $"obj[{i}] '{o.Name}' membership/flags differ"; }
                    }
                    // Every listed object's name must round-trip through the rebuilt buffer.
                    if (ok)
                        foreach (FrameNameTable.Data d in reloaded.FrameData!)
                        {
                            if (d.FrameIndex < 0 || d.FrameIndex >= objects.Count) continue;
                            if (objects[d.FrameIndex] is { } o && d.Name != o.Name.String)
                            { ok = false; detail = $"name @ frame {d.FrameIndex}: '{d.Name}' vs '{o.Name.String}'"; break; }
                        }

                    if (ok) semanticOk++; else { semanticFail++; failures.AppendLine($"FAIL {rel}: {detail}"); }
                    if (File.ReadAllBytes(tableFiles[0]).AsSpan().SequenceEqual(rebuiltBytes)) byteIdentical++;

                    // Once: prove an EDIT round-trips — flip an on-table object off, rebuild, and confirm it is
                    // dropped from the reloaded table (the others keep their membership).
                    if (!editRan)
                    {
                        int k = -1;
                        for (int i = 0; i < objects.Count; i++) if (objects[i] is { IsOnFrameTable: true }) { k = i; break; }
                        if (k >= 0)
                        {
                            editRan = true;
                            objects[k]!.IsOnFrameTable = false;
                            var edited = new FrameNameTable();
                            edited.BuildDataFromResource(fr);
                            byte[] eb = SerializeTable(edited);
                            var er = new FrameNameTable();
                            using (var ms = new MemoryStream(eb, false)) er.ReadFromFile(ms);
                            editOk = er.FrameData!.All(d => d.FrameIndex != k) && er.FrameData!.Length > 0;
                            if (!editOk) failures.AppendLine($"EDIT FAIL {rel}: toggling obj[{k}] off did not drop it");
                        }
                    }
                }
                catch (Exception ex) { semanticFail++; failures.AppendLine($"ERROR {rel}: {ex.Message}"); }
            }

            sb.AppendLine($"NAMETABLE PROBE — tables={tables} semantic-ok={semanticOk} semantic-fail={semanticFail} " +
                          $"byte-identical={byteIdentical} edit-roundtrip={(editRan ? (editOk ? "PASS" : "FAIL") : "n/a")}" +
                          $"{(filter is null ? "" : $" filter='{filter}'")}");
            sb.AppendLine($"elapsed: {sw.Elapsed.TotalSeconds:F1} s\n");
            if (failures.Length > 0) sb.Append(failures);
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static byte[] SerializeTable(FrameNameTable table)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) table.WriteToFile(bw);
        return ms.ToArray();
    }

    // File-by-file, byte-by-byte comparison of two directory trees. Returns null when identical.
    // SDSContent_old.xml is ignored — the pack step's manifest sort leaves that backup behind in
    // previously-packed reference folders; it is not extraction output.
    private static string? CompareTrees(string expectedRoot, string actualRoot)
    {
        var expected = Directory.GetFiles(expectedRoot, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(expectedRoot, p))
            .Where(p => !p.EndsWith("SDSContent_old.xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var actual = Directory.GetFiles(actualRoot, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(actualRoot, p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).ToArray();
        var extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0) return $"missing files: {string.Join(", ", missing.Take(5))}";
        if (extra.Length > 0) return $"extra files: {string.Join(", ", extra.Take(5))}";

        foreach (string relFile in expected)
        {
            byte[] a = File.ReadAllBytes(Path.Combine(expectedRoot, relFile));
            byte[] b = File.ReadAllBytes(Path.Combine(actualRoot, relFile));
            if (a.AsSpan().SequenceEqual(b)) continue;

            // Decompiled .xml resources: pre-fix extractions on a comma-decimal locale wrote floats as
            // "0,3412" (now always invariant "0.3412"). Compare with decimal commas normalized so both
            // reference eras verify; every other byte still has to match.
            if (relFile.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                string ta = NormalizeDecimalCommas(File.ReadAllText(Path.Combine(expectedRoot, relFile)));
                string tb = NormalizeDecimalCommas(File.ReadAllText(Path.Combine(actualRoot, relFile)));
                if (string.Equals(ta, tb, StringComparison.Ordinal)) continue;
            }

            return $"'{relFile}' differs ({a.Length}B vs {b.Length}B, first diff at {FirstDiff(a, b)})";
        }
        return null;
    }

    private static string NormalizeDecimalCommas(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"(?<=\d),(?=\d)", ".");

    // Reads the archive fully into memory, transparently unwrapping the XTEA layer some stock archives have.
    // FrameResource EDIT fidelity: move one object and assert the save changes nothing except that object's
    // transform. Runs entirely in memory (WriteToStream) — it never touches the working copy. This is the check
    // that was missing when a save turned an object's ParentIndex1 from -1 ("no parent") into a real index and
    // crashed the game. Output: %TEMP%\illusion_frameedit.txt
    internal static void RunFrameEditProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_frameedit.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string cityFolder = MafiaEnvironment.CityFolder;
            string[] archives = district == "*"
                ? Directory.GetFiles(cityFolder, "*.sds", SearchOption.TopDirectoryOnly)
                : new[] { Path.Combine(cityFolder, district + ".sds") };

            int checkedCount = 0, clean = 0, dirty = 0, skipped = 0;
            var detail = new StringBuilder();

            foreach (string archive in archives)
            {
                if (!File.Exists(archive)) { detail.AppendLine("no such district: " + archive); continue; }
                var sds = new FileInfo(archive);
                FrameResource? frame;
                try { frame = SdsMeshLoader.OpenScene(SdsMeshLoader.EnsureExtracted(sds)).FrameResource; }
                catch (Exception ex) { detail.AppendLine($"OPEN FAIL {sds.Name}: {ex.Message}"); continue; }
                if (frame?.FrameObjects is not { Count: > 0 }) continue;

                byte[] before = frame.WriteToStream();

                // Move one object the way the gizmo does: write its local transform back, translated.
                var target = frame.FrameObjects.Values
                    .OfType<FrameObjectBase>().FirstOrDefault();
                if (target is null) continue;
                Matrix4x4 original = target.LocalTransform;
                Matrix4x4 moved = original;
                moved.Translation = original.Translation + new Vector3(1.5f, 0f, 0f);
                target.LocalTransform = moved;

                byte[] after = frame.WriteToStream();

                // Undo: put the transform back exactly as it was. The bytes must return to the original — this is
                // the "move it, then Ctrl+Z, then pack" path, which shipped a corrupt archive when an undo's
                // re-selection let the property panel reparent the object behind the user's back.
                target.LocalTransform = original;
                byte[] reverted = frame.WriteToStream();
                if (!before.AsSpan().SequenceEqual(reverted))
                {
                    dirty++;
                    detail.AppendLine($"UNDO NOT CLEAN {sds.Name}: reverting the move did not restore the bytes");
                    if (detail.Length < 12000) AppendDiffRuns(detail, before, reverted);
                    checkedCount++;
                    continue;
                }

                checkedCount++;
                if (before.Length != after.Length)
                {
                    dirty++;
                    detail.AppendLine($"LENGTH CHANGED {sds.Name}: {before.Length} -> {after.Length}");
                    continue;
                }

                (int runs, int bytes, int firstRun, int lastEnd) = DiffShape(before, after);
                if (runs == 0)
                {
                    // The move did not reach the file at all. Not corruption, but the probe proves nothing here —
                    // the first frame object of this district has no writable transform (e.g. a dummy).
                    skipped++;
                    detail.AppendLine($"NO EFFECT {sds.Name}: the moved object's transform never reached the stream");
                    continue;
                }

                // A pure translation touches one contiguous span inside a single object's 4x3 matrix (<= 48 B).
                if (bytes <= 48 && lastEnd - firstRun <= 48) { clean++; continue; }

                dirty++;
                detail.AppendLine($"UNEXPECTED SPREAD {sds.Name}: {runs} run(s), {bytes} byte(s), span {firstRun}..{lastEnd}");
                if (detail.Length < 12000) AppendDiffRuns(detail, before, after);
            }

            sb.AppendLine($"FRAME EDIT PROBE — checked={checkedCount} clean={clean} suspicious={dirty} noEffect={skipped}");
            sb.AppendLine(dirty == 0 && checkedCount > 0 ? "RESULT: PASS"
                : checkedCount == 0 ? "RESULT: NO FILES" : "RESULT: FAIL");
            sb.AppendLine();
            sb.AppendLine("A one-object move must change only that object's transform bytes. Any other differing");
            sb.AppendLine("run means the save rewrote data the user never edited.");
            sb.AppendLine();
            sb.Append(detail);
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static (int Runs, int Bytes, int First, int LastEnd) DiffShape(byte[] x, byte[] y)
    {
        int n = Math.Min(x.Length, y.Length);
        int runs = 0, bytes = 0, first = -1, lastEnd = -1;
        int i = 0;
        while (i < n)
        {
            if (x[i] == y[i]) { i++; continue; }
            int start = i;
            while (i < n && x[i] != y[i]) i++;
            runs++;
            bytes += i - start;
            if (first < 0) first = start;
            lastEnd = i;
        }
        return (runs, bytes, first, lastEnd);
    }

    // FrameResource write fidelity: load every district's .fr and write it straight back with no edit at all. The
    // bytes must be identical — a save must never change data the user did not touch. This is the gap that let a
    // single hierarchy dword flip from -1 ("no parent") to a real index and take the game down with it.
    // Output: %TEMP%\illusion_frameroundtrip.txt
    internal static void RunFrameRoundtripProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_frameroundtrip.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string cityFolder = MafiaEnvironment.CityFolder;
            if (!Directory.Exists(cityFolder)) { sb.AppendLine("no city folder: " + cityFolder); return; }

            string[] archives = Directory.GetFiles(cityFolder, "*.sds", SearchOption.TopDirectoryOnly);
            int checkedCount = 0, identical = 0, differing = 0, failed = 0;
            var detail = new StringBuilder();

            foreach (string archive in archives)
            {
                var sds = new FileInfo(archive);
                string extracted;
                FrameResource? frame;
                try
                {
                    extracted = SdsMeshLoader.EnsureExtracted(sds);
                    frame = SdsMeshLoader.OpenScene(extracted).FrameResource;
                }
                catch (Exception ex) { failed++; detail.AppendLine($"OPEN FAIL {sds.Name}: {ex.Message}"); continue; }
                if (frame is null) continue;

                IReadOnlyList<string> files = SdsManifest.Load(extracted).GetFiles("FrameResource");
                if (files.Count == 0) continue;
                byte[] onDisk = File.ReadAllBytes(files[0]);

                byte[] written;
                try { written = frame.WriteToStream(); }
                catch (Exception ex) { failed++; detail.AppendLine($"WRITE FAIL {sds.Name}: {ex.Message}"); continue; }

                checkedCount++;
                if (onDisk.AsSpan().SequenceEqual(written)) { identical++; continue; }

                differing++;
                detail.AppendLine($"DIFFERS {sds.Name}: {onDisk.Length} B on disk -> {written.Length} B written");
                if (detail.Length < 12000) AppendDiffRuns(detail, onDisk, written);
            }

            sb.AppendLine($"FRAME ROUNDTRIP PROBE — archives={archives.Length} checked={checkedCount} " +
                          $"identical={identical} differing={differing} failed={failed}");
            sb.AppendLine(differing == 0 && failed == 0 && checkedCount > 0 ? "RESULT: PASS"
                : checkedCount == 0 ? "RESULT: NO FILES" : "RESULT: FAIL");
            sb.AppendLine();
            sb.Append(detail);
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Diffs two .sds archives resource-by-resource, grouped by resource type and matched WITHIN a type by order
    // (so the packer's deliberate type regrouping is not mistaken for corruption). Answers "what did a build
    // actually change relative to the stock archive" for every resource, not just the one that was edited.
    // Usage: --probe-archdiff <original.sds> <rebuilt.sds>. Output: %TEMP%\illusion_archdiff.txt
    internal static void RunArchiveDiffProbe(string? originalPath, string? rebuiltPath)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_archdiff.txt");
        var sb = new StringBuilder();
        try
        {
            if (originalPath is null || rebuiltPath is null)
            {
                sb.AppendLine("usage: --probe-archdiff <original.sds> <rebuilt.sds>");
                return;
            }
            if (!File.Exists(originalPath)) { sb.AppendLine("no such file: " + originalPath); return; }
            if (!File.Exists(rebuiltPath)) { sb.AppendLine("no such file: " + rebuiltPath); return; }
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            using MemoryStream rawA = ReadUnwrapped(new FileInfo(originalPath));
            using MemoryStream rawB = ReadUnwrapped(new FileInfo(rebuiltPath));
            SdsArchive a = SdsArchive.Load(rawA);
            SdsArchive b = SdsArchive.Load(rawB);

            sb.AppendLine($"ARCHIVE DIFF");
            sb.AppendLine($"  A (original): {originalPath}");
            sb.AppendLine($"  B (rebuilt):  {rebuiltPath}");
            sb.AppendLine($"A: {a.Entries.Count} entries ram={a.SlotRamRequired} vram={a.SlotVramRequired}");
            sb.AppendLine($"B: {b.Entries.Count} entries ram={b.SlotRamRequired} vram={b.SlotVramRequired}");
            sb.AppendLine();

            var byTypeA = GroupByType(a);
            var byTypeB = GroupByType(b);
            var types = new SortedSet<string>(byTypeA.Keys);
            types.UnionWith(byTypeB.Keys);

            int changedTotal = 0;
            foreach (string type in types)
            {
                List<byte[]> listA = byTypeA.TryGetValue(type, out var la) ? la : new List<byte[]>();
                List<byte[]> listB = byTypeB.TryGetValue(type, out var lb) ? lb : new List<byte[]>();
                int changed = 0;
                int n = Math.Min(listA.Count, listB.Count);
                for (int i = 0; i < n; i++)
                {
                    if (!listA[i].AsSpan().SequenceEqual(listB[i])) changed++;
                }
                if (changed == 0 && listA.Count == listB.Count) continue;
                changedTotal += changed + Math.Abs(listA.Count - listB.Count);
                sb.AppendLine($"  {type,-22} count {listA.Count} -> {listB.Count}, payload differs in {changed}/{n}");
                for (int i = 0; i < n; i++)
                {
                    if (listA[i].AsSpan().SequenceEqual(listB[i])) continue;
                    sb.AppendLine($"      [{i}] {listA[i].Length} B -> {listB[i].Length} B, first diff at {FirstDiff(listA[i], listB[i])}");
                    AppendDiffRuns(sb, listA[i], listB[i]);
                }
            }

            sb.AppendLine();
            sb.AppendLine(changedTotal == 0
                ? "RESULT: PASS — every resource payload is byte-identical"
                : $"RESULT: {changedTotal} resource payload(s) differ");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static Dictionary<string, List<byte[]>> GroupByType(SdsArchive archive)
    {
        var byType = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        foreach (ResourceEntry entry in archive.Entries)
        {
            string name = entry.TypeId >= 0 && entry.TypeId < archive.ResourceTypes.Count
                ? archive.ResourceTypes[entry.TypeId].Name : "?";
            if (!byType.TryGetValue(name, out List<byte[]>? list)) byType[name] = list = new List<byte[]>();
            list.Add(entry.Data ?? Array.Empty<byte>());
        }
        return byType;
    }

    // Contiguous runs of differing bytes, with both sides decoded as float32 when a run is 4-byte aligned and
    // 4 bytes long — the shape that says "one transform field changed" rather than "the layout shifted".
    private static void AppendDiffRuns(StringBuilder sb, byte[] x, byte[] y)
    {
        int n = Math.Min(x.Length, y.Length);
        int runs = 0, differing = 0, shown = 0;
        int i = 0;
        while (i < n)
        {
            if (x[i] == y[i]) { i++; continue; }
            int start = i;
            while (i < n && x[i] != y[i]) i++;
            int length = i - start;
            runs++;
            differing += length;
            if (shown++ < 12)
            {
                sb.AppendLine($"        run @{start} len {length}");
                sb.AppendLine($"          A: {Hex(x, start, length)}   (context {Hex(x, start - 8, length + 16)})");
                sb.AppendLine($"          B: {Hex(y, start, length)}   (context {Hex(y, start - 8, length + 16)})");
                if (length == 4)
                {
                    sb.AppendLine($"          as u32 {BitConverter.ToUInt32(x, start)} -> {BitConverter.ToUInt32(y, start)}" +
                                  $" | as f32 {BitConverter.ToSingle(x, start):R} -> {BitConverter.ToSingle(y, start):R}");
                }
            }
        }
        sb.AppendLine($"        => {runs} differing run(s), {differing} byte(s) of {n}");
    }

    private static string Hex(byte[] data, int start, int length)
    {
        int from = Math.Max(0, start);
        int to = Math.Min(data.Length, start + length);
        var sb = new StringBuilder();
        for (int i = from; i < to; i++) sb.Append(data[i].ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    private static string FirstDiff(byte[] x, byte[] y)
    {
        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++) if (x[i] != y[i]) return i.ToString();
        return n == x.Length && n == y.Length ? "none" : $"{n} (length)";
    }

    private static MemoryStream ReadUnwrapped(FileInfo sds)
    {
        // The native core detects and removes the XTEA wrapper; plain archives come back verbatim.
        byte[] payload = Formats.Native.Archive.NativeSds.Unwrap(File.ReadAllBytes(sds.FullName));
        return new MemoryStream(payload, writable: false);
    }

    // Walks the archive's block table (the same layout BlockReaderStream parses) and counts how each block is
    // stored. Detection mirrors BlockReaderStream.FromStream: oodle = 128-byte block header + the 0x1000000
    // alignment flag, zlib = 32-byte header, size==0 terminates the table. Parses the fixed little-endian PC
    // layout directly (magic+version+platform 12 B, FNV32, FileHeader 52 B with BlockTableOffset at absolute
    // offset 20, FNV32) rather than the vendor stream helpers — those live behind the Gibbed.IO binary the
    // exe deliberately does not reference.
    private static (int zlib, int oodle, int raw) CensusBlocks(MemoryStream s)
    {
        var br = new BinaryReader(s);
        s.Position = 0;
        uint sdsMagic = br.ReadUInt32(); // bytes 'SDS\0'
        if (sdsMagic != 0x00534453u)
            throw new InvalidDataException($"archive magic 0x{sdsMagic:X8}");

        s.Position = 20;
        uint blockTableOffset = br.ReadUInt32();

        s.Position = blockTableOffset;
        uint magic = br.ReadUInt32();
        if (magic != 0x6C7A4555u) // 'zlEU' as the stream stores it
            throw new InvalidDataException($"block table magic 0x{magic:X8} at 0x{blockTableOffset:X}");
        uint alignment = br.ReadUInt32();
        br.ReadByte(); // flags
        bool oodleFlag = (alignment & 0x1000000) != 0;

        int zlib = 0, oodle = 0, raw = 0;
        while (true)
        {
            uint size = br.ReadUInt32();
            bool compressed = br.ReadByte() != 0;
            if (size == 0) break;
            if (compressed)
            {
                // CompressedBlockHeader, 32 bytes: UncompressedSize u32, HeaderSize u32, ChunkSize s16,
                // ChunkCount s16, Unknown0C u32, Chunks u16[8] (their sum = CompressedSize).
                br.ReadUInt32();
                uint headerSize = br.ReadUInt32();
                br.ReadInt16();
                br.ReadInt16();
                br.ReadUInt32();
                uint compressedSize = 0;
                for (int i = 0; i < 8; i++) compressedSize += br.ReadUInt16();

                if (headerSize == 128 && oodleFlag) oodle++; else zlib++;
                s.Seek(compressedSize + (headerSize == 128 ? 96 : 0), SeekOrigin.Current);
            }
            else { raw++; s.Seek(size, SeekOrigin.Current); }
        }
        return (zlib, oodle, raw);
    }

    // Structural equality of two deserialized archives: header fields + resource-type table + per-entry
    // metadata and decompressed payload bytes. Returns null when equal, else a short description.
    // Build-faithfulness: the app's Build repacks an extracted district folder via SdsArchive.Pack (NOT the
    // in-memory Load→Save the roundtrip probe tests). This packs the (unedited) extracted folder exactly as Build
    // does and compares every resource entry to the ORIGINAL archive — a diff means the Build path corrupts the
    // .sds regardless of any edit (the game would then reject it at load). Output: %TEMP%\illusion_buildcheck.txt
    internal static void RunBuildCheckProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_buildcheck.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sdsPath = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sdsPath)) { sb.AppendLine("no such district: " + sdsPath); return; }
            var sds = new FileInfo(sdsPath);

            using MemoryStream raw = ReadUnwrapped(sds);
            SdsArchive orig = SdsArchive.Load(raw);

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            SdsArchive packed = SdsArchive.Pack(extracted, GameProfile.MafiaII);

            sb.AppendLine($"BUILDCHECK — district={district}");
            sb.AppendLine($"orig: {orig.Entries.Count} entries, ram={orig.SlotRamRequired} vram={orig.SlotVramRequired}");
            sb.AppendLine($"pack: {packed.Entries.Count} entries, ram={packed.SlotRamRequired} vram={packed.SlotVramRequired}");

            // Locate the Collisions entry (TypeId indexes the resource-type table).
            for (int i = 0; i < orig.Entries.Count && i < packed.Entries.Count; i++)
            {
                var eo = orig.Entries[i];
                string tn = eo.TypeId >= 0 && eo.TypeId < orig.ResourceTypes.Count ? orig.ResourceTypes[eo.TypeId].Name : "?";
                if (tn != "Collisions") continue;
                var ep = packed.Entries[i];
                sb.AppendLine($"  Collisions entry [{i}]: orig ver={eo.Version} ram={eo.SlotRamRequired} len={eo.Data?.Length}; " +
                              $"pack ver={ep.Version} ram={ep.SlotRamRequired} len={ep.Data?.Length}");
            }

            var dataDiffTypes = new Dictionary<string, int>();
            var ramDiffTypes = new Dictionary<string, int>();
            int dataDiff = 0, ramDiff = 0;
            int m = Math.Min(orig.Entries.Count, packed.Entries.Count);
            for (int i = 0; i < m; i++)
            {
                var eo = orig.Entries[i];
                var ep = packed.Entries[i];
                string tn = eo.TypeId >= 0 && eo.TypeId < orig.ResourceTypes.Count ? orig.ResourceTypes[eo.TypeId].Name : "?";
                if (!(eo.Data ?? Array.Empty<byte>()).AsSpan().SequenceEqual(ep.Data ?? Array.Empty<byte>()))
                {
                    dataDiff++;
                    dataDiffTypes[tn] = dataDiffTypes.GetValueOrDefault(tn) + 1;
                }
                if (eo.SlotRamRequired != ep.SlotRamRequired || eo.SlotVramRequired != ep.SlotVramRequired
                    || eo.OtherRamRequired != ep.OtherRamRequired || eo.OtherVramRequired != ep.OtherVramRequired)
                {
                    ramDiff++;
                    ramDiffTypes[tn] = ramDiffTypes.GetValueOrDefault(tn) + 1;
                }
            }
            sb.AppendLine($"per-entry: DATA differs in {dataDiff}/{m}, ram-vram differs in {ramDiff}/{m}");
            sb.AppendLine("  DATA-diff by type: " + string.Join(", ", dataDiffTypes.Select(kv => $"{kv.Key}={kv.Value}")));
            sb.AppendLine("  ram-diff by type:  " + string.Join(", ", ramDiffTypes.Select(kv => $"{kv.Key}={kv.Value}")));

            string? diff = CompareArchives(orig, packed);
            sb.AppendLine();
            sb.AppendLine($"Pack(extracted) vs original: {(diff is null ? "IDENTICAL — Build is faithful" : "DIFF → " + diff)}");
            sb.AppendLine(diff is null ? "RESULT: PASS" : "RESULT: FAIL");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static string? CompareArchives(SdsArchive a, SdsArchive b)
    {
        if (a.Version != b.Version) return $"version {a.Version} -> {b.Version}";
        if (a.Platform != b.Platform) return $"platform {a.Platform} -> {b.Platform}";
        if (a.SlotRamRequired != b.SlotRamRequired || a.SlotVramRequired != b.SlotVramRequired
            || a.OtherRamRequired != b.OtherRamRequired || a.OtherVramRequired != b.OtherVramRequired)
            return "header ram/vram requirements differ";
        if (!a.Unknown20.AsSpan().SequenceEqual(b.Unknown20)) return "Unknown20 header bytes differ";
        if (a.ResourceTypes.Count != b.ResourceTypes.Count)
            return $"resource-type count {a.ResourceTypes.Count} -> {b.ResourceTypes.Count}";
        for (int i = 0; i < a.ResourceTypes.Count; i++)
        {
            if (a.ResourceTypes[i] != b.ResourceTypes[i])
                return $"resource type [{i}] {a.ResourceTypes[i]} -> {b.ResourceTypes[i]}";
        }
        if (a.Entries.Count != b.Entries.Count)
            return $"entry count {a.Entries.Count} -> {b.Entries.Count}";
        for (int i = 0; i < a.Entries.Count; i++)
        {
            var ea = a.Entries[i];
            var eb = b.Entries[i];
            if (ea.TypeId != eb.TypeId) return $"entry [{i}] TypeId {ea.TypeId} -> {eb.TypeId}";
            if (ea.Version != eb.Version) return $"entry [{i}] Version {ea.Version} -> {eb.Version}";
            if (ea.SlotRamRequired != eb.SlotRamRequired || ea.SlotVramRequired != eb.SlotVramRequired
                || ea.OtherRamRequired != eb.OtherRamRequired || ea.OtherVramRequired != eb.OtherVramRequired)
                return $"entry [{i}] ram/vram requirements differ";
            byte[] da = ea.Data ?? Array.Empty<byte>();
            byte[] db = eb.Data ?? Array.Empty<byte>();
            if (!da.AsSpan().SequenceEqual(db))
                return $"entry [{i}] data differs ({da.Length}B vs {db.Length}B, first diff at {FirstDiff(da, db)})";
        }
        if (!string.Equals(a.ResourceInfoXml ?? "", b.ResourceInfoXml ?? "", StringComparison.Ordinal))
            return "ResourceInfoXml differs";
        return null;
    }
}
