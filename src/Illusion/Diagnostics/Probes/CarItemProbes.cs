using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Assets.Prefabs;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What a car PART points at, measured before anything mints one. Adding a climb box, a fuel tank or an axle
/// is two things, not one: a row in the prefab, and a FRAME for that row to name. Today the toolkit only ever
/// writes the row and can only point it at a frame that already exists — so this probe answers, for every
/// kind of part the shipped cars carry, what sort of frame is on the other end of the hash: a bone of the rig,
/// a Dummy, a Point, a plain Frame, or nothing at all.
/// <para>
/// It also measures the axle PAIR, because the file stores pairs and the reader doubles them: whether the two
/// halves of a shipped pair name different frames decides whether "add an axle" may copy one row twice.
/// Reads only; nothing is written. Output: %TEMP%\illusion_car_items.txt
/// </para>
/// </summary>
internal static class CarItemProbes
{
    /// <summary>
    /// Every part that names a frame, and how many of them a car holds. The count comes off the LIST, not off
    /// <see cref="PrefabFile.CarSlotCount"/> — that one answers 1 for the slots which are a single field, which
    /// is right for the panel and wrong for a census.
    /// </summary>
    private static IEnumerable<(string Label, IReadOnlyList<ulong> Hashes)> PartsOf(CarPrefab car) =>
    [
        ("seat", [.. car.Seats.Select(s => s.Frame)]),
        ("seat's door", [.. car.Seats.Select(s => s.DoorFrame)]),
        ("door", [.. car.Doors.Select(d => d.Frame)]),
        ("window", [.. car.Windows.Select(w => w.Frame)]),
        ("axle", [.. car.Axles.Select(a => a.Frame)]),
        ("axle brake drum", [.. car.Axles.Select(a => a.BrakeDrum)]),
        ("climb box dummy", [.. car.ClimbBoxes.Select(b => b.Dummy)]),
        ("climb box bone", [.. car.ClimbBoxes.Select(b => b.Bone)]),
        ("driving wheel", car.DrivingWheels),
        ("fuel tank", car.FuelTanks),
        ("exhaust emitter", car.ExhaustEmitters),
        ("wiper", car.Wipers),
        ("headlight", [car.HeadlightModel]),
        ("backlight", [car.BacklightModel]),
        ("toplight", [car.ToplightModel]),
        ("body", [car.BodyFrame]),
        ("scale bone", [car.ScaleBone]),
        ("rest bone", [car.RestBone]),
        ("snow rest", [car.SnowRest]),
    ];

    /// <summary>
    /// WHERE a climb box's geometry actually is — the Dummy the row names, or the row's own Min/Max.
    ///
    /// <para>
    /// Asked because a minted climb box could not be climbed in game. A part is two things, a row and a
    /// frame, and the collision volumes have already taught this lesson once: when both carry a placement,
    /// only one of them is read. If the row's Min/Max agree with the Dummy's own bounds on the shipped cars,
    /// they are two copies of one fact and the row is the one a mint has to write — moving the Dummy would
    /// then do exactly nothing, which is the report.
    /// </para>
    /// </summary>
    private static void ClimbGeometry(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ where a climb box's box actually is ════");

        int boxes = 0, dummyFound = 0, agreeLocal = 0, agreeWorld = 0, dummyIsUnit = 0;
        var examples = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            if (fr?.FrameObjects == null) continue;

            CarPrefab? car = null;
            try
            {
                foreach (string file in SdsManifest.Load(extracted).GetFiles("PREFAB"))
                {
                    if (PrefabFile.Load(file) is { Car: not null } p) { car = p.Car; break; }
                }
            }
            catch (Exception) { continue; }
            if (car == null) continue;

            var byHash = new Dictionary<ulong, FrameObjectBase>();
            foreach (FrameObjectBase f in fr.FrameObjects.Values.OfType<FrameObjectBase>())
            {
                if (f.Name.String is { Length: > 0 }) byHash.TryAdd(f.Name.Hash, f);
            }

            foreach (CarPrefab.ClimbBox box in car.ClimbBoxes)
            {
                boxes++;
                if (!byHash.TryGetValue(box.Dummy, out FrameObjectBase? frame)
                    || frame is not FrameObjectDummy dummy)
                {
                    continue;
                }
                dummyFound++;

                Vector3 dLo = dummy.Bounds.Min, dHi = dummy.Bounds.Max;
                Matrix4x4 world = dummy.WorldTransform;

                // Compared as centre + half-size rather than as two corners: a rotated Dummy's min corner is
                // not the min corner of the result, and the question is whether the row states the same BOX,
                // not the same two points.
                Vector3 rowMid = (box.Min + box.Max) * 0.5f, rowHalf = (box.Max - box.Min) * 0.5f;
                Vector3 dMid = (dLo + dHi) * 0.5f, dHalf = (dHi - dLo) * 0.5f;
                Vector3 placedMid = Vector3.Transform(dMid, world);

                if ((dMid - rowMid).Length() < 2e-2f && (dHalf - rowHalf).Length() < 2e-2f) agreeLocal++;
                if ((placedMid - rowMid).Length() < 2e-2f && (dHalf - rowHalf).Length() < 2e-2f) agreeWorld++;
                // A Dummy that is a unit placeholder says the box cannot be coming from it at all.
                if ((dHi - dLo).Length() < 0.2f) dummyIsUnit++;

                if (examples.Count < 8)
                {
                    examples.Add($"{sds.Name} {frame.Name}: row centre {rowMid:F2} half {rowHalf:F2}  |  "
                        + $"dummy half {dHalf:F2} placed at {placedMid:F2}");
                }
            }
        }

