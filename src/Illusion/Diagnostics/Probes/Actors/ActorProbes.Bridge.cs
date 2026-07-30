using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Sending an actor's prototype geometry to Blender. Part of <see cref="ActorProbes"/> — one file per
/// area of the actor layer.</summary>
internal static partial class ActorProbes
{
    /// <summary>
    /// The bridge against actor-placed geometry, without Blender in the loop.
    ///
    /// A prototype is parked at the origin and the .act carries its spawn matrix, so the two things that can go
    /// wrong are both about WHERE: the object arriving in Blender at (0,0,0) while the viewport shows it in the
    /// street, and — the damaging one — an untouched push reading as "moved" and baking the inverse placement
    /// into the prototype's own transform. Both are decided by which world matrix the export sends, and both are
    /// checked here against the very comparisons the push path makes.
    /// Output: %TEMP%\illusion_bridge_actor.txt
    /// </summary>
    internal static void RunBridgeActorProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_actor.txt");
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
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            (List<SdsFrameNode> roots, _, ISceneDocument? loaded) = SdsMeshLoader.LoadHierarchy(new FileInfo(sds));
            if (loaded is not SceneDocumentAdapter document)
            {
                sb.AppendLine("the district did not load");
                return;
            }
            ActorPlacements placements = document.Placements;
            sb.AppendLine($"BRIDGE ACTOR PROBE — district={district}, actors={placements.All.Count}\n");

            // ── Census: how much of what actors place can ride the bridge at all ──
            //
            // The plan's hypothesis was that actor prototypes are exact FrameObjectSingleMesh and not instanced,
            // so the existing exporter should take them. Measure it rather than assume it.
            int withGeometry = 0, meshes = 0, exportable = 0;
            var refusals = new SortedDictionary<string, int>();
            (ActorEntry Actor, FrameObjectSingleMesh Frame)? sample = null;
            foreach (ActorEntry actor in placements.All)
            {
                if (placements.TargetOf(actor) is not { } target) continue;
                var found = new List<FrameObjectSingleMesh>();
                CollectMeshes(target, found, new HashSet<FrameObjectBase>());
                if (found.Count == 0) continue;
                withGeometry++;
                meshes += found.Count;

                foreach (FrameObjectSingleMesh frame in found)
                {
                    IFrameNode node = document.Node(frame);
                    if (BridgeMeshExporter.TryExport(node, document, out string? reason) != null)
                    {
                        exportable++;
                        // Prefer a sample the placement actually MOVES: an actor sitting at the origin proves
                        // nothing about whether the placement made it into the payload.
                        if (sample == null && actor.Position.LengthSquared() > 1f) sample = (actor, frame);
                    }
                    else
                    {
                        refusals.TryGetValue(reason ?? "?", out int seen);
                        refusals[reason ?? "?"] = seen + 1;
                    }
                }
            }

            Check("actors place geometry the mesh exporter accepts", exportable > 0,
                $"{exportable}/{meshes} meshes across {withGeometry} actors with geometry");
            foreach ((string reason, int count) in refusals) sb.AppendLine($"    refused ×{count}: {reason}");

            if (sample is not var (sampleActor, sampleFrame) || sample == null)
            {
                Check("a placed prototype away from the origin was found", false, "none in this district");
                return;
            }

            // ── The one thing that decides both failure modes: which world the payload carries ──
            IFrameNode fn = document.Node(sampleFrame);
            MeshObjectPayload payload = BridgeMeshExporter.TryExport(fn, document, out _)!;

            sb.AppendLine();
            sb.AppendLine($"sample: actor '{sampleActor.EntityName}' ({sampleActor.Type}) at {sampleActor.Position}");
            sb.AppendLine($"    prototype '{sampleFrame.Name}' frame world  = {sampleFrame.WorldTransform.Translation}");
            sb.AppendLine($"    node (placement-aware) world = {fn.WorldTransform.Translation}");
            sb.AppendLine($"    payload world                = {payload.World.Translation}");

            Check("the payload carries the PLACED world, not the prototype's own",
                MatrixNear(payload.World, fn.WorldTransform)
                && !MatrixNear(payload.World, sampleFrame.WorldTransform),
                $"{payload.World.Translation} vs frame {sampleFrame.WorldTransform.Translation}");

