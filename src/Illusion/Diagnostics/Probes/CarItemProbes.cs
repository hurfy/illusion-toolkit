using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
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

            // Minting a part used to be measured here, against the builder that wrote the prefab on its own.
            // That builder is gone and the aggregate does the minting; what it does is measured by
            // --probe-car-markers, which asks the same questions of the surviving path.

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
