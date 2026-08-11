using System.Globalization;
using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using System.Windows;
using System.Windows.Controls;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
using Illusion.ViewModels;
using Illusion.Views;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What survives past fifty metres — measured, and asserted so that a change to the per-level reading fails
/// here rather than in game.
///
/// <para>
/// The far level is a shell rather than a second copy of the car, and the whole LOD switch rests on that
/// being true: it is why a component that is present near and absent far is an answer instead of a fault.
/// The numbers come from <c>docs/car-anatomy.md</c> (measured 2026-08-08) and are re-measured here off the
/// split table, resolving every split through THAT LEVEL'S OWN remap table — reusing LOD 0's for LOD 1 is
/// wrong outright, since the two tables are different sizes, and that is exactly the mistake this probe
/// exists to catch.
/// </para>
/// <para>
/// Reads only; nothing is written. Output: %TEMP%\illusion_car_lod.txt
/// </para>
/// </summary>
internal static class CarLodProbes
{
    internal static void RunCarLodProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_lod.txt");
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
            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");

            Corpus(sb, folder, Check);
            Switch(sb, folder, focus, Check);
            SingleLevel(sb, folder, Check);
            FarSave(sb, folder, focus, Check);
            Render(sb, folder, focus, Check);
            sb.Insert(0, $"CAR LOD PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "CAR LOD PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // ── the corpus: how many levels a car carries, and what reaches the far one ──

    private static void Corpus(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("════ the levels the corpus carries, and what reaches the far one ════");

        int cars = 0, twoLevel = 0, oneLevel = 0, agreeing = 0;
        var levelCounts = new Dictionary<int, int>();
        var singles = new List<string>();

        double paletteNear = 0, paletteFar = 0;
        int smallerFar = 0;

        int drawnNear = 0, goneFar = 0;
        int partBones = 0, partBonesFar = 0;
        int bareBones = 0, bareBonesFar = 0;
        int handleBones = 0, handleBonesFar = 0;
        int farOnly = 0;
        var farOnlyNames = new List<string>();
        int bodyBonesFar = 0;
        var survivingKinds = new Dictionary<string, int>(StringComparer.Ordinal);

        // What the AGGREGATE makes of the same cars at each level — the reading the tree shows.
        int componentsNear = 0, componentsTwoLevel = 0, componentsFar = 0, bodiesFar = 0, carsRead = 0;
        int partComponentsFar = 0, bareComponentsFar = 0;
        int sameFaults = 0, farKeptIdentity = 0, roundTrip = 0, roundTripKept = 0;
        int drawnAgree = 0, sameComponents = 0, listedFar = 0;
        var drawnDrift = new List<string>();
        var faultsNear = new Dictionary<CarFaultKind, int>();
        var faultsFar = new Dictionary<CarFaultKind, int>();
        var faultDrift = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            Car? near;
            try { near = Car.ReadFrom(extracted); }
            catch (Exception) { continue; }
            if (near?.Frames?.FrameObjects == null) continue;
            if (near.Prefab.Car is not { DeformPartCount: > 0 }) continue;
            if (near.Frames.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault()
                is not { } model)
            {
                continue;
            }
            cars++;
            string name = Path.GetFileNameWithoutExtension(sds.Name);

            // ── how many levels, read three ways ──
            int geometryLods = Lods(model);
            int blendLevels = BlendLevels(model);
            int skeletonLods = SkeletonLods(model);
            int levels = Math.Min(geometryLods, blendLevels);
            levelCounts[levels] = levelCounts.GetValueOrDefault(levels) + 1;
            if (geometryLods == blendLevels && blendLevels == skeletonLods) agreeing++;
            if (levels >= 2) twoLevel++; else { oneLevel++; singles.Add($"{name} ({levels})"); }

            carsRead++;
            componentsNear += near.Components.Count;
            foreach (CarFault fault in near.Faults)
            {
                faultsNear[fault.Kind] = faultsNear.GetValueOrDefault(fault.Kind) + 1;
            }

            if (levels < 2) continue;

            // ── the palette: how many bones each level's remap table names ──
            byte[] remapNear = Remap(model, 0);
            byte[] remapFar = Remap(model, 1);
            paletteNear += remapNear.Length;
            paletteFar += remapFar.Length;
            if (remapFar.Length < remapNear.Length) smallerFar++;

            // ── the coverage: which bones draw at which level ──
            string[] bonesByJoint = BonesByJoint(model);
            HashSet<ulong> near0 = DrawnAt(model, 0, bonesByJoint);
            HashSet<ulong> far1 = DrawnAt(model, 1, bonesByJoint);
            drawnNear += near0.Count;
            goneFar += near0.Count(b => !far1.Contains(b));
            foreach (ulong bone in far1.Where(b => !near0.Contains(b)))
            {
                farOnly++;
                if (farOnlyNames.Count < 12) farOnlyNames.Add($"{name}: {Named(bone, bonesByJoint)}");
            }

            // …split by who claims the bone: a deform part, a deform handle, or nothing at all.
            var claimedByPart = new HashSet<ulong>();
            var claimedByHandle = new HashSet<ulong>();
            foreach (CarDeformPart part in near.Prefab.CarDeformParts)
            {
                if (part.Frame != 0) claimedByPart.Add(part.Frame);
                foreach (CarDeformHandle handle in part.Handles) claimedByHandle.Add(handle.JointName);
            }
            foreach (ulong bone in near0)
            {
                bool survives = far1.Contains(bone);
                if (claimedByPart.Contains(bone))
                {
                    partBones++;
                    if (survives) partBonesFar++;
                }
                else if (claimedByHandle.Contains(bone))
                {
                    handleBones++;
                    if (survives) handleBonesFar++;
                }
                else
                {
                    bareBones++;
                    if (survives) bareBonesFar++;
                }
            }

            // The body's own bone is the one component a far level cannot do without: every marker whose bone
            // no component owns hangs off it.
            if (near.Body is { } body && far1.Contains(body.BoneHash)) bodyBonesFar++;
            foreach (CarComponent component in near.Components.Where(c => far1.Contains(c.BoneHash)))
            {
                survivingKinds[component.Kind] = survivingKinds.GetValueOrDefault(component.Kind) + 1;
            }

            // ── and what the aggregate makes of the far level ──
            //
            // The same car: a level decides how much each component DRAWS, not which of them exist. So the
            // component count, the body, the faults and the identities all have to come back unmoved, and
            // what moves is HasGeometry — which is what the tree lists by.
            componentsTwoLevel += near.Components.Count;
            Car far = Car.Stitch(near.Prefab, near.Frames, lod: 1, previous: near);
            componentsFar += far.Components.Count;
            if (far.Components.Count == near.Components.Count) sameComponents++;

            // The two readings of what is DRAWN there, against each other. This is where a change to the
            // per-level remap reading gets caught: reuse the NEAR table for the far level and the aggregate
            // says hundreds of bones draw at a level that cannot even name them, while this reading — which
            // resolves each split through that level's own table — goes on saying 164 over the whole corpus.
            var drawn = far.Components.Where(c => c.HasGeometry).Select(c => c.BoneHash).ToHashSet();
            var expected = far1.Where(b => !claimedByHandle.Contains(b)).ToHashSet();
            if (drawn.SetEquals(expected)) drawnAgree++;
            else if (drawnDrift.Count < 8)
            {
                drawnDrift.Add($"{name}: aggregate {drawn.Count}, this reading {expected.Count}");
            }
            partComponentsFar += far.Components.Count(c => !c.IsBare && c.HasGeometry);
            bareComponentsFar += far.Components.Count(c => c.IsBare && c.HasGeometry);
            listedFar += far.Components.Count(far.DrawnHere);
            if (far.Body != null) bodiesFar++;
            foreach (CarFault fault in far.Faults)
            {
                faultsFar[fault.Kind] = faultsFar.GetValueOrDefault(fault.Kind) + 1;
            }

            // ── identity across the switch, in both directions ──
            //
            // A component's identity is not the level's, so the door listed near and the door listed far are
            // the same door — that is what lets the selection survive the switch. And the way back matters as
            // much as the way out: a look at the far level and back must not renumber the car, or the modder
            // loses their place and every folded branch for having looked.
            farKeptIdentity += far.Components.Count(c => near.ComponentById(c.Id) != null);
            Car back = Car.Stitch(near.Prefab, near.Frames, lod: 0, previous: far);
            roundTrip += back.Components.Count;
            roundTripKept += back.Components.Count(c => near.ComponentById(c.Id) != null);

            // The diagnosis is the CAR's, not the level's: the same car read at either level has to report
            // the same things wrong with it, by kind and by count.
            string saidNear = Describe(faultsOf(near));
            string saidFar = Describe(faultsOf(far));
            if (string.Equals(saidNear, saidFar, StringComparison.Ordinal)) sameFaults++;
            else if (faultDrift.Count < 8) faultDrift.Add($"{name}: near [{saidNear}] far [{saidFar}]");

            static Dictionary<CarFaultKind, int> faultsOf(Car read)
            {
                var byKind = new Dictionary<CarFaultKind, int>();
                foreach (CarFault fault in read.Faults)
                {
                    byKind[fault.Kind] = byKind.GetValueOrDefault(fault.Kind) + 1;
                }
                return byKind;
            }
        }

        sb.AppendLine($"  cars with a car prefab carrying deform parts: {cars}");
        sb.AppendLine("  levels per car: " + string.Join(", ",
            levelCounts.OrderBy(p => p.Key).Select(p => $"{p.Key}×{p.Value}")));
        sb.AppendLine("  single-level cars: " + string.Join(", ", singles));
        check("a body carries two levels on 82 of the 85 cars and one on the other 3",
            cars == 85 && twoLevel == 82 && oneLevel == 3,
            $"{cars} cars, {twoLevel} with two levels, {oneLevel} with one");
        check("…and the geometry, the blend info and the skeleton agree on how many there are",
            cars > 0 && agreeing == cars, $"{agreeing} of {cars}");

        sb.AppendLine($"\n  the bone palette, averaged over the {twoLevel} two-level cars:");
        sb.AppendLine($"    LOD 0 {Avg(paletteNear, twoLevel)}    LOD 1 {Avg(paletteFar, twoLevel)}");
        sb.AppendLine($"    smaller at the far level on {smallerFar} of {twoLevel}");
        check("the far level's palette is a tenth of the near one's, on every two-level car",
            twoLevel > 0 && smallerFar == twoLevel
            && Math.Abs((paletteNear / twoLevel) - 73.3) < 0.1
            && Math.Abs((paletteFar / twoLevel) - 10.8) < 0.1,
            $"{Avg(paletteNear, twoLevel)} against {Avg(paletteFar, twoLevel)}, "
            + $"smaller on {smallerFar} of {twoLevel}");

        sb.AppendLine($"\n  bones that draw at the near level: {drawnNear}");
        sb.AppendLine($"    with nothing at the far level:     {goneFar}");
        sb.AppendLine($"    claimed by a deform part:          {partBonesFar} of {partBones} survive");
        sb.AppendLine($"    claimed by nothing (plates, lights): {bareBonesFar} of {bareBones}");
        sb.AppendLine($"    a deform handle:                   {handleBonesFar} of {handleBones}");
        sb.AppendLine($"    drawn at the FAR level only:       {farOnly}");
        foreach (string one in farOnlyNames) sb.AppendLine("      " + one);
        sb.AppendLine($"    the BODY's own bone draws there on   {bodyBonesFar} of {twoLevel} cars");
        sb.AppendLine("    what a component that survives is: " + string.Join(", ",
            survivingKinds.OrderByDescending(p => p.Value).Select(p => $"{p.Key}×{p.Value}")));
        check("the far level is a shell: 4882 of 5046 bones with near geometry have none there",
            drawnNear == 5046 && goneFar == 4882,
            $"{goneFar} of {drawnNear}");
        check("…and that holds in each population the car is made of",
            partBones == 1445 && partBonesFar == 98
            && bareBones == 2517 && bareBonesFar == 66
            && handleBones == 1084 && handleBonesFar == 0,
            $"parts {partBonesFar}/{partBones}, bare {bareBonesFar}/{bareBones}, "
            + $"handles {handleBonesFar}/{handleBones}");

        sb.AppendLine($"\n  what the aggregate stitches, over {carsRead} cars:");
        sb.AppendLine($"    at the near level  {componentsNear}");
        sb.AppendLine($"    at the far level   {componentsFar} "
            + $"({partComponentsFar} from a deform part, {bareComponentsFar} bare)");
        sb.AppendLine($"    cars with a body at the far level  {bodiesFar} of {twoLevel}");
        sb.AppendLine("    faults at the near level: " + Describe(faultsNear) + " (over all 85 cars)");
        sb.AppendLine("    faults at the far level:  " + Describe(faultsFar)
            + $" (over the {twoLevel} two-level ones)");
        foreach (string drift in faultDrift) sb.AppendLine("      " + drift);
        foreach (string drift in drawnDrift) sb.AppendLine("      " + drift);
        sb.AppendLine($"    components DRAWN at the far level   {partComponentsFar} from a deform part, "
            + $"{bareComponentsFar} bare");
        sb.AppendLine($"    …and LISTED there, with the body    {listedFar}");
        check("a car is the same car at either level: the components do not move, only what they draw",
            twoLevel > 0 && sameComponents == twoLevel && componentsFar == componentsTwoLevel,
            $"{componentsFar} far against {componentsTwoLevel} near, agreeing on {sameComponents} of "
            + $"{twoLevel} cars");
        check("what the aggregate says is drawn at the far level is what this reading finds drawn there",
            twoLevel > 0 && drawnAgree == twoLevel
            && partComponentsFar + bareComponentsFar == partBonesFar + bareBonesFar + farOnly,
            $"agreeing on {drawnAgree} of {twoLevel} cars, "
            + $"{partComponentsFar + bareComponentsFar} components drawing against "
            + $"{partBonesFar + bareBonesFar + farOnly} bones");
        sb.AppendLine($"    far components the near stitch also names   {farKeptIdentity} of {componentsFar}");
        sb.AppendLine($"    …and after switching back                  {roundTripKept} of {roundTrip}");
        check("the far level has a body on every car, so a marker still has somewhere to hang",
            twoLevel > 0 && bodiesFar == twoLevel, $"{bodiesFar} of {twoLevel}");
        check("a component keeps its identity across the switch, and across the way back",
            componentsFar > 0 && farKeptIdentity == componentsFar
            && roundTrip == componentsTwoLevel && roundTripKept == roundTrip,
            $"{farKeptIdentity} of {componentsFar} far, {roundTripKept} of {roundTrip} back, "
            + $"against {componentsTwoLevel} near");
        check("the diagnosis is the car's and not the level's: the same faults at either level",
            twoLevel > 0 && sameFaults == twoLevel, $"agreeing on {sameFaults} of {twoLevel} cars");
    }