            // This is the comparison the push path makes to decide whether the object MOVED in Blender. For an
            // untouched export it must say "no" — otherwise every prototype that comes back gets a transform
            // edit it never earned.
            Check("an untouched export reads as not moved", MatrixNear(payload.World, fn.WorldTransform),
                "the push path compares payload.World against the node's world");

            // ...and this is what it would write if it did. Re-localizing the exported world against the node's
            // parent world has to reproduce the prototype's own local transform exactly — that is what keeps the
            // actor's placement out of the frame.
            Matrix4x4 relocalized = TransformMath.ComputeLocalTransform(payload.World, fn.ParentWorldTransform);
            Check("re-localizing the exported world reproduces the prototype's local transform",
                MatrixNear(relocalized, fn.LocalTransform),
                $"{relocalized.Translation} vs {fn.LocalTransform.Translation}");

            // The geometry itself must stay prototype-local: Blender gets object-space vertices plus the object
            // matrix, so a placement leaking into the positions would double-apply it.
            Vector3 centroid = Vector3.Zero;
            foreach (Vector3 p in payload.Positions) centroid += p;
            centroid /= Math.Max(1, payload.Positions.Length);
            Check("the vertices stay in prototype space", centroid.Length() < sampleActor.Position.Length() * 0.5f + 10f,
                $"centroid {centroid} while the actor stands at {sampleActor.Position}");

            // ── Container round trip: what actually reaches Blender ──
            var container = new ExchangeContainer
            {
                Session = "probe",
                Producer = "toolkit",
            };
            MeshPayloadCodec.Add(container, payload);
            string file = Path.Combine(Path.GetTempPath(), "illusion_probe_actor.ilx");
            ExchangeWriter.Write(file, container);
            ExchangeContainer back = ExchangeReader.Read(file);
            MeshObjectPayload reread = MeshPayloadCodec.Read(back, back.Objects[0]);

            Check("the payload survives the .ilx round trip",
                MatrixNear(reread.World, payload.World)
                && reread.Positions.Length == payload.Positions.Length
                && reread.LoopVertexIndices.Length == payload.LoopVertexIndices.Length,
                $"{reread.Positions.Length} vertices, {reread.FaceMaterials.Length} faces");

            // And the same object, coming home unedited, must apply as nothing.
            BridgeMeshApplier.ApplyResult? result = BridgeMeshApplier.TryApply(fn, reread, out string? applyReason);
            Check("pushing it back unedited changes nothing", result != null && result.Unchanged,
                result == null ? applyReason ?? "refused" : $"{result.TouchedVertices} vertices touched");

            CheckSelectionAgreement(roots, placements, sb, Check);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"BRIDGE ACTOR PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    /// <summary>
    /// How much of a district's geometry is SHARED between frame objects — the thing that decides whether
    /// editing one object edits one object.
    ///
    /// A frame does not own its mesh: it references a geometry block, and that block's LOD references a vertex
    /// and an index buffer BY HASH. Nothing stops two frames from naming the same block, or two blocks from
    /// naming the same buffer. When they do, an edit is global: the toolkit swaps only the edited node's GPU
    /// mesh, so the viewport shows one object changed — but the file has one buffer, so the game shows every
    /// frame that references it changed. "Different in the editor, identical in the game" is exactly that.
    /// Output: %TEMP%\illusion_mesh_sharing.txt
    /// </summary>
    internal static void RunMeshSharingProbe(string district, string? nameFilter = null)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_mesh_sharing.txt");
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
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            Formats.Frames.FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            if (fr?.FrameObjects == null) { sb.AppendLine("the district carries no frame objects"); return; }

            var byGeometry = new Dictionary<int, List<string>>();
            var byVertexBuffer = new Dictionary<ulong, List<string>>();
            int meshes = 0;
            foreach (object? value in fr.FrameObjects.Values)
            {
                if (value is not FrameObjectSingleMesh mesh || mesh.Geometry is not { LOD.Length: > 0 } geometry)
                {
                    continue;
                }
                meshes++;
                string name = mesh.Name?.ToString() ?? "?";

                if (!byGeometry.TryGetValue(geometry.RefID, out List<string>? sameBlock))
                {
                    byGeometry[geometry.RefID] = sameBlock = [];
                }
                sameBlock.Add(name);

                ulong hash = geometry.LOD[0].VertexBufferRef.Hash;
                if (!byVertexBuffer.TryGetValue(hash, out List<string>? sameBuffer))
                {
                    byVertexBuffer[hash] = sameBuffer = [];
                }
                sameBuffer.Add(name);
            }

