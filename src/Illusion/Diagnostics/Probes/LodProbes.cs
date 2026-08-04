using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Editing a level of detail other than LOD0 — end to end, on a car body, which is the one thing in the
/// game that reliably ships two.
///
/// <para>
/// Three things belong to the GEOMETRY rather than to a level, and each is a way for a push into LOD1 to
/// damage LOD0 without touching it: the quantization parameters (one offset and factor for every level),
/// the frame's bounding box, and the per-bone face ranges (<c>BlendMeshSplits</c>, which address LOD0's
/// index buffer). This checks that the fine level survives a coarse-level edit intact — in its bytes where
/// the lattice held, and in its POSITIONS when the lattice had to move.
/// </para>
/// <para>Reads the extracted mirror only; nothing is written to the game. Output: %TEMP%\illusion_lod_edit.txt</para>
/// </summary>
internal static class LodProbes
{
    internal static void RunLodEditProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_lod_edit.txt");
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

            var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", focus + ".sds"));
            if (!car.Exists) { sb.AppendLine("no such archive: " + car.FullName); return; }

            (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(car);
            if (document == null) { sb.AppendLine("archive carries no frame objects"); return; }

            // ── The hierarchy: one row per level under the mesh, LOD 0 on, the rest behind their own eye ──
            var leaves = new List<Illusion.Scene.SceneNode>();
            var built = new List<Illusion.Scene.SceneNode>();
            foreach (SdsFrameNode r in roots) built.Add(Viewport.SceneTree.BuildSceneTree(r, leaves));

            Illusion.Scene.SceneNode? meshRow = null;
            foreach (Illusion.Scene.SceneNode r in built) meshRow ??= FindLodParent(r);
            Check("the multi-level mesh grew LOD rows", meshRow != null);
            if (meshRow != null)
            {
                List<Illusion.Scene.SceneNode> lodRows =
                    meshRow.Children.Where(c => c.Kind == "Lod").ToList();
                Check("one row per level, named in order",
                    lodRows.Count >= 2 && lodRows[0].Name == "LOD 0" && lodRows[1].Name == "LOD 1",
                    string.Join(", ", lodRows.Select(r => r.Name)));
                Check("LOD 0 is the one that is shown", lodRows[0].IsVisible);
                Check("the coarser levels start hidden", lodRows.Skip(1).All(r => !r.IsVisible));
                Check("the mesh row opens on its levels", meshRow.IsExpanded);
                Check("each row carries its own level and the frame's source",
                    lodRows.Select((r, i) => r.Lod == i && ReferenceEquals(r.Source, meshRow.Source)).All(x => x));
                Check("every level was queued for upload",
                    lodRows.All(r => r.Pending != null && leaves.Contains(r)));
                // A single-level mesh must NOT grow rows — a district is 98.8 % of those, and one extra row
                // each would double the tree for nothing.
                int strays = 0;
                foreach (Illusion.Scene.SceneNode r in built) strays += CountStrayLodRows(r);
                Check("single-level meshes grew none", strays == 0, $"{strays} rows on single-level meshes");
            }

            IFrameNode? node = null;
            foreach (SdsFrameNode r in roots) node ??= FindMultiLodNode(r);
            Check($"{focus} has a mesh with more than one level", node != null);
            if (node is not FrameNodeAdapter { Frame: FrameObjectSingleMesh frame }) return;

            int levels = frame.Geometry.LOD.Length;
            sb.AppendLine($"  {frame.Name}: {levels} levels, distances "
                + string.Join(", ", frame.Geometry.LOD.Select(l => l.Distance.ToString("F0",
                    System.Globalization.CultureInfo.InvariantCulture))));

            DecodedMesh? fine = SdsMeshLoader.DecodeLod(frame, 0);
            DecodedMesh? coarse = SdsMeshLoader.DecodeLod(frame, 1);
            Check("both levels decode", fine != null && coarse != null);
            if (fine == null || coarse == null) return;

            Check("the coarse level is its own, smaller mesh",
                coarse.NumVerts < fine.NumVerts && coarse.Lod == 1,
                $"{fine.NumVerts} → {coarse.NumVerts} vertices");
            Check("each level carries its own material slots",
                frame.Material.Materials.Count >= 2,
                $"{string.Join("/", frame.Material.Materials.Select(m => m.Length))} slots");
            Check("the levels are packed against ONE quantization",
                fine.DecompressionOffset == coarse.DecompressionOffset
                && fine.DecompressionFactor.Equals(coarse.DecompressionFactor));

            // What the fine level looks like before anything is pushed — bytes and float positions both,
            // since a re-quantization legitimately rewrites the bytes while keeping the positions.
            byte[] fineBefore = (byte[])fine.RawVertexData.Clone();
            Vector3[] finePositionsBefore = (Vector3[])fine.Positions.Clone();
            Formats.Mathematics.BoundingBox boundsBefore = frame.Boundings;

            // Export the COARSE level and push it back with every vertex nudged — a real edit of LOD1.
            MeshObjectPayload? payload = BridgeMeshExporter.TryExport(node, document, out string? reason, 1);
            Check("the coarse level rides the bridge", payload != null, reason ?? "");
            if (payload == null) return;
            Check("Blender is handed the coarse geometry, not LOD0's",
                payload.Positions.Length <= coarse.NumVerts,
                $"{payload.Positions.Length} welded vertices vs {fine.NumVerts} in LOD0");

            var delta = new Vector3(0f, 0f, payload.DecompressionFactor * 8f);
            for (int v = 0; v < payload.Positions.Length; v++) payload.Positions[v] += delta;

            BridgeMeshApplier.ApplyResult? result =
                BridgeMeshApplier.TryApply(node, payload, out string? applyReason, 1);
            Check("the push applies", result is { Unchanged: false },
                result == null ? applyReason ?? "refused" : $"{result.TouchedVertices} vertices touched");
            if (result is not { Unchanged: false }) return;
            Check("it is booked against level 1", result.Lod == 1, result.Lod.ToString());

            result.ApplyNew();

            DecodedMesh? coarseAfter = SdsMeshLoader.DecodeLod(frame, 1);
            DecodedMesh? fineAfter = SdsMeshLoader.DecodeLod(frame, 0);
            Check("both levels still decode after the push", coarseAfter != null && fineAfter != null);
            if (coarseAfter == null || fineAfter == null) return;

            Check("the coarse level moved",
                Moved(coarse.Positions, coarseAfter.Positions, delta, payload.DecompressionFactor),
                $"{result.TouchedVertices} vertices re-encoded");

            // The whole point: LOD0 must be exactly where it was. Its BYTES may legitimately change when the
            // lattice moved — then the positions are what has to hold, within one quantum.
            if (result.Requantized)
            {
                Check("re-quantization re-packed the untouched level",
                    result.RepackedLodCount > 0, "nothing was re-packed");
                Check("LOD0 kept its positions through the new lattice",
                    Same(finePositionsBefore, fineAfter.Positions, fineAfter.DecompressionFactor * 1.5f),
                    "the fine level shifted");
            }
            else
            {
                Check("LOD0's bytes are untouched (the lattice held)",
                    fineAfter.RawVertexData.AsSpan().SequenceEqual(fineBefore));
            }

            Check("the frame's bounds still cover LOD0",
                Covers(frame.Boundings, finePositionsBefore),
                $"{boundsBefore.Min} .. {boundsBefore.Max} → {frame.Boundings.Min} .. {frame.Boundings.Max}");

            if (frame is FrameObjectModel model)
            {
                // Each level names bones through its OWN remap pools; a push that rewrote the wrong level's
                // pools shows up here as a level that can no longer say which bone owns a vertex.
                Check("both levels still resolve their skin",
                    SdsMeshLoader.GlobalBoneIds(model, 0) != null
                    && SdsMeshLoader.GlobalBoneIds(model, 1) != null);
            }

            // Undo has to put BOTH levels back — the re-packed one included.
            result.RestoreOriginal();
            DecodedMesh? undone = SdsMeshLoader.DecodeLod(frame, 0);
            DecodedMesh? undoneCoarse = SdsMeshLoader.DecodeLod(frame, 1);
            Check("undo restores LOD0 byte for byte",
                undone != null && undone.RawVertexData.AsSpan().SequenceEqual(fineBefore));
            Check("undo restores the edited level",
                undoneCoarse != null
                && undoneCoarse.RawVertexData.AsSpan().SequenceEqual(coarse.RawVertexData));

            // ── The dangerous half: an edit that outgrows the shared lattice ──
            //
            // The offset and factor belong to the geometry block, so re-deriving them for LOD1 changes how
            // LOD0's untouched integers decode. Unless the fine level is re-packed with the coarse one, the
            // body silently moves and rescales — this is the case that has to be exercised, not reasoned about.
            sb.AppendLine();
            sb.AppendLine("  — pushing far enough to move the quantization lattice —");
            MeshObjectPayload? far = BridgeMeshExporter.TryExport(node, document, out _, 1);
            if (far == null) { Check("the coarse level exports again", false); return; }

            var bigDelta = new Vector3(0f, 0f, 6f); // metres — well past the body's own AABB
            for (int v = 0; v < far.Positions.Length; v++) far.Positions[v] += bigDelta;

            BridgeMeshApplier.ApplyResult? moved =
                BridgeMeshApplier.TryApply(node, far, out string? farReason, 1);
            Check("the far push applies", moved is { Unchanged: false },
                moved == null ? farReason ?? "refused" : $"{moved.TouchedVertices} vertices touched");
            if (moved is not { Unchanged: false }) return;

            Check("it had to re-derive the lattice", moved.Requantized,
                moved.Requantized ? "" : "the delta fitted the old lattice — the re-pack path went untested");
            Check("the untouched level was re-packed with it", moved.RepackedLodCount > 0,
                $"{moved.RepackedLodCount} level(s)");

            moved.ApplyNew();
            DecodedMesh? fineMoved = SdsMeshLoader.DecodeLod(frame, 0);
            Check("LOD0 decodes against the new lattice", fineMoved != null);
            Check("and it is still exactly where it was",
                fineMoved != null
                && Same(finePositionsBefore, fineMoved.Positions, moved.NewDecompressionFactor * 1.5f),
                fineMoved == null ? "" : Drift(finePositionsBefore, fineMoved.Positions));
            Check("the bounds cover the fine level at the new lattice",
                fineMoved != null && Covers(frame.Boundings, fineMoved.Positions));

            moved.RestoreOriginal();
            DecodedMesh? fineRestored = SdsMeshLoader.DecodeLod(frame, 0);
            Check("undo restores LOD0 through the lattice change",
                fineRestored != null && fineRestored.RawVertexData.AsSpan().SequenceEqual(fineBefore));

            // ── The other dangerous half: a push that changes TOPOLOGY takes the rebuild path, which swaps
            // the whole level — its LOD block, its material ranges AND its index buffer. Each level names its
            // own buffer, so a rebuild that writes into LOD0's leaves the two levels cross-wired: LOD0 keeps
            // its vertex count but points at the coarse mesh's indices, and the body draws mangled.
            sb.AppendLine();
            sb.AppendLine("  — a topology change on the coarse level —");
            uint[] fineIndicesBefore = (uint[])fine.Indices.Clone();
            uint[] coarseIndicesBefore = (uint[])coarse.Indices.Clone();

            MeshObjectPayload? cut = BridgeMeshExporter.TryExport(node, document, out _, 1);
            if (cut == null || cut.LoopOrigIndex.Length < 6) { Check("the coarse level exports again", false); return; }
            int loops = cut.LoopOrigIndex.Length - 3;   // drop one triangle
            cut.LoopVertexIndices = cut.LoopVertexIndices.AsSpan(0, loops).ToArray();
            cut.LoopNormals = cut.LoopNormals.AsSpan(0, loops).ToArray();
            cut.LoopUvs = cut.LoopUvs.AsSpan(0, loops).ToArray();
            cut.LoopOrigIndex = cut.LoopOrigIndex.AsSpan(0, loops).ToArray();
            cut.FaceMaterials = cut.FaceMaterials.AsSpan(0, loops / 3).ToArray();

            BridgeMeshApplier.ApplyResult? rebuilt =
                BridgeMeshApplier.TryApply(node, cut, out string? cutReason, 1);
            Check("deleting a face on LOD1 rebuilds it", rebuilt is { TopologyRebuilt: true },
                cutReason ?? "not rebuilt");
            if (rebuilt is not { TopologyRebuilt: true }) return;
            Check("the rebuild is booked against level 1", rebuilt.Lod == 1, rebuilt.Lod.ToString());

            rebuilt.ApplyNew();
            DecodedMesh? fineAfterCut = SdsMeshLoader.DecodeLod(frame, 0);
            DecodedMesh? coarseAfterCut = SdsMeshLoader.DecodeLod(frame, 1);
            Check("both levels still decode after the rebuild", fineAfterCut != null && coarseAfterCut != null);
            if (fineAfterCut == null || coarseAfterCut == null) return;

            Check("the coarse level's index buffer took the change",
                coarseAfterCut.Indices.Length == coarseIndicesBefore.Length - 3,
                $"{coarseIndicesBefore.Length} → {coarseAfterCut.Indices.Length}");
            Check("LOD0's index buffer was NOT touched",
                fineAfterCut.Indices.AsSpan().SequenceEqual(fineIndicesBefore),
                $"{fineIndicesBefore.Length} → {fineAfterCut.Indices.Length} indices");
            Check("every level's indices still address its own vertices",
                MaxIndex(fineAfterCut.Indices) < fineAfterCut.NumVerts
                && MaxIndex(coarseAfterCut.Indices) < coarseAfterCut.NumVerts,
                $"LOD0 max {MaxIndex(fineAfterCut.Indices)} of {fineAfterCut.NumVerts}, "
                + $"LOD1 max {MaxIndex(coarseAfterCut.Indices)} of {coarseAfterCut.NumVerts}");
            Check("every level's material ranges fit its index buffer",
                RangesFit(frame, 0, fineAfterCut.Indices.Length) && RangesFit(frame, 1, coarseAfterCut.Indices.Length));

            rebuilt.RestoreOriginal();
            DecodedMesh? fineFinal = SdsMeshLoader.DecodeLod(frame, 0);
            Check("undo puts both index buffers back",
                fineFinal != null && fineFinal.Indices.AsSpan().SequenceEqual(fineIndicesBefore)
                && SdsMeshLoader.DecodeLod(frame, 1) is { } coarseFinal
                && coarseFinal.Indices.AsSpan().SequenceEqual(coarseIndicesBefore));
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            fail++;
        }
        finally
        {
            sb.Insert(0, $"LOD editing — {focus}\n{pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
            Console.WriteLine(sb.ToString());
            Console.WriteLine("→ " + outFile);
        }
    }