    // ── the switch on the panel: one car, two levels, and the modder's place kept across them ──

    private static void Switch(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ the switch, on the real panel ════");
        var archive = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!ComponentTreeProbes.Stage(archive, out ScenePanel? panel, out _) || panel == null)
        {
            check("the focus car stages", false, archive.FullName);
            return;
        }

        ComponentTreeViewModel components = panel.Components;
        if (components.Car is not { } near)
        {
            check("the focus car stitches", false, focus);
            return;
        }

        List<string> nearRows = Rows(components);
        var nearIds = components.Roots.SelectMany(r => r.SelfAndDescendants())
            .ToDictionary(r => r.Id.Value, r => r.Name);
        int nearFaults = components.Faults.Count;
        string geometry = Fingerprint(near);

        sb.AppendLine($"  {focus}: {near.Lods} levels, {nearRows.Count} rows near");
        check("the switch is above the tree, with a segment for each level the car carries",
            panel.LodSwitch.Visibility == Visibility.Visible && panel.LodSwitch.IsEnabled
            && panel.LodSwitch.Children.Count == near.Lods && components.CanSwitchLod
            && Segments(panel).SequenceEqual(["Near", "Far"], StringComparer.Ordinal),
            $"{panel.LodSwitch.Children.Count} segments ({string.Join(" | ", Segments(panel))}), "
            + $"visible={panel.LodSwitch.Visibility}, enabled={panel.LodSwitch.IsEnabled}");
        check("…and the near one is lit, which is where a car opens",
            components.Lod == 0 && Lit(panel) == 0, $"level {components.Lod}, segment {Lit(panel)}");

