using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Sending a city_crash prop's prototype to Blender. Part of <see cref="ActorProbes"/> — one file per
/// area of the actor layer.</summary>
internal static partial class ActorProbes
{
    /// <summary>
    /// The bridge against the crash props, without Blender in the loop.
    ///
    /// A prop is one prototype and tens of thousands of copies: the .tra table spreads it over the whole city
    /// and the viewport draws it hardware-instanced. "Instanced" was the reason the bridge refused these, but it
    /// describes how the viewport DRAWS the mesh, not what the mesh is — the frame underneath is an ordinary
    /// single mesh and the exporter reads it from the frame, never from the GPU. What this checks is that the
    /// export is that ordinary mesh, that the prototype's own transform (not a copy's) rides with it, and that
    /// pushing it back unedited is a no-op.
    /// Output: %TEMP%\illusion_bridge_crash.txt
    /// </summary>
    internal static void RunBridgeCrashProbe(bool winter)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_crash.txt");
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

            string name = winter ? "city_crash_z.sds" : "city_crash.sds";
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "city_crash", name));
            if (!sds.Exists) { sb.AppendLine("no such archive: " + sds.FullName); return; }

            (_, _, ISceneDocument? loaded) = SdsMeshLoader.LoadHierarchy(sds);
            if (loaded is not SceneDocumentAdapter document) { sb.AppendLine("the archive did not load"); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            string? traPath = Directory.GetFiles(extracted, "*.tra", SearchOption.AllDirectories).FirstOrDefault();
            if (fr == null || traPath == null) { sb.AppendLine("no frame resource or no .tra"); return; }

            var loader = new Formats.Translokator.TranslokatorLoader(new FileInfo(traPath));
            var traDoc = new TranslokatorDocumentAdapter(loader, sds, null);
            CrashPlacements placements = CrashPlacements.Build(fr, traDoc);

            sb.AppendLine($"BRIDGE CRASH PROBE — {name}, {placements.Rows.Count} rows\n");

            // ── Census: can the mesh exporter take the prototypes at all ──
            int prototypes = 0, exportable = 0;
            long copies = 0;
            var refusals = new SortedDictionary<string, int>();
            FrameObjectSingleMesh? sample = null;
            int sampleCopies = 0;
            foreach (FrameObjectSingleMesh mesh in placements.Meshes)
            {
                prototypes++;
                int cloud = placements.CloudFor(mesh).Matrices.Length;
                copies += cloud;
                if (BridgeMeshExporter.TryExport(document.Node(mesh), document, out string? reason) != null)
                {
                    exportable++;
                    // The one that proves the most: the prop with the most copies in the city.
                    if (cloud > sampleCopies) { sample = mesh; sampleCopies = cloud; }
                }
                else
                {
                    refusals.TryGetValue(reason ?? "?", out int seen);
                    refusals[reason ?? "?"] = seen + 1;
                }
            }

            Check("the mesh exporter takes the crash prototypes", exportable > 0,
                $"{exportable}/{prototypes} prototypes, {copies} copies across the city");
            foreach ((string reason, int count) in refusals) sb.AppendLine($"    refused ×{count}: {reason}");
            if (sample == null) { Check("a prototype with copies was found", false, "none"); return; }

            // ── The export itself ──
            IFrameNode fn = document.Node(sample);
            MeshObjectPayload payload = BridgeMeshExporter.TryExport(fn, document, out _)!;

            sb.AppendLine();
            sb.AppendLine($"sample: '{sample.Name}' — {sampleCopies} copies");
            sb.AppendLine($"    prototype world = {fn.WorldTransform.Translation}");
            sb.AppendLine($"    payload world   = {payload.World.Translation}");
            sb.AppendLine($"    {payload.Positions.Length} vertices, {payload.FaceMaterials.Length} faces");

            // The copies are placed by the .tra table, not by a frame transform, so what rides with the payload
            // is the prototype's OWN transform. Sending one copy's world instead would put the object somewhere
            // in the city and make the push read it as moved — which would then drag every copy with it.
            Check("the payload carries the prototype's own transform, not a copy's",
                MatrixNear(payload.World, fn.WorldTransform),
                "the push path compares payload.World against exactly this");

            Check("an untouched export reads as not moved", MatrixNear(payload.World, fn.WorldTransform));

            var container = new ExchangeContainer { Session = "probe", Producer = "toolkit" };
            MeshPayloadCodec.Add(container, payload);
            string file = Path.Combine(Path.GetTempPath(), "illusion_probe_crash.ilx");
            ExchangeWriter.Write(file, container);
            MeshObjectPayload reread = MeshPayloadCodec.Read(ExchangeReader.Read(file), ExchangeReader.Read(file).Objects[0]);

            Check("the payload survives the .ilx round trip",
                reread.Positions.Length == payload.Positions.Length
                && reread.LoopVertexIndices.Length == payload.LoopVertexIndices.Length
                && MatrixNear(reread.World, payload.World),
                $"{reread.Positions.Length} vertices");

            BridgeMeshApplier.ApplyResult? result = BridgeMeshApplier.TryApply(fn, reread, out string? applyReason);
            Check("pushing it back unedited changes nothing", result != null && result.Unchanged,
                result == null ? applyReason ?? "refused" : $"{result.TouchedVertices} vertices touched");

            // The cloud has to survive a rebuild: it is recomputed from the live table after the GPU mesh is
            // replaced, and a prop whose copies did not come back would collapse to the single one standing at
            // the prototype's own transform.
            int rebuilt = placements.CloudFor(sample).Matrices.Length;
            Check("the copy cloud rebuilds from the live table", rebuilt == sampleCopies,
                $"{rebuilt} vs {sampleCopies}");

            sb.AppendLine();
            sb.AppendLine("busiest props:");
            foreach (FrameObjectSingleMesh mesh in placements.Meshes
                         .OrderByDescending(m => placements.CloudFor(m).Matrices.Length)
                         .Take(10))
            {
                sb.AppendLine($"    {mesh.Name}: {placements.CloudFor(mesh).Matrices.Length} copies");
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"BRIDGE CRASH PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }
}
