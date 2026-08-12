using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Whether the component view is the car. The <c>Car</c> aggregate stitches four parallel lists into
/// components, hangs the markers off them and reports what it could not stitch; this probe walks every
/// shipped car through it and ASSERTS the counts the corpus was measured to hold, rather than printing them
/// for someone to read.
/// <para>
/// The numbers come from <c>docs/car-anatomy.md</c> and were measured by <c>--probe-car-physics</c>'s census
/// on 2026-08-08: 1698 deform parts, 2587 bones with geometry that no part claims, 101 without, 1402 deform
/// handles, 1081 markers, 934 parts carrying both copies of their parent link. The geometry census here is
/// computed independently of the aggregate, off the split table, so this compares two readings rather than
/// one reading with itself.
/// </para>
/// <para>
/// Reads only; nothing is written. The rename checks mutate a frame graph in memory and never save it.
/// Output: %TEMP%\illusion_car_components.txt
/// </para>
/// </summary>
internal static class CarComponentProbes
{
    internal static void RunCarComponentsProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_components.txt");
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

            Census(sb, folder, Check);
            Reachable(sb, folder, Check);
            Identity(sb, folder, focus, Check);
            Broken(sb, folder, focus, Check);
            sb.Insert(0, $"CAR COMPONENTS PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "CAR COMPONENTS PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // ── the corpus: does the aggregate reassemble every shipped car into the parts it was measured to have ──

    private static void Census(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("════ the component census over every shipped car ════");

        int cars = 0;
        int partComponents = 0, bareComponents = 0, expectedBare = 0, bareByPieces = 0, boneNoGeometry = 0;
        int carsWherePartsMatch = 0, carsWhereBareMatch = 0;

        int handles = 0, handlesThatAreAComponent = 0, handlesThatArePartBones = 0;
        int markers = 0, markersUnresolved = 0, markersOnAComponent = 0;
        var markersByRole = new Dictionary<CarMarkerRole, int>();

        int parentIndexTotal = 0, parentIndexAgrees = 0;
        int parentLinkTotal = 0, parentLinkApplied = 0;

        int resolvedComponents = 0, lookupFindsItself = 0, carsWithDuplicateBone = 0;

        int bodies = 0, bodiesOnScaleBone = 0, bodiesOfKindBody = 0;
        var bodyBoneSpellings = new Dictionary<string, int>(StringComparer.Ordinal);

        var faultsByKind = new Dictionary<CarFaultKind, int>();
        var faultExamples = new List<string>();
        var emptySplitBones = new List<string>();
        var mismatches = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            // Opened through the aggregate itself: finding the car in an archive is not what is under test
            // here, and re-deriving it would only give this probe a second way of being wrong. What IS
            // measured independently — the rig and its geometry — is read below, off the frame graph.
            Car? car;
            try { car = Car.ReadFrom(extracted); }
            catch (Exception) { continue; }
            if (car?.Frames?.FrameObjects == null) continue;
            if (car.Prefab.Car is not { DeformPartCount: > 0 } assembly) continue;
            cars++;

            FrameResource frames = car.Frames;
            IReadOnlyList<CarDeformPart> parts = car.Prefab.CarDeformParts;

            // ── the independent reading: the rig and its geometry, off the split table ──
            string[] bonesByJoint = BonesByJoint(frames);
            Dictionary<ulong, string> boneNames = BoneNames(frames);
            Dictionary<ulong, (int Pieces, int Drawn)> geometry = PiecesPerBone(frames, bonesByJoint);

            var partBones = new HashSet<ulong>();
            var handleBones = new HashSet<ulong>();
            foreach (CarDeformPart part in parts)
            {
                if (part.Frame != 0) partBones.Add(part.Frame);
                foreach (CarDeformHandle handle in part.Handles) handleBones.Add(handle.JointName);
            }
            handlesThatArePartBones += handleBones.Count(h => partBones.Contains(h));

            int bareHere = 0, noGeometryHere = 0;
            var expectedBareBones = new HashSet<ulong>();
            foreach (ulong bone in boneNames.Keys)
            {
                if (partBones.Contains(bone) || handleBones.Contains(bone)) continue;
                (int pieces, int drawn) = geometry.GetValueOrDefault(bone);
                if (pieces > 0) bareByPieces++;
                if (drawn > 0) { bareHere++; expectedBareBones.Add(bone); }
                else noGeometryHere++;

                // A bone with a seat in the split table whose pieces hold no face at all. This is where the
                // census's piece-counting reading and this one part company, so it is named rather than
                // absorbed into a total.
                if (pieces > 0 && drawn == 0)
                {
                    emptySplitBones.Add(
                        $"{Path.GetFileNameWithoutExtension(sds.Name)}: "
                        + $"\"{boneNames.GetValueOrDefault(bone, "?")}\" — {pieces} pieces, none holding a "
                        + "face; " + DescribeSplit(frames, bonesByJoint, bone));
                }
            }
            expectedBare += bareHere;
            boneNoGeometry += noGeometryHere;

            // ── against what the aggregate produced ──
            int partsHere = car.Components.Count(c => !c.IsBare);
            int bareFromAggregate = car.Components.Count(c => c.IsBare);
            partComponents += partsHere;
            bareComponents += bareFromAggregate;
            if (partsHere == parts.Count) carsWherePartsMatch++;
            if (bareFromAggregate == bareHere) carsWhereBareMatch++;
            else
            {
                // The aggregate and this probe disagree about which bones are components. Name them: the
                // count alone says nothing anybody can act on.
                var minted = car.Components.Where(c => c.IsBare).Select(c => c.BoneHash).ToHashSet();
                foreach (ulong bone in expectedBareBones.Where(b => !minted.Contains(b))
                             .Concat(minted.Where(b => !expectedBareBones.Contains(b))))
                {
                    mismatches.Add(
                        $"{Path.GetFileNameWithoutExtension(sds.Name)}: \"{boneNames.GetValueOrDefault(bone, "?")}\" "
                        + $"— {geometry.GetValueOrDefault(bone).Pieces} pieces of which "
                        + $"{geometry.GetValueOrDefault(bone).Drawn} drawn, aggregate "
                        + $"{(minted.Contains(bone) ? "minted" : "did not mint")}");
                }
            }

            var componentOfPart = new Dictionary<int, CarComponent>();
            foreach (CarComponent component in car.Components)
            {
                if (!component.IsBare) componentOfPart[component.PartIndex] = component;
                handles += component.Handles.Count;
                handlesThatAreAComponent +=
                    component.Handles.Count(h => car.ComponentOfBone(h.BoneHash) != null);
                if (!component.BoneResolves) continue;
                resolvedComponents++;
                if (ReferenceEquals(car.ComponentOfBone(component.BoneHash), component)) lookupFindsItself++;
            }
            if (car.Faults.Any(f => f.Kind == CarFaultKind.DuplicateComponentBone)) carsWithDuplicateBone++;

            // ── nesting: both copies of the parent link, and whether the tree actually follows it ──
            foreach (CarDeformPart part in parts)
            {
                if (componentOfPart.GetValueOrDefault(part.Index) is not { } component) continue;
                CarComponent? byHash = car.ComponentOfBone(part.ParentFrame);
                if (part.ParentIndex >= 0)
                {
                    parentIndexTotal++;
                    if (ReferenceEquals(componentOfPart.GetValueOrDefault(part.ParentIndex), byHash))
                    {
                        parentIndexAgrees++;
                    }
                }
                if (byHash == null || ReferenceEquals(byHash, component)) continue;
                parentLinkTotal++;
                if (ReferenceEquals(component.Parent, byHash)) parentLinkApplied++;
            }

            // ── markers ──
            markers += car.Markers.Count;
            markersUnresolved += car.Markers.Count(m => !m.Resolved);
            markersOnAComponent += car.Components.Sum(c => c.Markers.Count);
            foreach (CarMarker marker in car.Markers)
            {
                markersByRole[marker.Role] = markersByRole.GetValueOrDefault(marker.Role) + 1;
            }

            // ── the body, and the four spellings of the bone it stands on ──
            if (car.Body is { } body)
            {
                bodies++;
                bodyBoneSpellings[body.Name] = bodyBoneSpellings.GetValueOrDefault(body.Name) + 1;
                if (body.BoneHash == assembly.ScaleBone) bodiesOnScaleBone++;
                if (body.PartType == 1) bodiesOfKindBody++;
            }

            foreach (CarFault fault in car.Faults)
            {
                faultsByKind[fault.Kind] = faultsByKind.GetValueOrDefault(fault.Kind) + 1;
                if (faultExamples.Count < 12)
                {
                    faultExamples.Add($"{Path.GetFileNameWithoutExtension(sds.Name)}: {fault}");
                }
            }
        }

        sb.AppendLine($"  cars with a car prefab carrying deform parts: {cars}");
        check("the corpus is the 85 cars the census measured", cars == 85, $"{cars} cars");

        sb.AppendLine($"\n  components:");
        sb.AppendLine($"    from a deform part   {partComponents}");
        sb.AppendLine($"    bare (a bone with geometry no part claims)   {bareComponents}, "
            + $"independently counted {expectedBare}");
        sb.AppendLine($"    bones no part claims that carry NO geometry  {boneNoGeometry} (these mint none; "
            + "101 of them under the census's reading)");
        sb.AppendLine($"    the same bones counted by SPLIT PIECES, the way the 2026-08-08 census counted "
            + $"them: {bareByPieces}");
        foreach (string bone in emptySplitBones) sb.AppendLine("      " + bone);
        foreach (string mismatch in mismatches) sb.AppendLine("      MISMATCH " + mismatch);
        check("every deform part yields exactly one component",
            cars > 0 && partComponents == 1698 && carsWherePartsMatch == cars,
            $"{partComponents} components, one per part on {carsWherePartsMatch} of {cars} cars");
        check("every bone with geometry that no deform part claims yields a bare component",
            bareComponents == 2586 && bareComponents == expectedBare && carsWhereBareMatch == cars,
            $"{bareComponents} minted, {expectedBare} expected, agreeing on {carsWhereBareMatch} of {cars}");
        // The census counted PIECES and reported 2587. Exactly one of those pieces-bearing bones draws
        // nothing at all, so the two readings are both asserted rather than one of them quietly rewritten.
        check("…which is the census's 2587 minus the one bone whose every piece holds no face",
            bareByPieces == 2587 && bareComponents == bareByPieces - 1 && emptySplitBones.Count == 1,
            $"{bareByPieces} by pieces, {bareComponents} by faces, {emptySplitBones.Count} holding no face");
        check("a bone with no geometry mints nothing",
            boneNoGeometry == 102 && bareComponents + boneNoGeometry == 2688,
            $"{boneNoGeometry} such bones, none of them a component");

        sb.AppendLine($"\n  bone-to-component lookup:");
        sb.AppendLine($"    components whose bone resolves      {resolvedComponents}");
        sb.AppendLine($"    the lookup returns that very one    {lookupFindsItself}");
        sb.AppendLine($"    cars where two components share a bone  {carsWithDuplicateBone}");
        check("bone-to-component is a straight lookup, unambiguous on every car",
            cars > 0 && carsWithDuplicateBone == 0 && lookupFindsItself == resolvedComponents,
            $"{lookupFindsItself} of {resolvedComponents}, ambiguous on {carsWithDuplicateBone} of {cars}");

        sb.AppendLine($"\n  nesting, off the prefab's own parent link:");
        sb.AppendLine($"    parts carrying BOTH copies of it        {parentIndexTotal}");
        sb.AppendLine($"    the two copies name the same component  {parentIndexAgrees}");
        sb.AppendLine($"    links that name another component       {parentLinkTotal}");
        sb.AppendLine($"    …and the component hangs off it         {parentLinkApplied}");
        check("the parent link's two copies agree, and the tree follows it",
            parentIndexTotal == 934 && parentIndexAgrees == parentIndexTotal
            && parentLinkTotal > 0 && parentLinkApplied == parentLinkTotal,
            $"{parentIndexAgrees} of {parentIndexTotal} agree, {parentLinkApplied} of {parentLinkTotal} applied");

        sb.AppendLine($"\n  deform handles:");
        sb.AppendLine($"    listed on their component        {handles}");
        sb.AppendLine($"    that are a component of their own {handlesThatAreAComponent}");
        sb.AppendLine($"    that are some part's OWN bone     {handlesThatArePartBones}");
        check("a deform handle is a list on its component, never a component of its own",
            handles == 1402 && handlesThatAreAComponent == 0 && handlesThatArePartBones == 0,
            $"{handles} handles, {handlesThatAreAComponent} of them components");

        sb.AppendLine($"\n  markers, by role:");
        foreach ((CarMarkerRole role, int count) in markersByRole.OrderBy(p => p.Key))
        {
            sb.AppendLine($"    {role,-16} {count}");
        }
        sb.AppendLine($"    total {markers}, unresolved {markersUnresolved}, "
            + $"hanging off a component {markersOnAComponent}");
        check("every marker resolves to the component owning its bone, or to the body",
            markers == 1081 && markersUnresolved == 0 && markersOnAComponent == markers,
            $"{markers} markers, {markersUnresolved} unresolved, {markersOnAComponent} placed");

        sb.AppendLine($"\n  the body, and the bone it stands on:");
        sb.AppendLine($"    cars with a body component        {bodies}");
        sb.AppendLine($"    …standing on the prefab's scale bone {bodiesOnScaleBone}");
        sb.AppendLine($"    …and of part kind \"body\"           {bodiesOfKindBody}");
        sb.AppendLine("    spellings of that bone's name: " + string.Join(", ",
            bodyBoneSpellings.OrderByDescending(p => p.Value).Select(p => $"\"{p.Key}\"×{p.Value}")));
        int spellings = bodyBoneSpellings.Count;
        int commonest = bodyBoneSpellings.Count == 0 ? 0 : bodyBoneSpellings.Max(p => p.Value);
        // One name written several ways: the same letters, differing only in case and in whether the two
        // words are joined by a space or an underscore.
        bool oneName = bodyBoneSpellings.Keys
            .Select(n => n.ToLowerInvariant().Replace('_', ' ')).Distinct().Count() == 1;
        check("the body is found on every car, by the hash of the scale bone",
            cars > 0 && bodies == cars && bodiesOnScaleBone == cars && bodiesOfKindBody == cars,
            $"{bodies} of {cars}, on the scale bone {bodiesOnScaleBone}, of kind body {bodiesOfKindBody}");
        check("…which a string match could not do: it is one name written five ways",
            spellings == 5 && oneName && commonest < cars,
            $"{spellings} spellings, the commonest covering {commonest} of {cars} cars");

        sb.AppendLine($"\n  faults over the whole corpus:");
        if (faultsByKind.Count == 0) sb.AppendLine("    none");
        foreach ((CarFaultKind kind, int count) in faultsByKind.OrderByDescending(p => p.Value))
        {
            sb.AppendLine($"    {kind,-26} {count}");
        }
        foreach (string example in faultExamples) sb.AppendLine("      " + example);
        check("a shipped car stitches without a broken bone, a lost marker or a missing body",
            faultsByKind.GetValueOrDefault(CarFaultKind.PartWithoutBone) == 0
            && faultsByKind.GetValueOrDefault(CarFaultKind.ComponentBoneUnresolved) == 0
            && faultsByKind.GetValueOrDefault(CarFaultKind.DuplicateComponentBone) == 0
            && faultsByKind.GetValueOrDefault(CarFaultKind.ParentLinksDisagree) == 0
            && faultsByKind.GetValueOrDefault(CarFaultKind.ParentLoop) == 0
            && faultsByKind.GetValueOrDefault(CarFaultKind.MarkerUnresolved) == 0
            && faultsByKind.GetValueOrDefault(CarFaultKind.NoBody) == 0,
            string.Join(", ", faultsByKind.Select(p => $"{p.Key}×{p.Value}")));
    }