        // The row a modder is standing on, and one that will not survive the switch. Both are asked for by
        // NAME afterwards, because the whole question is whether the same component is still the same one.
        ComponentRowViewModel? body = components.BodyRow;
        ComponentRowViewModel? going = components.Roots.SelectMany(r => r.SelfAndDescendants())
            .FirstOrDefault(r => !ReferenceEquals(r, body) && r.Component.HasGeometry);
        if (body == null || going == null)
        {
            check("the focus car has a body and something else to select", false, focus);
            return;
        }
        components.Select(body);
        ComponentId wasSelected = components.Selected?.Id ?? ComponentId.None;
        // …and the same question asked of a component the far level does NOT draw, which is 96.7 % of them:
        // the tree lights nothing there, and the way back has to put the modder where they were — the
        // viewport never let go of the bone, and a tree disagreeing with the gizmo is how the wrong thing
        // gets deleted.
        components.Select(going);
        ComponentId wasGoing = components.Selected?.Id ?? ComponentId.None;
        components.Lod = 1;
        bool nothingLit = components.Selected == null;
        components.Lod = 0;
        check("a component the far level does not draw comes back selected when the near one does",
            wasGoing.IsSet && nothingLit && components.Selected?.Id == wasGoing,
            $"\"{going.Name}\": {(nothingLit ? "nothing lit far" : "something lit far")}, "
            + $"back on {components.Selected?.Name ?? "nothing"}");
        components.Select(body);

