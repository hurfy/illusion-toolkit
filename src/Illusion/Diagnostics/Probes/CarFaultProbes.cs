using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What the aggregate could NOT stitch, and whether it says so. The editor opens every car, including the
/// ones it cannot fully understand — those are the cars the tool is most needed for — so every failure has to
/// come back as a named fault rather than as a missing row.
///
/// <para>
/// Each fault kind is reproduced here, on a natural example where the corpus has one and on a synthetic car
/// where it does not, and the whole corpus is swept so that a fault that fires on 85 shipped cars is caught
/// as noise rather than shipped as a diagnosis.
/// </para>
/// <para>
/// Nothing is written into the game's folders: the mutations are made on frame graphs and prefabs held in
/// memory, and the one save this probe makes is redirected into a scratch mirror under the temp directory.
/// Output: %TEMP%\illusion_car_faults.txt
/// </para>
/// </summary>
internal static class CarFaultProbes
{
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "illusion_car_faults");

    internal static void RunCarFaultsProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_faults.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err)) { Check("the game environment initialised", false, err ?? ""); return; }
            Clear();
            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");

            Corpus(sb, folder, Check);
            Natural(sb, folder, focus, Check);
            Quiet(sb, folder, focus, Check);
            Synthetic(sb, folder, focus, Check);
            Verbatim(sb, folder, focus, Check);
        }
        catch (Exception ex)
        {
            Check("no unexpected exception", false, ex.ToString());
        }
        finally
        {
            // The header goes on in FINALLY, not at the end of the try. A probe's exit code is always 0, so
            // the verdict line IS the result — and a run that fell out early because the game folder moved
            // would otherwise write a report with no verdict at all, which reads as a green one.
            sb.Insert(0, $"CAR FAULTS PROBE ({focus}): {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // ── what the shipped corpus itself reports ──

    private static void Corpus(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("════ what the 85 shipped cars report ════");

        int cars = 0, breaks = 0;
        var byKind = new Dictionary<CarFaultKind, int>();
        var carsByKind = new Dictionary<CarFaultKind, int>();
        var examples = new Dictionary<CarFaultKind, List<string>>();

        int frames = 0, framesOnTable = 0, modelsOnTable = 0;
        int componentsNamingAFrame = 0, componentFramesOffTable = 0;
        var frameTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var onTableTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var modelChains = new List<string>();
        var onTableNames = new List<string>();
        var ownFrames = new List<string>();

        int doorParts = 0, doorRows = 0, windowParts = 0, windowRows = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            Car? car;
            try { car = Car.ReadFrom(extracted); }
            catch (Exception) { continue; }
            if (car?.Frames?.FrameObjects == null) continue;
            if (car.Prefab.Car is not { DeformPartCount: > 0 } assembly) continue;
            cars++;
            string name = Path.GetFileNameWithoutExtension(sds.Name);

            var here = new HashSet<CarFaultKind>();
            foreach (CarFault fault in car.Faults)
            {
                byKind[fault.Kind] = byKind.GetValueOrDefault(fault.Kind) + 1;
                if (!fault.ShipsThisWay) breaks++;
                here.Add(fault.Kind);
                List<string> shown = examples.TryGetValue(fault.Kind, out List<string>? list)
                    ? list
                    : examples[fault.Kind] = [];
                if (shown.Count < 8) shown.Add($"{name}: {fault.What}");
            }
            foreach (CarFaultKind kind in here)
            {
                carsByKind[kind] = carsByKind.GetValueOrDefault(kind) + 1;
            }

            // ── the frame name table, which nothing above has ever counted on a car ──
            foreach (object o in car.Frames.FrameObjects.Values)
            {
                if (o is not FrameObjectBase frame) continue;
                frames++;
                string type = frame.GetType().Name;
                frameTypes[type] = frameTypes.GetValueOrDefault(type) + 1;
                if (!frame.IsOnFrameTable) continue;
                framesOnTable++;
                onTableTypes[type] = onTableTypes.GetValueOrDefault(type) + 1;
            }
            if (car.Frames.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault() is { } model)
            {
                var chain = new List<string>();
                bool reached = false;
                for (FrameObjectBase? at = model; at != null; at = at.Parent)
                {
                    chain.Add($"{at.Name?.String}[{at.GetType().Name}{(at.IsOnFrameTable ? " ON" : "")}]");
                    if (at.IsOnFrameTable) { reached = true; break; }
                }
                if (reached) modelsOnTable++;
                if (modelChains.Count < 6) modelChains.Add($"{name}: " + string.Join(" ← ", chain));
            }
            foreach (object o in car.Frames.FrameObjects.Values)
            {
                if (o is FrameObjectBase f && f.IsOnFrameTable && onTableNames.Count < 20)
                {
                    onTableNames.Add($"{name}: \"{f.Name?.String}\" {f.GetType().Name} "
                        + $"flags={(int)f.FrameNameTableFlags} children={f.Children.Count}");
                }
            }

            var byName = new Dictionary<ulong, FrameObjectBase>();
            foreach (object o in car.Frames.FrameObjects.Values)
            {
                if (o is FrameObjectBase f && f.Name?.String is { Length: > 0 } n)
                {
                    byName.TryAdd(Fnv64.Hash(n), f);
                }
            }
            foreach (CarComponent component in car.Components)
            {
                if (component.BoneHash == 0
                    || !byName.TryGetValue(component.BoneHash, out FrameObjectBase? own))
                {
                    continue;
                }
                componentsNamingAFrame++;
                bool reached = false;
                for (FrameObjectBase? at = own; at != null && !reached; at = at.Parent)
                {
                    reached = at.IsOnFrameTable;
                }
                if (!reached) componentFramesOffTable++;
                if (ownFrames.Count < 8)
                {
                    ownFrames.Add($"{name}: \"{component.Name}\" is also {own.GetType().Name}, "
                        + $"{(reached ? "reached" : "NOT reached")} from the table");
                }
            }

            // ── the two collections that can be lined up against a part kind ──
            doorParts += car.Components.Count(c => c.PartType == 4);
            doorRows += assembly.Doors.Count;
            windowParts += car.Components.Count(c => c.PartType == 5);
            windowRows += assembly.Windows.Count;
        }

        sb.AppendLine($"  cars: {cars}");
        sb.AppendLine("\n  faults over the corpus, by kind:");
        if (byKind.Count == 0) sb.AppendLine("    none");
        foreach ((CarFaultKind kind, int count) in byKind.OrderByDescending(p => p.Value))
        {
            sb.AppendLine($"    {kind,-28} {count,5}  on {carsByKind.GetValueOrDefault(kind)} cars");
            foreach (string line in examples[kind]) sb.AppendLine("        " + line);
        }

        sb.AppendLine($"\n  the frame name table:");
        sb.AppendLine($"    frame objects across the corpus  {frames}, of them on the table {framesOnTable}");
        sb.AppendLine("    by type: " + string.Join(", ",
            frameTypes.OrderByDescending(p => p.Value)
                .Select(p => $"{p.Key}×{p.Value} ({onTableTypes.GetValueOrDefault(p.Key)} on table)")));
        sb.AppendLine($"    cars whose model REACHES the table through its parents  {modelsOnTable} of {cars}");
        foreach (string line in modelChains) sb.AppendLine("      " + line);
        sb.AppendLine("    what is actually on the table:");
        foreach (string line in onTableNames) sb.AppendLine("      " + line);
        sb.AppendLine($"    components whose bone also names a frame  {componentsNamingAFrame}, "
            + $"of them out of the table's reach {componentFramesOffTable}");
        foreach (string line in ownFrames) sb.AppendLine("      " + line);

        sb.AppendLine($"\n  the two collections a part kind can be lined up against:");
        sb.AppendLine($"    door parts {doorParts} against door rows {doorRows}");
        sb.AppendLine($"    window parts {windowParts} against window rows {windowRows}");

        check("the corpus is the 85 cars the census measured", cars == 85, $"{cars} cars");

        // The eight kinds that mean the toolkit misread a car rather than that the car is odd. A shipped car
        // raising one of these is the toolkit being wrong, and the whole fault display would be crying wolf.
        int misread = byKind.GetValueOrDefault(CarFaultKind.PartWithoutBone)
            + byKind.GetValueOrDefault(CarFaultKind.ComponentBoneUnresolved)
            + byKind.GetValueOrDefault(CarFaultKind.DuplicateComponentBone)
            + byKind.GetValueOrDefault(CarFaultKind.ParentLinksDisagree)
            + byKind.GetValueOrDefault(CarFaultKind.ParentUnresolved)
            + byKind.GetValueOrDefault(CarFaultKind.ParentLoop)
            + byKind.GetValueOrDefault(CarFaultKind.MarkerUnresolved)
            + byKind.GetValueOrDefault(CarFaultKind.NoBody);
        check("no shipped car raises a fault that would mean the toolkit misread it", misread == 0,
            $"{misread} such faults");
        // The same distinction the aggregate carries on every fault, so the panel can say "this car is odd"
        // where it would otherwise say "this car did not stitch" about an archive nobody has touched.
        check("…and every fault a shipped car DOES raise says so on itself",
            breaks == 0 && byKind.Values.Sum() == 41,
            $"{breaks} of {byKind.Values.Sum()} faults claim no shipped car is written that way");

        // THE NUMBER THE SPEC DELIBERATELY DID NOT CARRY. car-anatomy.md holds 217 door parts and 193 door
        // rows in two places that never referred to each other, and whether the difference was 24 or whether
        // the two counts were even comparable was unestablished. They are comparable, and it is 24 — every one
        // of them a door part with no row of its own, on 7 cars.
        check("the door lists do not line up, by exactly the 24 the two figures invited",
            doorParts == 217 && doorRows == 193
            && byKind.GetValueOrDefault(CarFaultKind.ComponentWithoutRow) == 24
            && carsByKind.GetValueOrDefault(CarFaultKind.ComponentWithoutRow) == 7,
            $"{doorParts} parts, {doorRows} rows, "
            + $"{byKind.GetValueOrDefault(CarFaultKind.ComponentWithoutRow)} faults on "
            + $"{carsByKind.GetValueOrDefault(CarFaultKind.ComponentWithoutRow)} cars");
        check("…while the window lists line up exactly, so the fault is about doors and not about the rule",
            windowParts == 527 && windowRows == 527, $"{windowParts} parts, {windowRows} rows");

        // The other direction, and it is one car: the half-track's axles name the hinge bones of its tracks,
        // which carry no geometry and therefore mint nothing. A row pointing at a part of the car the tree
        // cannot show is exactly what this is for.
        check("a row with no component is 16 axle rows on one tracked vehicle",
            byKind.GetValueOrDefault(CarFaultKind.RowWithoutComponent) == 16
            && carsByKind.GetValueOrDefault(CarFaultKind.RowWithoutComponent) == 1,
            $"{byKind.GetValueOrDefault(CarFaultKind.RowWithoutComponent)} rows on "
            + $"{carsByKind.GetValueOrDefault(CarFaultKind.RowWithoutComponent)} cars");

        check("one shipped bone still holds its seat in the split table with no face left in it",
            byKind.GetValueOrDefault(CarFaultKind.BareComponentLostGeometry) == 1
            && carsByKind.GetValueOrDefault(CarFaultKind.BareComponentLostGeometry) == 1,
            $"{byKind.GetValueOrDefault(CarFaultKind.BareComponentLostGeometry)} bones");

        // A car's model is on the name table on 0 of 85 and REACHED from it on 85 of 85 — one hop, through a
        // holder frame. Asking whether the frame is listed rather than reached would call every shipped car
        // invisible, which is how this rule was found.
        check("the name table REACHES every shipped car's model, and lists none of them",
            modelsOnTable == cars && cars > 0 && framesOnTable == 247
            && onTableTypes.GetValueOrDefault(nameof(FrameObjectModel)) == 0
            && byKind.GetValueOrDefault(CarFaultKind.FrameNotOnNameTable) == 0,
            $"{modelsOnTable} of {cars} reached, {framesOnTable} frames listed, "
            + $"{onTableTypes.GetValueOrDefault(nameof(FrameObjectModel))} of them models");
        check("…and the 5 components whose bone also names a frame are all that model itself",
            componentsNamingAFrame == 5 && componentFramesOffTable == 0,
            $"{componentsNamingAFrame} components, {componentFramesOffTable} out of reach");
    }

    // ── the kinds the corpus itself carries ──

    private static void Natural(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ the fault kinds a shipped car already carries ════");

        // A tracked vehicle: axle rows naming hinge bones nothing draws, and two doors with no door row.
        if (Open(folder, "half_track_pha") is { } track)
        {
            CarFault[] rows = [.. track.Faults.Where(f => f.Kind == CarFaultKind.RowWithoutComponent)];
            CarFault[] doors = [.. track.Faults.Where(f => f.Kind == CarFaultKind.ComponentWithoutRow)];
            sb.AppendLine($"  half_track_pha: {track.Components.Count} components, "
                + $"{track.Faults.Count} faults");
            foreach (CarFault fault in track.Faults.Take(4)) sb.AppendLine("    " + fault);

            // "Axle " is exclusive because the rot-wing rows are labelled "Rot wing" — three collections come
            // out of one loop, and a label starting with another's would let a regression swap which of them
            // is reported while both the count and this prefix still matched.
            int axles = rows.Count(f => f.What.StartsWith("Axle ", StringComparison.Ordinal));
            check("a car whose rows name bones nothing draws opens, and says which rows",
                rows.Length == 16 && axles == 16,
                $"{rows.Length} rows, {axles} of them axles");
            // The other half hangs on a component, which is what puts it on a ROW of the tree rather than
            // only in the list — the ticket's "shown as a fault on the component it belongs to".
            check("a door with no door row is a fault ON that door, and the door is still a component",
                doors.Length == 2
                && doors.All(f => track.ComponentById(f.Component) is { Kind: "door" })
                && doors.All(f => track.FaultsOf(f.Component).Contains(f)),
                $"{doors.Length} doors, "
                + $"{doors.Count(f => track.ComponentById(f.Component) != null)} of them found in the tree");
        }
        else
        {
            check("half_track_pha is extracted", false, "the tracked vehicle is not in the corpus");
        }

        // And the one bone in the whole corpus whose geometry is gone while its seat in the split table is
        // not: without this it would simply stop being a row and nobody would know it ever was one.
        if (Open(folder, focus) is { } car)
        {
            CarFault? lost = car.Faults.FirstOrDefault(f =>
                f.Kind == CarFaultKind.BareComponentLostGeometry);
            sb.AppendLine($"  {focus}: {lost?.What ?? "no lost geometry"}");
            check("a bone that has lost its last face is reported rather than quietly not minted",
                lost != null && lost.What.Contains("deform_top_roof", StringComparison.Ordinal)
                && car.Components.All(c => c.Name != "deform_top_roof"),
                lost?.What ?? "nothing reported");
        }
        else
        {
            check("the focus car is extracted", false, focus);
        }
    }

    // ── what must stay quiet ──

    /// <summary>
    /// The two ways a fault pass can start shouting about a car nobody has touched.
    ///
    /// <para>
    /// A diagnosis that fires on healthy cars is one nobody reads on a broken one, so the cases where the
    /// aggregate knows LESS than usual — the far level, where 4882 of 5046 components have no geometry, and
    /// a car whose frame resource would not open at all — are asserted to say no more than they did before.
    /// </para>
    /// </summary>
    private static void Quiet(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ what must stay quiet ════");

        // The far level. Rows are LOD-independent — an axle row names the same bone at both — while the bare
        // components they resolve against are not, so testing a row against "did this mint a component HERE"
        // would report most of the car's rows the moment the switch moved.
        foreach (string name in new[] { "half_track_pha", focus })
        {
            Car? near = Open(folder, name, lod: 0);
            Car? far = Open(folder, name, lod: 1);
            if (near == null || far == null) { check($"{name} is extracted", false, name); continue; }

            int rowsNear = near.Faults.Count(f => f.Kind == CarFaultKind.RowWithoutComponent);
            int rowsFar = far.Faults.Count(f => f.Kind == CarFaultKind.RowWithoutComponent);
            sb.AppendLine($"  {name}: {near.Components.Count} components at LOD 0 and "
                + $"{far.Components.Count} at LOD 1, row faults {rowsNear} → {rowsFar}");
            check($"switching {name} to the far level says nothing new about its rows",
                rowsNear == rowsFar && far.Components.Count < near.Components.Count,
                $"{rowsNear} near, {rowsFar} far");
        }

        // And a car whose frame resource could not be opened. It stitches on purpose (Car.ReadFrom catches
        // the read), and there is nothing to line the prefab's rows up against — sixty raw-hex lines saying
        // so would bury the one thing worth reading, which is that every part's bone is unresolved.
        if (Open(folder, focus) is { } whole)
        {
            Car blind = Car.Stitch(whole.Prefab, frames: null);
            sb.AppendLine($"  {focus} stitched with no frame graph at all: {blind.Faults.Count} faults, "
                + string.Join(", ", blind.Faults.Select(f => f.Kind).Distinct()));
            // What it DOES say — every part's bone is gone, and the references that reached through the rig
            // reach nothing — is the honest reading of a car with no rig. What it must not do is add a line
            // per prefab row on top of that, which is a second telling of the same fact in the noisiest form
            // available.
            check("a car whose rig would not load says its bones are gone, not that every row is",
                blind.Faults.Count > 0
                && !blind.Faults.Any(f => f.Kind == CarFaultKind.RowWithoutComponent)
                && blind.Faults.Any(f => f.Kind == CarFaultKind.ComponentBoneUnresolved),
                string.Join(", ", blind.Faults.Select(f => f.Kind).Distinct()));
        }
    }

    // ── and the ones it does not, made on purpose ──

    private static void Synthetic(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ the fault kinds no shipped car carries ════");
        sb.AppendLine("  Four of the twelve are not reachable from here — PartWithoutBone,");
        sb.AppendLine("  DuplicateComponentBone, ParentLinksDisagree and ParentLoop each need a PART's bone");
        sb.AppendLine("  hash or its parent link written, and the prefab writer exposes neither. Renaming a");
        sb.AppendLine("  bone cannot stand in: the hash on the part does not move with it, which is why a");
        sb.AppendLine("  rename reads as an unresolved bone rather than as a clash. They are asserted at 0");
        sb.AppendLine("  over the corpus above and nowhere else.");

        // ── a rename made in Blender ──
        if (Open(folder, focus) is not { Frames: not null } renamed)
        {
            check("the focus car is extracted", false, focus);
            return;
        }
        CarComponent? door = renamed.Components.FirstOrDefault(c =>
            !c.IsBare && c.BoneJoint >= 0 && c.PartType != 1);
        HashName[] bones = Bones(renamed);
        if (door == null || bones.Length == 0)
        {
            check("the focus car has a part to break", false, focus);
            return;
        }
        int wholeParts = renamed.Components.Count(c => !c.IsBare);
        bones[door.BoneJoint].String += "_renamedInBlender";
        Car broken = Car.Stitch(renamed.Prefab, renamed.Frames, lod: 0);
        CarFault? gone = broken.Faults.FirstOrDefault(f =>
            f.Kind == CarFaultKind.ComponentBoneUnresolved);
        sb.AppendLine($"  bone of part {door.PartIndex} renamed: {gone?.What ?? "nothing reported"}");
        check("a component whose bone no longer resolves is shown broken, not hidden",
            gone != null && broken.ComponentById(gone.Component) is { BoneResolves: false }
            && broken.Components.Count(c => !c.IsBare) == wholeParts,
            gone?.What ?? "nothing reported");

        // ── a marker that reaches no bone ──
        if (Open(folder, focus) is { Frames: not null } stray)
        {
            CarMarker? marker = stray.Markers.FirstOrDefault(m => m.Resolved && !m.OnOwnBone);
            FrameObjectBase? frame = marker == null
                ? null
                : stray.Frames.FrameObjects.Values.OfType<FrameObjectBase>()
                    .FirstOrDefault(f => f.Name?.String is { Length: > 0 } n
                        && Fnv64.Hash(n) == marker.Frame);
            if (frame?.Name != null)
            {
                frame.Name.Set(frame.Name.String + "_moved");
                Car homeless = Car.Stitch(stray.Prefab, stray.Frames, lod: 0);
                CarFault? adrift = homeless.Faults.FirstOrDefault(f =>
                    f.Kind == CarFaultKind.MarkerUnresolved);
                sb.AppendLine("  a marker's frame renamed: " + (adrift?.What ?? "nothing reported"));
                check("a marker whose frame reaches no bone is named rather than dropped",
                    adrift != null, adrift?.What ?? "nothing reported");
            }
        }

        // ── a bare component whose split went with a push ──
        //
        // The other signature of lost geometry, and the irreversible one: the seat in the split table is gone
        // too, so nothing on disk remembers the bone ever drew anything. Only the previous stitch does.
        if (Open(folder, focus) is { Frames: not null } before)
        {
            CarComponent? bare = before.Components.FirstOrDefault(c => c.IsBare && c.BoneJoint >= 0);
            FrameObjectModel model = before.Frames.FrameObjects.Values.OfType<FrameObjectModel>().First();
            if (bare != null)
            {
                model.BlendMeshSplits = [.. model.BlendMeshSplits.Where(s =>
                    !string.Equals(s.JointName, bare.Name, StringComparison.Ordinal)
                    && Named(s, model, before) != bare.BoneHash)];

                Car after = Car.Stitch(before.Prefab, before.Frames, lod: 0, previous: before);
                CarFault? lost = after.Faults.FirstOrDefault(f =>
                    f.Kind == CarFaultKind.BareComponentLostGeometry && f.Component == bare.Id);
                Car blind = Car.Stitch(before.Prefab, before.Frames, lod: 0);
                Car far = Car.Stitch(before.Prefab, before.Frames, lod: 1, previous: before);

                sb.AppendLine($"  the split of \"{bare.Name}\" dropped: "
                    + (lost?.What ?? "nothing reported"));
                check("a bare component that has lost its last geometry is reported, not quietly unminted",
                    lost != null && after.ComponentById(bare.Id) == null,
                    lost?.What ?? "nothing reported");
                check("…only against the previous stitch, because nothing on disk remembers it drew",
                    !blind.Faults.Any(f => f.Kind == CarFaultKind.BareComponentLostGeometry
                        && f.Component == bare.Id),
                    "a car opened cold claimed to know");
                // The far level is where this check would do its damage if it were not LOD-scoped: 4882 of
                // 5046 components have no geometry there, so a comparison across a switch would report the
                // whole car as lost. The dead SEAT below is a different question and rightly still answered
                // there — the split table is one block, shared by both levels.
                check("…and never across a LOD switch, where 96.7 % of components have no counterpart",
                    !far.Faults.Any(f => f.Kind == CarFaultKind.BareComponentLostGeometry
                        && f.Component.IsSet),
                    $"{far.Faults.Count(f => f.Kind == CarFaultKind.BareComponentLostGeometry && f.Component.IsSet)} "
                    + "components called lost at the far level");
            }
        }

        // ── a car the name table does not reach ──
        if (Open(folder, focus) is { Frames: not null } dark)
        {
            FrameObjectModel model = dark.Frames.FrameObjects.Values.OfType<FrameObjectModel>().First();
            var cleared = new List<FrameObjectBase>();
            for (FrameObjectBase? at = model; at != null; at = at.Parent)
            {
                if (at.IsOnFrameTable) { at.IsOnFrameTable = false; cleared.Add(at); }
            }
            Car invisible = Car.Stitch(dark.Prefab, dark.Frames, lod: 0);
            CarFault? unlisted = invisible.Faults.FirstOrDefault(f =>
                f.Kind == CarFaultKind.FrameNotOnNameTable);
            sb.AppendLine($"  {cleared.Count} holder frame(s) taken off the table: "
                + (unlisted?.What ?? "nothing reported"));
            check("a component the frame name table does not reach is flagged before the car is spawned",
                cleared.Count > 0 && unlisted != null
                && invisible.ComponentById(unlisted.Component) is { Kind: "body" },
                unlisted?.What ?? "nothing reported");
        }

        // ── no body at all ──
        if (Open(folder, focus) is { Frames: not null } headless)
        {
            HashName[] rig = Bones(headless);
            CarComponent? body = headless.Body;
            if (body is { BoneJoint: >= 0 })
            {
                rig[body.BoneJoint].String += "_gone";
                headless.Prefab.SetCarValue(CarValueSlot.DeformPartType, body.PartIndex, 0, 0);
                Car nobody = Car.Stitch(headless.Prefab, headless.Frames, lod: 0);
                CarFault? none = nobody.Faults.FirstOrDefault(f => f.Kind == CarFaultKind.NoBody);
                sb.AppendLine("  the scale bone renamed and the body part retyped: "
                    + (none?.What ?? "nothing reported"));
                check("a car with no body opens and says so, rather than losing its homeless markers",
                    none != null && nobody.Body == null
                    && nobody.Markers.Count == headless.Markers.Count,
                    none?.What ?? "nothing reported");
            }
        }
    }

    /// <summary>The bone a split names, resolved the way the aggregate resolves it — through level 0's own
    /// remap table, because a raw <c>BlendIndex</c> is a bone id 2.2 % of the time.</summary>
    private static ulong Named(FrameObjectModel.WeightedByMeshSplit split, FrameObjectModel model, Car car)
    {
        byte[] remap;
        try
        {
            FrameBlendInfo.BoneIndexInfo[] levels = model.GetBlendInfoObject().BoneIndexInfos ?? [];
            remap = levels.Length > 0 ? levels[0].BoneRemapIDs ?? [] : [];
        }
        catch (Exception) { return 0; }
        if (split.BlendIndex >= remap.Length) return 0;

        HashName[] bones = Bones(car);
        int bone = remap[split.BlendIndex];
        return bone >= 0 && bone < bones.Length ? Fnv64.Hash(bones[bone].ToString()) : 0;
    }

    // ── and a broken car still saves byte for byte ──

    private static void Verbatim(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ a car carrying faults still saves byte for byte ════");

        // The shipped ones first: three cars between them carrying every fault kind the corpus has.
        int carried = 0, identical = 0;
        foreach (string name in new[] { "half_track_pha", "lassiter_69_destr", focus })
        {
            if (Open(folder, name) is not { PrefabPath: not null } car || car.Faults.Count == 0) continue;
            carried++;
            CarSave saved = car.Save(Redirect(folder, name));
            byte[] was = File.ReadAllBytes(car.PrefabPath);
            byte[] now = saved.Written.Count == 1 ? File.ReadAllBytes(saved.Written[0]) : [];
            bool same = saved.Ok && was.AsSpan().SequenceEqual(now);
            if (same) identical++;
            sb.AppendLine($"  {name}: {car.Faults.Count} faults, saved "
                + $"{(same ? "byte-identical" : "DIFFERENT")}"
                + (saved.Ok ? "" : " — " + string.Join("; ", saved.Lost.Take(2))));
        }
        check("a shipped car the toolkit could not fully stitch saves back byte-identical",
            carried == 3 && identical == carried, $"{identical} of {carried}");

        // And a car broken on purpose in the rig. Its prefab was never touched, so the whole point is that the
        // fault path leaves it alone: reporting damage must not be a reason to rewrite anything.
        if (Open(folder, focus) is not { PrefabPath: not null, Frames: not null } car2) return;
        HashName[] bones = Bones(car2);
        CarComponent? part = car2.Components.FirstOrDefault(c => !c.IsBare && c.BoneJoint >= 0
            && c.PartType != 1);
        if (part == null) return;
        bones[part.BoneJoint].String += "_gone";
        foreach (object o in car2.Frames.FrameObjects.Values)
        {
            if (o is FrameObjectBase f) f.IsOnFrameTable = false;
        }

        Car wrecked = Car.Stitch(car2.Prefab, car2.Frames, lod: 0, previous: null, car2.PrefabPath,
            car2.Extracted);
        CarSave save = wrecked.Save(Redirect(folder, focus + "_wrecked"));
        byte[] original = File.ReadAllBytes(car2.PrefabPath);
        byte[] written = save.Written.Count == 1 ? File.ReadAllBytes(save.Written[0]) : [];
        sb.AppendLine($"  {focus} with its rig broken: {wrecked.Faults.Count} faults, "
            + $"{save.Written.Count} file(s) written");
        check("a car broken on purpose saves its prefab back byte-identical and rewrites nothing else",
            wrecked.Faults.Count > 0 && save.Ok && save.Written.Count == 1
            && original.AsSpan().SequenceEqual(written),
            save.Ok ? $"{save.Written.Count} written" : string.Join("; ", save.Lost.Take(2)));
    }

    // ── opening one car ──

    private static Car? Open(string folder, string name, int lod = 0)
    {
        string extracted = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, name + ".sds")));
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) return null;
        try { return Car.ReadFrom(extracted, lod); }
        catch (Exception) { return null; }
    }

    private static HashName[] Bones(Car car)
    {
        if (car.Frames?.FrameObjects == null) return [];
        FrameObjectModel? model = car.Frames.FrameObjects.Values.OfType<FrameObjectModel>()
            .FirstOrDefault();
        try { return model?.GetSkeletonObject().BoneNames ?? []; }
        catch (Exception) { return []; }
    }

    private static Func<string, string> Redirect(string folder, string name) =>
        path => Path.Combine(Scratch, name, Path.GetFileName(path));

    private static void Clear()
    {
        try
        {
            if (Directory.Exists(Scratch)) Directory.Delete(Scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover mirror from a previous run only costs disk: every file this run compares is one it
            // wrote itself, and a redirected write overwrites whatever was there.
        }
    }
}