    // ── nothing the assembly holds is unreachable ──

    /// <summary>
    /// Every number and every frame reference the retired Prefab tab put on screen, asked for again through
    /// the component view — because "the tab is gone and nothing it showed is now unreachable" is a promise
    /// about a list, and a list is exactly the sort of thing that loses an entry unnoticed.
    ///
    /// <para>
    /// Reachability is the ADDRESS, not the label: a field carries the slot it is written through, so this
    /// walks every component's damage fields, its own-bone rows, its handles and its markers, collects the
    /// addresses they offer, and compares that set with what the tab used to reach. A slot no car in the
    /// corpus carries at all — a bus entry on a coupé — is not a hole, so the comparison is over the whole
    /// corpus and counts a slot as reachable if any car offers it and as ABSENT if no car has a row for it.
    /// </para>
    /// </summary>
    private static void Reachable(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ what the retired Prefab tab showed, reached through the components ════");

        var values = new HashSet<CarValueSlot>();
        var frames = new HashSet<CarFrameSlot>();
        var carried = new HashSet<CarValueSlot>();
        var carriedFrames = new HashSet<CarFrameSlot>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            Car? car;
            try { car = Car.ReadFrom(extracted); }
            catch (Exception) { continue; }
            if (car?.Prefab.Car == null) continue;

            foreach (CarField field in Fields(car)) Take(field, values, frames);

            // What this car actually HAS a row for, so a slot nothing in the corpus carries can be told
            // apart from one the component view forgot.
            foreach (CarValueSlot slot in ShownValues)
            {
                if (!float.IsNaN(car.Prefab.GetCarValue(slot, 0))) carried.Add(slot);
            }
            foreach (CarFrameSlot slot in ShownFrames)
            {
                if (car.Prefab.CarSlotCount(slot) > 0) carriedFrames.Add(slot);
            }
        }