        // ── over to the far level ──
        components.Lod = 1;
        if (components.Car is not { } far)
        {
            check("the car re-stitches at the far level", false, focus);
            return;
        }
        List<string> farRows = Rows(components);
        sb.AppendLine($"  far: {farRows.Count} rows — {string.Join(", ", farRows.Take(8))}");

        check("the tree redraws for the chosen level, and it is nearly empty",
            components.Lod == 1 && Lit(panel) == 1 && far.Lod == 1
            && farRows.Count == far.Components.Count(far.DrawnHere)
            && farRows.Count < nearRows.Count / 4,
            $"{farRows.Count} rows against {nearRows.Count} near");
        // Every row the far level lists is drawn there — except the body, which is the car itself and where
        // every marker whose own component is not drawn here hangs. The car still HOLDS the rest: what the
        // level decides is what the tree lists, not what the car is made of.
        List<CarComponent> listed = [.. components.Roots.SelectMany(r => r.SelfAndDescendants())
            .Select(r => r.Component)];
        check("…and every row it lists carries geometry at that level, or is the body",
            listed.Count > 0 && listed.All(c => c.HasGeometry || ReferenceEquals(c, far.Body))
            && far.Components.Count == near.Components.Count,
            $"{listed.Count(c => !c.HasGeometry)} rows drawing nothing, "
            + $"{far.Components.Count} components against {near.Components.Count} near");
        check("a component present near and absent far is no fault of the car",
            components.Faults.Count == nearFaults,
            $"{components.Faults.Count} faults far against {nearFaults} near");
        // …and the strip is the one this car has NOW: its rows lead to components, and a list left pointing
        // at the previous stitch's rows would select something that is no longer in the tree.
        check("…and the diagnosis on the panel is the one this level was stitched with",
            ReferenceEquals(panel.FaultList.ItemsSource, components.Faults)
            && components.Faults.All(f => f.Component == null
                || ReferenceEquals(components.RowOf(f.Component.Id), f.Component)),
            $"{components.Faults.Count} rows, bound: "
            + $"{ReferenceEquals(panel.FaultList.ItemsSource, components.Faults)}");
        check("the selection survives the switch where the same component is drawn",
            components.Selected?.Id == wasSelected && wasSelected.IsSet,
            $"was {wasSelected}, now {components.Selected?.Id.ToString() ?? "nothing"}");

