using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Prefabs;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What the PREFAB containers actually hold, across the whole game. A prefab entry is keyed by an FNV64 hash
/// and carries a type id plus a blob of bit-packed init data that the core keeps opaque — so before any of it
/// is typed, this says which types exist, how many of each, how big they are, and which archives carry them.
/// Scope before code: typing a variant nothing ships is work for nothing.
/// <para>
/// Reads the extracted mirror only; nothing is unpacked, parsed twice or written.
/// Output: %TEMP%\illusion_prefabs.txt
/// </para>
/// </summary>
internal static class PrefabProbes
{
    // The names MafiaToolkit's reference gives the type ids (its Prefab.cs dispatch). Ids 0, 1 and 11 exist in
    // the shipped data and that reference cannot parse them, so they stay bare numbers here too.
    private static readonly Dictionary<int, string> TypeNames = new()
    {
        [2] = "S_CarInitData",
        [3] = "S_COInitData",
        [4] = "S_ActorDeformInitData",
        [5] = "S_WheelInitData",
        [6] = "S_PhThingActorBaseInitData",
        [7] = "S_DoorInitData",
        [8] = "S_LiftInitData",
        [9] = "S_BoatInitData",
        [10] = "S_WagonInitData",
    };

    /// <summary>The init-data variants the core reads rather than carrying opaquely. Grows as they are typed.</summary>
    private static readonly HashSet<int> Typed = [2, 5, 6];

    internal static void RunPrefabsProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_prefabs.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        int decoded = 0, decodable = 0, containersExact = 0, containers = 0;
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            string sdsRoot = Path.Combine(MafiaEnvironment.PcFolder, "sds");
            FileInfo[] archives = new DirectoryInfo(sdsRoot).GetFiles("*.sds", SearchOption.AllDirectories);
            Array.Sort(archives, (a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));

            var perType = new Dictionary<int, Stat>();
            var carriers = new List<(string Path, int Entries, string Types)>();
            int scanned = 0, withPrefab = 0, unreadable = 0;

            foreach (FileInfo sds in archives)
            {
                string extracted = MafiaEnvironment.ExtractedDir(sds);
                if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;
                scanned++;

                IReadOnlyList<string> files;
                try { files = SdsManifest.Load(extracted).GetFiles("PREFAB"); }
                catch (Exception) { continue; }
                if (files.Count == 0) continue;
                withPrefab++;

                var typesHere = new SortedSet<int>();
                int entries = 0;
                foreach (string file in files)
                {
                    string path = Path.Combine(extracted, file);
                    PrefabFile prefab;
                    try { prefab = PrefabFile.Load(path); }
                    catch (Exception) { unreadable++; continue; }

                    // The container has to come back byte for byte, whether its entries were decoded or not.
                    // A typed entry is re-encoded from its typed fields, so this is the only thing standing
                    // between "we read the layout" and "we read something that happened to parse".
                    containers++;
                    try
                    {
                        if (File.ReadAllBytes(path).AsSpan().SequenceEqual(prefab.ToBytes())) containersExact++;
                    }
                    catch (Exception) { /* counted as not exact */ }

                    foreach (int kind in prefab.DecodedKinds)
                    {
                        if (kind != 0) decoded++;
                    }
                    foreach ((int type, int _) in prefab.Entries)
                    {
                        if (Typed.Contains(type)) decodable++;
                    }

                    entries += prefab.PrefabCount;
                    foreach ((int type, int size) in prefab.Entries)
                    {
                        typesHere.Add(type);
                        if (!perType.TryGetValue(type, out Stat s)) s = new Stat();
                        s.Count++;
                        s.Bytes += size;
                        s.Min = s.Count == 1 ? size : Math.Min(s.Min, size);
                        s.Max = Math.Max(s.Max, size);
                        s.Archives.Add(Rel(sdsRoot, sds));
                        perType[type] = s;
                    }
                }

                carriers.Add((Rel(sdsRoot, sds), entries, string.Join(" ", typesHere.Select(Name))));
            }

            sb.AppendLine($"PREFAB CENSUS: {withPrefab} of {scanned} extracted archives carry a PREFAB\n");
            if (unreadable > 0) sb.AppendLine($"{unreadable} container(s) would not read\n");

            sb.AppendLine($"{"type",-30} {"entries",8} {"archives",9} {"min B",8} {"max B",8} {"total B",10}");
            foreach ((int type, Stat s) in perType.OrderByDescending(p => p.Value.Count))
            {
                sb.AppendLine($"{Name(type),-30} {s.Count,8} {s.Archives.Count,9} {s.Min,8} {s.Max,8} {s.Bytes,10}");
            }