        var missingValues = ShownValues.Where(s => carried.Contains(s) && !values.Contains(s)).ToList();
        var missingFrames = ShownFrames.Where(s => carriedFrames.Contains(s) && !frames.Contains(s)).ToList();

        sb.AppendLine($"    numbers the tab showed: {ShownValues.Length}, carried by some car {carried.Count}, "
            + $"reachable on a component {values.Count(v => carried.Contains(v))}");
        sb.AppendLine($"    frame references: {ShownFrames.Length}, carried by some car {carriedFrames.Count}, "
            + $"reachable {frames.Count(v => carriedFrames.Contains(v))}");
        foreach (CarValueSlot slot in missingValues) sb.AppendLine($"      UNREACHABLE number {slot}");
        foreach (CarFrameSlot slot in missingFrames) sb.AppendLine($"      UNREACHABLE reference {slot}");
        foreach (CarValueSlot slot in ShownValues.Where(s => !carried.Contains(s)))
        {
            sb.AppendLine($"      (no shipped car carries {slot})");
        }
        foreach (CarFrameSlot slot in ShownFrames.Where(s => !carriedFrames.Contains(s)))
        {
            sb.AppendLine($"      (no shipped car carries {slot})");
        }

        check("every number the Prefab tab showed is edited on a component now",
            missingValues.Count == 0, string.Join(", ", missingValues));
        check("…and so is every frame reference it let you re-point",
            missingFrames.Count == 0, string.Join(", ", missingFrames));
    }

    /// <summary>Every field the component view offers on one car, wherever it hangs.</summary>
    private static IEnumerable<CarField> Fields(Car car)
    {
        foreach (CarComponent component in car.Components)
        {
            foreach (CarField field in component.DamageFields) yield return field;
            foreach (CarHandle handle in component.Handles)
            {
                foreach (CarField field in handle.Fields) yield return field;
            }
            foreach (CarComponentRow row in component.Rows)
            {
                foreach (CarField field in row.Fields) yield return field;
            }
            foreach (CarMarker marker in component.Markers)
            {
                foreach (CarField field in marker.Fields) yield return field;
            }
        }
    }

    private static void Take(CarField field, HashSet<CarValueSlot> values, HashSet<CarFrameSlot> frames)
    {
        if (field.Kind == CarFieldKind.Frame) frames.Add(field.FrameSlot);
        else values.Add(field.Slot);
    }

    /// <summary>
    /// The numbers the Prefab tab put on screen. The raw flag WORD is deliberately not among them: it is
    /// reachable as five named bits instead, and writing the word back through a float is the very bug that
    /// cost the top of a 0x80000508 — see <c>--probe-car-damage</c>.
    /// </summary>
    private static readonly CarValueSlot[] ShownValues =
    [
        CarValueSlot.SeatType, CarValueSlot.SeatPosition,
        CarValueSlot.DoorHandle, CarValueSlot.DoorLock,
        CarValueSlot.WindowDepth, CarValueSlot.WindowOpenable,
        CarValueSlot.AxleType, CarValueSlot.AxleBrakeDrumRadius, CarValueSlot.AxleBrakeDrumMass,
        CarValueSlot.AxleMass,
        CarValueSlot.ClimbBoxMin, CarValueSlot.ClimbBoxMax,
        CarValueSlot.BoneRange, CarValueSlot.ReduceBboxZ,
        CarValueSlot.DcbResistance, CarValueSlot.DcbHitpoints,
        CarValueSlot.DeformPartType, CarValueSlot.DeformPartEffectGroup, CarValueSlot.DeformCentreOfMass,
        CarValueSlot.DeformMass, CarValueSlot.DeformResistance,
        CarValueSlot.DeformSpeedMin, CarValueSlot.DeformSpeedMax,
        CarValueSlot.DeformEnergyStart, CarValueSlot.DeformEnergyDrop,
    ];

    /// <summary>The frame references it let you re-point.</summary>
    private static readonly CarFrameSlot[] ShownFrames =
    [
        CarFrameSlot.RootFrame, CarFrameSlot.ScaleBone, CarFrameSlot.Body, CarFrameSlot.RestBone,
        CarFrameSlot.SnowRest, CarFrameSlot.MotorVentilator,
        // A light is a marker WHILE its slot is filled, and a slot nothing fills has no marker to be on — so
        // these are asked for as chassis rows, and an empty one is still a row.
        CarFrameSlot.Headlight, CarFrameSlot.Backlight, CarFrameSlot.Toplight,
        CarFrameSlot.DrivingWheel, CarFrameSlot.LocalWindEmitter,
        CarFrameSlot.BusSeat, CarFrameSlot.EnterBus,
        CarFrameSlot.DrWheelSnapWheel, CarFrameSlot.DrWheelSnapLeft, CarFrameSlot.DrWheelSnapRight,
        CarFrameSlot.SeatDoor, CarFrameSlot.AxleBrakeDrum, CarFrameSlot.DcbDoor,
    ];

    // ── identity: the toolkit's own, and what it has to survive ──

    private static void Identity(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ identity across a re-stitch and a rename ════");

        string extracted = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, focus + ".sds")));
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
        {
            check("the focus car is extracted", false, focus);
            return;
        }

        Car? before = Car.ReadFrom(extracted);
        if (before?.Frames == null)
        {
            check("the focus car has a prefab and a frame graph", false, focus);
            return;
        }
        PrefabFile prefab = before.Prefab;
        FrameResource frames = before.Frames;

        sb.AppendLine($"  {focus}: {before.Components.Count} components "
            + $"({before.Components.Count(c => !c.IsBare)} with a deform part), "
            + $"{before.Markers.Count} markers, {before.Faults.Count} faults");

        // A re-stitch with nothing changed must not renumber the car — that is what "session-stable" means
        // in the only case a modder meets it constantly: a bridge push that touched something else.
        Car again = Car.Stitch(prefab, frames, lod: 0, previous: before);
        int kept = again.Components.Count(c => before.ComponentById(c.Id) != null);
        check("a re-stitch that changes nothing keeps every identity",
            again.Components.Count == before.Components.Count && kept == again.Components.Count,
            $"{kept} of {again.Components.Count} kept");

        // The rename. A bare component's bone is renamed in the rig, which is what the Blender bridge does to
        // one; nothing in the prefab names it, so the only thing that could carry the identity across is the
        // component's place in the rig — which is the whole reason identity is not the name hash.
        CarComponent? bare = before.Components.FirstOrDefault(c => c.IsBare && c.BoneJoint >= 0);
        if (bare == null)
        {
            check("the focus car has a bare component to rename", false, focus);
            return;
        }
        FrameObjectModel model = frames.FrameObjects.Values.OfType<FrameObjectModel>().First();
        HashName[] bones = model.GetSkeletonObject().BoneNames!;
        string was = bones[bare.BoneJoint].String;
        bones[bare.BoneJoint].String = was + "_renamed";

        Car renamed = Car.Stitch(prefab, frames, lod: 0, previous: before);
        CarComponent? nowCalled = renamed.ComponentById(bare.Id);
        sb.AppendLine($"  renamed \"{was}\" (joint {bare.BoneJoint}) to \"{was}_renamed\": "
            + $"identity {bare.Id} is now \"{nowCalled?.Name ?? "gone"}\"");
        check("a component keeps its identity across a rename of its bone",
            nowCalled != null && nowCalled.Name == was + "_renamed" && nowCalled.BoneJoint == bare.BoneJoint,
            nowCalled == null ? "the identity was not carried" : nowCalled.Name);
        check("…and its bone hash moved with the name, so it is a different bone by hash",
            nowCalled != null && nowCalled.BoneHash != bare.BoneHash,
            nowCalled == null ? "" : $"{bare.BoneHash:X16} → {nowCalled.BoneHash:X16}");

        bones[bare.BoneJoint].String = was;
    }

    // ── what could not be stitched is a list, not a silence ──

    private static void Broken(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ a car the aggregate cannot fully understand ════");

        string extracted = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, focus + ".sds")));
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) return;

        Car? whole = Car.ReadFrom(extracted);
        if (whole?.Frames == null) return;
        PrefabFile prefab = whole.Prefab;
        FrameResource frames = whole.Frames;

        CarComponent? door = whole.Components.FirstOrDefault(c => !c.IsBare && c.BoneJoint >= 0
            && c.PartType != 1);
        if (door == null) { check("the focus car has a part to break", false, focus); return; }

        // The rename made in Blender that the prefab knows nothing about: the bone is gone under that name,
        // the game silently stops moving the panel, and the component must be SHOWN broken rather than
        // vanish — the one failure mode a tolerant reader would otherwise hide.
        FrameObjectModel model = frames.FrameObjects.Values.OfType<FrameObjectModel>().First();
        HashName[] bones = model.GetSkeletonObject().BoneNames!;
        string was = bones[door.BoneJoint].String;
        bones[door.BoneJoint].String = was + "_gone";

        Car broken = Car.Stitch(prefab, frames, lod: 0);
        CarComponent? still = broken.Components.FirstOrDefault(c => c.PartIndex == door.PartIndex);
        CarFault? fault = broken.Faults.FirstOrDefault(f =>
            f.Kind == CarFaultKind.ComponentBoneUnresolved && f.Component == still?.Id);

        sb.AppendLine($"  renamed the bone of part {door.PartIndex} (\"{was}\", {door.Kind}) out from "
            + $"under it: {broken.Components.Count} components, {broken.Faults.Count} faults");
        if (fault != null) sb.AppendLine("    " + fault);
        check("a component whose bone no longer resolves is kept, not dropped",
            still != null && broken.Components.Count(c => !c.IsBare)
                == whole.Components.Count(c => !c.IsBare),
            still == null ? "the component vanished" : still.Name);
        check("…and it is reported as a fault rather than shown as if it were fine",
            still is { BoneResolves: false } && fault != null,
            fault?.What ?? "no fault reported");

        bones[door.BoneJoint].String = was;
    }

    // ── the independent reading of the rig, kept apart from the aggregate's own ──

    /// <summary>A bone's splits, piece by piece: how many material bursts and face ranges each piece holds,
    /// and whether those ranges land inside LOD 0's index buffer. What tells the two readings apart.</summary>
    private static string DescribeSplit(FrameResource frames, string[] bonesByJoint, ulong bone)
    {
        if (frames.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault() is not { } model)
        {
            return "no model";
        }
        byte[] remap = [];
        try
        {
            FrameBlendInfo.BoneIndexInfo[] levels = model.GetBlendInfoObject().BoneIndexInfos ?? [];
            if (levels.Length > 0) remap = levels[0].BoneRemapIDs ?? [];
        }
        catch (Exception) { return "no blend info"; }
        int indices = model.GetIndexBuffer(0)?.GetData()?.Length ?? 0;

        var said = new List<string>();
        foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
        {
            int id = split.BlendIndex < remap.Length ? remap[split.BlendIndex] : -1;
            if (id < 0 || id >= bonesByJoint.Length || Fnv64.Hash(bonesByJoint[id]) != bone) continue;
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
            {
                int bursts = piece.Data?.Length ?? 0;
                int ranges = piece.Data?.Sum(b => b.Data?.Length ?? 0) ?? 0;
                int outside = piece.Data?.Sum(b =>
                    b.Data?.Count(r => r.StartIndex + (r.NumFaces * 3) > indices) ?? 0) ?? 0;
                said.Add($"piece[{bursts} bursts, {ranges} ranges, {outside} past the buffer]");
            }
        }
        return said.Count == 0 ? "no split names it" : string.Join(" ", said) + $" of {indices} indices";
    }

    private static string[] BonesByJoint(FrameResource frames) =>
        frames.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault() is { } model
            ? [.. (model.GetSkeletonObject().BoneNames ?? []).Select(b => b.ToString())]
            : [];

    private static Dictionary<ulong, string> BoneNames(FrameResource frames)
    {
        var names = new Dictionary<ulong, string>();
        foreach (FrameObjectModel model in frames.FrameObjects.Values.OfType<FrameObjectModel>())
        {
            foreach (HashName bone in model.GetSkeletonObject().BoneNames ?? [])
            {
                string name = bone.ToString();
                if (name.Length > 0) names.TryAdd(Fnv64.Hash(name), name);
            }
        }
        return names;
    }

    /// <summary>
    /// What each bone carries at LOD 0, read independently of the aggregate — a probe that asks the same
    /// code the same question twice measures nothing. A split's bone is <c>BoneRemapIDs[BlendIndex]</c>
    /// through the flat remap table.
    /// <para>
    /// TWO readings, because they differ: <c>Pieces</c> counts a bone's split pieces, which is what the
    /// 2026-08-08 census counted, and <c>Drawn</c> counts only the pieces that hold a face. They part company
    /// on exactly one bone in the corpus, and that is asserted below rather than reconciled away.
    /// </para>
    /// </summary>
    private static Dictionary<ulong, (int Pieces, int Drawn)> PiecesPerBone(
        FrameResource frames, string[] bonesByJoint)
    {
        var found = new Dictionary<ulong, (int Pieces, int Drawn)>();
        if (frames.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault() is not { } model)
        {
            return found;
        }

        byte[] remap = [];
        try
        {
            FrameBlendInfo.BoneIndexInfo[] levels = model.GetBlendInfoObject().BoneIndexInfos ?? [];
            if (levels.Length > 0) remap = levels[0].BoneRemapIDs ?? [];
        }
        catch (Exception) { return found; }
        int indices = model.GetIndexBuffer(0)?.GetData()?.Length ?? 0;

        foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
        {
            int bone = split.BlendIndex < remap.Length ? remap[split.BlendIndex] : -1;
            if (bone < 0 || bone >= bonesByJoint.Length) continue;
            FrameObjectModel.BlendMeshSplitInfo[] all = split.Data ?? [];
            if (all.Length == 0) continue;

            int drawn = all.Count(piece => (piece.Data ?? []).Any(burst =>
                (burst.Data ?? []).Any(range => range.StartIndex + (range.NumFaces * 3) <= indices)));
            ulong hash = Fnv64.Hash(bonesByJoint[bone]);
            (int pieces, int wasDrawn) = found.GetValueOrDefault(hash);
            found[hash] = (pieces + all.Length, wasDrawn + drawn);
        }
        return found;
    }
}