    private static int MaxIndex(uint[] indices)
    {
        int max = -1;
        foreach (uint i in indices) max = Math.Max(max, (int)i);
        return max;
    }

    // Does the level's material table address only indices the level's buffer holds?
    private static bool RangesFit(FrameObjectSingleMesh mesh, int lod, int indexCount)
    {
        if (mesh.Material?.Materials is not { } all || lod >= all.Count) return true;
        foreach (Formats.Frames.Resources.MaterialStruct m in all[lod])
        {
            if (m.StartIndex + (m.NumFaces * 3) > indexCount) return false;
        }
        return true;
    }

    // The first tree row that grew LOD children.
    private static Illusion.Scene.SceneNode? FindLodParent(Illusion.Scene.SceneNode node)
    {
        if (node.Children.Any(c => c.Kind == "Lod")) return node;
        foreach (Illusion.Scene.SceneNode child in node.Children)
        {
            if (FindLodParent(child) is { } found) return found;
        }
        return null;
    }

    // LOD rows hanging under a row that has geometry of its own — the shape a single-level mesh must never take.
    private static int CountStrayLodRows(Illusion.Scene.SceneNode node)
    {
        int strays = node.Pending != null && node.Children.Any(c => c.Kind == "Lod") ? 1 : 0;
        foreach (Illusion.Scene.SceneNode child in node.Children) strays += CountStrayLodRows(child);
        return strays;
    }