        sb.AppendLine($"  {boxes} climb boxes, {dummyFound} whose Dummy is in the archive");
        sb.AppendLine($"    the row is the dummy's box, unplaced:           {agreeLocal}/{dummyFound}");
        sb.AppendLine($"    the row is the dummy's box, PLACED:             {agreeWorld}/{dummyFound}");
        sb.AppendLine($"    dummies too small to be the box themselves:     {dummyIsUnit}/{dummyFound}");
        foreach (string e in examples) sb.AppendLine("      " + e);

        // The two are one fact written twice: the row states the Dummy's own extents, moved to where the
        // Dummy stands. So a minted box has to WRITE that row from its frame — copying the donor's row
        // leaves the new box sitting exactly on top of the old one, which is a climb box you cannot climb.
        check("a climb box's row is its Dummy's box, placed where the Dummy stands",
            dummyFound > 0 && agreeWorld * 10 > dummyFound * 9,
            $"{agreeWorld} of {dummyFound} agree once placed, {agreeLocal} agree unplaced");
    }

    internal static void RunCarItemsProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_items.txt");
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
            ClimbGeometry(sb, folder, Check);
            One(sb, folder, focus);
            Minting(sb, folder, focus, Check);

            sb.Insert(0, $"CAR ITEMS PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "CAR ITEMS PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static void Census(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        var kinds = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        int archives = 0, pairs = 0, pairsDiffer = 0, pairsSame = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            PrefabFile? prefab = TryPrefab(extracted);
            if (prefab?.Car == null) continue;
            FrameResource? fr = TryFrames(extracted);
            if (fr == null) continue;
            archives++;

            Dictionary<ulong, string> what = FrameKinds(fr);

            foreach ((string label, IReadOnlyList<ulong> hashes) in PartsOf(prefab.Car))
            {
                counts.TryAdd(label, []);
                counts[label].Add(hashes.Count);
                foreach (ulong hash in hashes)
                {
                    string kind = hash == 0 ? "(none)" : what.GetValueOrDefault(hash, "UNRESOLVED");
                    kinds.TryAdd(label, new Dictionary<string, int>(StringComparer.Ordinal));
                    kinds[label][kind] = kinds[label].GetValueOrDefault(kind) + 1;
                }
            }

            // The axle pair: do the two halves name different frames? "Add an axle" copies a row today, and if
            // shipped pairs differ then a copied pair is two axles standing in the same place.
            IReadOnlyList<CarPrefab.Axle> axleList = prefab.Car.Axles;
            for (int i = 0; i + 1 < axleList.Count; i += 2)
            {
                pairs++;
                if (axleList[i].Frame == axleList[i + 1].Frame) pairsSame++; else pairsDiffer++;
            }
        }

        sb.AppendLine($"CAR PARTS: {archives} archives with a car prefab\n");
        sb.AppendLine($"    {"part",-18} {"archives",8} {"total",6} {"per car",10}  what the hash names");
        foreach (string label in counts.Keys)
        {
            if (!counts.TryGetValue(label, out List<int>? perCar)) continue;
            List<int> used = [.. perCar.Where(n => n > 0)];
            int total = perCar.Sum();
            string spread = used.Count == 0
                ? "-"
                : used.Min() == used.Max() ? used.Min().ToString() : $"{used.Min()}..{used.Max()}";
            string what = kinds.TryGetValue(label, out Dictionary<string, int>? k)
                ? string.Join("  ", k.OrderByDescending(p => p.Value).Select(p => $"{p.Key} ×{p.Value}"))
                : "-";
            sb.AppendLine($"    {label,-18} {used.Count,8} {total,6} {spread,10}  {what}");
        }

        sb.AppendLine($"\n    axle pairs: {pairs} — {pairsDiffer} name two different frames, "
            + $"{pairsSame} name the same one twice");
        check("a shipped axle pair names two different frames — so a copied pair is not an axle",
            pairsDiffer > pairsSame, $"{pairsDiffer} differ, {pairsSame} identical");

        // The kinds that never point at a bone are the ones a new part could mint a helper frame for; the ones
        // that always do need the rig to grow first, which is a different and much larger job.
        foreach (string label in kinds.Keys)
        {
            if (!kinds.TryGetValue(label, out Dictionary<string, int>? k) || k.Count == 0) continue;
            bool bones = k.ContainsKey("bone");
            bool helpers = k.Keys.Any(x => x is "Dummy" or "Point" or "Frame");
            if (!bones && helpers)
            {
                check($"\"{label}\" never needs a bone — a helper frame is enough to mint one",
                    true, string.Join(", ", k.Keys));
            }
        }
    }

    private static void One(StringBuilder sb, string folder, string focus)
    {
        var sds = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!sds.Exists) return;
        string extracted = MafiaEnvironment.ExtractedDir(sds);
        PrefabFile? prefab = TryPrefab(extracted);
        FrameResource? fr = TryFrames(extracted);
        if (prefab?.Car == null || fr == null) return;

        Dictionary<ulong, string> what = FrameKinds(fr);
        Dictionary<ulong, string> names = FrameNames(fr);

        sb.AppendLine($"\n════ {focus}, part by part ════");
        foreach ((string label, IReadOnlyList<ulong> hashes) in PartsOf(prefab.Car))
        {
            if (hashes.Count == 0) continue;
            sb.AppendLine($"    {label} ×{hashes.Count}");
            for (int i = 0; i < hashes.Count; i++)
            {
                ulong hash = hashes[i];
                sb.AppendLine($"        [{i}] 0x{hash:X16}  {names.GetValueOrDefault(hash, "?"),-24} "
                    + $"{what.GetValueOrDefault(hash, hash == 0 ? "(none)" : "UNRESOLVED")}");
            }
        }
    }

    /// <summary>
    /// Minting a part end to end, on a scratch copy: the helper frame appears in the graph on the chosen bone,
    /// the prefab gains a row, the row NAMES that frame (the whole point — a row pointing anywhere else is a
    /// part that exists and does nothing), undo takes both halves back, and redo puts both back.
    /// </summary>
    private static void Minting(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var sds = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!sds.Exists) return;
        string source = MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(source, "SDSContent.xml"))) return;
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_caritems_scratch");

        sb.AppendLine("\n════ minting a part ════");
        try
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(scratch, Path.GetFileName(file)));
            }

            FrameResource? fr = TryFrames(scratch);
            FrameObjectModel? model = fr?.FrameObjects!.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (fr == null || model == null) { sb.AppendLine("    no skinned model"); return; }

            HashName[] bones = model.GetSkeletonObject().BoneNames ?? [];
            int bone = Array.FindIndex(bones, n =>
                string.Equals(n.ToString(), "Scale_bone", StringComparison.OrdinalIgnoreCase));
            if (bone < 0) bone = 0;

            foreach (CarItemKind kind in CarPartBuilder.AddableKinds)
            {
                int before = Count(scratch, kind);
                Assets.Prefabs.AddedCarPart? added =
                    CarPartBuilder.Add(model, bone, kind, scratch, out string? refusal);
                check($"{CarPartBuilder.Words(kind)} can be minted whole", added != null, refusal ?? "");
                if (added == null) continue;

                check($"…the prefab gained one — {kind}", Count(scratch, kind) == before + 1,
                    $"{before} -> {Count(scratch, kind)}");
                check("…and the new row names the frame that was minted",
                    NamesFrame(scratch, kind, Fnv64.Hash(added.Name)),
                    $"\"{added.Name}\" on bone \"{bones[bone]}\"");
                check("…the frame is in the graph, hung off that bone",
                    fr.FrameObjects!.ContainsKey(added.Frame.RefID)
                    && (model.AttachmentReferences ?? []).Any(r =>
                        ReferenceEquals(r.Attachment, added.Frame) && r.JointIndex == bone),
                    "");

                // The panel's view of it, WITHOUT a save: the frame is only in memory until Save, so the
                // assembly has to be told the open graph's names or the part reads as a bare hash and cannot
                // be pointed anywhere — which is exactly how it looked when the user first tried it.
                var liveNames = new Dictionary<ulong, string>();
                foreach (object o in fr.FrameObjects!.Values)
                {
                    if (o is FrameObjectBase f && f.Name.String is { Length: > 0 } n) liveNames[f.Name.Hash] = n;
                }
                PrefabAssembly? shown = PrefabAssembly.ReadFrom(scratch, liveNames);
                check("…the panel resolves the new frame before any Save",
                    shown != null && shown.FrameChoices.Any(c => c.Name == added.Name),
                    shown == null ? "no assembly" : $"{shown.FrameChoices.Count} choices");
                check("…and no row of it is left dangling",
                    shown != null && shown.Entries.Sum(e => e.DanglingCount) == 0,
                    $"{shown?.Entries.Sum(e => e.DanglingCount)} dangling");

                // The writer is the real judge. A frame that does not survive being written is a part that
                // exists in the editor and is simply not in the game — the failure mode this whole phase is
                // about. Checked with the name table too: a frame the table does not carry is invisible.
                var reread = new FrameResource();
                using (var stream = new MemoryStream(fr.WriteToStream())) reread.ReadFromFile(stream);
                FrameObjectBase? survivor = reread.FrameObjects?.Values.OfType<FrameObjectBase>()
                    .FirstOrDefault(f => string.Equals(f.Name?.ToString(), added.Name, StringComparison.Ordinal));
                check("…the frame survives the writer",
                    survivor != null, survivor == null ? "gone after a write" : added.Name);
                check("…and comes back on the same joint, on the name table like its neighbours",
                    survivor != null
                    && reread.FrameObjects!.Values.OfType<FrameObjectModel>().Any(m =>
                        (m.AttachmentReferences ?? []).Any(r =>
                            ReferenceEquals(r.Attachment, survivor) && r.JointIndex == bone))
                    && survivor.IsOnFrameTable == added.Donor.IsOnFrameTable,
                    survivor == null ? "" : $"table {survivor.IsOnFrameTable}, donor {added.Donor.IsOnFrameTable}");

                CarPartBuilder.Remove(model, added);
                check("…undo takes back both halves", Count(scratch, kind) == before
                    && !fr.FrameObjects!.ContainsKey(added.Frame.RefID), $"back to {Count(scratch, kind)}");

                CarPartBuilder.Restore(model, added);
                check("…and redo puts back both, not two of one", Count(scratch, kind) == before + 1
                    && fr.FrameObjects!.ContainsKey(added.Frame.RefID), $"{Count(scratch, kind)} rows");
                CarPartBuilder.Remove(model, added);
            }

            // A CLIMB BOX end to end: minted, dragged, saved. Its row is the only copy the game reads, so
            // every one of those steps has to reach the row — reported as "I added a climb box and cannot
            // climb it", because the row was a copy of the donor's and said the old box's place and size.
            Assets.Prefabs.AddedCarPart? climb =
                CarPartBuilder.Add(model, bone, CarItemKind.ClimbBox, scratch, out string? climbWhy);
            check("a climb box can be minted", climb != null, climbWhy ?? "");
            if (climb?.Frame is FrameObjectDummy box)
            {
                CarPrefab.ClimbBox row = LastClimb(scratch);
                (Vector3 min, Vector3 max) = Assets.Cars.Car.BoxOf(box);
                check("…and its row states ITS box, not a copy of the donor's",
                    (row.Min - min).Length() < 1e-3f && (row.Max - max).Length() < 1e-3f,
                    $"row {row.Min:F2}…{row.Max:F2} vs frame {min:F2}…{max:F2}");
                check("…and that box is big enough to stand on, not a 5 cm speck",
                    (max - min).Length() > 0.5f, $"{max - min:F2}");

                // Dragged and scaled with the gizmo, then saved — the sync a save runs is what carries it.
                box.LocalTransform = Matrix4x4.CreateScale(3f) * Matrix4x4.CreateTranslation(0.2f, 1.4f, 0.9f);
                int moved = Assets.Cars.Car.SyncMarkers(scratch, fr, [box], out _);
                CarPrefab.ClimbBox after = LastClimb(scratch);
                (Vector3 wantMin, Vector3 wantMax) = Assets.Cars.Car.BoxOf(box);
                check("moving and scaling the Dummy carries through to the row the game climbs",
                    moved >= 1 && (after.Min - wantMin).Length() < 1e-3f
                    && (after.Max - wantMax).Length() < 1e-3f,
                    $"{moved} rewritten; row {after.Min:F2}…{after.Max:F2} vs frame {wantMin:F2}…{wantMax:F2}");
                check("…and running the same sync again writes nothing",
                    Assets.Cars.Car.SyncMarkers(scratch, fr, [box], out _) == 0, "");

                CarPartBuilder.Remove(model, climb!);
            }

            // The kinds that name a bone must refuse rather than mint a Dummy the game will not drive.
            foreach (CarItemKind kind in new[]
                     { CarItemKind.Door, CarItemKind.Window, CarItemKind.AxlePair, CarItemKind.Wiper })
            {
                Assets.Prefabs.AddedCarPart? refused =
                    CarPartBuilder.Add(model, bone, kind, scratch, out string? why);
                check($"{CarPartBuilder.Words(kind)} is refused, with the reason",
                    refused == null && !string.IsNullOrWhiteSpace(why), why ?? "minted anyway!");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("    FAILED: " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>The climb-box row that was added last — read back off disk, which is where the game reads it.</summary>
    private static CarPrefab.ClimbBox LastClimb(string extracted)
    {
        PrefabFile? prefab = Assets.Prefabs.PrefabEditing.OpenFirst(extracted);
        IReadOnlyList<CarPrefab.ClimbBox> boxes = prefab?.Car?.ClimbBoxes ?? [];
        return boxes.Count > 0 ? boxes[^1] : default;
    }

    private static int Count(string extracted, CarItemKind kind)
    {
        PrefabFile? prefab = TryPrefab(extracted);
        return prefab?.CarItemCount(kind) ?? -1;
    }

    private static bool NamesFrame(string extracted, CarItemKind kind, ulong hash)
    {
        PrefabFile? prefab = TryPrefab(extracted);
        if (prefab?.Car is not { } car) return false;
        return kind switch
        {
            CarItemKind.ClimbBox => car.ClimbBoxes.Any(b => b.Dummy == hash),
            CarItemKind.FuelTank => car.FuelTanks.Contains(hash),
            CarItemKind.Seat => car.Seats.Any(s => s.Frame == hash),
            CarItemKind.Exhaust => car.ExhaustEmitters.Contains(hash),
            _ => false,
        };
    }

    // ── helpers ──

    private static PrefabFile? TryPrefab(string extracted)
    {
        try
        {
            string? file = SdsManifest.Load(extracted).GetFiles("PREFAB").FirstOrDefault();
            return file == null ? null : PrefabFile.Load(file);
        }
        catch (Exception) { return null; }
    }

    private static FrameResource? TryFrames(string extracted)
    {
        try { return SdsMeshLoader.OpenScene(extracted).FrameResource; }
        catch (Exception) { return null; }
    }

    /// <summary>What each hash in the archive names: a bone of the rig, or a frame and its type.</summary>
    private static Dictionary<ulong, string> FrameKinds(FrameResource fr)
    {
        var kinds = new Dictionary<ulong, string>();
        foreach (FrameObjectBase frame in fr.FrameObjects!.Values)
        {
            string? name = frame.Name?.ToString();
            if (name == null) continue;
            kinds.TryAdd(Fnv64.Hash(name), TypeOf(frame));
        }
        // Bones last: a bone name and a frame name can collide, and the prefab means the bone.
        foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
        {
            HashName[] bones;
            try { bones = model.GetSkeletonObject().BoneNames ?? []; }
            catch (Exception) { continue; }
            foreach (HashName bone in bones) kinds[Fnv64.Hash(bone.ToString() ?? "")] = "bone";
        }
        return kinds;
    }

    private static Dictionary<ulong, string> FrameNames(FrameResource fr)
    {
        var names = new Dictionary<ulong, string>();
        foreach (FrameObjectBase frame in fr.FrameObjects!.Values)
        {
            string? name = frame.Name?.ToString();
            if (name != null) names.TryAdd(Fnv64.Hash(name), name);
        }
        foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
        {
            HashName[] bones;
            try { bones = model.GetSkeletonObject().BoneNames ?? []; }
            catch (Exception) { continue; }
            foreach (HashName bone in bones)
            {
                string name = bone.ToString() ?? "";
                names[Fnv64.Hash(name)] = name;
            }
        }
        return names;
    }

    private static string TypeOf(FrameObjectBase frame) => frame switch
    {
        FrameObjectModel => "Model",
        FrameObjectSingleMesh => "SingleMesh",
        FrameObjectDummy => "Dummy",
        FrameObjectPoint => "Point",
        FrameObjectArea => "Area",
        FrameObjectSector => "Sector",
        FrameObjectCollision => "Collision",
        FrameObjectTarget => "Target",
        FrameObjectDeflector => "Deflector",
        FrameObjectFrame => "Frame",
        _ => frame.GetType().Name,
    };
}