            sb.AppendLine("\n── which folders carry prefabs ──");
            foreach (IGrouping<string, (string Path, int Entries, string Types)> g in carriers
                         .GroupBy(c => c.Path.Contains('\\') ? c.Path[..c.Path.IndexOf('\\')] : "(root)")
                         .OrderByDescending(g => g.Count()))
            {
                sb.AppendLine($"    {g.Key,-20} {g.Count(),5} archives, {g.Sum(c => c.Entries),6} entries");
            }

            // A car in full: this is the file that says how the thing is assembled, and every reference in it
            // is an FNV64 of a FRAME name — a bone, a dummy, a point of the car's own model.
            sb.AppendLine($"\n\n════ {focus} ════");
            var car = new FileInfo(Path.Combine(sdsRoot, "cars", focus + ".sds"));
            string carExtracted = MafiaEnvironment.ExtractedDir(car);
            if (!File.Exists(Path.Combine(carExtracted, "SDSContent.xml")))
            {
                sb.AppendLine("not extracted");
            }
            else
            {
                // Every frame name of the car, by hash — the table the prefab's references are checked against.
                var frames = new Dictionary<ulong, string>();
                try
                {
                    if (SdsMeshLoader.OpenScene(carExtracted).FrameResource is { FrameObjects: not null } fr)
                    {
                        foreach (object o in fr.FrameObjects.Values)
                        {
                            if (o is FrameObjectBase f && f.Name.String is { Length: > 0 } n) frames[f.Name.Hash] = n;
                        }
                        foreach (FrameObjectModel m in fr.FrameObjects.Values.OfType<FrameObjectModel>())
                        {
                            foreach (HashName bone in m.GetSkeletonObject().BoneNames ?? [])
                            {
                                if (bone.String is { Length: > 0 } bn) frames[bone.Hash] = bn;
                            }
                        }
                    }
                }
                catch (Exception) { /* the resolve table is a nicety; the census is not */ }

                foreach (string file in SdsManifest.Load(carExtracted).GetFiles("PREFAB"))
                {
                    PrefabFile prefab = PrefabFile.Load(Path.Combine(carExtracted, file));
                    sb.AppendLine($"\n{file}: {prefab.PrefabCount} entries");
                    sb.AppendLine($"    {"hash",-22} {"type",-30} {"bytes",8}");
                    for (int i = 0; i < prefab.PrefabCount; i++)
                    {
                        (int type, int size) = prefab.Entries[i];
                        sb.AppendLine($"    {prefab.Hashes[i],-22} {Name(type),-30} {size,8}");
                    }

                    if (prefab.Car is { } assembly) DumpCar(sb, assembly, frames, Check);
                }

                // The plain reader, through one call. Everything above proves the FORMAT is understood; this
                // proves a caller gets the same answer with every reference resolved against the archive's
                // own frames — telling a resolved reference from a dangling one is the whole of its job.
                PrefabAssembly? panel = PrefabAssembly.Read(car);
                Check("the reader reads an assembly for a car", panel != null);
                if (panel != null)
                {
                    PrefabEntryView? entry = panel.Entries.FirstOrDefault(e => e.Decoded);
                    Check("...with a decoded car entry in it",
                        entry is { TypeName: "S_CarInitData" }, entry?.TypeName ?? "(none)");

                    if (entry != null)
                    {
                        string[] want = ["Chassis", "Doors", "Seats", "Windows", "Axles"];
                        var have = entry.Groups.Select(g => g.Title).ToList();
                        Check("...banded into the groups the panel shows",
                            Array.TrueForAll(want, w => have.Contains(w)), string.Join(", ", have));

                        List<PrefabRefView> refs = [.. entry.Groups.SelectMany(g => g.Rows)
                            .Where(r => r.Kind is PrefabRefKind.Reference or PrefabRefKind.Dangling)];
                        int resolved = refs.Count(r => r.Kind == PrefabRefKind.Reference);
                        Check("...and its references resolve to real frame names",
                            refs.Count > 0 && resolved == refs.Count,
                            $"{resolved} of {refs.Count} — dangling: " + string.Join(", ",
                                refs.Where(r => r.Kind == PrefabRefKind.Dangling).Take(4).Select(r => r.Label)));
                        Check("a resolved row shows the name, not the hash",
                            refs.Exists(r => r.Kind == PrefabRefKind.Reference && !r.Value.StartsWith("0x", StringComparison.Ordinal)),
                            refs.Count > 0 ? refs[0].Value : "(none)");
                        Check("the entry's tally counts what the rows say",
                            entry.DanglingCount == refs.Count(r => r.Kind == PrefabRefKind.Dangling), entry.Status);
                    }
                }
            }

            // The Prefab TAB used to be measured here — whether it was reachable in both editor windows, and
            // whether pointing a slot at another frame reached the file. There is no tab: a car's assembly is
            // the component tree, and the aggregate is the only thing that writes a prefab. What that tree
            // shows is --probe-component-tree's question, and what the aggregate writes is
            // --probe-car-roundtrip's, --probe-car-markers' and --probe-collision-role's.

