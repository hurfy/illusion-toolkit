using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Effects;
using Illusion.Assets.EntityData;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Effects;
using Illusion.Scene;
using Illusion.ViewModels;
using Illusion.Viewport;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What an <c>.eff</c> is. Every car ships one, and until now the toolkit carried it as an opaque blob —
/// so a car's own smoke, fire and sparks were the one part of it nothing could read.
///
/// <para>
/// <b>The container.</b> A chunk is <c>u32 tag, u32 size, byte[size - 8] payload</c>, size INCLUDING the
/// header. A container's payload is child chunks packed back to back filling it exactly — no padding — so
/// "is this a container" is answered by trying to tile it and seeing whether it comes out even. The root
/// tag is 666. Tags are local to their parent: a 2 inside a pattern means generations, a 2 inside an
/// operator's parameter block means something else entirely. (Layout per MafiaToolkit's reader, which took
/// it from the game's own <c>C_InputChunk</c> / <c>C_EffectsLibrary::Load</c>; every claim below is
/// re-measured here against the shipped files rather than taken on faith.)
/// </para>
/// <para>
/// <b>The schema, as far as this probe walks it.</b> 666 root → 668 patterns → pattern (tag 0; payload
/// starts <c>u32 version, u32 id</c>) → 1 frames (100 each) / 2 generations (200 each: 0 = name, then
/// operators as 300 = <c>u32 type, u8 enabled</c>) / 400 sounds.
/// </para>
/// Reads only; nothing is written. Output: %TEMP%\illusion_effects.txt
/// </summary>
internal static class EffectProbes
{
    /// <summary>The one tag that is not context-local — the file says this or it is not an effects file.</summary>
    private const uint RootTag = 666;

    private const uint PatternsTag = 668;
    private const uint FramesTag = 1;
    private const uint GenerationsTag = 2;
    private const uint FrameTag = 100;
    private const uint GenerationTag = 200;
    private const uint OperatorTag = 300;
    private const uint SoundTag = 400;
    private const uint NameTag = 0;

    /// <summary>
    /// The operator kinds, by the id stored in a 300 chunk. Names from the game's own E_OperatorType as the
    /// reference toolkit recorded it; what this probe measures is only WHICH ids the shipped files use.
    /// </summary>
    private static readonly string[] OperatorNames =
    [
        "Birth", "Position", "Speed", "Texture", "Rotation", "Scale", "Color", "Shape", "EmittingParticle",
        "Acceleration", "Physics", "LOD", "Light", "ScaleNU", "MotionInheritance", "ColorHSV", "Emissivity",
        "ShiftedTexture",
    ];

    private readonly record struct Chunk(uint Tag, int Start, int Length);

    private sealed record Pattern(uint Id, int Bytes, int Frames, int Sounds, List<string> Generations);

    internal static void RunEffectsProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_effects.txt");
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

            Dictionary<uint, List<(string Archive, string Folder)>> owners = Corpus(sb, Check);
            OneCar(sb, focus, Check);
            Wiring(sb, owners, Check);
            Editing(sb, focus, Check);
            Tab(sb, focus, Check);

