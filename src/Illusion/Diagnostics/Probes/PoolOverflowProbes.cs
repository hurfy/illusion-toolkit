using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Geometry;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Opening another buffer pool when every existing one is full.
///
/// A pool file holds at most 128 buffers — not a format limit (the count is a 32-bit field) but the shipped
/// game's own convention, kept exactly: of its 3268 pool files none exceeds 128 and 1020 sit right on it. An
/// archive that needs more simply carries more pool files, and the toolkit now does the same instead of
/// refusing the copy.
///
/// The part that bites is not the file — it is the MANIFEST. Packing builds an archive from SDSContent.xml,
/// never from the folder, so a pool that is written but not announced is dropped at Build and the archive then
/// names buffers nothing carries.
/// </summary>
internal static class PoolOverflowProbes
{
    /// <summary>Output: %TEMP%\illusion_pooloverflow.txt</summary>
    internal static void RunPoolOverflowProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_pooloverflow.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string scratch = Path.Combine(Path.GetTempPath(), "illusion_pooloverflow_out");
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            if (fr == null) { sb.AppendLine("no frame resource"); return; }

            sb.AppendLine($"POOL OVERFLOW PROBE — district={district}\n");
            int vertexPools = fr.VertexBuffers.Sources.Count;
            int indexPools = fr.IndexBuffers.Sources.Count;
            sb.AppendLine($"pools as shipped: {vertexPools} vertex, {indexPools} index");
            foreach (BufferPoolSource s in fr.IndexBuffers.Sources)
            {
                sb.AppendLine($"    {Path.GetFileName(s.FilePath)}: {s.Hashes.Count} buffers");
            }

            // ── Fill every index pool to the brim, then ask for one more ──
            //
            // Done in memory on the loaded resource: the point is what the manager decides, and filling with
            // empty buffers costs nothing. The archive on disk is not touched by this part.
            int filled = 0;
            foreach (BufferPoolSource source in fr.IndexBuffers.Sources)
            {
                while (source.Hashes.Count < IndexBufferPool.MaxBuffersPerPool)
                {
                    var filler = new IndexBuffer((ulong)(0xF111_0000_0000_0000UL + (ulong)filled));
                    filler.SetFormat(1);
                    filler.SetData([0, 1, 2]);
                    if (!fr.IndexBuffers.TryAddToPool(filler)) break;
                    filled++;
                }
            }
            Check("every shipped pool can be filled to the cap", filled > 0,
                $"{filled} filler buffer(s) added across {indexPools} pool(s)");

            bool allFull = fr.IndexBuffers.Sources
                .Take(indexPools)
                .All(s => s.Hashes.Count >= IndexBufferPool.MaxBuffersPerPool);
            Check("the shipped pools are now all at the cap", allFull,
                string.Join(", ", fr.IndexBuffers.Sources.Take(indexPools).Select(s => s.Hashes.Count)));

            var overflow = new IndexBuffer(0xF222_0000_0000_0001UL);
            overflow.SetFormat(1);
            overflow.SetData([0, 1, 2]);
            bool accepted = fr.IndexBuffers.TryAddToPool(overflow);
            Check("a buffer past the cap opens a new pool instead of being refused", accepted,
                accepted ? "" : "TryAddToPool still returns false");

            BufferPoolSource? minted = fr.IndexBuffers.Sources.FirstOrDefault(s => s.IsNew);
            Check("the new pool is named the way the game names them",
                minted != null
                && Path.GetFileName(minted.FilePath).StartsWith("IndexBufferPool_", StringComparison.Ordinal)
                && Path.GetExtension(minted.FilePath) == ".ibp"
                && Path.GetDirectoryName(minted.FilePath) == Path.GetDirectoryName(fr.IndexBuffers.Sources[0].FilePath),
                minted == null ? "no new source" : Path.GetFileName(minted.FilePath));

            Check("the new pool does not collide with a file already there",
                minted != null && !File.Exists(minted.FilePath),
                minted == null ? "" : minted.FilePath);

            Check("the buffer is findable again through the manager",
                fr.IndexBuffers.GetBuffer(overflow.Hash) != null);

            // ── The manifest half, on a COPY of the folder ──
            //
            // The archive itself is never written: SDSContent.xml is the one file in an extracted folder that
            // nothing can reconstruct, so the append is exercised on a throwaway copy.
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);
            File.Copy(Path.Combine(extracted, "SDSContent.xml"), Path.Combine(scratch, "SDSContent.xml"));

            SdsManifest before = SdsManifest.Load(scratch);
            int poolsBefore = before.GetFiles("IndexBufferPool").Count;
            bool added = before.AddEntry("IndexBufferPool", "IndexBufferPool_999.ibp", version: 2);
            bool twice = before.AddEntry("IndexBufferPool", "IndexBufferPool_999.ibp", version: 2);

            SdsManifest after = SdsManifest.Load(scratch);
            int poolsAfter = after.GetFiles("IndexBufferPool").Count;
            Check("the manifest gains the new pool and survives a reload", added && !twice
                && poolsAfter == poolsBefore + 1
                && after.HasFile("IndexBufferPool_999.ibp"),
                $"{poolsBefore} → {poolsAfter}, re-adding returned {twice}");

            // Everything the manifest listed before must still be listed: an append that reorders or drops
            // entries would repack the archive wrong in ways nothing else here would notice.
            bool keptAll = true;
            foreach (string type in new[] { "FrameResource", "FrameNameTable", "IndexBufferPool", "VertexBufferPool" })
            {
                if (after.GetFiles(type).Count < before.GetFiles(type).Count - (type == "IndexBufferPool" ? 1 : 0))
                {
                    keptAll = false;
                }
            }
            Check("no existing entry was lost", keptAll);

            // And the packer has to accept the result — the manifest is XML it parses on its own path.
            bool parses;
            try
            {
                var doc = new System.Xml.XPath.XPathDocument(Path.Combine(scratch, "SDSContent.xml"));
                parses = doc.CreateNavigator().Select("/SDSResource/ResourceEntry").Count == poolsBefore + 1
                    + after.GetFiles("VertexBufferPool").Count
                    + CountOtherEntries(Path.Combine(scratch, "SDSContent.xml"), poolsAfter,
                        after.GetFiles("VertexBufferPool").Count);
            }
            catch (Exception)
            {
                parses = false;
            }
            Check("the rewritten manifest is still well-formed XML the packer reads", parses);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
        }

        sb.Insert(0, $"POOL OVERFLOW PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    // Total entries minus the two pool kinds — the rest of the manifest, which must be untouched.
    private static int CountOtherEntries(string path, int indexPools, int vertexPools)
    {
        var doc = new System.Xml.XPath.XPathDocument(path);
        int total = doc.CreateNavigator().Select("/SDSResource/ResourceEntry").Count;
        return total - indexPools - vertexPools;
    }
}
