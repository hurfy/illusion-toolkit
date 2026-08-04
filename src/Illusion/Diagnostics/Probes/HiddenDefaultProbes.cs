using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What a car archive holds besides the car, and what the scene opens with hidden.
///
/// <para>
/// A car's archive is not one object. Alongside the body there are particle-emitter shells — the volume rain
/// is spawned in, the one over the bonnet — which are full meshes in the frame tree and are drawn like any
/// other, so opening a car shows the car inside a couple of translucent boxes. The game never draws them.
/// This probe surveys what is really in there across the shipped cars, then checks the default the scene
/// opens with: the body visible, the shells not.
/// </para>
/// </summary>
internal static class HiddenDefaultProbes
{
    /// <summary>Districts read for the accidental-match survey. A district is a slow archive and this is a
    /// sanity sweep, not a census — the report says so rather than reading as full coverage.</summary>
    private const int DistrictSample = 10;

    internal static void RunHiddenDefaultsProbe(string car)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_hidden_defaults.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string carsFolder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            string sds = Path.Combine(carsFolder, car + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such car: " + sds); return; }

            // ---- 1. One car, drawn out ---------------------------------------------------------------
            (IReadOnlyList<SdsFrameNode> roots, _, _) = SdsMeshLoader.LoadHierarchy(new FileInfo(sds));
            var leaves = new List<SceneNode>();
            var built = new List<SceneNode>();
            foreach (SdsFrameNode r in roots) built.Add(SceneTree.BuildSceneTree(r, leaves));

            sb.AppendLine($"── {car}: the archive's top-level frames ──");
            foreach (SceneNode root in built)
            {
                sb.AppendLine($"  {(root.IsVisible ? "shown " : "HIDDEN")}  {root.Name}");
                foreach (SceneNode leaf in Meshes(root))
                {
                    sb.AppendLine($"            {(leaf.IsVisible ? "shown " : "HIDDEN")}  mesh {leaf.Name}");
                }
            }
            sb.AppendLine();

            SceneNode? body = leaves.FirstOrDefault(n => n.Name == "Root");
            Check("the car's own body is there", body != null, string.Join(", ", leaves.Select(l => l.Name)));
            Check("...and it is what the scene opens showing", body?.IsVisible == true);

            List<SceneNode> shells = leaves.Where(n => DefaultHidden.IsEmitterShell(n.Name)).ToList();
            Check("the emitter shells are found by name",
                shells.Count > 0, string.Join(", ", shells.Select(s => s.Name)));
            Check("...and none of them is drawn", shells.All(s => !s.IsVisible),
                string.Join(", ", shells.Where(s => s.IsVisible).Select(s => s.Name)));

            // The eye the user actually unticks is the holder frame's, and it reads as off because nothing
            // under it is drawn — the aggregate, not a second flag to keep in step.
            List<SceneNode> holders = built.Where(r => Meshes(r).Any() && Meshes(r).All(m => !m.IsVisible)).ToList();
            Check("the shells' holder frames read as hidden too",
                holders.Count == built.Count(r => Meshes(r).Any(m => DefaultHidden.IsEmitterShell(m.Name))),
                string.Join(", ", holders.Select(h => h.Name)));
            Check("the body's own holder frame is still shown",
                built.Any(r => r.IsVisible && Meshes(r).Any(m => m.Name == "Root")),
                string.Join(", ", built.Where(r => r.IsVisible).Select(r => r.Name)));

            // ---- 2. The same question across every shipped car ---------------------------------------
            // A rule keyed on names has to be checked against the names that exist, not the one car in hand:
            // a spelling this misses is a shell left on screen, and a word it over-matches is a body switched
            // off on load.
            sb.AppendLine("── every shipped car: meshes whose name looks like an emitter ──");
            var spellings = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hiddenNames = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int scanned = 0, withShell = 0;
            foreach (string file in Directory.Exists(carsFolder)
                         ? Directory.GetFiles(carsFolder, "*.sds")
                         : [])
            {
                try
                {
                    (IReadOnlyList<SdsFrameNode> rs, _, _) = SdsMeshLoader.LoadHierarchy(new FileInfo(file));
                    var ls = new List<SceneNode>();
                    foreach (SdsFrameNode r in rs) SceneTree.BuildSceneTree(r, ls);
                    scanned++;
                    bool any = false;
                    foreach (SceneNode l in ls)
                    {
                        // Anything with "emi"/"emm" in it, so the survey shows near-misses of the rule as
                        // well as its hits — that is the whole point of surveying rather than assuming.
                        string lower = l.Name.ToLowerInvariant();
                        if (!lower.Contains("emi") && !lower.Contains("emm")) continue;
                        spellings.TryGetValue(l.Name, out int n);
                        spellings[l.Name] = n + 1;
                        if (DefaultHidden.IsEmitterShell(l.Name))
                        {
                            hiddenNames.TryGetValue(l.Name, out int h);
                            hiddenNames[l.Name] = h + 1;
                            any = true;
                        }
                    }
                    if (any) withShell++;
                }
                catch (Exception ex) { sb.AppendLine($"  (skipped {Path.GetFileName(file)}: {ex.Message})"); }
            }

            foreach ((string name, int count) in spellings)
            {
                bool hit = DefaultHidden.IsEmitterShell(name);
                sb.AppendLine($"  {(hit ? "hidden" : "shown ")}  {name}  ×{count}");
            }
            sb.AppendLine($"  {scanned} car archives, {withShell} of them with a shell the rule hides");
            sb.AppendLine();

            Check("the rule hits every spelling the corpus uses",
                spellings.Count > 0 && spellings.Keys.All(DefaultHidden.IsEmitterShell),
                string.Join(", ", spellings.Keys.Where(k => !DefaultHidden.IsEmitterShell(k))));
            Check("nearly every car carries one, so this is the normal case not a special one",
                scanned > 0 && withShell > scanned / 2, $"{withShell} of {scanned}");

            // ---- 3. And the districts, because the rule is not car-only ------------------------------
            // BuildSceneTree is the whole editor's tree, so this default reaches a city archive as well. The
            // question there is not what it hides but what it MIGHT hide by accident: a district name that
            // merely reads like an emitter would switch off a building.
            sb.AppendLine($"── districts (first {DistrictSample} of them): names near the rule ──");
            var districtNames = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string[] districts = Directory.Exists(MafiaEnvironment.CityFolder)
                ? Directory.GetFiles(MafiaEnvironment.CityFolder, "*.sds")
                : [];
            int districtsScanned = 0;
            foreach (string file in districts.Take(DistrictSample))
            {
                try
                {
                    (IReadOnlyList<SdsFrameNode> rs, _, _) = SdsMeshLoader.LoadHierarchy(new FileInfo(file));
                    var ls = new List<SceneNode>();
                    foreach (SdsFrameNode r in rs) SceneTree.BuildSceneTree(r, ls);
                    districtsScanned++;
                    foreach (SceneNode l in ls)
                    {
                        string lower = l.Name.ToLowerInvariant();
                        if (!lower.Contains("emi") && !lower.Contains("emm")) continue;
                        districtNames.TryGetValue(l.Name, out int n);
                        districtNames[l.Name] = n + 1;
                    }
                }
                catch (Exception ex) { sb.AppendLine($"  (skipped {Path.GetFileName(file)}: {ex.Message})"); }
            }
            foreach ((string name, int count) in districtNames)
            {
                sb.AppendLine($"  {(DefaultHidden.IsEmitterShell(name) ? "hidden" : "shown ")}  {name}  ×{count}");
            }
            sb.AppendLine($"  {districtsScanned} of {districts.Length} district archives read "
                          + $"(a sample, not the lot — the rest are not covered by this run)");
            sb.AppendLine();

            Check("nothing in a district is hidden by accident",
                districtNames.Keys.All(n => !DefaultHidden.IsEmitterShell(n)
                                            || n.Contains("emitter", StringComparison.OrdinalIgnoreCase)
                                            || n.Contains("emmiter", StringComparison.OrdinalIgnoreCase)),
                string.Join(", ", districtNames.Keys.Where(DefaultHidden.IsEmitterShell)));

            sb.Insert(0, $"HIDDEN DEFAULTS PROBE ({car}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "HIDDEN DEFAULTS PROBE: FAIL\n\n");
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static IEnumerable<SceneNode> Meshes(SceneNode node)
    {
        if (node.Pending != null) yield return node;
        foreach (SceneNode child in node.Children)
        {
            foreach (SceneNode found in Meshes(child)) yield return found;
        }
    }
}
