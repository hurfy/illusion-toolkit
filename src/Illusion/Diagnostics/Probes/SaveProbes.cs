using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the save/pack chain: FrameResource save + repack, and versioned .sds backups.</summary>
internal static class SaveProbes
{
    // Save + pack chain against a real district: proves an in-memory transform edit survives
    // SdsWriter.SaveFrameResource → reload, and that the extracted folder repacks into a valid .sds. Both are
    // done non-destructively: the FrameResource file is restored from a byte snapshot, and the pack targets a
    // TEMP .sds (never the game archive), then that .sds is re-opened to confirm it is a well-formed archive.
    internal static void RunSaveProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_save.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string? frFile = null;
        byte[]? original = null;
        string tempSds = Path.Combine(Path.GetTempPath(), $"illusion_save_{district}.sds");
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            sb.AppendLine($"SDS: {sds.FullName} exists={sds.Exists}");
            if (!sds.Exists) { sb.AppendLine("no such district"); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            FrameObjectBase? target = fr?.FrameObjects?.Values.OfType<FrameObjectBase>().FirstOrDefault();
            if (fr == null || target == null) { sb.AppendLine("no editable frame object"); return; }
            sb.AppendLine($"editing object[0] '{target.Name}' of {fr.FrameObjects!.Count} frame objects\n");

            // Snapshot the FrameResource file bytes so we can restore the extracted folder afterwards.
            frFile = SdsManifest.Load(extracted).GetFiles("FrameResource")[0];
            original = File.ReadAllBytes(frFile);

            // Apply a known translation delta (translation lives in M41..M43, which IS serialized).
            Matrix4x4 before = target.LocalTransform;
            Matrix4x4 edited = before;
            edited.M41 = before.M41 + 12.5f;
            edited.M42 = before.M42 - 7.25f;
            edited.M43 = before.M43 + 3.5f;
            target.LocalTransform = edited;

            // Save through the real helper, then reload a FRESH FrameResource from disk and compare.
            string written = SdsWriter.SaveFrameResource(fr, sds);
            Check("SaveFrameResource wrote the FrameResource file", string.Equals(written, frFile, StringComparison.OrdinalIgnoreCase), written);

            FrameResource fr2 = SdsMeshLoader.OpenScene(extracted).FrameResource!;
            FrameObjectBase reloaded = fr2.FrameObjects.Values.OfType<FrameObjectBase>().First();
            Vector3 got = reloaded.LocalTransform.Translation;
            Check("Edited translation round-trips through save+reload", Approx(got, edited.Translation, 1e-2f),
                $"want {edited.Translation} got {got}");
            Check("Reloaded translation differs from the original", !Approx(got, before.Translation, 1e-2f),
                $"orig {before.Translation}");

            // Restore the extracted folder to its pristine bytes and confirm.
            File.WriteAllBytes(frFile, original);
            original = null;
            Vector3 restored = SdsMeshLoader.OpenScene(extracted).FrameResource!
                .FrameObjects.Values.OfType<FrameObjectBase>().First().LocalTransform.Translation;
            Check("FrameResource file restored to original", Approx(restored, before.Translation, 1e-2f), restored.ToString());

            // Pack the (pristine) extracted folder into a TEMP .sds — exercises Pack + Save without
            // touching the game archive — then re-open it exactly like the loader does to prove it is well-formed.
            if (File.Exists(tempSds)) File.Delete(tempSds);
            SdsArchive toPack = SdsArchive.Pack(extracted, GameProfile.MafiaII);
            using (FileStream output = File.Create(tempSds))
            {
                toPack.Save(output, new SdsWriteOptions());
            }
            var packed = new FileInfo(tempSds);
            Check("Packed .sds created", packed.Exists && packed.Length > 0, packed.Exists ? $"{packed.Length:N0} bytes" : "missing");

            SdsArchive af = SdsArchive.Open(tempSds);
            Check("Packed .sds re-opens with resources", af.Entries.Count > 0, $"{af.Entries.Count} entries");
            bool hasFr = af.ResourceTypes.Any(t => string.Equals(t.Name, "FrameResource", StringComparison.OrdinalIgnoreCase));
            Check("Packed .sds declares a FrameResource type", hasFr);

            sb.Insert(0, $"SAVE PROBE ({district}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "SAVE PROBE: FAIL\n\n");
        }
        finally
        {
            // Best-effort cleanup so a failed run never leaves the extracted folder edited or temp files behind.
            try { if (original != null && frFile != null) File.WriteAllBytes(frFile, original); } catch { }
            try { if (File.Exists(tempSds)) File.Delete(tempSds); } catch { }
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Versioned .sds backups (SdsWriter.BackupArchive / BackupDir): timestamped copies land in a "backups" folder
    // beside the archive; every build adds a new version (none pruned); a same-second collision uniquifies with a
    // counter; a not-yet-existing archive backs up to null. All exercised on TEMP files — no game data, no GPU.
    internal static void RunBackupProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_backup.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string root = Path.Combine(Path.GetTempPath(), "illusion_backup_probe");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);

            var sds = new FileInfo(Path.Combine(root, "fake.sds"));
            byte[] payload = Enumerable.Range(0, 512).Select(i => (byte)(i * 7)).ToArray();
            File.WriteAllBytes(sds.FullName, payload);

            string expectedDir = Path.Combine(root, "backups");
            Check("BackupDir is a 'backups' folder beside the archive",
                string.Equals(SdsWriter.BackupDir(sds), expectedDir, StringComparison.OrdinalIgnoreCase),
                SdsWriter.BackupDir(sds));

            // 1) First backup: exact timestamped name, folder created, byte-for-byte copy of the stock archive.
            var when1 = new DateTime(2026, 7, 17, 14, 30, 12);
            string? b1 = SdsWriter.BackupArchive(sds, when1);
            Check("First backup returns a path", b1 != null, b1 ?? "null");
            Check("Backup lands in the backups folder",
                b1 != null && string.Equals(Path.GetDirectoryName(b1), expectedDir, StringComparison.OrdinalIgnoreCase));
            Check("Backup name is <stem>_<yyyyMMdd_HHmmss>.sds",
                b1 != null && Path.GetFileName(b1) == "fake_20260717_143012.sds",
                b1 == null ? "" : Path.GetFileName(b1));
            Check("Backup is a byte-for-byte copy",
                b1 != null && File.Exists(b1) && File.ReadAllBytes(b1).SequenceEqual(payload));

            // 2) Same-second second backup: collision guard appends a counter (nothing overwritten).
            string? b2 = SdsWriter.BackupArchive(sds, when1);
            Check("Same-second backup uniquifies with a counter",
                b2 != null && b2 != b1 && Path.GetFileName(b2) == "fake_20260717_143012_2.sds",
                b2 == null ? "null" : Path.GetFileName(b2));

            // 3) A later build gets its own version — all versions are kept.
            var when2 = when1.AddSeconds(5);
            string? b3 = SdsWriter.BackupArchive(sds, when2);
            Check("Later backup is a new version",
                b3 != null && Path.GetFileName(b3) == "fake_20260717_143017.sds",
                b3 == null ? "null" : Path.GetFileName(b3));
            int versions = Directory.GetFiles(expectedDir, "fake_*.sds").Length;
            Check("All three versions are kept (none pruned)", versions == 3, versions.ToString());

            // 4) A not-yet-existing archive has nothing to preserve → null.
            var missing = new FileInfo(Path.Combine(root, "does_not_exist.sds"));
            string? b4 = SdsWriter.BackupArchive(missing, when1);
            Check("Missing archive backs up to null", b4 == null);

            // 5) The bulk unpacker must SKIP our backups (they are full .sds copies) — otherwise every kept version
            //    gets re-extracted into /resources on the next unpack. Regression guard for that sweep.
            Check("Unpacker skips a .sds inside the backups folder",
                b1 != null && !ResourceUnpacker.IsUnpackableGameSds(b1), b1 ?? "null");
            Check("Unpacker still unpacks a normal game .sds",
                ResourceUnpacker.IsUnpackableGameSds(@"C:\game\pc\sds\city\midtown.sds"));

            sb.Insert(0, $"BACKUP PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "BACKUP PROBE: FAIL\n\n");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Restore-from-backup (SdsWriter.ListBackups / RestoreArchive / DeleteExtracted): the live archive swaps
    // back to a chosen version atomically, history is never touched, sibling stems sharing the backups folder
    // don't cross-match, refusals leave everything alone, and the extracted mirror deletes marker-first.
    // All exercised on TEMP files — no game data, no GPU.
    internal static void RunRestoreProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_restore.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string root = Path.Combine(Path.GetTempPath(), "illusion_restore_probe");
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);

            // Two versions of a fake archive: v1 backed up, then the archive "rebuilt" to v2 and backed up again.
            var sds = new FileInfo(Path.Combine(root, "fake.sds"));
            byte[] v1 = Enumerable.Range(0, 512).Select(i => (byte)(i * 7)).ToArray();
            byte[] v2 = Enumerable.Range(0, 512).Select(i => (byte)(i * 11)).ToArray();
            File.WriteAllBytes(sds.FullName, v1);
            var when1 = new DateTime(2026, 7, 17, 14, 30, 12);
            string? b1 = SdsWriter.BackupArchive(sds, when1);
            File.WriteAllBytes(sds.FullName, v2);
            string? b2 = SdsWriter.BackupArchive(sds, when1.AddSeconds(5));

            // Neighbors in the SAME backups folder that a loose "fake_*" pattern would swallow: a sibling
            // archive whose stem extends ours (city_crash vs city_crash_z in real data), and a
            // stamp-shaped file whose digits are not a date.
            var sibling = new FileInfo(Path.Combine(root, "fake_z.sds"));
            File.WriteAllBytes(sibling.FullName, v1);
            SdsWriter.BackupArchive(sibling, when1);
            File.WriteAllText(Path.Combine(SdsWriter.BackupDir(sds), "fake_99999999_999999.sds"), "not a date");

            // 1) Listing: exactly our two versions, newest first, stamps parsed from the names.
            IReadOnlyList<SdsWriter.BackupInfo> backups = SdsWriter.ListBackups(sds);
            Check("ListBackups sees exactly the archive's own versions", backups.Count == 2,
                string.Join(", ", backups.Select(b => b.File.Name)));
            Check("Newest version first",
                backups.Count == 2 && backups[0].File.Name == "fake_20260717_143017.sds",
                backups.Count == 0 ? "empty" : backups[0].File.Name);
            Check("Stamp parses from the name",
                backups.Count == 2 && backups[0].Stamp == when1.AddSeconds(5) && backups[1].Stamp == when1);
            Check("Sibling-stem backups don't cross-match", SdsWriter.ListBackups(sibling).Count == 1);

            // 2) Restore v1 over the live archive: byte-exact swap, history untouched, temp cleaned up.
            SdsWriter.RestoreArchive(sds, new FileInfo(b1!));
            Check("Archive matches the chosen backup byte-for-byte",
                File.ReadAllBytes(sds.FullName).SequenceEqual(v1));
            Check("Backup history untouched (both versions still listed)", SdsWriter.ListBackups(sds).Count == 2);
            Check("The newer version still holds its own bytes", File.ReadAllBytes(b2!).SequenceEqual(v2));
            Check("No temp file left beside the archive", !File.Exists(sds.FullName + ".tmp"));

            // 3) Refusals leave the archive alone: a missing backup file and an empty one both throw.
            bool threwMissing = false;
            try { SdsWriter.RestoreArchive(sds, new FileInfo(Path.Combine(root, "backups", "fake_20990101_000000.sds"))); }
            catch (FileNotFoundException) { threwMissing = true; }
            Check("Missing backup refuses (FileNotFoundException)",
                threwMissing && File.ReadAllBytes(sds.FullName).SequenceEqual(v1));

            string empty = Path.Combine(root, "backups", "fake_20990101_000001.sds");
            File.WriteAllBytes(empty, Array.Empty<byte>());
            bool threwEmpty = false;
            try { SdsWriter.RestoreArchive(sds, new FileInfo(empty)); }
            catch (IOException) { threwEmpty = true; }
            Check("Empty backup refuses (IOException)",
                threwEmpty && File.ReadAllBytes(sds.FullName).SequenceEqual(v1));

            // 4) Extracted-mirror deletion: recursive, and a no-op when the mirror is already gone.
            string extracted = Path.Combine(root, "resources", "fake.sds");
            Directory.CreateDirectory(Path.Combine(extracted, "sub"));
            File.WriteAllText(Path.Combine(extracted, "SDSContent.xml"), "<SDSResource/>");
            File.WriteAllText(Path.Combine(extracted, "sub", "data.bin"), "x");
            SdsWriter.DeleteExtracted(extracted);
            Check("Extracted mirror fully deleted", !Directory.Exists(extracted));
            SdsWriter.DeleteExtracted(extracted);
            Check("Deleting a missing mirror is a no-op", !Directory.Exists(extracted));

            sb.Insert(0, $"RESTORE PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "RESTORE PROBE: FAIL\n\n");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Frame-object delete persistence (DetachedFrames) against a real district: detach a leaf object and a
    // whole subtree, prove a save while detached drops them from the FrameResource AND the rebuilt name
    // table, then reattach and prove the next save is byte-identical to the pre-delete save (undo is
    // byte-faithful). In-memory only — no game file is written. Output: %TEMP%\illusion_framedelete.txt
    internal static void RunFrameDeleteProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_framedelete.txt");
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
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            if (!sds.Exists) { sb.AppendLine($"no such district: {sds.FullName}"); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            if (fr?.FrameObjects is not { Count: > 0 }) { sb.AppendLine("no frame objects"); return; }
            var doc = new SceneDocumentAdapter(fr, sds);

            var objs = fr.FrameObjects.Values.OfType<FrameObjectBase>().ToList();
            FrameObjectBase? leaf = objs.FirstOrDefault(o => o.Children.Count == 0);
            FrameObjectBase? branch = objs.FirstOrDefault(o =>
                o.Children.Count > 0 && !SubtreeContains(o, leaf));
            if (leaf == null || branch == null) { sb.AppendLine("no suitable leaf/branch objects"); return; }

            int subtreeSize = CountSubtree(branch);
            sb.AppendLine($"leaf '{leaf.Name}', branch '{branch.Name}' ({subtreeSize} objects)\n");

            byte[] bytes0 = fr.WriteToStream();
            int objects0 = fr.FrameObjects.Count;
            int tabled0 = OnTableCount(fr);

            // 1) Leaf + whole branch subtree detached: gone from the object dictionary.
            var frames = new List<IFrameNode> { doc.Node(leaf) };
            CollectSubtree(branch, doc, frames);
            DetachedFrames? detached = DetachedFrames.Capture(doc, frames);
            Check("Capture yields a detachment", detached != null, $"{frames.Count} frames");
            if (detached == null) return;

            detached.Detach();
            Check("Detach removes the subtree from FrameObjects",
                fr.FrameObjects.Count == objects0 - 1 - subtreeSize,
                $"{objects0} → {fr.FrameObjects.Count}");
            Check("The leaf's holder no longer lists it",
                leaf.Parent?.Children.Contains(leaf) != true
                && leaf.Root?.Children.Contains(leaf) != true
                && fr.FrameScenes.Values.All(s => !s.Children.Contains(leaf)));

            // 2) A save while detached persists the deletion: reload finds neither object.
            byte[] bytes1 = fr.WriteToStream();
            var fr1 = new FrameResource();
            using (var ms = new MemoryStream(bytes1, false)) fr1.ReadFromFile(ms);
            Check("Save while detached drops the objects",
                fr1.FrameObjects.Count == objects0 - 1 - subtreeSize,
                $"reloaded {fr1.FrameObjects.Count}");

            // 3) The rebuilt frame name table (what SaveFrameNameTable writes) drops the deleted entries.
            var table = new FrameNameTable();
            table.BuildDataFromResource(fr);
            int tabledNow = OnTableCount(fr);
            Check("Rebuilt name table matches the surviving on-table objects",
                (table.FrameData?.Length ?? -1) == tabledNow,
                $"table {table.FrameData?.Length}, on-table now {tabledNow}, was {tabled0}");
            Check("Detaching removed the expected on-table entries",
                tabledNow == tabled0 - TabledIn(frames),
                $"{tabled0} → {tabledNow}, detached carried {TabledIn(frames)}");

            // 4) Reattach: dictionary, blocks and holder slots all back — the next save is byte-identical.
            detached.Reattach();
            Check("Reattach restores the object count", fr.FrameObjects.Count == objects0);
            Check("The leaf is held again",
                leaf.Parent?.Children.Contains(leaf) == true
                || leaf.Root?.Children.Contains(leaf) == true
                || fr.FrameScenes.Values.Any(s => s.Children.Contains(leaf)));
            byte[] bytes2 = fr.WriteToStream();
            Check("Undo save is byte-identical to the pre-delete save",
                bytes2.AsSpan().SequenceEqual(bytes0),
                bytes2.Length == bytes0.Length
                    ? $"first diff at {FirstDiff(bytes0, bytes2)}"
                    : $"length {bytes0.Length} vs {bytes2.Length}");

            // 5) Detach again (redo) — same result as the first apply.
            detached.Detach();
            byte[] bytes3 = fr.WriteToStream();
            Check("Redo save matches the first deleted save",
                bytes3.AsSpan().SequenceEqual(bytes1),
                bytes3.Length == bytes1.Length
                    ? $"first diff at {FirstDiff(bytes1, bytes3)}"
                    : $"length {bytes1.Length} vs {bytes3.Length}");
            detached.Reattach();

            sb.Insert(0, $"FRAME DELETE PROBE ({district}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "FRAME DELETE PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }

        static int CountSubtree(FrameObjectBase o) => 1 + o.Children.Sum(CountSubtree);

        static bool SubtreeContains(FrameObjectBase o, FrameObjectBase? other) =>
            other != null && (ReferenceEquals(o, other) || o.Children.Any(c => SubtreeContains(c, other)));

        static void CollectSubtree(FrameObjectBase o, SceneDocumentAdapter doc, List<Illusion.Domain.IFrameNode> into)
        {
            into.Add(doc.Node(o));
            foreach (FrameObjectBase c in o.Children) CollectSubtree(c, doc, into);
        }

        static int OnTableCount(FrameResource fr) =>
            fr.FrameObjects.Values.OfType<FrameObjectBase>().Count(o => o.IsOnFrameTable);

        static int TabledIn(List<Illusion.Domain.IFrameNode> frames) =>
            frames.Count(f => f is FrameNodeAdapter a && a.Frame.IsOnFrameTable);
    }

    // Frame-object duplication (FrameDuplicator) against a real district: duplicate a static mesh, prove the
    // copy is byte-faithful (raw vertex bytes, indices, transform, parents), independent (fresh buffers under
    // fresh hashes), survives a save, and that undo restores the pre-duplicate save byte-identically.
    // In-memory only — no game file is written. Output: %TEMP%\illusion_duplicate.txt
    internal static void RunDuplicateProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_duplicate.txt");
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
            MafiaMaterials.EnsureLoaded();
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            if (!sds.Exists) { sb.AppendLine($"no such district: {sds.FullName}"); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            if (fr?.FrameObjects is not { Count: > 0 }) { sb.AppendLine("no frame objects"); return; }
            var doc = new SceneDocumentAdapter(fr, sds);

            FrameObjectSingleMesh? source = fr.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                .FirstOrDefault(o => o.GetType() == typeof(FrameObjectSingleMesh)
                    && o.Geometry.LOD is { Length: > 0 }
                    && o.GetVertexBuffer(0) != null && o.GetIndexBuffer(0) != null);
            if (source == null) { sb.AppendLine("no duplicable mesh"); return; }
            sb.AppendLine($"duplicating '{source.Name}' ({fr.FrameObjects.Count} frame objects)\n");

            byte[] bytes0 = fr.WriteToStream();
            int objects0 = fr.FrameObjects.Count;

            FrameDuplicator.DuplicatedObject? dup = FrameDuplicator.TryDuplicate(doc, doc.Node(source), out string? reason);
            Check("TryDuplicate succeeds", dup != null, reason ?? "");
            if (dup == null) return;

            FrameObjectSingleMesh clone = fr.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                .First(o => !ReferenceEquals(o, source) && o.Name.String.StartsWith(source.Name.String + "_copy",
                    StringComparison.Ordinal));
            Check("Copy is registered", fr.FrameObjects.Count == objects0 + 1, $"{fr.FrameObjects.Count}");
            Check("Copy has a unique name", clone.Name.String == source.Name.String + "_copy", clone.Name.String);
            Check("Copy keeps the source transform", clone.LocalTransform == source.LocalTransform);
            Check("Copy keeps the source parents",
                ReferenceEquals(clone.Parent, source.Parent) && ReferenceEquals(clone.Root, source.Root));
            Check("Copy keeps name-table membership", clone.IsOnFrameTable == source.IsOnFrameTable);

            // Independence: fresh blocks and fresh buffers under fresh hashes.
            Check("Copy has its own geometry/material blocks",
                !ReferenceEquals(clone.Geometry, source.Geometry) && !ReferenceEquals(clone.Material, source.Material));
            Check("Copy references different buffers",
                clone.Geometry.LOD[0].VertexBufferRef.Hash != source.Geometry.LOD[0].VertexBufferRef.Hash
                && clone.Geometry.LOD[0].IndexBufferRef.Hash != source.Geometry.LOD[0].IndexBufferRef.Hash);

            // Byte-faithful geometry: identical raw vertex bytes and indices.
            VertexBuffer? srcVb = source.GetVertexBuffer(0);
            VertexBuffer? cloneVb = clone.GetVertexBuffer(0);
            IndexBuffer? srcIb = source.GetIndexBuffer(0);
            IndexBuffer? cloneIb = clone.GetIndexBuffer(0);
            Check("Copy's buffers resolve", cloneVb != null && cloneIb != null);
            Check("Vertex bytes are identical",
                srcVb != null && cloneVb != null && cloneVb.Data.AsSpan().SequenceEqual(srcVb.Data));
            Check("Indices are identical",
                srcIb != null && cloneIb != null && cloneIb.GetData().SequenceEqual(srcIb.GetData())
                && cloneIb.IndexFormat == srcIb.IndexFormat);
            Check("Render mesh was produced", dup.Mesh.Positions.Length > 0);

            // A save while duplicated carries both objects; the copy's LOD refs stay resolvable.
            byte[] bytes1 = fr.WriteToStream();
            var fr1 = new FrameResource();
            using (var ms = new MemoryStream(bytes1, false)) fr1.ReadFromFile(ms);
            Check("Save carries the copy", fr1.FrameObjects.Count == objects0 + 1, $"{fr1.FrameObjects.Count}");
            FrameObjectSingleMesh? reloaded = fr1.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                .FirstOrDefault(o => o.Name.String == clone.Name.String);
            Check("Reloaded copy links geometry and material",
                reloaded is { MeshIndex: >= 0, MaterialIndex: >= 0 }
                && reloaded.Geometry.LOD.Length == source.Geometry.LOD.Length);

            // Undo: everything the duplicate brought comes back out — the next save is byte-identical.
            dup.Detach();
            Check("Undo restores the object count", fr.FrameObjects.Count == objects0);
            Check("Undo removes the cloned buffers",
                cloneVb != null && fr.VertexBuffers.GetBuffer(cloneVb.Hash) == null
                && cloneIb != null && fr.IndexBuffers.GetBuffer(cloneIb.Hash) == null);
            byte[] bytes2 = fr.WriteToStream();
            Check("Undo save is byte-identical to the pre-duplicate save",
                bytes2.AsSpan().SequenceEqual(bytes0),
                bytes2.Length == bytes0.Length
                    ? $"first diff at {FirstDiff(bytes0, bytes2)}"
                    : $"length {bytes0.Length} vs {bytes2.Length}");

            // Redo: the same duplicate returns.
            dup.Reattach();
            byte[] bytes3 = fr.WriteToStream();
            Check("Redo save matches the duplicated save",
                bytes3.AsSpan().SequenceEqual(bytes1),
                bytes3.Length == bytes1.Length
                    ? $"first diff at {FirstDiff(bytes1, bytes3)}"
                    : $"length {bytes1.Length} vs {bytes3.Length}");
            dup.Detach();

            sb.Insert(0, $"DUPLICATE PROBE ({district}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "DUPLICATE PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Reparent (hierarchy) via SceneDocumentAdapter.Reparent against a real district: reparent an object under
    // another, confirm the parent ref + runtime graph update and that it survives save+reload; that a cycle
    // (parent under its own child) is rejected; and that reparenting under a scene folder works. Non-destructive —
    // mutates only the in-memory FrameResource, writes no game file. Output: %TEMP%\illusion_reparent.txt
    internal static void RunReparentProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_reparent.txt");
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
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            if (!sds.Exists) { sb.AppendLine($"no such district: {sds.FullName}"); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            if (fr?.FrameObjects is not { Count: > 0 }) { sb.AppendLine("no frame objects"); return; }
            var doc = new SceneDocumentAdapter(fr, sds);

            var objs = fr.FrameObjects.Values.OfType<FrameObjectBase>().ToList();
            FrameObjectBase? child = objs.FirstOrDefault(o => o.Children.Count == 0);
            FrameObjectBase? newParent = objs.FirstOrDefault(o =>
                !ReferenceEquals(o, child) && !ReferenceEquals(o, child?.Parent) && o.Children.Count > 0)
                ?? objs.FirstOrDefault(o => !ReferenceEquals(o, child) && !ReferenceEquals(o, child?.Parent));
            if (child == null || newParent == null) { sb.AppendLine("no suitable objects"); return; }

            string childName = child.Name.ToString(), parentName = newParent.Name.ToString();
            sb.AppendLine($"child '{childName}' → parent '{parentName}'\n");

            bool ok = doc.Reparent(doc.Node(child), doc.Node(newParent));
            Check("Reparent under an object succeeds", ok);
            Check("Parent ref points to the new parent",
                child.Refs.TryGetValue(FrameEntryRefTypes.Parent1, out int r) && r == newParent.RefID,
                $"ref={r}");
            Check("Runtime graph updated (child.Parent == new parent)", ReferenceEquals(child.Parent, newParent));

            // Save + reload: the parent link must persist through the frame stream (parent index recompute).
            byte[] bytes = fr.WriteToStream();
            var fr2 = new FrameResource();
            using (var ms = new MemoryStream(bytes, false)) fr2.ReadFromFile(ms);
            FrameObjectBase? child2 = fr2.FrameObjects.Values.OfType<FrameObjectBase>()
                .FirstOrDefault(o => o.Name.ToString() == childName);
            Check("Reparent persists through save + reload",
                child2?.Parent != null && child2.Parent.Name.ToString() == parentName,
                child2?.Parent?.Name.ToString() ?? "null");

            // Cycle rejection: reparent an object under one of its own children.
            FrameObjectBase? withKid = objs.FirstOrDefault(o => o.Children.Count > 0);
            if (withKid != null)
                Check("Reparent under a descendant is rejected (cycle)",
                    !doc.Reparent(doc.Node(withKid), doc.Node(withKid.Children[0])));

            // Reparent under a scene folder. Scene membership lives in ParentIndex2, and ParentIndex1 must be
            // cleared: (-1, scene) is the shape the game ships for a scene-anchored root, while a scene ordinal
            // in ParentIndex1 occurs nowhere in stock data and makes the engine refuse the district.
            if (fr.FrameScenes is { Count: > 0 })
            {
                FrameHeaderScene scene = fr.FrameScenes.Values.First();
                bool okScene = doc.Reparent(doc.Node(child), new FrameSceneAdapter(scene));
                bool haveParent2 = child.Refs.TryGetValue(FrameEntryRefTypes.Parent2, out int rr);
                Check("Reparent under a scene folder anchors via ParentIndex2",
                    okScene && haveParent2 && rr == scene.RefID,
                    $"parent2Ref={(haveParent2 ? rr.ToString() : "none")}");
                Check("Reparent under a scene folder clears ParentIndex1",
                    !child.Refs.ContainsKey(FrameEntryRefTypes.Parent1) && child.ParentIndex1.Index == -1,
                    $"parentIndex1={child.ParentIndex1.Index}");
                Check("Scene folder lists the object as a child", scene.Children.Contains(child));
            }

            sb.Insert(0, $"REPARENT PROBE ({district}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "REPARENT PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }
}
