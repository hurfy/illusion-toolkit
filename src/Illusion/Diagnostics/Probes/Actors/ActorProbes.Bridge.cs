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

            (_, _, ISceneDocument? loaded) = SdsMeshLoader.LoadHierarchy(new FileInfo(sds));
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
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"BRIDGE ACTOR PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
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