    // The first mesh in the tree that ships more than one level — a car body, in practice.
    private static IFrameNode? FindMultiLodNode(SdsFrameNode node)
    {
        if (node.Source is FrameNodeAdapter { Frame: FrameObjectSingleMesh mesh } adapter
            && mesh.Geometry?.LOD is { Length: > 1 })
        {
            return adapter;
        }
        foreach (SdsFrameNode child in node.Children)
        {
            if (FindMultiLodNode(child) is { } found) return found;
        }
        return null;
    }

    // Every vertex moved by (about) the delta it was pushed by.
    private static bool Moved(Vector3[] before, Vector3[] after, Vector3 delta, float quantum)
    {
        if (before.Length != after.Length || before.Length == 0) return false;
        float tolerance = quantum * 2f;
        for (int i = 0; i < before.Length; i++)
        {
            if ((after[i] - before[i] - delta).Length() > tolerance) return false;
        }
        return true;
    }

    // The worst per-vertex displacement, so a failure says HOW far the level moved rather than only that it did.
    private static string Drift(Vector3[] before, Vector3[] after)
    {
        if (before.Length != after.Length) return $"{before.Length} vs {after.Length} vertices";
        float worst = 0f;
        for (int i = 0; i < before.Length; i++) worst = MathF.Max(worst, (after[i] - before[i]).Length());
        return $"worst drift {worst:F5} m";
    }

    private static bool Same(Vector3[] before, Vector3[] after, float tolerance)
    {
        if (before.Length != after.Length) return false;
        for (int i = 0; i < before.Length; i++)
        {
            if ((after[i] - before[i]).Length() > tolerance) return false;
        }
        return true;
    }

    private static bool Covers(Formats.Mathematics.BoundingBox box, Vector3[] positions)
    {
        const float slack = 1e-3f;
        foreach (Vector3 p in positions)
        {
            if (p.X < box.Min.X - slack || p.Y < box.Min.Y - slack || p.Z < box.Min.Z - slack
                || p.X > box.Max.X + slack || p.Y > box.Max.Y + slack || p.Z > box.Max.Z + slack)
            {
                return false;
            }
        }
        return true;
    }
}