            // An archive with no PREFAB must give the reader nothing to show, rather than an empty card.
            var plain = new FileInfo(Path.Combine(sdsRoot, "cars", "cars_universal.sds"));
            if (File.Exists(Path.Combine(MafiaEnvironment.ExtractedDir(plain), "SDSContent.xml"))
                && !SdsManifest.Load(MafiaEnvironment.ExtractedDir(plain)).HasType("PREFAB"))
            {
                Check("an archive with no PREFAB gives the panel nothing", PrefabAssembly.Read(plain) == null);
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally
        {
            Check("every PREFAB container re-emits byte for byte",
                containers > 0 && containersExact == containers, $"{containersExact} of {containers}");
            Check("every entry of a typed variant was decoded",
                decodable > 0 && decoded == decodable, $"{decoded} of {decodable}");
            sb.Insert(0, $"PREFAB PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    /// <summary>
    /// One car's assembly, with every hash resolved against the archive's own frame and bone names. This is
    /// the check that the layout is not merely self-consistent: byte-exactness proves the reader and writer
    /// agree, but only names landing on real frames prove the fields were read where they actually are.
    /// </summary>
    private static void DumpCar(StringBuilder sb, CarPrefab car, Dictionary<ulong, string> frames,
        Action<string, bool, string> check)
    {
        string N(ulong hash) => hash == 0 ? "—" : frames.TryGetValue(hash, out string? n) ? n : $"?{hash:X16}";

        sb.AppendLine("\n── how this car is put together ──");
        sb.AppendLine($"    root {N(car.RootFrame)}, scale bone {N(car.ScaleBone)}, body {N(car.BodyFrame)}, " +
                      $"rest {N(car.RestBone)}");
        sb.AppendLine($"    lights: head {N(car.HeadlightModel)}, back {N(car.BacklightModel)}, " +
                      $"top {N(car.ToplightModel)}; snow rest {N(car.SnowRest)}");
        sb.AppendLine($"    driving wheels: {string.Join(", ", car.DrivingWheels.Select(N))}");
        sb.AppendLine($"    fuel tanks: {string.Join(", ", car.FuelTanks.Select(N))}");
        sb.AppendLine($"    exhausts: {string.Join(", ", car.ExhaustEmitters.Select(N))}");
        sb.AppendLine($"    wipers: {string.Join(", ", car.Wipers.Select(N))}");
        sb.AppendLine($"    deformable parts: {car.DeformPartCount}");

        sb.AppendLine($"\n    seats ({car.Seats.Count}):");
        foreach (CarPrefab.Seat s in car.Seats)
            sb.AppendLine($"        {N(s.Frame),-18} through {N(s.DoorFrame),-14} type {s.Type} index {s.Index} at {s.Position}");

        sb.AppendLine($"    doors ({car.Doors.Count}):");
        foreach (CarPrefab.DoorPoints d in car.Doors)
            sb.AppendLine($"        {N(d.Frame),-18} handle {d.HandlePosition} lock {d.LockPosition}");

        sb.AppendLine($"    windows ({car.Windows.Count}):");
        foreach (CarPrefab.Window win in car.Windows)
            sb.AppendLine($"        {N(win.Frame),-18} depth {win.Depth:F3} {(win.IsOpenable ? "openable" : "fixed")}");

        sb.AppendLine($"    axles ({car.Axles.Count}):");
        foreach (CarPrefab.Axle a in car.Axles)
            sb.AppendLine($"        {N(a.Frame),-18} drum {N(a.BrakeDrum),-16} type {a.Type} " +
                          $"radius {a.BrakeDrumRadius:F3} drum mass {a.BrakeDrumMass:F1} axle mass {a.AxleMass:F1}");

        sb.AppendLine($"    climb boxes ({car.ClimbBoxes.Count}):");
        foreach (CarPrefab.ClimbBox c in car.ClimbBoxes)
            sb.AppendLine($"        on {N(c.Bone),-16} dummy {N(c.Dummy),-14} {c.Min} .. {c.Max}");

        var references = car.AllFrameReferences.ToList();
        int resolved = references.Count(h => frames.ContainsKey(h));
        check("every frame the car's prefab names exists in the archive",
            references.Count > 0 && resolved == references.Count,
            $"{resolved} of {references.Count}");
        check("the car has the parts a car has",
            car.Seats.Count > 0 && car.Doors.Count > 0 && car.Axles.Count > 0,
            $"{car.Seats.Count} seats, {car.Doors.Count} doors, {car.Axles.Count} axles");
    }


    private static string Name(int type) =>
        TypeNames.TryGetValue(type, out string? n) ? $"{type} {n}" : type.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Rel(string root, FileInfo file) =>
        Path.GetRelativePath(root, file.FullName);

    private struct Stat
    {
        public int Count;
        public long Bytes;
        public int Min;
        public int Max;
        public HashSet<string> Archives;

        public Stat() => Archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