            sb.Insert(0, $"EFFECTS PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "EFFECTS PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // ── every .eff in the game ──

    private static Dictionary<uint, List<(string Archive, string Folder)>> Corpus(
        StringBuilder sb, Action<string, bool, string> check)
    {
        sb.AppendLine("════ every .eff in the game ════");

        var byFolder = new SortedDictionary<string, (int Files, long Bytes, int Patterns)>(StringComparer.Ordinal);
        var owners = new Dictionary<uint, List<(string Archive, string Content)>>();
        var operators = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int files = 0, rooted = 0, tiled = 0, patterns = 0, twice = 0;

        foreach (FileInfo eff in EveryEffectFile())
        {
            files++;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(eff.FullName); }
            catch (IOException) { continue; }

            List<Chunk>? top = Tile(bytes, 0, bytes.Length);
            if (top is not [{ Tag: RootTag } root]) continue;
            rooted++;

            List<Pattern> found = Patterns(bytes, root, operators);
            if (found.Count > 0 || Tile(bytes, root.Start, root.Length) != null) tiled++;
            patterns += found.Count;

            string folder = Folder(eff);
            (int f, long b, int p) = byFolder.TryGetValue(folder, out (int, long, int) had) ? had : (0, 0L, 0);
            byFolder[folder] = (f + 1, b + eff.Length, p + found.Count);

            string owner = eff.Directory!.Name;
            string content = folder;
            if (found.Select(p => p.Id).Distinct().Count() != found.Count) twice++;
            foreach (Pattern pattern in found)
            {
                if (!owners.TryGetValue(pattern.Id, out List<(string, string)>? list))
                {
                    owners[pattern.Id] = list = [];
                }
                list.Add((owner, content));
            }
        }

        foreach ((string folder, (int f, long b, int p)) in byFolder)
        {
            sb.AppendLine($"  {folder,-16} {f,4} files  {b,12:N0} bytes  {p,5} patterns");
        }

        check("every effects file opens as a chunk tree with the 666 root", files > 0 && rooted == files,
            $"{rooted}/{files} files");
        check("the chunk tree tiles its payload exactly, with nothing left over", rooted > 0 && tiled == rooted,
            $"{tiled}/{rooted} files");

        // The numbering is what a modder has to live inside: an effect is named by an id, so adding one
        // means finding a free number, and the question is how far "free" has to reach. Measured, because
        // the answer decides whether a new effect is a one-archive matter or a game-wide search.
        //
        // Measured answer: the numbering is not one global register. Ids repeat freely between a district
        // and the archives that ship beside it — sicily and its crash archive share 46, and a district and
        // the particle library share one — so the game clearly tolerates a repeat between things it has
        // loaded together, presumably by keeping each library apart.
        //
        // CARS are the strict corner, and the only one that matters for editing one: a car's id never
        // appears outside the cars folder, and inside it only on a variant of the same car (a winter twin,
        // the prison version of a bus). So picking a number for a new car effect means avoiding the numbers
        // the other cars use — not searching the whole game.
        int repeated = 0, carLeaks = 0;
        var examples = new List<string>();
        var pairs = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach ((uint id, List<(string Archive, string Folder)> list) in owners.OrderBy(o => o.Key))
        {
            if (list.Select(o => o.Archive).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 1) continue;
            repeated++;

            string[] kinds = [.. list.Select(o => o.Folder).Distinct(StringComparer.Ordinal).Order()];
            string pair = string.Join(" + ", kinds);
            pairs[pair] = pairs.TryGetValue(pair, out int n) ? n + 1 : 1;

            if (kinds.Contains("cars") && kinds.Length > 1) carLeaks++;
            if (kinds.Contains("cars") && examples.Count < 6)
            {
                examples.Add($"    id {id}: {string.Join(", ", list.Select(o => o.Archive).Distinct())}");
            }
        }

        sb.AppendLine($"\n  {patterns} patterns carry {owners.Count} distinct ids; "
            + $"{repeated} id(s) appear in more than one archive");
        foreach ((string pair, int count) in pairs.OrderByDescending(p => p.Value))
        {
            sb.AppendLine($"    {pair,-28} {count,4} shared id(s)");
        }
        foreach (string example in examples) sb.AppendLine(example);

        check("no archive ever gives one id to two of its own effects", twice == 0,
            twice == 0 ? "an id is unique inside its file" : $"{twice} files repeat an id");
        check("a car's effect id never appears outside the cars, so a new one only has to dodge other cars",
            repeated > 0 && carLeaks == 0,
            carLeaks == 0 ? "no car id is used by a district, a shop or the library" : $"{carLeaks} leak out");

        sb.AppendLine("\n── operator kinds used, across every effect in the game ──");
        foreach ((string name, int count) in operators.OrderByDescending(o => o.Value))
        {
            sb.AppendLine($"    {name,-20} {count,6}");
        }
        check("the operator ids in the files stay inside the known 18 kinds",
            operators.Count > 0 && !operators.Keys.Any(k => k.StartsWith("op", StringComparison.Ordinal)),
            $"{operators.Count} kinds seen");

        return owners;
    }

