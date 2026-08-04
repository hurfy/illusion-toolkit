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
    private const int DefaultDistrictSample = 10;

    internal static void RunHiddenDefaultsProbe(string car, int districtSample = DefaultDistrictSample)
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

            // ---- 2b. What the extra frames are actually CALLED ----------------------------------------
            // The mesh-name rule leaves shells on screen in a quarter of the cars, so the question is what
            // the top-level frames themselves look like: which root holds the body, and what the others are
            // named. Every car with more than one root carrying geometry, printed in full.
            sb.AppendLine("── every car with more than one top-level frame carrying geometry ──");
            int multiRoot = 0, bodyRootNamedAfterArchive = 0;
            var extrasShown = new List<string>();
            var bodiesHidden = new List<string>();
            var catalogueHidden = new List<string>();
            foreach (string file in Directory.Exists(carsFolder)
                         ? Directory.GetFiles(carsFolder, "*.sds")
                         : [])
            {
                try
                {
                    (IReadOnlyList<SdsFrameNode> rs, _, _) = SdsMeshLoader.LoadHierarchy(new FileInfo(file));
                    var tops = new List<SceneNode>();
                    foreach (SdsFrameNode r in rs) tops.Add(SceneTree.BuildSceneTree(r, []));
                    List<SceneNode> withGeometry = tops.Where(t => Meshes(t).Any()).ToList();
                    if (withGeometry.Count < 2) continue;
                    multiRoot++;

                    string archive = Path.GetFileNameWithoutExtension(file);
                    sb.AppendLine($"  {archive}");
                    foreach (SceneNode root in withGeometry)
                    {
                        bool carries = Meshes(root).Any(IsSkinnedBody);
                        if (carries && Same(root.Name, archive)) bodyRootNamedAfterArchive++;
                        sb.AppendLine($"      {(root.IsVisible ? "shown " : "HIDDEN")} "
                                      + $"{(carries ? "BODY" : "    ")}  {root.Name}"
                                      + $"   [{string.Join(", ", Meshes(root).Select(Describe))}]");

                        // The catalogue archives are not cars: cars_universal is the prototype shelf, and a
                        // default that switched its wheels off would empty the one place they can be looked at.
                        if (archive.StartsWith("cars_universal", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!root.IsVisible) catalogueHidden.Add($"{archive}/{root.Name}");
                            continue;
                        }
                        if (carries && !root.IsVisible) bodiesHidden.Add($"{archive}/{root.Name}");
                        if (!carries && root.IsVisible) extrasShown.Add($"{archive}/{root.Name}");
                    }
                }
                catch { /* reported by the pass above */ }
            }
            sb.AppendLine($"  {multiRoot} cars with more than one root carrying geometry; "
                          + $"in {bodyRootNamedAfterArchive} of them the body's root is named after the archive");
            sb.AppendLine();

            // The three ways this default can be wrong, each one named rather than counted: a shell still on
            // screen (what the mesh-name rule left behind), a body switched off, a catalogue emptied.
            Check("no extra frame is left showing on any car", extrasShown.Count == 0,
                string.Join(", ", extrasShown));
            Check("no car's body is hidden", bodiesHidden.Count == 0, string.Join(", ", bodiesHidden));
            Check("the prototype catalogue is left alone", catalogueHidden.Count == 0,
                string.Join(", ", catalogueHidden));

            // ---- 3. And the districts, because the rule is not car-only ------------------------------
            // BuildSceneTree is the whole editor's tree, so this default reaches a city archive as well. The
            // question there is not what it hides but what it MIGHT hide by accident: a district name that
            // merely reads like an emitter would switch off a building.
            sb.AppendLine($"── districts (first {districtSample} of them): names near the rule ──");
            var districtNames = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string[] districts = Directory.Exists(MafiaEnvironment.CityFolder)
                ? Directory.GetFiles(MafiaEnvironment.CityFolder, "*.sds")
                : [];
            int districtsScanned = 0, districtRoots = 0;
            var districtHolders = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in districts.Take(districtSample))
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

                    // And the two SHAPES the car rule keys on, at the level it keys on them: a district's
                    // own top-level frames. A shape that is common here is a shape that cannot be a default.
                    foreach (SdsFrameNode r in rs)
                    {
                        districtRoots++;
                        if (DefaultHidden.IsSceneryHolder(r.Name))
                        {
                            districtHolders.TryGetValue(r.Name, out int n);
                            districtHolders[r.Name] = n + 1;
                        }
                    }
                }
                catch (Exception ex) { sb.AppendLine($"  (skipped {Path.GetFileName(file)}: {ex.Message})"); }
            }
            foreach ((string name, int count) in districtNames)
            {
                sb.AppendLine($"  {(DefaultHidden.IsEmitterShell(name) ? "hidden" : "shown ")}  {name}  ×{count}");
            }
            sb.AppendLine($"  top-level frames a district carries: {districtRoots}, of which "
                          + $"{districtHolders.Count} match the car rule"
                          + (districtHolders.Count == 0 ? "" : ": " + string.Join(", ", districtHolders.Keys)));
            sb.AppendLine($"  {districtsScanned} of {districts.Length} district archives read "
                          + $"(a sample, not the lot — the rest are not covered by this run)");
            sb.AppendLine();

            // The districts do have a handful of their own emitter volumes — a fountain's, a fire's, the
            // puddle outside the Sea Gift — and they are the same kind of thing, so the same default reaches
            // them. Pinned by count and by shape: this is a short tail, not a city quietly going dark. Every
            // one of them is an "emit" name; NOT ONE district frame matches the _rain or the NNN_NN_ shape.
            Check("the districts' own emitter volumes are the only thing the rule finds there",
                districtHolders.Count < 32
                && districtHolders.Keys.All(n => n.StartsWith("emit", StringComparison.OrdinalIgnoreCase)),
                $"{districtHolders.Count} of {districtRoots} top-level frames");

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

    /// <summary>
    /// A mesh and how big it is. Size is the question a name cannot answer: an emitter shell is a box the
    /// size of the whole car, while a real part is a part.
    /// </summary>
    private static string Describe(SceneNode node)
    {
        if (node.Pending is not { Positions: { Length: > 0 } p }) return node.Name;
        var min = new System.Numerics.Vector3(float.MaxValue);
        var max = new System.Numerics.Vector3(float.MinValue);
        foreach (System.Numerics.Vector3 v in p)
        {
            min = System.Numerics.Vector3.Min(min, v);
            max = System.Numerics.Vector3.Max(max, v);
        }
        System.Numerics.Vector3 size = max - min;
        return $"{node.Name} {size.X:F1}x{size.Y:F1}x{size.Z:F1}";
    }

    /// <summary>Whether this mesh is the car itself — the one skinned model an archive carries.</summary>
    private static bool IsSkinnedBody(SceneNode node) =>
        node.Source is Assets.Adapters.FrameNodeAdapter { Frame: Formats.Frames.ObjectTypes.FrameObjectModel };

    /// <summary>Frame names and file names differ in case and in separators, and in nothing else that matters.</summary>
    private static bool Same(string frame, string archive) =>
        string.Equals(frame.Replace(" ", "").Replace("_", ""),
            archive.Replace(" ", "").Replace("_", ""), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<SceneNode> Meshes(SceneNode node)
    {
        if (node.Pending != null) yield return node;
        foreach (SceneNode child in node.Children)
        {
            foreach (SceneNode found in Meshes(child)) yield return found;
        }
    }
}