            int sharedBlocks = byGeometry.Values.Count(g => g.Count > 1);
            int framesOnSharedBlock = byGeometry.Values.Where(g => g.Count > 1).Sum(g => g.Count);
            int sharedBuffers = byVertexBuffer.Values.Count(g => g.Count > 1);
            int framesOnSharedBuffer = byVertexBuffer.Values.Where(g => g.Count > 1).Sum(g => g.Count);

            sb.AppendLine($"MESH SHARING PROBE — district={district}\n");
            Check("the district has meshes", meshes > 0, $"{meshes} single-mesh frames");

            // Sharing is how the game is BUILT, not a defect — so this reports rather than asserts. What is
            // checked is that the census is sound: every mesh lands in exactly one group of each kind, and a
            // group never has fewer members than one.
            Check("the census accounts for every mesh",
                byGeometry.Values.Sum(g => g.Count) == meshes
                && byVertexBuffer.Values.Sum(g => g.Count) == meshes
                && byGeometry.Count <= meshes && byVertexBuffer.Count <= meshes,
                $"{byGeometry.Count} geometry blocks and {byVertexBuffer.Count} LOD0 vertex buffers for {meshes} meshes");
            sb.AppendLine($"       SHARED: {framesOnSharedBuffer}/{meshes} frames sit on a LOD0 vertex buffer "
                + $"another frame also uses ({sharedBuffers} such buffers); {framesOnSharedBlock} share a whole "
                + $"geometry block ({sharedBlocks} such blocks). Editing any of those edits all of them.");

            sb.AppendLine();
            sb.AppendLine($"geometry blocks: {byGeometry.Count} for {meshes} meshes");
            sb.AppendLine($"LOD0 vertex buffers: {byVertexBuffer.Count} for {meshes} meshes");
            sb.AppendLine();
            sb.AppendLine("largest groups sharing one LOD0 vertex buffer:");
            foreach ((ulong hash, List<string> names) in byVertexBuffer
                         .Where(p => p.Value.Count > 1)
                         .OrderByDescending(p => p.Value.Count)
                         .Take(15))
            {
                sb.AppendLine($"    {hash:X16} ×{names.Count}: {string.Join(", ", names.Take(8))}"
                              + (names.Count > 8 ? ", …" : ""));
            }