        // A component the far level does not draw is still the car's — same identity, same bone, still what
        // the bone lookup answers — and the tree simply has no row for it, which is the answer rather than a
        // fault. That the LOOKUP still answers is what keeps a viewport click off the body: without it every
        // bone the far level does not draw would resolve to the car as a whole.
        check("…and a component the far level does not draw keeps its place in the car, minus its row",
            far.ComponentById(going.Id) is { } still && ReferenceEquals(
                far.ComponentOfBone(still.BoneHash), still)
            && components.RowOf(going.Id) == null && farRows.Count < nearRows.Count,
            $"\"{going.Name}\" is {(components.RowOf(going.Id) == null ? "not listed" : "still listed")}, "
            + $"the car {(far.ComponentById(going.Id) != null ? "still holds it" : "has lost it")}");

        // …and nothing on it can be EDITED from here. The lists an edit is written into — a new part's
        // parent, the components a collision can hang off — are the car's rather than the level's, so
        // choosing from what a shell happens to show would write a link the modder would never have picked
        // from the near tree.
        if (panel.ComponentTree.ComponentTree.ContextMenu is { } menu)
        {
            components.Select(components.BodyRow);
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
            List<MenuItem> items = [.. menu.Items.OfType<MenuItem>()
                .Where(m => m.Visibility == Visibility.Visible)];
            List<MenuItem> acting = [.. items.Where(
                m => (m.Header as string ?? "") != "Show in Raw tree")];
            check("nothing can be edited from the far level, and the menu says why",
                acting.Count > 0 && acting.All(m => !m.IsEnabled
                    && (m.ToolTip as string ?? "").Contains("Switch to Near", StringComparison.Ordinal)),
                string.Join(", ", acting.Select(m => (m.Header as string ?? "?")
                    + (m.IsEnabled ? " ENABLED" : ""))));
        }

