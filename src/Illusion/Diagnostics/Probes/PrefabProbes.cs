using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Prefabs;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
using Illusion.Domain;
using Illusion.Scene;
using Illusion.ViewModels;
using Illusion.Viewport;
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

                // The Prefab tab reads exactly this, through one call. Everything above proves the FORMAT is
                // understood; this proves the panel gets the same answer, resolved names and all — the tab's
                // whole job is telling a resolved reference from a dangling one.
                PrefabAssembly? panel = PrefabAssembly.Read(car);
                Check("the panel reads an assembly for a car", panel != null);
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

            // The tab has to be REACHABLE, which is a different question from the data being right. The two
            // editor windows show different trees: the map editor shows the real chain (folder → archive →
            // FrameResource → frames), the resource editor a FLATTENED one that starts at the frame roots.
            // The archive row the first version hung this tab on simply does not exist in the second window,
            // so the tab could never appear where cars are actually opened.
            CheckReachable(car, Check);

            // Pointing a slot at a different frame, on a SCRATCH copy — writing a prefab means writing into
            // the working copy, which is the player's own install.
            CheckEditing(car, Check, sb);

            // An archive with no PREFAB must give the panel nothing to show, rather than an empty card.
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

    /// <summary>
    /// Builds the app's real tree shape around a car, then selects a row out of the FLATTENED view the
    /// resource editor shows — and asserts the panel's prefab flag comes up from there. Selecting the archive
    /// row is not enough of a test: that row is only ever on screen in the map editor.
    /// </summary>
    private static void CheckReachable(FileInfo car, Action<string, bool, string> check)
    {
        (List<Illusion.Assets.Sds.SdsFrameNode> roots, _, ISceneDocument? document) =
            SdsMeshLoader.LoadHierarchy(car);
        if (document == null || roots.Count == 0)
        {
            check("the car loads a scene document to hang the tab on", false, "no document");
            return;
        }

        var host = new D3DImageHost();
        SceneNode folder = host.Tree.GetOrCreateFolder("probe");
        var sdsNode = new SceneNode(car.Name, "Sds", true);
        var frNode = new SceneNode("FrameResource", "FrameResource", true) { Source = document };
        var leaves = new List<SceneNode>();
        foreach (Illusion.Assets.Sds.SdsFrameNode r in roots) frNode.AddChild(SceneTree.BuildSceneTree(r, leaves));
        sdsNode.AddChild(frNode);
        folder.AddChild(sdsNode);
        host.Tree.RebuildStageRoots();

        check("the flattened tree drops the archive row the tab used to need",
            !host.Tree.StageRoots.Any(n => string.Equals(n.Kind, "Sds", StringComparison.Ordinal)),
            string.Join(", ", host.Tree.StageRoots.Take(3).Select(n => $"{n.Name}:{n.Kind}")));

        var vm = new SelectionViewModel(host);
        vm.SetNode(host.Tree.StageRoots.Count > 0 ? host.Tree.StageRoots[0] : frNode);
        Pump(() => vm.HasPrefab);
        check("the prefab tab is reachable from a row the resource editor shows",
            vm.HasPrefab, vm.PrefabSummary);

        // ...and from an object deep inside the car, which is what a user usually has selected.
        SceneNode? deep = leaves.Count > 0 ? leaves[0] : null;
        if (deep != null)
        {
            vm.SetNode(deep);
            Pump(() => vm.HasPrefab);
            check("...and from a mesh inside it", vm.HasPrefab, deep.Name);
        }

        // ...and with NOTHING selected, which is the state a car is in the moment it opens. A fresh
        // view-model, so this cannot pass on a prefab the earlier selections already cached.
        var untouched = new SelectionViewModel(host);
        untouched.RefreshPrefab();
        Pump(() => untouched.HasPrefab);
        check("...and with nothing selected at all", untouched.HasPrefab, untouched.PrefabSummary);

        // Every reference the panel shows is a picker over the archive's own frames, and every frame the
        // archive has is in it. There is no path in the panel that takes a typed name.
        List<PrefabRowViewModel> rows = [.. untouched.PrefabEntries
            .SelectMany(e => e.Groups).SelectMany(g => g.Rows)];
        List<PrefabRowViewModel> pickers = [.. rows.Where(r => r.CanEdit)];
        check("the panel turns references into pickers", pickers.Count > 0,
            $"{pickers.Count} of {rows.Count} rows");
        check("a picker offers the whole archive to choose from",
            pickers.Count > 0 && pickers[0].Choices.Count > 1, $"{pickers.FirstOrDefault()?.Choices.Count ?? 0}");
        check("a picker opens on the frame the slot names now",
            pickers.Exists(p => p.SelectedFrame != null && p.SelectedFrame.Name == p.Value),
            pickers.Count > 0 ? $"{pickers[0].Value} / {pickers[0].SelectedFrame?.Name}" : "(none)");
        check("a fact row is not a picker", rows.Exists(r => !r.CanEdit), "");

        // A restore swaps the .sds and re-extracts it under the SAME path, so a scene change has to RE-READ
        // rather than trust that "same archive means same answer". It did trust it, and a rolled-back car went
        // on showing the parts and collision volumes it no longer had — the rollback looked like it had not
        // worked. Observable without touching the install: a real re-read hands back fresh row objects.
        object? firstRow = untouched.PrefabEntries.SelectMany(e => e.Groups)
            .SelectMany(g => g.Rows).FirstOrDefault();
        untouched.RefreshPrefab();
        Pump(() => untouched.HasPrefab);
        object? afterRefresh = untouched.PrefabEntries.SelectMany(e => e.Groups)
            .SelectMany(g => g.Rows).FirstOrDefault();
        check("a scene change re-reads the prefab instead of serving the cached one",
            firstRow != null && afterRefresh != null && !ReferenceEquals(firstRow, afterRefresh),
            firstRow == null ? "no rows to compare" : "fresh rows after refresh");

        // What the bands offer. A car has one headlight, so Lights takes no "+"; a seat IS a part, so its
        // row takes an "×", while the brake drum that belongs to an axle does not.
        List<PrefabGroupRowsViewModel> bands = [.. untouched.PrefabEntries.SelectMany(e => e.Groups)];
        // Every band that IS a list offers adding one — no band adds a kind other than its own, which is what
        // one band called "running gear" holding four different lists used to do.
        // The bands whose parts the toolkit can mint offer a "+". The rest are editable but not extendable:
        // a deform part or a door-damage record is a nested tree, and inventing one would be inventing
        // physics the game reads.
        string[] extendable = ["Seats", "Doors", "Windows", "Axles", "Climb boxes",
                               "Driving wheels", "Fuel tanks", "Exhausts", "Wipers"];
        check("every band the toolkit can mint a part for offers adding one",
            bands.Where(b => extendable.Contains(b.Title)).All(b => b.CanAdd)
            && bands.Where(b => !extendable.Contains(b.Title)).All(b => !b.CanAdd),
            "no + on: " + string.Join(", ", bands.Where(b => !b.CanAdd).Select(b => b.Title)));
        check("a fixed band offers no adding",
            bands.Any(b => b.Title == "Chassis" && !b.CanAdd), "");
        // A band with nothing in it is the one case where the header is the whole band — and it is exactly
        // when the "+" matters most, because the band's own rows are gone and it is the only way back.
        var empty = new PrefabGroupRowsViewModel(
            new PrefabGroupView("Wipers", []), [], CarItemKind.Wiper, _ => { });
        check("an empty band still stands, and still offers its +",
            empty.IsVisible && empty.CanAdd && empty.IsEmpty, "");
        empty.Search("");
        check("...and clearing the search does not hide it", empty.IsVisible && empty.IsEmpty, "");
        empty.Search("wip");
        check("...and it can be found by the band's own name, which no row of it could match for",
            empty.IsVisible && empty.IsExpanded, "");
        empty.Search("zzz");
        check("...but not by something it is not", !empty.IsVisible, "");
        // The band header counts THINGS. Twelve rows describing four doors is four doors.
        check("a band's count is its parts, not the rows it takes to describe them",
            bands.First(b => b.Title == "Doors").Badge
                == bands.First(b => b.Title == "Doors").Rows.Count(r => r.CanRemove)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"Doors badge {bands.First(b => b.Title == "Doors").Badge}");
        check("every band is drawn in its own colour",
            bands.Select(b => b.Accent).Distinct().Count() >= bands.Count - 1,
            $"{bands.Select(b => b.Accent).Distinct().Count()} of {bands.Count}");
        check("a part is dropped by its own row, not by one of its fields",
            rows.Any(r => r.Label == "Seat 1" && r.CanRemove)
            && rows.Any(r => r.Label == "Brake drum" && !r.CanRemove), "");
        check("a part names itself once — the row IS the frame, there is no header repeating it",
            !rows.Any(r => r.IsHeader) && rows.Any(r => r.Label == "Door 1" && r.IsPicker),
            string.Join(", ", rows.Where(r => r.IsPicker).Take(3).Select(r => r.Label)));
        // Every kind of value the game reads is a row with a widget, not grey prose under a name.
        check("a number is a field, not a caption",
            rows.Any(r => r.IsNumber && r.Label == "Depth"),
            string.Join(", ", rows.Where(r => r.IsNumber).Select(r => r.Label).Distinct()));
        check("a yes/no is a switch", rows.Any(r => r.IsFlag && r.Label == "Opens"), "");
        check("a position is a vector box",
            rows.Any(r => r.IsVector && r.Label == "Handle"),
            string.Join(", ", rows.Where(r => r.IsVector).Select(r => r.Label).Distinct()));
        check("every row has exactly one shape",
            rows.TrueForAll(r => new[] { r.IsHeader, r.IsPicker, r.IsNumber, r.IsFlag, r.IsVector, r.IsText }
                .Count(x => x) == 1), "");
        // Parts are told apart by AIR, not by an indent: the panel is a narrow column, and a left margin on
        // every field is width taken off the field itself.
        check("a part's fields keep the same left edge as the part",
            rows.Where(r => r.IsField).All(r => r.Spacing.Left == 0)
            && rows.Where(r => !r.IsField).All(r => r.Spacing.Left == 0),
            string.Join(", ", rows.Select(r => r.Spacing.Left).Distinct()));
        // Each thing in a band gets its own plate: a door and its handle and lock are one element, while a
        // band of plain slots (the chassis) stays a single one — four boxes of one line each say nothing.
        PrefabGroupRowsViewModel doorsBand = bands.First(b => b.Title == "Doors");
        check("a band puts every part on a plate of its own",
            doorsBand.Elements.Count == doorsBand.Rows.Count(r => r.CanRemove),
            $"{doorsBand.Elements.Count} elements of {doorsBand.Rows.Count} rows");
        check("a part's fields sit on the part's own plate",
            doorsBand.Elements.All(e => e.Rows.Count > 1 && e.Rows[0].CanRemove),
            string.Join(" | ", doorsBand.Elements.Select(e => string.Join(",", e.Rows.Select(r => r.Label)))));
        check("a band of plain slots stays one plate",
            bands.First(b => b.Title == "Chassis").Elements.Count == 1,
            $"{bands.First(b => b.Title == "Chassis").Elements.Count}");
        // Search. A car's assembly is over two hundred lines, so the tab has to answer "where is X" — both
        // by the name of a row and by the frame it names.
        int allBands = bands.Count(b => b.IsVisible);
        untouched.PrefabSearch = "mass";
        check("searching by a field's name keeps the bands that have one",
            bands.Any(b => b.IsVisible) && bands.Count(b => b.IsVisible) < allBands,
            string.Join(", ", bands.Where(b => b.IsVisible).Select(b => b.Title)));
        check("...and a matched field keeps the part it belongs to",
            bands.Where(b => b.IsVisible).SelectMany(b => b.Elements)
                .All(e => e.Rows.Count > 0 && !e.Rows[0].IsField), "");
        check("...and the bands it matched open themselves",
            bands.Where(b => b.IsVisible).All(b => b.IsExpanded)
            && bands.Where(b => !b.IsVisible).All(b => !b.IsExpanded), "");

        untouched.PrefabSearch = "doorFL";
        check("searching by a BONE name finds every part that uses it",
            bands.Where(b => b.IsVisible).SelectMany(b => b.Elements).SelectMany(e => e.Rows)
                .Any(r => r.Value == "doorFL"),
            string.Join(", ", bands.Where(b => b.IsVisible).Select(b => b.Title)));

        untouched.PrefabSearch = "zzzz-no-such-thing";
        check("a search that matches nothing says so rather than showing an empty tab",
            untouched.PrefabNothingFound && bands.All(b => !b.IsVisible), "");

        // What made typing in that box freeze the app. Every keystroke re-split every band and handed WPF a
        // fresh set of element view models — new instances, so no container could be reused, so the whole
        // panel was rebuilt from scratch per character over a car's ~500 rows of heavy templates. The
        // arithmetic barely registers; the layout pass is what stops the frame.
        untouched.PrefabSearch = "";
        PrefabGroupRowsViewModel widest = bands.OrderByDescending(b => b.Elements.Count).First();
        object[] before = [.. widest.Elements];
        untouched.PrefabSearch = "";                    // the same query again
        check("re-running the same search rebuilds nothing at all",
            widest.Elements.Count == before.Length
            && widest.Elements.Select((e, i) => ReferenceEquals(e, before[i])).All(same => same),
            $"{widest.Title}, {before.Length} elements");

        untouched.PrefabSearch = "a";
        untouched.PrefabSearch = "";
        check("…and clearing it hands back the very same elements, so the panel is reused not rebuilt",
            widest.Elements.Count == before.Length
            && widest.Elements.Select((e, i) => ReferenceEquals(e, before[i])).All(same => same),
            $"{widest.Elements.Count} vs {before.Length}");

        check("the tab's size is on the record — this is what one keystroke used to rebuild", true,
            $"{bands.Sum(b => b.Rows.Count)} rows in {bands.Count} bands, widest \"{widest.Title}\" "
                + $"at {widest.Rows.Count}");

        // An edit rewrites the file and rebuilds these rows; a band the user had opened has to still be open
        // afterwards, or the panel folds shut under their hands the moment they change something.
        untouched.PrefabSearch = "";
        PrefabGroupRowsViewModel doorBand = untouched.PrefabEntries
            .SelectMany(e => e.Groups).First(b => b.Title == "Doors");
        doorBand.IsExpanded = true;
        untouched.RefreshPrefab();
        Pump(() => untouched.PrefabEntries.SelectMany(e => e.Groups).Any(b => b.Title == "Doors"));
        check("a band the user opened stays open across a rebuild",
            untouched.PrefabEntries.SelectMany(e => e.Groups)
                .First(b => b.Title == "Doors").IsExpanded, "");
        check("...and the bands they left closed stay closed",
            untouched.PrefabEntries.SelectMany(e => e.Groups)
                .Where(b => b.Title != "Doors").All(b => !b.IsExpanded), "");

        bands = [.. untouched.PrefabEntries.SelectMany(e => e.Groups)];
        foreach (PrefabGroupRowsViewModel b in bands) b.IsExpanded = false;

        untouched.PrefabSearch = "";
        check("clearing the search puts everything back",
            bands.Count(b => b.IsVisible) == allBands && !untouched.PrefabNothingFound,
            $"{bands.Count(b => b.IsVisible)} of {allBands}");
        check("...and closes the bands again rather than handing back a wall",
            bands.All(b => !b.IsExpanded), "");

        // Every band that has parts with fields puts each part on a plate of its own — including the ones
        // the toolkit cannot mint, which is what the first split rule got wrong.
        check("a band whose parts carry fields splits into a plate per part",
            bands.First(b => b.Title == "Damage").Elements.Count > 1
            && bands.First(b => b.Title == "Door damage").Elements.Count > 1,
            $"damage={bands.First(b => b.Title == "Damage").Elements.Count}, "
            + $"door damage={bands.First(b => b.Title == "Door damage").Elements.Count}");

        check("only a frame reference carries the resolve dot",
            rows.Where(r => r.HasDot).All(r => r.IsPicker)
            && rows.Any(r => r.IsNumber && !r.HasDot), "");
        // Every row is name-on-top, control-below-full-width. A position is the panel's own vector block,
        // which already draws its own name and buttons, so the row does not draw a second one.
        check("a position draws its own name and needs no caption above it",
            rows.Where(r => r.IsVector).All(r => !r.HasCaption)
            && rows.Where(r => !r.IsVector).All(r => r.HasCaption), "");

        check("an axle row removes its PAIR, by pair index",
            rows.Where(r => r.Removable == CarItemKind.AxlePair).Select(r => r.RemoveIndex).Distinct().Count()
                == rows.Count(r => r.Removable == CarItemKind.AxlePair) / 2,
            string.Join(",", rows.Where(r => r.Removable == CarItemKind.AxlePair).Select(r => r.RemoveIndex)));
    }

    /// <summary>
    /// The write path: point a slot at another frame, read it back, take it back. On a duplicate of the
    /// working copy — a prefab edit lands in the extracted folder, and that folder IS the game install.
    /// <para>The assertion that matters is the last one: after the edit the container still re-emits from
    /// its typed model, which is what proves the bit-packed layout survived being rewritten.</para>
    /// </summary>
    private static void CheckEditing(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_prefab_edit");
        try
        {
            string source = MafiaEnvironment.ExtractedDir(car);
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, scratch, StringComparison.Ordinal));
            foreach (string f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(f, f.Replace(source, scratch, StringComparison.Ordinal), overwrite: true);

            PrefabAssembly? before = PrefabAssembly.ReadFrom(scratch);
            if (before == null) { check("the scratch copy still has a prefab", false, scratch); return; }

            check("the panel offers the archive's own frames to pick from",
                before.FrameChoices.Count > 0, $"{before.FrameChoices.Count} frames");
            check("a reference row knows which slot it is",
                before.Entries.SelectMany(e => e.Groups).SelectMany(g => g.Rows).Any(r => r.CanEdit), "");

            // Point the headlight at some other frame of the same car.
            PrefabRefView head = before.Entries.SelectMany(e => e.Groups).SelectMany(g => g.Rows)
                .First(r => r.Slot == CarFrameSlot.Headlight);
            FrameChoice target = before.FrameChoices.First(c => c.Name != head.Value);

            PrefabEditing.Change? change = PrefabEditing.SetFrameIn(
                scratch, CarFrameSlot.Headlight, 0, target.Hash, "Headlight");
            check("setting a slot reports what it changed", change != null,
                change == null ? "(refused)" : $"{change.Before:X} -> {change.After:X}");
            if (change == null) return;

            PrefabAssembly? after = PrefabAssembly.ReadFrom(scratch);
            PrefabRefView? nowHead = after?.Entries.SelectMany(e => e.Groups).SelectMany(g => g.Rows)
                .FirstOrDefault(r => r.Slot == CarFrameSlot.Headlight);
            check("the slot now names the frame that was picked",
                nowHead is { Kind: PrefabRefKind.Reference } && nowHead.Value == target.Name,
                $"{nowHead?.Value} (wanted {target.Name})");
            check("...and nothing else in the entry went dangling",
                after != null && !after.HasDangling,
                after == null ? "(unreadable)" : after.Entries.First(e => e.Decoded).Status);

            PrefabEditing.Restore(change, change.Before);
            PrefabAssembly? undone = PrefabAssembly.ReadFrom(scratch);
            PrefabRefView? backHead = undone?.Entries.SelectMany(e => e.Groups).SelectMany(g => g.Rows)
                .FirstOrDefault(r => r.Slot == CarFrameSlot.Headlight);
            check("undo puts the original frame back", backHead?.Value == head.Value,
                $"{backHead?.Value} (was {head.Value})");
            sb.AppendLine($"\nedit probe: headlight {head.Value} -> {target.Name} -> {backHead?.Value}");

            // EVERY slot, not a sample: write a value the file cannot already hold, read it back through the
            // panel's own reader, and put it back. This is what makes "the car is fully editable" a measured
            // claim rather than a list of the fields somebody remembered to wire up.
            {
                using var scratchPrefab = new MemoryStream();
                string prf = SdsManifest.Load(scratch).GetFiles("PREFAB")[0];
                int slots = 0, wrote = 0, restored = 0;
                var badValue = new List<string>();
                foreach (CarValueSlot slot in Enum.GetValues<CarValueSlot>())
                {
                    PrefabFile before2 = PrefabFile.Load(prf);
                    if (float.IsNaN(before2.GetCarValue(slot, 0))) continue;   // this car has none of these
                    slots++;

                    // A flag stores what a flag can store, so probing it with 7 would only prove that a bool
                    // is a bool. Everything else takes the value verbatim.
                    float probe = slot == CarValueSlot.WindowOpenable ? 1f : 7f;
                    PrefabEditing.ValueChange? c = PrefabEditing.SetValueIn(
                        scratch, slot, 0, 0, probe, slot.ToString());
                    if (c == null) { badValue.Add(slot + " (refused)"); continue; }
                    if (Math.Abs(PrefabFile.Load(prf).GetCarValue(slot, 0) - probe) < 1e-4) wrote++;
                    else badValue.Add(slot.ToString());
                    PrefabEditing.RestoreValue(c, c.Before);
                    if (Math.Abs(PrefabFile.Load(prf).GetCarValue(slot, 0) - c.Before) < 1e-4) restored++;
                }
                check("every number slot this car has writes and reads back",
                    slots > 0 && wrote == slots,
                    $"{wrote} of {slots} — {string.Join(", ", badValue)}");
                check("...and every one of them undoes exactly", restored == slots, $"{restored} of {slots}");

                int frames = 0, framesOk = 0, undoValues = 0;
                var badFrame = new List<string>();
                var badUndo = new List<string>();
                foreach (CarFrameSlot slot in Enum.GetValues<CarFrameSlot>())
                {
                    PrefabFile file = PrefabFile.Load(prf);
                    if (file.CarSlotCount(slot) == 0) continue;
                    ulong was = file.GetCarFrame(slot, 0);
                    frames++;
                    PrefabEditing.Change? c = PrefabEditing.SetFrameIn(scratch, slot, 0, target.Hash, slot.ToString());
                    if (c == null) { badFrame.Add(slot + " (refused)"); continue; }
                    if (PrefabFile.Load(prf).GetCarFrame(slot, 0) != target.Hash)
                    {
                        badFrame.Add(slot + " (not written)");
                        continue;
                    }
                    // What the change SAYS was there, checked against what was really there. This is the
                    // whole of an undo: Ctrl+Z writes back this number and nothing else, so a slot that
                    // records the wrong one takes the pick back to something that was never in the file.
                    if (c.Before == was) undoValues++;
                    else badUndo.Add($"{slot} (recorded {c.Before:X}, was {was:X})");
                    // Undone the way the app undoes it — through the change, not through a value the probe
                    // kept for itself, which would test a path no user can reach.
                    PrefabEditing.Restore(c, c.Before);
                    if (PrefabFile.Load(prf).GetCarFrame(slot, 0) == was) framesOk++;
                    else badFrame.Add(slot + " (not restored)");
                }
                check("every frame slot this car has writes, reads back and undoes",
                    frames > 0 && framesOk == frames,
                    $"{framesOk} of {frames} — {string.Join(", ", badFrame)}");
                check("...and every one of them records the frame that was really there to undo to",
                    frames > 0 && undoValues == frames,
                    $"{undoValues} of {frames} — {string.Join(", ", badUndo)}");
                check("the archive still reads whole after every slot was written and put back",
                    PrefabAssembly.ReadFrom(scratch) is { } a5 && a5.Entries.Any(e => e.Decoded)
                    && !a5.HasDangling, "");
            }

            // ── Numbers: typed into a field, written to the file, taken back ──
            PrefabEditing.ValueChange? depth = PrefabEditing.SetValueIn(
                scratch, CarValueSlot.WindowDepth, 0, 0, 0.75f, "Depth");
            check("a number typed into a field reaches the file",
                depth != null && Value(PrefabAssembly.ReadFrom(scratch), "Depth") == "0.750",
                Value(PrefabAssembly.ReadFrom(scratch), "Depth") ?? "(none)");
            if (depth != null)
            {
                PrefabEditing.RestoreValue(depth, depth.Before);
                check("...and undo puts the old number back",
                    Math.Abs(depth.Before - (PrefabAssembly.ReadFrom(scratch) is { } a2
                        ? a2.Entries.SelectMany(e => e.Groups).SelectMany(g => g.Rows)
                            .First(r => r.Label == "Depth").X : -1)) < 1e-4, $"{depth.Before}");
            }

            PrefabEditing.ValueChange? handle = PrefabEditing.SetValueIn(
                scratch, CarValueSlot.DoorHandle, 0, 1, -2.5f, "Handle");
            check("one axis of a position can be set on its own",
                handle != null && PrefabAssembly.ReadFrom(scratch) is { } a3
                && Math.Abs(a3.Entries.SelectMany(e => e.Groups).SelectMany(g => g.Rows)
                    .First(r => r.Label == "Handle").Y - (-2.5f)) < 1e-4, "");
            if (handle != null) PrefabEditing.RestoreValue(handle, handle.Before);

            check("the archive still packs after its numbers were edited",
                PrefabAssembly.ReadFrom(scratch) is { } a4 && !a4.HasDangling, "");

            // ── The panel's own row, not only the writer under it ──
            // The row the panel binds to is a SNAPSHOT of the file, and typing a number does not rebuild the
            // panel — rebuilding would pull the fields out from under the caret. So a value that reaches the
            // file but not the row resets itself on screen a moment later, and a write that worked looks
            // exactly like one that was refused. Driven here through the row, the way the panel drives it.
            {
                PrefabAssembly? live = PrefabAssembly.ReadFrom(scratch);
                List<PrefabRefView> all = live == null
                    ? []
                    : [.. live.Entries.SelectMany(e => e.Groups).SelectMany(g => g.Rows)];
                IReadOnlyList<FrameChoice> choices = live?.FrameChoices ?? [];

                PrefabRowViewModel Bind(PrefabRefView row, bool accept = true)
                {
                    var vm = new PrefabRowViewModel(row, choices, (_, _) => true);
                    vm.OnSetValue((r, ax, v) => accept && r.ValueSlot is { } s
                        && PrefabEditing.SetValueIn(scratch, s, r.Index, ax, v, r.Label) != null);
                    return vm;
                }

                PrefabRefView typeRow = all.First(r => r.Label == "Type" && r.Kind == PrefabRefKind.Number);
                PrefabRowViewModel typeVm = Bind(typeRow);
                typeVm.Number = 3f;
                check("a number typed into the panel stays in the field once the file takes it",
                    Math.Abs(typeVm.Number - 3f) < 1e-4, $"{typeVm.Number} (was {typeRow.X})");
                check("...and it really did reach the file",
                    Value(PrefabAssembly.ReadFrom(scratch), "Type") == "3",
                    Value(PrefabAssembly.ReadFrom(scratch), "Type") ?? "(none)");
                check("...and the row's own text follows the field, so the search still agrees with it",
                    typeVm.Value == "3", typeVm.Value);

                PrefabRefView sits = all.First(r => r.Label == "Sits at");
                PrefabRowViewModel sitsVm = Bind(sits);
                sitsVm.Y = -1.25f;
                check("an axis of a position keeps what was typed into it",
                    Math.Abs(sitsVm.Y - -1.25f) < 1e-4, $"{sitsVm.Y}");
                check("...and moving one axis leaves the other two where they were",
                    Math.Abs(sitsVm.X - sits.X) < 1e-4 && Math.Abs(sitsVm.Z - sits.Z) < 1e-4,
                    $"({sitsVm.X}, {sitsVm.Y}, {sitsVm.Z})");

                PrefabRefView opens = all.First(r => r.Label == "Opens");
                PrefabRowViewModel opensVm = Bind(opens);
                bool wasOpen = opensVm.Flag;
                opensVm.Flag = !wasOpen;
                check("a switch flipped in the panel stays flipped",
                    opensVm.Flag == !wasOpen && opensVm.Value == (!wasOpen ? "yes" : "no"), opensVm.Value);

                PrefabRowViewModel refused = Bind(typeRow, accept: false);
                refused.Number = 42f;
                check("a refused write leaves the field showing what the file has, not what was typed",
                    Math.Abs(refused.Number - typeRow.X) < 1e-4, $"{refused.Number}");
            }

            // ── Adding and dropping whole parts ──
            foreach ((CarItemKind kind, string group) in new[]
                     {
                         (CarItemKind.Seat, "Seats"), (CarItemKind.Door, "Doors"),
                         (CarItemKind.Window, "Windows"), (CarItemKind.ClimbBox, "Climb boxes"),
                         (CarItemKind.Wiper, "Wipers"), (CarItemKind.AxlePair, "Axles"),
                     })
            {
                int before2 = Parts(PrefabAssembly.ReadFrom(scratch), group);
                PrefabEditing.ItemChange? added = PrefabEditing.AddItemIn(
                    scratch, kind, target.Hash, kind.ToString());
                int after2 = Parts(PrefabAssembly.ReadFrom(scratch), group);
                // A pair is two axles, so two blocks appear at once.
                int step = kind == CarItemKind.AxlePair ? 2 : 1;
                check($"adding a {kind} lands in the file",
                    added != null && after2 == before2 + step, $"{before2} -> {after2}");

                if (added == null) continue;
                check($"...and the entry still reads back whole after a {kind} was added",
                    PrefabAssembly.ReadFrom(scratch) is { } a && a.Entries.Any(e => e.Decoded), "");

                PrefabEditing.TakeAway(added);
                check($"...and taking a {kind} away puts the count back",
                    Parts(PrefabAssembly.ReadFrom(scratch), group) == before2,
                    $"{Parts(PrefabAssembly.ReadFrom(scratch), group)} (was {before2})");
            }

            // Dropping a shipped part and putting it back must restore it byte for byte, not a look-alike.
            PrefabAssembly? full = PrefabAssembly.ReadFrom(scratch);
            string doorsBefore = Describe(full, "Doors");
            int doorsWere = Parts(full, "Doors");
            PrefabEditing.ItemChange? dropped = PrefabEditing.RemoveItemIn(scratch, CarItemKind.Door, 1, "Door");
            check("dropping a door removes exactly one",
                dropped != null && Parts(PrefabAssembly.ReadFrom(scratch), "Doors") == doorsWere - 1,
                $"{Parts(PrefabAssembly.ReadFrom(scratch), "Doors")} (was {doorsWere})");
            if (dropped != null)
            {
                PrefabEditing.PutBack(dropped);
                check("...and putting it back restores the very same door",
                    Describe(PrefabAssembly.ReadFrom(scratch), "Doors") == doorsBefore, "");
            }

            // ── Emptying a band completely, and filling it again ──
            // The "+" lives on the band header, so a band that disappeared with its last part would be a
            // one-way door: a car that lost its last wiper could never have another. Emptied here on purpose,
            // one part at a time, and then refilled.
            foreach ((CarItemKind kind, string group, int step) in new[]
                     {
                         (CarItemKind.Wiper, "Wipers", 1),
                         (CarItemKind.Door, "Doors", 1),
                         (CarItemKind.AxlePair, "Axles", 2),
                     })
            {
                int left = Parts(PrefabAssembly.ReadFrom(scratch), group);
                for (int guard = 0; left > 0 && guard < 64; guard++)
                {
                    if (PrefabEditing.RemoveItemIn(scratch, kind, 0, group) == null) break;
                    left = Parts(PrefabAssembly.ReadFrom(scratch), group);
                }
                PrefabAssembly? bare = PrefabAssembly.ReadFrom(scratch);
                check($"a band emptied of every {kind} is still there",
                    bare?.Entries.SelectMany(e => e.Groups).Any(g => g.Title == group) == true,
                    $"{group}: {Parts(bare, group)} left");
                check($"...and the entry still reads back whole with no {kind} at all",
                    bare?.Entries.Any(e => e.Decoded) == true && bare?.HasDangling == false, "");

                check($"...and a {kind} can be added back into the empty band",
                    PrefabEditing.AddItemIn(scratch, kind, target.Hash, group) != null
                    && Parts(PrefabAssembly.ReadFrom(scratch), group) == step,
                    $"{Parts(PrefabAssembly.ReadFrom(scratch), group)} (wanted {step})");
                check($"...and the archive reads whole again once the {kind} is back",
                    PrefabAssembly.ReadFrom(scratch) is { HasDangling: false } back
                    && back.Entries.Any(e => e.Decoded), "");
            }
        }
        catch (Exception ex)
        {
            check("the prefab edit path runs", false, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* a scratch folder left behind is not worth failing over */ }
        }
    }

    private static string? Value(PrefabAssembly? assembly, string label) =>
        assembly?.Entries.SelectMany(e => e.Groups).SelectMany(g => g.Rows)
            .FirstOrDefault(r => r.Label == label)?.Value;

    // How many PARTS a band holds. A part made of several fields is a header plus its rows, so counting rows
    // would count fields; a bare list has no headers, so there every row is a part.
    private static int Parts(PrefabAssembly? assembly, string group)
    {
        PrefabGroupView? band = assembly?.Entries.SelectMany(e => e.Groups)
            .FirstOrDefault(g => g.Title == group);
        if (band == null) return -1;
        int parts = band.Rows.Count(r => r.Item != null);
        return parts > 0 ? parts : band.Rows.Count;
    }

    private static string Describe(PrefabAssembly? assembly, string group) =>
        assembly == null ? "" : string.Join(" | ",
            assembly.Entries.SelectMany(e => e.Groups).Where(g => g.Title == group)
                .SelectMany(g => g.Rows).Select(r => $"{r.Label}={r.Value}:{r.Detail}"));

    // The read runs off the UI thread; give it the dispatcher turns it needs to come back.
    private static void Pump(Func<bool> until)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (!until() && DateTime.UtcNow < end)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(10);
        }
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