            if (!string.IsNullOrEmpty(nameFilter))
            {
                sb.AppendLine();
                sb.AppendLine($"groups holding a frame whose name contains '{nameFilter}':");
                int shown = 0;
                foreach ((ulong hash, List<string> names) in byVertexBuffer.OrderByDescending(p => p.Value.Count))
                {
                    if (!names.Any(n => n.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))) continue;
                    shown++;
                    sb.AppendLine($"    {hash:X16} ×{names.Count}: {string.Join(", ", names.Take(12))}"
                                  + (names.Count > 12 ? ", …" : ""));
                }
                if (shown == 0) sb.AppendLine("    (no frame of this district matches)");
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"MESH SHARING PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    /// <summary>
    /// Selecting the ACTOR and selecting its FRAME in the tree have to reach the same meshes.
    ///
    /// They are found two different ways. The tree is built from the loader's hierarchy; the actor path walks
    /// <c>FrameObjectBase.Children</c> and looks each mesh up in the row map. If those two disagree — a mesh the
    /// tree hangs under the prototype that the frame graph does not, or the other way round — then Tab on an
    /// actor edits a different set of objects than Tab on its frame, and the one the game reads can be the one
    /// that was left alone.
    /// </summary>
    private static void CheckSelectionAgreement(List<SdsFrameNode> roots, ActorPlacements placements,
        StringBuilder sb, Action<string, bool, string> check)
    {
        // Frame → its node in the loader's hierarchy (what the scene tree is built from, one row per frame).
        var rowOf = new Dictionary<FrameObjectBase, SdsFrameNode>();
        foreach (SdsFrameNode root in roots) IndexRows(root, rowOf);

        int compared = 0, disagreed = 0, missingRow = 0;
        var examples = new List<string>();
        foreach (ActorEntry actor in placements.All)
        {
            if (placements.TargetOf(actor) is not { } target) continue;
            if (!rowOf.TryGetValue(target, out SdsFrameNode? row)) { missingRow++; continue; }

            // What selecting the actor reaches: the frame graph walked by ParentIndex1 children.
            var viaActor = new HashSet<FrameObjectBase>();
            CollectMeshFrames(target, viaActor, new HashSet<FrameObjectBase>());
            // What selecting the frame row reaches: the tree's own descendants.
            var viaTree = new HashSet<FrameObjectBase>();
            CollectRowMeshes(row, viaTree);
            if (viaActor.Count == 0 && viaTree.Count == 0) continue;

            compared++;
            if (viaActor.SetEquals(viaTree)) continue;
            disagreed++;
            if (examples.Count < 6)
            {
                IEnumerable<string> onlyTree = viaTree.Except(viaActor).Select(f => f.Name?.ToString() ?? "?");
                IEnumerable<string> onlyActor = viaActor.Except(viaTree).Select(f => f.Name?.ToString() ?? "?");
                examples.Add($"{actor.EntityName} → '{target.Name}': tree {viaTree.Count} vs actor {viaActor.Count}"
                    + $" | only in tree: {string.Join(", ", onlyTree.Take(4))}"
                    + $" | only via actor: {string.Join(", ", onlyActor.Take(4))}");
            }
        }

        check("selecting the actor reaches the same meshes as selecting its frame", disagreed == 0,
            $"{compared} prototypes compared, {disagreed} disagree, {missingRow} target(s) have no row");
        foreach (string line in examples) sb.AppendLine("    " + line);
    }

    private static void IndexRows(SdsFrameNode node, Dictionary<FrameObjectBase, SdsFrameNode> map)
    {
        if (node.Source is FrameNodeAdapter fna) map[fna.Frame] = node;
        foreach (SdsFrameNode child in node.Children) IndexRows(child, map);
    }

    private static void CollectRowMeshes(SdsFrameNode node, HashSet<FrameObjectBase> found)
    {
        if (node.Mesh != null && node.Source is FrameNodeAdapter fna) found.Add(fna.Frame);
        foreach (SdsFrameNode child in node.Children) CollectRowMeshes(child, found);
    }

    private static void CollectMeshFrames(FrameObjectBase frame, HashSet<FrameObjectBase> found,
        HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return;
        if (frame is FrameObjectSingleMesh { Geometry: not null }) found.Add(frame);
        foreach (FrameObjectBase child in frame.Children) CollectMeshFrames(child, found, seen);
    }

    // The mesh leaves under a prototype holder, guarded against the cycles a malformed hierarchy can carry.
    private static void CollectMeshes(FrameObjectBase frame, List<FrameObjectSingleMesh> found,
        HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return;
        if (frame is FrameObjectSingleMesh mesh && mesh.GetType() == typeof(FrameObjectSingleMesh))
        {
            found.Add(mesh);
        }
        foreach (FrameObjectBase child in frame.Children) CollectMeshes(child, found, seen);
    }

    private static bool MatrixNear(Matrix4x4 a, Matrix4x4 b)
    {
        const float eps = 1e-3f;
        return MathF.Abs(a.M11 - b.M11) < eps && MathF.Abs(a.M12 - b.M12) < eps && MathF.Abs(a.M13 - b.M13) < eps
            && MathF.Abs(a.M21 - b.M21) < eps && MathF.Abs(a.M22 - b.M22) < eps && MathF.Abs(a.M23 - b.M23) < eps
            && MathF.Abs(a.M31 - b.M31) < eps && MathF.Abs(a.M32 - b.M32) < eps && MathF.Abs(a.M33 - b.M33) < eps
            && MathF.Abs(a.M41 - b.M41) < eps && MathF.Abs(a.M42 - b.M42) < eps && MathF.Abs(a.M43 - b.M43) < eps;
    }
}