        // ── and back ──
        components.Lod = 0;
        List<string> backRows = Rows(components);
        var backIds = components.Roots.SelectMany(r => r.SelfAndDescendants())
            .ToDictionary(r => r.Id.Value, r => r.Name);
        check("switching back lists exactly the near tree again",
            components.Lod == 0 && Lit(panel) == 0
            && backRows.SequenceEqual(nearRows, StringComparer.Ordinal),
            $"{backRows.Count} rows against {nearRows.Count}");
        check("…with every component still the one it was, so a look at the far level costs nothing",
            backIds.Count == nearIds.Count
            && backIds.All(p => nearIds.TryGetValue(p.Key, out string? was) && was == p.Value),
            $"{backIds.Count(p => nearIds.ContainsKey(p.Key))} of {backIds.Count} identities kept");
        check("the selection is back on the row it was on",
            components.Selected?.Id == wasSelected,
            $"{components.Selected?.Name ?? "nothing"} selected");

        // The one thing the switch must never do. Quantization, the split table and the per-bone bounds are
        // SHARED across levels, so a switch that touched any of them through the far level would break the
        // near one — the reason nothing here is editable through the level at all.
        check("nothing about the switch touches geometry",
            string.Equals(Fingerprint(components.Car!), geometry, StringComparison.Ordinal),
            "the split table, the hit boxes, the remap tables and the index buffers are unchanged");
    }

    /// <summary>
    /// A car opened on the FAR level and saved with no edit is the file it came from, byte for byte.
    ///
    /// <para>
    /// The switch changes which components are listed, and a modder who looks at the far level and then hits
    /// Build must not find that the look cost them the 96.7 % of the car it does not draw. Written into a
    /// scratch mirror, so the game's own folders are never touched.
    /// </para>
    /// </summary>
    private static void FarSave(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ saved from the far level ════");
        string extracted = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, focus + ".sds")));
        Car? far = Car.ReadFrom(extracted, lod: 1);
        if (far?.PrefabPath == null)
        {
            check("the focus car reads at the far level", false, focus);
            return;
        }

        string mirror = Path.Combine(Path.GetTempPath(), "illusion_car_lod_scratch");
        try { if (Directory.Exists(mirror)) Directory.Delete(mirror, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        CarSave saved = far.Save(path => Path.Combine(mirror, Path.GetRelativePath(extracted, path)));
        byte[] original = File.ReadAllBytes(far.PrefabPath);
        string written = Path.Combine(mirror, Path.GetRelativePath(extracted, far.PrefabPath));
        byte[] again = File.Exists(written) ? File.ReadAllBytes(written) : [];

        sb.AppendLine($"  {focus}: {far.Components.Count} components at the far level, "
            + $"{original.Length} bytes in, {again.Length} out");
        check("a car saved from the far level is the file it came from, byte for byte",
            saved.Ok && again.Length == original.Length && again.AsSpan().SequenceEqual(original),
            saved.Ok
                ? $"{original.Length} vs {again.Length}, first differing at {FirstDiff(original, again)}"
                : string.Join("; ", saved.Lost));

        try { Directory.Delete(mirror, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The three shipped cars that carry a single level: the switch is shown DISABLED there rather
    /// than hidden, so that the answer is "there is only one" and not a control that comes and goes.</summary>
    private static void SingleLevel(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ a car that carries one level ════");
        var archive = new FileInfo(Path.Combine(folder, "half_track_pha.sds"));
        if (!ComponentTreeProbes.Stage(archive, out ScenePanel? panel, out _) || panel == null)
        {
            check("the single-level car stages", false, archive.FullName);
            return;
        }

        ComponentTreeViewModel components = panel.Components;
        List<string> rows = Rows(components);
        sb.AppendLine($"  half_track_pha: {components.Lods} level, {rows.Count} rows");

        check("the switch is disabled on a car that carries only one level",
            components.Lods == 1 && !components.CanSwitchLod
            && panel.LodSwitch.Visibility == Visibility.Visible && !panel.LodSwitch.IsEnabled,
            $"{components.Lods} level, enabled={panel.LodSwitch.IsEnabled}, "
            + $"visible={panel.LodSwitch.Visibility}");
        // …and it says WHY where the modder will look for the reason. A tooltip on a disabled element is
        // suppressed by WPF unless it is told otherwise, so the sentence has to be asked for explicitly.
        List<RadioButton> segments = [.. panel.LodSwitch.Children.OfType<RadioButton>()];
        check("…and says so on hover, which a disabled element does not do by default",
            segments.Count > 0 && segments.All(s => ToolTipService.GetShowOnDisabled(s)
                && (s.ToolTip as string ?? "").Contains(
                    "single level of detail", StringComparison.Ordinal)),
            segments.Count == 0 ? "no segments" : segments[0].ToolTip?.ToString() ?? "no tooltip");

        // …and asking for a level that does not exist changes nothing, whoever asks — the view-model refuses
        // it rather than the button being the only thing standing between a modder and an empty tree.
        components.Lod = 1;
        check("…and a level that does not exist cannot be reached another way",
            components.Lod == 0 && Rows(components).SequenceEqual(rows, StringComparer.Ordinal),
            $"level {components.Lod}, {Rows(components).Count} rows");
    }

    /// <summary>
    /// A picture of the panel showing the far level. Nothing above says that two segmented switches, the
    /// fault strip and a tree all fit across a panel 340 px wide, or that a tree of one row reads as an
    /// answer rather than as a panel that failed — and that is the only question a layout can fail on its
    /// own terms.
    /// </summary>
    private static void Render(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var archive = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!ComponentTreeProbes.Stage(archive, out ScenePanel? panel, out _) || panel == null) return;
        panel.Components.Lod = 1;
        ComponentTreeProbes.Snapshot(panel, "illusion_car_lod.png", "the switch draws", check, sb);
    }

    private static List<string> Rows(ComponentTreeViewModel components) =>
        [.. components.Roots.SelectMany(r => r.SelfAndDescendants()).Select(r => r.Name)];

    private static List<string> Segments(ScenePanel panel) =>
        [.. panel.LodSwitch.Children.OfType<System.Windows.Controls.RadioButton>()
            .Select(b => b.Content?.ToString() ?? "")];

    /// <summary>Which segment is lit, or -1 when none is — which must not read as the first one.</summary>
    private static int Lit(ScenePanel panel)
    {
        int at = 0;
        foreach (System.Windows.Controls.RadioButton segment in
                 panel.LodSwitch.Children.OfType<System.Windows.Controls.RadioButton>())
        {
            if (segment.IsChecked == true) return at;
            at++;
        }
        return -1;
    }

    /// <summary>
    /// Everything about the model that a level is read THROUGH, as one string: the split table, the per-piece
    /// face ranges, the hit boxes, each level's remap table and each level's index buffer. All of it is shared
    /// between the levels — which is why editing the far level through them breaks the near one, and why the
    /// switch is allowed to read it and nothing more.
    /// </summary>
    private static string Fingerprint(Car car)
    {
        var sb = new StringBuilder();
        if (car.Frames?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault() is not { } model)
        {
            return "";
        }

        foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
        {
            sb.Append(split.BlendIndex).Append('{');
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
            {
                foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                {
                    foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                    {
                        sb.Append(range.StartIndex).Append('/').Append(range.NumFaces).Append(',');
                    }
                }
            }
            sb.Append("}|");
        }
        foreach (FrameObjectModel.HitBoxInfo box in model.HitBoxes ?? [])
        {
            sb.Append(box.Unk).Append(':').Append(box.Position).Append(':').Append(box.Size).Append('|');
        }
        for (int level = 0; level < Lods(model); level++)
        {
            sb.Append(Convert.ToBase64String(Remap(model, level))).Append('|');
            try { sb.Append(model.GetIndexBuffer(level)?.GetData()?.Length ?? -1).Append('|'); }
            catch (Exception) { sb.Append("?|"); }
        }
        return sb.ToString();
    }

    private static string Describe(Dictionary<CarFaultKind, int> faults) =>
        faults.Count == 0 ? "none" : string.Join(", ",
            faults.OrderByDescending(p => p.Value).Select(p => $"{p.Key}×{p.Value}"));

    private static string Avg(double total, int over) =>
        over == 0 ? "—" : (total / over).ToString("0.0", CultureInfo.InvariantCulture);

    // ── the frame graph, read at one level ──

    private static int Lods(FrameObjectModel model)
    {
        try { return model.GetGeometry().LOD?.Length ?? 0; }
        catch (Exception) { return 0; }
    }

    private static int BlendLevels(FrameObjectModel model)
    {
        try { return model.GetBlendInfoObject().BoneIndexInfos?.Length ?? 0; }
        catch (Exception) { return 0; }
    }

    private static int SkeletonLods(FrameObjectModel model)
    {
        try { return model.GetSkeletonObject().LodRemapIDCount?.Length ?? 0; }
        catch (Exception) { return 0; }
    }

    private static byte[] Remap(FrameObjectModel model, int lod)
    {
        try
        {
            FrameBlendInfo.BoneIndexInfo[] levels = model.GetBlendInfoObject().BoneIndexInfos ?? [];
            return lod < levels.Length ? levels[lod].BoneRemapIDs ?? [] : [];
        }
        catch (Exception) { return []; }
    }

    private static string[] BonesByJoint(FrameObjectModel model)
    {
        try { return [.. (model.GetSkeletonObject().BoneNames ?? []).Select(b => b.ToString())]; }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// The bones that carry geometry at one level, read the way the aggregate reads them: a split's bone is
    /// <c>BoneRemapIDs[BlendIndex]</c> through THAT level's own table, and a piece counts when one of its face
    /// ranges holds a face and fits inside that level's index buffer.
    /// </summary>
    private static HashSet<ulong> DrawnAt(FrameObjectModel model, int lod, string[] bonesByJoint)
    {
        var drawn = new HashSet<ulong>();
        byte[] remap = Remap(model, lod);
        if (remap.Length == 0) return drawn;

        int indices;
        try { indices = model.GetIndexBuffer(lod)?.GetData()?.Length ?? 0; }
        catch (Exception) { return drawn; }

        foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
        {
            int bone = split.BlendIndex < remap.Length ? remap[split.BlendIndex] : -1;
            if (bone < 0 || bone >= bonesByJoint.Length || bonesByJoint[bone].Length == 0) continue;
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
            {
                foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                {
                    foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                    {
                        if (range.NumFaces > 0 && range.StartIndex + (range.NumFaces * 3) <= indices)
                        {
                            drawn.Add(Fnv64.Hash(bonesByJoint[bone]));
                        }
                    }
                }
            }
        }
        return drawn;
    }

    private static string Named(ulong bone, string[] bonesByJoint)
    {
        foreach (string name in bonesByJoint)
        {
            if (name.Length > 0 && Fnv64.Hash(name) == bone) return name;
        }
        return bone.ToString("X16", CultureInfo.InvariantCulture);
    }
}