    // ── one car, in full ──

    private static void OneCar(StringBuilder sb, string focus, Action<string, bool, string> check)
    {
        FileInfo? eff = EveryEffectFile()
            .FirstOrDefault(f => f.Directory!.Name.StartsWith(focus, StringComparison.OrdinalIgnoreCase));
        if (eff == null) { sb.AppendLine($"\n{focus}: no effects file"); return; }

        byte[] bytes = File.ReadAllBytes(eff.FullName);
        List<Chunk>? top = Tile(bytes, 0, bytes.Length);
        if (top is not [{ Tag: RootTag } root]) { sb.AppendLine($"\n{focus}: not an effects file"); return; }

        var operators = new SortedDictionary<string, int>(StringComparer.Ordinal);
        List<Pattern> found = Patterns(bytes, root, operators);

        sb.AppendLine($"\n════ {focus} — {eff.Name} ({bytes.Length:N0} bytes) ════");
        foreach (Pattern pattern in found)
        {
            sb.AppendLine($"  effect id {pattern.Id}  {pattern.Bytes:N0} bytes  "
                + $"{pattern.Frames} frame(s), {pattern.Generations.Count} generation(s), "
                + $"{pattern.Sounds} sound(s)");
            foreach (string generation in pattern.Generations) sb.AppendLine($"      {generation}");
        }

        // Two per car is the shape the whole corpus has; it is what tells us the file is the car's OWN
        // effects rather than a copy of the library it draws from.
        check("a car's effects file holds a handful of effects, not a library", found.Count is > 0 and <= 4,
            $"{focus}: {found.Count}");
    }

    // ── what names those ids ──

    private static void Wiring(
        StringBuilder sb, Dictionary<uint, List<(string Archive, string Folder)>> owners,
        Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ which of a car's own numbers name its effects ════");
        sb.AppendLine("  (the .eff says only \"effect 384\"; the field that holds 384 is what says what it IS)");

        var hits = new SortedDictionary<string, int>(StringComparer.Ordinal);
        // And the other way round: an id field of the car that its OWN file does not answer — which is most
        // of them, and where they point instead.
        var elsewhere = new SortedDictionary<string, (int Library, int Own, int Nowhere)>(StringComparer.Ordinal);
        int cars = 0, matched = 0, orphans = 0;

        foreach (FileInfo eff in EveryEffectFile().Where(f => Folder(f) == "cars"))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(eff.FullName); }
            catch (IOException) { continue; }
            List<Chunk>? top = Tile(bytes, 0, bytes.Length);
            if (top is not [{ Tag: RootTag } root]) continue;

            var ignored = new SortedDictionary<string, int>(StringComparer.Ordinal);
            List<Pattern> found = Patterns(bytes, root, ignored);
            if (found.Count == 0) continue;

            // The archive beside the effects file — its EntityDataStorage is where a car's numbers live.
            var sds = new FileInfo(Path.Combine(
                MafiaEnvironment.PcFolder, "sds", "cars", eff.Directory!.Name));
            CarTuning? tuning;
            try { tuning = CarTuning.Read(sds); }
            catch (Exception) { continue; }
            if (tuning == null) continue;
            cars++;

            var byName = new List<(string Name, long Value)>();
            foreach (TuningTableView table in tuning.Tables)
            {
                foreach (TuningBandView band in table.Bands)
                {
                    foreach (TuningElementView element in band.Elements)
                    {
                        foreach (TuningFieldView row in element.Rows) byName.Add((row.Name, row.Number));
                    }
                }
            }

            bool any = false;
            foreach (Pattern pattern in found)
            {
                string[] names = [.. byName.Where(f => f.Value == pattern.Id).Select(f => f.Name).Distinct()];
                if (names.Length == 0) { orphans++; continue; }
                any = true;
                foreach (string name in names) hits[name] = hits.TryGetValue(name, out int n) ? n + 1 : 1;
            }
            if (any) matched++;

