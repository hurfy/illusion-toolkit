using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Translokator;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Probes of the city_crash placement editor: the Translokator write path, the transform round trip through the
/// placement adapter, the streaming-grid bookkeeping that add/move/delete must keep honest, and the seasonal
/// mirror. Everything runs against the real archives but writes only into memory and a throwaway folder — the
/// game's own working copies are never touched.
/// </summary>
internal static class CrashEditProbes
{
    // Output: %TEMP%\illusion_crashedit.txt
    internal static void RunCrashEditProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_crashedit.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
        }

        try
        {
            if (!ProbeAssert.InitEnv(out string? envError))
            {
                sb.AppendLine("ENV ERROR: " + envError);
                File.WriteAllText(outFile, sb.ToString());
                return;
            }

            var sds = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "city_crash", "city_crash.sds"));
            Check("city_crash.sds exists", sds.Exists, sds.FullName);
            if (!sds.Exists)
            {
                File.WriteAllText(outFile, sb.ToString());
                return;
            }

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            string? traPath = SdsTranslokatorSaver.ResolvePath(extracted);
            Check("Translokator resolves through the SDS manifest", traPath != null, traPath ?? "(none)");
            if (traPath == null)
            {
                File.WriteAllText(outFile, sb.ToString());
                return;
            }

            byte[] original = File.ReadAllBytes(traPath);
            var table = new TranslokatorLoader(new FileInfo(traPath));
            Check("table parses", table.ObjectGroups.Length > 0 && table.Grids.Length > 0,
                $"{table.ObjectGroups.Length} groups, {table.Grids.Length} grids");

            // 1. An untouched table must re-encode to the very bytes it came from. Everything below only means
            //    something because of this: the writer re-quantizes every placement rather than echoing it.
            byte[] rewritten = table.ToBytes();
            Check("untouched table re-saves byte-for-byte", rewritten.AsSpan().SequenceEqual(original),
                rewritten.Length == original.Length
                    ? $"first diff at {ProbeAssert.FirstDiff(original, rewritten)}"
                    : $"length {original.Length} vs {rewritten.Length}");

            // Find a row with copies to work on.
            Formats.Translokator.Object? row = null;
            foreach (ObjectGroup group in table.ObjectGroups)
            {
                foreach (Formats.Translokator.Object candidate in group.Objects)
                {
                    if (candidate.Instances.Count > 0) { row = candidate; break; }
                }
                if (row != null) break;
            }
            Check("a row with placements exists", row != null);
            if (row == null)
            {
                File.WriteAllText(outFile, sb.ToString());
                return;
            }

            var document = new TranslokatorDocumentAdapter(table, sds);
            Instance first = row.Instances[0];
            TranslokatorInstanceAdapter node = document.Node(first, row);
            Check("adapter is canonical per placement", ReferenceEquals(node, document.Node(first, row)));

            // 2. The transform round trip: read the placement's matrix and write the very same one back. The
            //    Euler triple and the quantized bytes must both survive, or a click-and-release would edit a
            //    placement the user only selected.
            Matrix4x4 before = node.LocalTransform;
            node.LocalTransform = before;
            Check("re-applying the same matrix leaves the placement alone",
                ProbeAssert.Approx(node.Instance.Position, first.Position)
                && table.ToBytes().AsSpan().SequenceEqual(original));

            // 3. A move and its exact reverse must restore the file — this is what proves the grid bookkeeping
            //    is symmetric (the cell counts are decremented and incremented, never rebuilt).
            Vector3 origin = first.Position;
            node.LocalTransform = TransformMath.Compose(
                first.Quaternion, new Vector3(first.Scale), origin + new Vector3(250f, 180f, 0f));
            byte[] moved = table.ToBytes();
            Check("a move changes the file", !moved.AsSpan().SequenceEqual(original));

            node.LocalTransform = TransformMath.Compose(first.Quaternion, new Vector3(first.Scale), origin);
            byte[] movedBack = table.ToBytes();
            Check("moving back restores the file byte-for-byte",
                movedBack.AsSpan().SequenceEqual(original),
                $"first diff at {ProbeAssert.FirstDiff(original, movedBack)}");

            // 4. Rotation and scale survive a round trip through the matrix (the file stores Euler degrees and a
            //    single scale factor, so both go through a conversion the gizmo depends on).
            var spin = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
            node.LocalTransform = TransformMath.Compose(spin, new Vector3(1.35f), origin);
            bool rotOk = ProbeAssert.QApprox(node.Instance.Quaternion, spin, 2e-2f);
            bool scaleOk = MathF.Abs(node.Instance.Scale - 1.35f) < 0.02f;
            Check("rotation survives the matrix round trip", rotOk, node.Instance.Rotation.ToString());
            Check("scale survives the matrix round trip", scaleOk, node.Instance.Scale.ToString("0.000"));

            // Put it back so the counts below start from the shipped state.
            node.LocalTransform = before;

            // A drag must only invalidate the row it touched. Flagging the whole document instead made every
            // frame rebuild all ~800 prototype clouds in the archive, which is what dropped the viewport to a
            // couple of frames per second — so this guards the narrow invalidation, not just its effect.
            document.ConsumeDirtyRows();
            node.LocalTransform = TransformMath.Compose(
                first.Quaternion, new Vector3(first.Scale), origin + new Vector3(1f, 0f, 0f));
            IReadOnlyList<Formats.Translokator.Object> touched = document.ConsumeDirtyRows();
            Check("a drag invalidates exactly one table row", touched.Count == 1 && ReferenceEquals(touched[0], row),
                $"{touched.Count} rows");
            Check("consuming the dirty set clears it", document.ConsumeDirtyRows().Count == 0);
            node.LocalTransform = before;

            // 5. Add and delete: the grid cell that counts this row must follow, and the file must return to its
            //    original bytes once the added copy is removed again.
            int cellBefore = GridCell(table, row, origin);
            int countBefore = row.Instances.Count;
            bool idOk = document.TryAllocateId(out ushort id);
            Check("a free placement id is available", idOk, $"id {id}");

            var added = new Instance { Position = origin + new Vector3(3f, 0f, 0f), Scale = 1f, ID = id };
            document.InsertPlacement(row, added, row.Instances.Count, mirror: false);
            Check("insert appends the placement", row.Instances.Count == countBefore + 1);
            Check("insert bumps the streaming grid cell", GridCell(table, row, added.Position) == cellBefore + 1,
                $"{cellBefore} → {GridCell(table, row, added.Position)}");
            Check("the added id is unique", UniqueIds(table));

            document.RemovePlacement(row, added, mirror: false);
            Check("delete removes the placement", row.Instances.Count == countBefore);
            byte[] afterAddDelete = table.ToBytes();
            Check("add + delete restores the file byte-for-byte",
                afterAddDelete.AsSpan().SequenceEqual(original),
                $"first diff at {ProbeAssert.FirstDiff(original, afterAddDelete)}");

            // 6. The seasonal twin: the winter archive holds the same placements, so an edit to a linked copy
            //    has to land in both tables.
            var winter = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "city_crash", "city_crash_z.sds"));
            if (winter.Exists)
            {
                string winterExtracted = SdsMeshLoader.EnsureExtracted(winter);
                string? winterTra = SdsTranslokatorSaver.ResolvePath(winterExtracted);
                if (winterTra != null)
                {
                    var winterTable = new TranslokatorLoader(new FileInfo(winterTra));
                    var paired = new TranslokatorDocumentAdapter(
                        new TranslokatorLoader(new FileInfo(traPath)), sds, winterTable, winter);

                    Formats.Translokator.Object pairedRow = FindRow(paired.Table, row.Name.String)!;
                    Instance pairedFirst = pairedRow.Instances[0];
                    TranslokatorInstanceAdapter pairedNode = paired.Node(pairedFirst, pairedRow);
                    Check("a shipped placement starts linked to its winter twin", pairedNode.SeasonLinked);

                    Vector3 target = pairedFirst.Position + new Vector3(0f, 0f, 12f);
                    pairedNode.LocalTransform = TransformMath.Compose(
                        pairedFirst.Quaternion, new Vector3(pairedFirst.Scale), target);

                    Instance? twin = FindById(winterTable, row.Name.String, pairedFirst.ID);
                    Check("the move reached the winter table", twin != null && ProbeAssert.Approx(twin.Position, target),
                        twin?.Position.ToString() ?? "(twin not found)");
                    Check("the winter archive is enlisted for the build",
                        paired.CompanionArchives.Count == 1);

                    // Unlinking stops the mirroring, which is what "delete in this season only" rests on.
                    Vector3 twinBefore = twin!.Position;
                    pairedNode.SeasonLinked = false;
                    pairedNode.LocalTransform = TransformMath.Compose(
                        pairedFirst.Quaternion, new Vector3(pairedFirst.Scale), target + new Vector3(0f, 0f, 9f));
                    Check("unlinking stops the mirror", ProbeAssert.Approx(twin.Position, twinBefore));
                }
            }
            else
            {
                sb.AppendLine("(no city_crash_z.sds — seasonal checks skipped)");
            }

            // 7. The save path writes a readable table into a throwaway extracted folder.
            string scratch = Path.Combine(Path.GetTempPath(), "illusion_crashedit_out");
            try
            {
                if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
                CopyExtractedShell(extracted, scratch, traPath);
                string written = SdsTranslokatorSaver.SaveToExtracted(table, scratch, "city_crash.sds (probe)");
                var reloaded = new TranslokatorLoader(new FileInfo(written));
                Check("the written table reloads with the same placement count",
                    Count(reloaded) == Count(table), $"{Count(reloaded)} vs {Count(table)}");
                Check("the written table is byte-identical to the untouched original",
                    File.ReadAllBytes(written).AsSpan().SequenceEqual(original));
            }
            finally
            {
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { /* scratch */ }
            }

            // 8. What the renderer draws a copy at, and what a click is tested against, must be the same matrix.
            //    They are computed by different code — the loader builds the instance buffer, PickCrash and the
            //    selection outline rebuild each matrix from the table — so the only thing keeping them together
            //    is that neither adds anything of its own. A .tra matrix is already an absolute world placement,
            //    and this archive ships actor packs claiming the very prototypes the table instances, so folding
            //    an actor placement in here would scatter whole rows and leave every click missing them.
            (List<SdsFrameNode> crashRoots, _, ISceneDocument? crashDoc, CrashPlacements? crashPlacements) =
                SdsMeshLoader.LoadCrashHierarchy(sds);
            if (crashDoc is SceneDocumentAdapter scene && crashPlacements != null)
            {
                var meshByFrame = new Dictionary<FrameObjectBase, MeshData>();
                foreach (SdsFrameNode root in crashRoots) IndexMeshes(root, meshByFrame);

                Dictionary<FrameObjectSingleMesh, CrashPlacements.Cloud> clouds = crashPlacements.BuildClouds();
                int claimed = 0, compared = 0, drifted = 0;
                string firstDrift = "";
                foreach (FrameObjectSingleMesh prototype in crashPlacements.Meshes)
                {
                    if (!scene.Placements.For(prototype).IsIdentity) claimed++;

                    if (!meshByFrame.TryGetValue(prototype, out MeshData? md)) continue;
                    if (md.Instances == null || md.Instances.Length == 0) continue;

                    Matrix4x4[] expected = clouds[prototype].Matrices;
                    compared++;
                    for (int i = 0; i < md.Instances.Length && i < expected.Length; i++)
                    {
                        if (md.Instances[i].Equals(expected[i])) continue;
                        drifted++;
                        if (firstDrift.Length == 0)
                        {
                            firstDrift = $"{prototype.Name} copy {i}: drawn at {md.Instances[i].Translation}, " +
                                         $"clicked at {expected[i].Translation}";
                        }
                        break;
                    }
                }
                Check("the archive's actors do claim instanced prototypes (the trap is still armed)", claimed > 0,
                    $"{claimed} of {crashPlacements.Meshes.Count()} prototypes are actor-placed");
                Check("every copy is drawn where a click looks for it", drifted == 0 && compared > 0,
                    $"rows compared={compared}, drifted={drifted} {firstDrift}");
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("EXCEPTION: " + ex);
        }

        sb.Insert(0, $"CRASH EDIT PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    // Mesh leaves by the frame object they render, so a prototype can be matched to what the loader built for it.
    private static void IndexMeshes(SdsFrameNode node, Dictionary<FrameObjectBase, MeshData> into)
    {
        if (node.Mesh != null && node.Source is FrameNodeAdapter adapter) into[adapter.Frame] = node.Mesh;
        foreach (SdsFrameNode child in node.Children) IndexMeshes(child, into);
    }

    // The streaming-grid cell that counts a row's copies at this position (the grid whose key is its GridMax).
    private static int GridCell(TranslokatorLoader table, Formats.Translokator.Object row, Vector3 position)
    {
        foreach (Grid grid in table.Grids)
        {
            if (grid.Key != (short)row.GridMax) continue;
            int cx = Math.Clamp((int)((position.X - grid.Origin.X) / grid.CellSize.X), 0, grid.Width - 1);
            int cy = Math.Clamp((int)((position.Y - grid.Origin.Y) / grid.CellSize.Y), 0, grid.Height - 1);
            return grid.Data[cy * grid.Width + cx];
        }
        return -1;
    }

    private static bool UniqueIds(TranslokatorLoader table)
    {
        var seen = new HashSet<ushort>();
        foreach (ObjectGroup group in table.ObjectGroups)
        {
            foreach (Formats.Translokator.Object row in group.Objects)
            {
                foreach (Instance copy in row.Instances)
                {
                    if (!seen.Add(copy.ID)) return false;
                }
            }
        }
        return true;
    }

    private static int Count(TranslokatorLoader table)
    {
        int n = 0;
        foreach (ObjectGroup group in table.ObjectGroups)
        {
            foreach (Formats.Translokator.Object row in group.Objects) n += row.Instances.Count;
        }
        return n;
    }

    private static Formats.Translokator.Object? FindRow(TranslokatorLoader table, string name)
    {
        foreach (ObjectGroup group in table.ObjectGroups)
        {
            foreach (Formats.Translokator.Object row in group.Objects)
            {
                if (string.Equals(row.Name.String, name, StringComparison.Ordinal)) return row;
            }
        }
        return null;
    }

    private static Instance? FindById(TranslokatorLoader table, string rowName, ushort id)
    {
        Formats.Translokator.Object? row = FindRow(table, rowName);
        if (row == null) return null;
        foreach (Instance copy in row.Instances)
        {
            if (copy.ID == id) return copy;
        }
        return null;
    }

    // The save path insists on a real extracted folder (manifest + the resource file it names). Mirror just
    // those two so the probe can write without going anywhere near the live working copy.
    private static void CopyExtractedShell(string source, string target, string traPath)
    {
        Directory.CreateDirectory(target);
        File.Copy(Path.Combine(source, "SDSContent.xml"), Path.Combine(target, "SDSContent.xml"));
        string relative = Path.GetRelativePath(source, traPath);
        string destination = Path.Combine(target, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(traPath, destination);
    }
}