            // Every OTHER effect the car names — the smoke, the explosion, the sparks. None of those are in
            // its own file, so they have to resolve somewhere, and where decides what a per-car tab can
            // reach and what it cannot.
            var mine = found.Select(p => p.Id).ToHashSet();
            foreach ((string name, long value) in byName)
            {
                if (!name.EndsWith("ID", StringComparison.Ordinal)
                    && !name.EndsWith("Id", StringComparison.Ordinal)) continue;
                // A car's tail is mostly SOUND ids, which are numbered in their own space entirely. Left in,
                // they would collide with effect numbers by coincidence and read as if the sound resolved to
                // an effect. The physics material is a third space again.
                if (name.Contains("Snd", StringComparison.Ordinal)
                    || name.Contains("Sound", StringComparison.Ordinal)
                    || name.Contains("Material", StringComparison.Ordinal)) continue;
                if (value <= 0 || value > uint.MaxValue) continue;

                (int library, int own, int nowhere) = elsewhere.TryGetValue(name, out (int, int, int) had)
                    ? had : (0, 0, 0);
                if (mine.Contains((uint)value)) own++;
                else if (owners.TryGetValue((uint)value, out List<(string Archive, string Folder)>? where)
                    && where.Count > 0)
                {
                    library++;
                }
                else nowhere++;
                elsewhere[name] = (library, own, nowhere);
            }
        }

        sb.AppendLine($"\n  {cars} cars read; {matched} have at least one effect their own tuning names, "
            + $"{orphans} effect(s) nothing names");
        foreach ((string name, int count) in hits.OrderByDescending(h => h.Value))
        {
            sb.AppendLine($"    {name,-32} names an effect on {count,3} cars");
        }

        sb.AppendLine("\n── and every other effect a car names: does its own file answer? ──");
        sb.AppendLine($"    {"field",-32} {"own file",8} {"another",8} {"no file",8}");
        foreach ((string name, (int library, int own, int nowhere)) in elsewhere.OrderByDescending(e => e.Value.Own))
        {
            sb.AppendLine($"    {name,-32} {own,8} {library,8} {nowhere,8}");
        }

        check("a car's effects are named by fields of its own tuning table", cars > 0 && hits.Count > 0,
            hits.Count == 0 ? "no field of any car holds one of its effect ids" : $"{hits.Count} field name(s)");

        // The one that decides how far a per-car effects tab can go: if most of a car's effect ids answered
        // in its own file, the tab would be the whole story. They do not.
        int inOwn = elsewhere.Values.Sum(v => v.Own);
        int outside = elsewhere.Values.Sum(v => v.Library + v.Nowhere);
        check("most of a car's effect ids are NOT in its own file — the file holds two of them", outside > inOwn,
            $"{inOwn} answered by the car's own file, {outside} not");
    }

    // ── what the tab will do with it ──

    /// <summary>
    /// The editing layer, exercised without touching the player's install: every car's effects read into
    /// rows, one value written and read back, and one effect copied — with the copy handed to the core's
    /// own reader, which is the thing that has to keep accepting the file.
    /// </summary>
    private static void Editing(StringBuilder sb, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ reading a car's effects as editable rows ════");

        int cars = 0, read = 0, values = 0, roles = 0;
        CarEffects? sample = null;

        foreach (FileInfo sds in new DirectoryInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars"))
            .GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;
            cars++;

            CarEffects? effects;
            try { effects = CarEffects.ReadFrom(extracted, sds); }
            catch (Exception) { continue; }
            if (effects == null) continue;

            read++;
            values += effects.ValueCount;
            roles += effects.Effects.Count(e => e.Role.Length > 0);
            if (sds.Name.StartsWith(focus, StringComparison.OrdinalIgnoreCase)) sample = effects;
        }

        sb.AppendLine($"  {read} of {cars} extracted cars carry effects; {values} editable values in all, "
            + $"{roles} effect(s) their own tuning names");
        check("every car's effects file opens as rows the panel can bind", cars > 0 && read > 0 && values > 0,
            $"{read}/{cars} cars, {values} values");

        if (sample == null) { sb.AppendLine($"  {focus}: not extracted, nothing to exercise"); return; }

        sb.AppendLine($"\n── {focus} ──");
        foreach (EffectView effect in sample.Effects)
        {
            sb.AppendLine($"  {effect.Title} — {effect.Summary}, {effect.ValueCount} value(s)");
            foreach (EffectGenerationView generation in effect.Generations)
            {
                sb.AppendLine($"      {generation.Title}: {generation.OperatorList}");
                foreach (EffectOperatorView op in generation.Operators.Take(3))
                {
                    foreach (EffectParamView param in op.Parameters.Take(3))
                    {
                        string shown = param.Summary.Length > 0
                            ? param.Summary
                            : string.Join(", ", param.Rows.Select(r => r.Value.ToString("0.###")));
                        sb.AppendLine($"          {op.Title} · {param.Title}: {shown}");
                    }
                }
            }
        }

        // A write is four bytes and nothing else. Measured against a copy of the buffer, because a rule
        // that only holds in principle is the kind that quietly stops holding.
        EffectValueRow? row = sample.Effects
            .SelectMany(e => e.Generations).SelectMany(g => g.Operators)
            .SelectMany(o => o.Parameters).SelectMany(p => p.Rows)
            .FirstOrDefault();
        if (row == null) { check("a value can be written back", false, "no value row to write"); return; }

        byte[] before = [.. sample.Tree.Bytes];
        sample.Tree.WriteFloat(row.Offset, row.Value + 12.5f);
        int differing = 0, outside = 0;
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i] == sample.Tree.Bytes[i]) continue;
            differing++;
            if (i < row.Offset || i >= row.Offset + 4) outside++;
        }
        float readBack = sample.Tree.ReadFloat(row.Offset);
        sample.Tree.WriteFloat(row.Offset, row.Value);

        // Fewer than four is normal and not a weaker result: a float that shares a byte with the one it
        // replaces leaves that byte alone. What matters is that nothing OUTSIDE the four moved.
        check("writing one value moves nothing outside its own four bytes",
            differing is > 0 and <= 4 && outside == 0, $"{differing} byte(s) changed, {outside} of them elsewhere");
        check("the value reads back as it was written", Math.Abs(readBack - (row.Value + 12.5f)) < 1e-4f,
            $"{readBack} vs {row.Value + 12.5f}");
        check("putting it back leaves the file as it was found", sample.Tree.Bytes.SequenceEqual(before),
            "byte for byte");

        // Adding an effect: a copy under a free id. What has to survive is everything else in the file, and
        // the core's own reader — which carries the .eff byte-exact and would be the first to notice a
        // size the splice failed to correct.
        uint fresh = sample.NextFreeId();
        byte[] grown;
        try { grown = sample.BytesWithCopyOf(sample.Effects[0].Id); }
        catch (Exception ex) { check("an effect can be copied", false, ex.Message); return; }

        EffectsTree? reread = EffectsTree.Read(grown);
        sb.AppendLine($"\n  copy of effect {sample.Effects[0].Id} as {fresh}: "
            + $"{sample.Tree.Bytes.Length:N0} → {grown.Length:N0} bytes, "
            + $"{sample.Effects.Count} → {reread?.Effects.Count ?? 0} effects");

        check("a copy leaves a file that still walks", reread != null, reread == null ? "will not parse" : "");
        check("a copy adds exactly one effect, under the free id",
            reread != null && reread.Effects.Count == sample.Effects.Count + 1
            && reread.IdOf(reread.Effects[^1]) == fresh,
            reread == null ? "no tree" : $"{reread.Effects.Count} effects, last id {reread.IdOf(reread.Effects[^1])}");
        check("the effects that were already there keep their ids",
            reread != null && reread.Effects.Take(sample.Effects.Count)
                .Select(reread.IdOf).SequenceEqual(sample.Effects.Select(e => e.Id)),
            "unchanged");

        bool coreKeeps;
        try
        {
            using var stream = new MemoryStream(grown, writable: false);
            coreKeeps = Formats.Effects.EffectsFile.Read(stream).ToBytes().SequenceEqual(grown);
        }
        catch (Exception) { coreKeeps = false; }
        check("the core still carries the grown file byte for byte", coreKeeps,
            coreKeeps ? "round-trips" : "the core rejects or rewrites it");
    }

    // ── the tab ──

    /// <summary>
    /// The panel side, read-only on purpose: the tab exists, it appears for a car with nothing selected,
    /// and it builds the rows. Nothing here commits a value — that would write the player's extracted copy,
    /// and the write path is already exercised above against a buffer.
    /// </summary>
    private static void Tab(StringBuilder sb, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ the Effects tab ════");

        var panel = new Views.ScenePanel();
        System.Windows.Controls.TabItem? tab = panel.PropertyTabs.Items
            .OfType<System.Windows.Controls.TabItem>()
            .FirstOrDefault(t => (t.Header as string) == "Effects");
        check("the panel carries an Effects tab", tab != null, tab == null ? "no tab named Effects" : "");

        var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", focus + ".sds"));
        if (!car.Exists) { sb.AppendLine($"  {focus}: not installed"); return; }

        (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(car);
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
        foreach (SdsFrameNode root in roots) frNode.AddChild(SceneTree.BuildSceneTree(root, leaves));
        sdsNode.AddChild(frNode);
        folder.AddChild(sdsNode);
        host.Tree.RebuildStageRoots();

        var vm = new SelectionViewModel(host);
        vm.RefreshEffects();
        Pump(() => vm.HasEffects);

        check("the tab is reachable with nothing selected", vm.HasEffects, vm.EffectsSummary);
        if (!vm.HasEffects) return;

        check("the picker offers every effect the archive owns",
            vm.EffectList.Count == 2 && vm.HasManyEffects, $"{vm.EffectList.Count} effect(s)");
        check("one effect is on screen, not all of them",
            vm.SelectedEffect != null && ReferenceEquals(vm.SelectedEffect, vm.EffectList[0]),
            vm.SelectedEffect?.Title ?? "(none)");
        // The file numbers its effects and says nothing else about them. A tab reading "Effect 384" twice
        // would be two rows of nothing, so the role from the car's own tuning is what makes the picker
        // usable at all.
        check("the effects are named by what the car calls them, not just numbered",
            vm.EffectList.Any(e => e.Title.Contains("Fire", StringComparison.Ordinal))
            && vm.EffectList.Any(e => e.Title.Contains("Rain", StringComparison.Ordinal)),
            string.Join(" | ", vm.EffectList.Select(e => e.Title)));

        EffectRowsViewModel first = vm.EffectList[0];
        sb.AppendLine($"  {first.Title}: {first.Summary} — {first.Status}");
        foreach (EffectGenerationRowsViewModel band in first.Generations)
        {
            sb.AppendLine($"    {band.Title}: {band.Operators.Count} operator card(s), "
                + $"{band.Operators.Sum(o => o.Rows.Count)} value(s)");
        }

        check("every generation starts folded, since a car's fire is eight of them",
            first.Generations.All(g => !g.IsExpanded), $"{first.Generations.Count} band(s)");
        check("the bands hold operator cards with editable numbers",
            first.Generations.Sum(g => g.Operators.Count) > 0
            && first.Generations.Sum(g => g.Operators.Sum(o => o.Rows.Count)) > 0,
            $"{first.Generations.Sum(g => g.Operators.Count)} cards, "
            + $"{first.Generations.Sum(g => g.Operators.Sum(o => o.Rows.Count))} values");
    }

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

    // ── the container ──

    /// <summary>
    /// The children of a payload, or null when it is a leaf. A container's children fill it exactly; any
    /// leftover byte, or a size that runs past the end or cannot hold its own header, means the bytes are
    /// data rather than chunks. Leaves that happen to tile are possible in principle and harmless here:
    /// this probe only ever descends by tag, and reads nothing it did not find under a known one.
    /// </summary>
    private static List<Chunk>? Tile(byte[] bytes, int start, int length)
    {
        var found = new List<Chunk>();
        int at = start, end = start + length;
        while (at + 8 <= end)
        {
            uint tag = BitConverter.ToUInt32(bytes, at);
            uint size = BitConverter.ToUInt32(bytes, at + 4);
            if (size < 8 || at + size > end) return null;
            found.Add(new Chunk(tag, at + 8, (int)size - 8));
            at += (int)size;
        }
        return at == end && found.Count > 0 ? found : null;
    }

    private static List<Pattern> Patterns(byte[] bytes, Chunk root, IDictionary<string, int> operators)
    {
        var found = new List<Pattern>();
        foreach (Chunk branch in Tile(bytes, root.Start, root.Length) ?? [])
        {
            if (branch.Tag != PatternsTag) continue;
            foreach (Chunk pattern in Tile(bytes, branch.Start, branch.Length) ?? [])
            {
                if (pattern.Length < 8) continue;
                uint id = BitConverter.ToUInt32(bytes, pattern.Start + 4);
                int frames = 0, sounds = 0;
                var generations = new List<string>();

                foreach (Chunk part in Tile(bytes, pattern.Start + 8, pattern.Length - 8) ?? [])
                {
                    List<Chunk> kids = Tile(bytes, part.Start, part.Length) ?? [];
                    if (part.Tag == FramesTag) { frames += kids.Count(k => k.Tag == FrameTag); continue; }
                    if (part.Tag == GenerationsTag)
                    {
                        foreach (Chunk generation in kids.Where(k => k.Tag == GenerationTag))
                        {
                            generations.Add(Generation(bytes, generation, operators));
                        }
                        continue;
                    }
                    sounds += kids.Count(k => k.Tag == SoundTag);
                }

                found.Add(new Pattern(id, pattern.Length + 8, frames, sounds, generations));
            }
        }
        return found;
    }

    /// <summary>One generation as a line: its name when it has one, and the operators that make it.</summary>
    private static string Generation(byte[] bytes, Chunk generation, IDictionary<string, int> operators)
    {
        string? name = null;
        var used = new List<string>();

        foreach (Chunk part in Tile(bytes, generation.Start, generation.Length) ?? [])
        {
            if (part.Tag == NameTag) { name = Text(bytes, part); continue; }
            foreach (Chunk op in Tile(bytes, part.Start, part.Length) ?? [])
            {
                if (op.Tag != OperatorTag || op.Length < 5) continue;
                uint type = BitConverter.ToUInt32(bytes, op.Start);
                bool enabled = bytes[op.Start + 4] != 0;
                string label = type < OperatorNames.Length ? OperatorNames[type] : $"op{type}";
                operators[label] = operators.TryGetValue(label, out int n) ? n + 1 : 1;
                used.Add(enabled ? label : label + " (off)");
            }
        }

        string title = string.IsNullOrEmpty(name) ? "generation" : $"\"{name}\"";
        return used.Count == 0 ? $"{title}: no operators" : $"{title}: {string.Join(", ", used)}";
    }

    /// <summary>A length-prefixed name at the head of a payload, or null when there is none.</summary>
    private static string? Text(byte[] bytes, Chunk chunk)
    {
        if (chunk.Length < 4) return null;
        uint count = BitConverter.ToUInt32(bytes, chunk.Start);
        if (count == 0 || count > 128 || 4 + count > chunk.Length) return null;
        return Encoding.ASCII.GetString(bytes, chunk.Start + 4, (int)count).TrimEnd('\0');
    }

    private static IEnumerable<FileInfo> EveryEffectFile()
    {
        string root = MafiaEnvironment.ResourcesFolder is { } mirror
            ? Path.Combine(mirror, "pc", "sds")
            : Path.Combine(MafiaEnvironment.PcFolder, "sds");
        return Directory.Exists(root)
            ? new DirectoryInfo(root).GetFiles("*.eff", SearchOption.AllDirectories).OrderBy(f => f.FullName)
            : [];
    }

    /// <summary>The archive's own folder — cars, city, particles: what kind of thing owns the effects.</summary>
    private static string Folder(FileInfo eff) => eff.Directory!.Parent!.Name;
}
