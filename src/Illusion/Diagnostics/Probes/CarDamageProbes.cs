using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What a component does when it is hit: the damage parameters on its own deform part, the flags the engine
/// switches on, and the deform handles that decide how far and how hard it crumples — measured over the corpus,
/// then written, saved and read back to see whether an edit reaches the file and reaches nothing else.
///
/// <para>
/// The flag bits are the reason half of this exists. Five of them have a name, all of it annotated by the
/// reference toolkit and none of it measured in game, and they share one 32-bit word with twenty-seven
/// meanings nobody has read. So the census asks which bits shipped cars actually set, and the write path is
/// asked to prove it moves ONE of them.
/// </para>
/// <para>
/// NOTHING IS WRITTEN INTO THE GAME'S FOLDERS. The focus car's working copy is mirrored into the temp
/// directory and every edit lands in the mirror, which is also what makes the comparison possible: the
/// original is still there to compare against.
/// </para>
/// <para>Output: %TEMP%\illusion_car_damage.txt</para>
/// </summary>
internal static class CarDamageProbes
{
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "illusion_car_damage");

    internal static void RunCarDamageProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_damage.txt");
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
            string? mirror = Mirror(focus, folder, sb, Check);
            if (mirror != null)
            {
                RoundTrip(sb, mirror, Check);
                Flags(sb, mirror, Check);
                Handles(sb, mirror, Check);
                Scoped(sb, mirror, Check);
                Undo(sb, mirror, Check);
                Refusals(sb, mirror, Check);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            sb.Insert(0, $"COMPONENT DAMAGE PARAMETERS ({focus}): {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // ── the corpus is the oracle ──

    private static void Corpus(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("════ what the shipped cars carry ════");

        int cars = 0, parts = 0, tuned = 0, bare = 0, bareWithFields = 0, partsWithFields = 0;
        int none = 0, one = 0, more = 0, handles = 0;
        var bits = new Dictionary<int, int>();
        var bitKinds = new Dictionary<int, Dictionary<string, int>>();
        uint widest = 0;
        var groups = new HashSet<int>();
        int rangeZero = 0, radiusZero = 0;
        float massMin = float.MaxValue, massMax = float.MinValue, massZero = 0;
        var untuned = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            Car? car;
            try { car = Car.ReadFrom(extracted); }
            catch (Exception) { continue; }
            if (car?.Prefab.Car == null) continue;
            cars++;

            foreach (CarComponent component in car.Components)
            {
                if (component.IsBare)
                {
                    bare++;
                    if (component.DamageFields.Count > 0 || component.Handles.Count > 0) bareWithFields++;
                    continue;
                }
                parts++;
                if (component.DamageFields.Count > 0) partsWithFields++;
                if (component.Damage is { } damage)
                {
                    tuned++;
                    massMin = MathF.Min(massMin, damage.Mass);
                    massMax = MathF.Max(massMax, damage.Mass);
                    if (damage.Mass == 0f) massZero++;
                    groups.Add(damage.EffectGroup);
                    widest = Math.Max(widest, damage.Flags);
                    for (int bit = 0; bit < 32; bit++)
                    {
                        if (((damage.Flags >> bit) & 1u) == 0u) continue;
                        bits[bit] = bits.GetValueOrDefault(bit) + 1;
                        Dictionary<string, int> kinds =
                            bitKinds.TryGetValue(bit, out Dictionary<string, int>? found)
                                ? found
                                : bitKinds[bit] = new Dictionary<string, int>(StringComparer.Ordinal);
                        kinds[component.Kind] = kinds.GetValueOrDefault(component.Kind) + 1;
                    }
                }
                else if (untuned.Count < 8)
                {
                    untuned.Add($"{sds.Name} \"{component.Name}\" ({component.Kind})");
                }

                switch (component.Handles.Count)
                {
                    case 0: none++; break;
                    case 1: one++; break;
                    default: more++; break;
                }
                foreach (CarHandle handle in component.Handles)
                {
                    handles++;
                    if (handle.Range == Vector3.Zero) rangeZero++;
                    if (handle.Radius == 0f) radiusZero++;
                }
            }
        }

        sb.AppendLine($"  {cars} cars: {parts} deform parts, {bare} bare components");
        sb.AppendLine($"  parts with a tuning block {tuned}, without {parts - tuned}"
            + (untuned.Count == 0 ? "" : " — e.g. " + string.Join("; ", untuned)));
        sb.AppendLine($"  handles: {none} parts carry none, {one} carry one, {more} carry two or more; "
            + $"{handles} handles in all ({rangeZero} with a zero range, {radiusZero} with a zero radius)");
        sb.AppendLine($"  part mass {massMin:F2}…{massMax:F2} kg ({massZero} at zero); "
            + $"{groups.Count} distinct effect groups, 0…{(groups.Count == 0 ? 0 : groups.Max())}");
        sb.AppendLine($"  widest flag word 0x{widest:X} ({widest})");
        foreach ((int bit, int count) in bits.OrderBy(p => p.Key))
        {
            string named = Named(bit);
            Dictionary<string, int> kinds = bitKinds[bit];
            sb.AppendLine($"    bit {bit} (0x{1u << bit:X}) ×{count}{named} — "
                + string.Join(", ", kinds.OrderByDescending(p => p.Value).Take(6)
                    .Select(p => $"{p.Key} ×{p.Value}")));
        }

        // The numbers the spec asserts against rather than prints. A change to how a handle or a part is read
        // shows up here instead of in game.
        check("every deform part of every shipped car yields one component", parts == 1698, $"{parts}");
        check("1093 parts carry no deform handle, 356 carry one and 249 carry two or more",
            none == 1093 && one == 356 && more == 249, $"{none} / {one} / {more}");
        check("and there are 1402 handles in all", handles == 1402, $"{handles}");
        check("every deform part has damage parameters to show", parts > 0 && partsWithFields == parts,
            $"{partsWithFields} of {parts}");
        check("and no bare component has any — there is no part for them to be on",
            bare > 0 && bareWithFields == 0, $"{bareWithFields} of {bare}");

        // Why every flag is written through its own BIT rather than by reading the word, flipping and writing
        // it back. Two separate reasons, and the corpus states both.
        check("some shipped part sets a flag bit nobody has named, so the unnamed bits are load-bearing",
            bits.Keys.Any(b => Named(b).Length == 0), string.Join(", ", bits.Keys.OrderBy(b => b)));
        check("and a shipped flag word is wider than a float carries exactly, so the whole word could not be "
            + "the thing written", widest > (1u << 24), $"widest 0x{widest:X}");

        // Two of the five named flags are set on NOTHING that shipped. Worth knowing before offering them:
        // turning one on is doing something no shipped car does, and the honest place to say so is the hint
        // beside the switch.
        foreach ((string label, int bit) in new[]
                 {
                     ("always-dynamic", 1), ("kill-part", 4), ("snow", 10), ("AI box", 13), ("fade off", 18),
                 })
        {
            sb.AppendLine($"  {label} (bit {bit}): set on {bits.GetValueOrDefault(bit)} of {tuned} parts");
        }
        check("kill-part is a window's flag and nothing else's — the corpus corroborates the annotation",
            bitKinds.TryGetValue(4, out Dictionary<string, int>? kill) && kill.Count == 1
            && kill.ContainsKey("window"),
            string.Join(", ", bitKinds.GetValueOrDefault(4)?.Keys ?? Enumerable.Empty<string>()));
        check("the AI box and fade off flags are set on no shipped part at all",
            bits.GetValueOrDefault(13) == 0 && bits.GetValueOrDefault(18) == 0,
            $"AI box ×{bits.GetValueOrDefault(13)}, fade off ×{bits.GetValueOrDefault(18)}");
    }

    /// <summary>What the reference toolkit calls a bit, or nothing at all when it names none.</summary>
    private static string Named(int bit) => bit switch
    {
        1 => " always-dynamic", 4 => " kill-part", 10 => " snow", 13 => " AI box", 18 => " fade off",
        _ => "",
    };

    // ── the mirror ──

    /// <summary>Copies the focus car's working copy into the temp directory, so every edit below lands there and
    /// the game's own folders are never written to.</summary>
    private static string? Mirror(
        string focus, string folder, StringBuilder sb, Action<string, bool, string> check)
    {
        string source = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, focus + ".sds")));
        if (!File.Exists(Path.Combine(source, "SDSContent.xml")))
        {
            check("the focus car is extracted", false, focus);
            return null;
        }

        string mirror = Path.Combine(Scratch, focus);
        try
        {
            if (Directory.Exists(mirror)) Directory.Delete(mirror, recursive: true);
            Directory.CreateDirectory(mirror);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(mirror, Path.GetFileName(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            check("the focus car can be mirrored into the temp directory", false, ex.Message);
            return null;
        }

        sb.AppendLine($"\n\n════ the focus car, mirrored into {mirror} ════");
        return mirror;
    }

    // ── what was typed comes back ──

    /// <summary>Every damage parameter typed onto a component, read again from the ARCHIVE that was written
    /// rather than from the aggregate that wrote it.</summary>
    private static void RoundTrip(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ a component's own damage parameters survive a round trip ════");

        Damage(sb, mirror, check, "Mass of this component", f => f with { Number = 61.5f },
            f => MathF.Abs(f.Number - 61.5f) < 1e-3f);
        Damage(sb, mirror, check, "Resistance", f => f with { Number = 0.42f },
            f => MathF.Abs(f.Number - 0.42f) < 1e-5f);
        Damage(sb, mirror, check, "Speed window, lowest", f => f with { Number = 2.5f },
            f => MathF.Abs(f.Number - 2.5f) < 1e-5f);
        Damage(sb, mirror, check, "Speed window, highest", f => f with { Number = 33.25f },
            f => MathF.Abs(f.Number - 33.25f) < 1e-4f);
        Damage(sb, mirror, check, "Energy at the start", f => f with { Number = 1200f },
            f => MathF.Abs(f.Number - 1200f) < 1e-2f);
        Damage(sb, mirror, check, "Energy drop", f => f with { Number = 0.125f },
            f => MathF.Abs(f.Number - 0.125f) < 1e-6f);
        Damage(sb, mirror, check, "Centre of mass of this component",
            f => f with { Point = new Vector3(0.13f, -0.24f, 0.35f) },
            f => (f.Point - new Vector3(0.13f, -0.24f, 0.35f)).Length() < 1e-5f);
        Damage(sb, mirror, check, "Effect group", f => f with { Number = 7f }, f => f.Number == 7f);
    }

    /// <summary>
    /// A flag is one bit of a word that holds thirty-two, and the twenty-seven nobody has named have to reach
    /// the game exactly as they arrived — so the question is not only whether the flag moved but whether
    /// anything else in the word did.
    /// </summary>
    private static void Flags(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ a flag moves its own bit and no other ════");

        foreach (string label in new[] { "Always dynamic", "Kill part", "Snow", "AI box", "Fade off" })
        {
            Car? car = Car.ReadFrom(mirror);
            // A part that actually SETS this flag where the car has one, so the check clears a live bit rather
            // than setting a dead one on whichever part happens to be first — and so the word it has to leave
            // alone is a word with something in it.
            CarComponent? part = Carrying(car, label) ?? Pick(car, label);
            if (car == null || part?.Damage is not { } was) { check($"the focus car has {label}", false, ""); continue; }

            bool had = part.DamageFields.First(f => f.Label == label).Number != 0f;
            sb.AppendLine($"  {label}: on \"{part.Name}\" ({part.Kind}), which {(had ? "sets" : "does not set")} it");
            CarEdit? edit = car.SetDamage(part,
                [.. part.DamageFields.Select(f =>
                    f.Label == label ? f with { Number = had ? 0f : 1f } : f)],
                out string? refusal);
            if (edit == null) { check($"{label} can be written", false, refusal ?? ""); continue; }
            CarSave saved = car.Save();
            if (!saved.Ok) { check($"{label} saves", false, string.Join("; ", saved.Lost)); continue; }

            // Found again by its place in the PART LIST, which is the one handle on a component that a fresh
            // read reproduces: the identity is minted per read, and the flag this edit just moved is the very
            // thing "the part that carries it" was selecting on.
            Car? read = Car.ReadFrom(mirror);
            uint now = At(read, part.PartIndex)?.Damage?.Flags ?? 0u;
            uint want = was.Flags ^ (1u << Bit(label));
            sb.AppendLine($"  {label} on \"{part.Name}\": {(had ? "yes" : "no")} → "
                + $"{(had ? "no" : "yes")}, the word 0x{was.Flags:X} → 0x{now:X}");
            check($"{label} flips its own bit and leaves the other thirty-one alone", now == want,
                $"0x{now:X}, wanted 0x{want:X}");

            // …and back, so the next flag starts from the car as it shipped.
            Car? undo = Car.ReadFrom(mirror);
            CarComponent? again = At(undo, part.PartIndex);
            if (undo == null || again == null) continue;
            if (undo.SetDamage(again,
                    [.. again.DamageFields.Select(f =>
                        f.Label == label ? f with { Number = had ? 1f : 0f } : f)],
                    out _) != null)
            {
                undo.Save();
            }
        }
    }

    // ── the handles ──

    private static void Handles(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ how far and how hard a component crumples ════");

        Handle(sb, mirror, check, "Range", f => f with { Point = new Vector3(0.09f, 0.18f, 0.27f) },
            f => (f.Point - new Vector3(0.09f, 0.18f, 0.27f)).Length() < 1e-5f);
        Handle(sb, mirror, check, "Intensity", f => f with { Number = 0.65f },
            f => MathF.Abs(f.Number - 0.65f) < 1e-5f);
        Handle(sb, mirror, check, "Radius", f => f with { Number = 0.31f },
            f => MathF.Abs(f.Number - 0.31f) < 1e-5f);

        // A component that does not crumple says so rather than showing an empty list — and it is the
        // commoner of the two answers, on 1093 of the 1698 shipped parts.
        Car? car = Car.ReadFrom(mirror);
        CarComponent? still = car?.Components.FirstOrDefault(c => !c.IsBare && !c.Crumples);
        CarComponent? crumples = car?.Components.FirstOrDefault(c => c.Crumples);
        sb.AppendLine($"  \"{still?.Name}\" ({still?.Kind}) does not crumple; \"{crumples?.Name}\" "
            + $"({crumples?.Kind}) crumples around {crumples?.Handles.Count} "
            + $"handle(s): {string.Join(", ", crumples?.Handles.Select(h => h.Name) ?? [])}");
        check("the focus car has a component that does not crumple and one that does",
            still != null && crumples != null, "");
        check("and every handle of it carries its three numbers",
            crumples != null && crumples.Handles.All(h => h.Fields.Count == 3), "");

        // The flat address a handle is written through has to name the handle it was read from — get this
        // wrong by one and a modder tuning a door tunes the boot lid, silently.
        if (car?.Prefab.Car != null)
        {
            int flat = 0;
            bool aligned = true;
            foreach (CarDeformPart part in car.Prefab.CarDeformParts)
            {
                foreach (CarDeformHandle handle in part.Handles)
                {
                    aligned &= car.Prefab.CarHandleIndex(part.Index, handle.Index) == flat
                        && car.Prefab.CarHandleAt(flat) == (part.Index, handle.Index);
                    flat++;
                }
            }
            check("every handle's flat address round-trips to the part and handle it came from",
                aligned && flat == car.Prefab.CarHandleCount(), $"{flat} handles");
        }
    }

    // ── everything the modder did not touch is left as it was ──

    private static void Scoped(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ what a damage edit is allowed to disturb ════");

        Car? car = Car.ReadFrom(mirror);
        CarComponent? part = Pick(car, "Mass of this component");
        if (car?.PrefabPath == null || part == null) { check("the focus car reads", false, ""); return; }

        PrefabFile before = PrefabFile.Load(car.PrefabPath);
        CarEdit? edit = car.SetDamage(part,
            [.. part.DamageFields.Select(f =>
                f.Label == "Mass of this component" ? f with { Number = 77.75f } : f)],
            out string? refusal);
        if (edit == null) { check("a part's mass can be written", false, refusal ?? ""); return; }
        car.Save();

        Car? read = Car.ReadFrom(mirror);
        IReadOnlyList<string> moved = read == null ? [] : before.Diff(read.Prefab);
        sb.AppendLine($"  a mass changed on \"{part.Name}\": the prefab's fields that moved — "
            + $"{(moved.Count == 0 ? "none" : string.Join("; ", moved.Take(4)))}");
        check("the edit shows up in the prefab as the one field it was made on and nothing else",
            moved.Count == 1 && moved[0].Contains("Mass", StringComparison.Ordinal),
            moved.Count == 0 ? "nothing moved at all" : string.Join("; ", moved.Take(4)));

        // …and every OTHER component is byte for byte what it was. Asked of the parts rather than of the file,
        // because the file did change — in the one place it was meant to.
        Car? again = Car.ReadFrom(mirror);
        int same = 0, differs = 0;
        if (again != null)
        {
            IReadOnlyList<CarDeformPart> had = before.CarDeformParts;
            IReadOnlyList<CarDeformPart> now = again.Prefab.CarDeformParts;
            for (int i = 0; i < Math.Min(had.Count, now.Count); i++)
            {
                if (i == part.PartIndex) continue;
                if (Same(had[i], now[i])) same++; else differs++;
            }
        }
        sb.AppendLine($"  the other parts: {same} unchanged, {differs} changed");
        check("saving after a damage edit leaves every other component as it was",
            same > 0 && differs == 0, $"{differs} changed");

        // The derived data is geometry's, not the damage model's: a component is exactly as shootable after
        // its mass changes as it was before, so nothing is rebuilt and the frame resource is not touched.
        string? rig = Rig(mirror);
        byte[] rigWas = rig == null ? [] : File.ReadAllBytes(rig);
        Car? third = Car.ReadFrom(mirror);
        CarComponent? more = third == null ? null : Pick(third, "Resistance");
        if (rig == null || third == null || more == null) return;
        if (third.SetDamage(more,
                [.. more.DamageFields.Select(f => f.Label == "Resistance" ? f with { Number = 0.9f } : f)],
                out _) != null)
        {
            third.Save();
        }
        check("a damage edit leaves the frame resource alone — nothing about it is derived from geometry",
            rigWas.AsSpan().SequenceEqual(File.ReadAllBytes(rig)),
            $"{rigWas.Length} bytes before, {new FileInfo(rig).Length} after");
    }

    /// <summary>Whether two readings of the same deform part say the same thing about everything this ticket
    /// can change — the tuning block, the centre of mass, the effect group, the flags and the handles.</summary>
    private static bool Same(CarDeformPart a, CarDeformPart b) =>
        a.Flags == b.Flags && a.EffectGroup == b.EffectGroup && a.CentreOfMass == b.CentreOfMass
        && a.Tuning == b.Tuning && a.Handles.Count == b.Handles.Count
        && a.Handles.Zip(b.Handles).All(p => p.First == p.Second);

    // ── one undo step ──

    private static void Undo(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ one intent is one undo step ════");

        Car? car = Car.ReadFrom(mirror);
        CarComponent? part = Pick(car, "Mass of this component");
        if (car?.PrefabPath == null || part == null) { check("the focus car reads", false, ""); return; }

        byte[] was = File.ReadAllBytes(car.PrefabPath);
        CarEdit? edit = car.SetDamage(part,
            [.. part.DamageFields.Select(f => f.Label switch
            {
                "Mass of this component" => f with { Number = 15.5f },
                "Effect group" => f with { Number = 9f },
                "Kill part" => f with { Number = f.Number != 0f ? 0f : 1f },
                _ => f,
            })],
            out string? refusal);
        if (edit == null) { check("three parameters can be written at once", false, refusal ?? ""); return; }
        car.Save();
        bool changed = !File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(was);

        for (int i = 0; i < 3; i++)
        {
            car.Restore(edit.Before);
            car.Save();
            car.Restore(edit.After);
            car.Save();
        }
        car.Restore(edit.Before);
        CarSave saved = car.Save();

        sb.AppendLine($"  three parameters in one intent: the prefab changed ({changed}), then (undo, redo) "
            + $"×3 → undo came back ({File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(was)})");
        check("the edit reached the file at all", changed, $"the prefab moved: {changed}");
        check("one undo puts all three back byte for byte",
            saved.Ok && File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(was), "");

        // …and the UNDO leaves the frame resource alone too, not only the edit. A snapshot that carried the
        // model's hit boxes would write them back over the live model on every restore and mark the frame graph
        // dirty, so taking back a mass would demand — and get — a rewritten frame resource, for an edit that
        // never touched geometry. On a car with no frame resource to write into it would refuse the undo
        // outright, with an error about geometry.
        check("neither the undo nor the redo asks for the frame resource to be rewritten",
            !car.FramesChanged, $"the frame graph wants a rewrite: {car.FramesChanged}");

        // The same question of a REFUSAL, which also restores the snapshot — and does it on a car the modder is
        // still holding, so a dirty flag left behind there rides along into whatever they save next.
        Car? refused = Car.ReadFrom(mirror);
        CarComponent? again = refused == null ? null : Pick(refused, "Mass of this component");
        if (refused == null || again == null) return;
        string? rig = Rig(mirror);
        byte[] rigWas = rig == null ? [] : File.ReadAllBytes(rig);
        CarEdit? denied = refused.SetDamage(again,
            [.. again.DamageFields.Select(f =>
                f.Label == "Mass of this component" ? f with { Number = float.NaN } : f)],
            out string? why);
        refused.Save();
        sb.AppendLine($"  a refused edit: {why}; the frame graph dirty afterwards: {refused.FramesChanged}");
        check("a refused edit puts the car back without marking the frame graph dirty",
            denied == null && !refused.FramesChanged,
            $"refused: {denied == null}, the frame graph wants a rewrite: {refused.FramesChanged}");
        check("…and the frame resource on disk is untouched by it",
            rig != null && rigWas.AsSpan().SequenceEqual(File.ReadAllBytes(rig)),
            rig == null ? "no frame resource" : $"{rigWas.Length} bytes, still {new FileInfo(rig).Length}");
    }

    // ── the refusals ──

    private static void Refusals(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ what cannot be done, and whether it says why ════");

        Car? car = Car.ReadFrom(mirror);
        CarComponent? part = Pick(car, "Mass of this component");
        if (car?.PrefabPath == null || part == null) { check("the focus car reads", false, ""); return; }
        byte[] was = File.ReadAllBytes(car.PrefabPath);

        CarEdit? edit = car.SetDamage(part,
            [.. part.DamageFields.Select(f =>
                f.Label == "Mass of this component" ? f with { Number = float.NaN } : f)],
            out string? refusal);
        sb.AppendLine($"  a mass that is not a number: {refusal}");
        check("a value that is not a number refuses with the reason",
            edit == null && refusal is { Length: > 5 }, refusal ?? "it was allowed");

        CarComponent? bare = car.Components.FirstOrDefault(c => c.IsBare);
        if (bare != null)
        {
            CarEdit? onBare = car.SetDamage(bare, part.DamageFields, out refusal);
            sb.AppendLine($"  damage on \"{bare.Name}\", which has no deform part: {refusal}");
            check("a bare component refuses damage parameters with the reason",
                onBare == null && refusal is { Length: > 20 }, refusal ?? "it was allowed");
        }

        CarComponent? crumples = car.Components.FirstOrDefault(c => c.Crumples);
        if (crumples != null)
        {
            CarEdit? handle = car.SetHandle(crumples, crumples.Handles[0],
                [.. crumples.Handles[0].Fields.Select(f =>
                    f.Label == "Radius" ? f with { Number = float.PositiveInfinity } : f)],
                out refusal);
            sb.AppendLine($"  a crumple radius that is not finite: {refusal}");
            check("a handle value that is not finite refuses with the reason",
                handle == null && refusal is { Length: > 5 }, refusal ?? "it was allowed");
        }

        check("and not one of those refusals wrote anything",
            File.ReadAllBytes(car.PrefabPath).AsSpan().SequenceEqual(was), "");
    }

    // ── plumbing ──

    /// <summary>
    /// Edits one damage parameter, saves, reads the ARCHIVE again and asks the question of what came back.
    ///
    /// <para>
    /// The component is found again by IDENTITY across the re-read, which is what the identity is for: it
    /// survives a re-stitch, so the question is asked of the same component and not of whichever one happens
    /// to be first in the list.
    /// </para>
    /// </summary>
    private static void Damage(
        StringBuilder sb, string mirror, Action<string, bool, string> check, string label,
        Func<CarField, CarField> change, Func<CarField, bool> want)
    {
        Car? car = Car.ReadFrom(mirror);
        CarComponent? part = Pick(car, label);
        if (car == null || part == null) { check($"the focus car has a part with \"{label}\"", false, ""); return; }

        CarEdit? edit = car.SetDamage(part,
            [.. part.DamageFields.Select(f => f.Label == label ? change(f) : f)], out string? refusal);
        if (edit == null) { check($"{label} can be written", false, refusal ?? ""); return; }
        CarSave saved = car.Save();
        if (!saved.Ok) { check($"{label} saves", false, string.Join("; ", saved.Lost)); return; }

        // Found again by the same criterion rather than by identity: an identity is minted per read and carried
        // only across a re-STITCH, so a fresh read of the file has its own numbering.
        Car? read = Car.ReadFrom(mirror);
        CarField? field = Pick(read, label)?.DamageFields.FirstOrDefault(f => f.Label == label);
        sb.AppendLine($"  {label} on \"{part.Name}\" ({part.Kind}): {field?.Text ?? "gone"}");
        check($"\"{label}\" comes back from the archive as it was typed", field != null && want(field), "");
    }

    /// <summary>The same, for one of a component's deform handles.</summary>
    private static void Handle(
        StringBuilder sb, string mirror, Action<string, bool, string> check, string label,
        Func<CarField, CarField> change, Func<CarField, bool> want)
    {
        Car? car = Car.ReadFrom(mirror);
        CarComponent? part = car?.Components.FirstOrDefault(c => c.Crumples);
        if (car == null || part == null) { check("the focus car has a component that crumples", false, ""); return; }

        CarHandle handle = part.Handles[0];
        CarEdit? edit = car.SetHandle(part, handle,
            [.. handle.Fields.Select(f => f.Label == label ? change(f) : f)], out string? refusal);
        if (edit == null) { check($"a handle's {label} can be written", false, refusal ?? ""); return; }
        CarSave saved = car.Save();
        if (!saved.Ok) { check($"a handle's {label} saves", false, string.Join("; ", saved.Lost)); return; }

        // Found again the way it was found in the first place, not by identity: an identity is minted per read
        // and carried only across a re-STITCH, so a fresh read of the file has its own numbering.
        Car? read = Car.ReadFrom(mirror);
        CarComponent? back = read?.Components.FirstOrDefault(c => c.Crumples);
        CarField? field = back?.Handles.ElementAtOrDefault(0)?.Fields
            .FirstOrDefault(f => f.Label == label);
        sb.AppendLine($"  {label} of \"{handle.Name}\" on \"{part.Name}\": {field?.Text ?? "gone"}");
        check($"a handle's \"{label}\" comes back from the archive as it was typed",
            field != null && want(field), "");
    }

    /// <summary>The first component whose damage fields carry the given label — which for the six tuning
    /// numbers means the first part the file carries a tuning block for.</summary>
    private static CarComponent? Pick(Car? car, string label) =>
        car?.Components.FirstOrDefault(c => c.DamageFields.Any(f => f.Label == label));

    /// <summary>The first component that actually SETS the named flag, or null when this car sets it nowhere —
    /// which is every car for two of the five, since AI box and fade off ship on nothing.</summary>
    private static CarComponent? Carrying(Car? car, string label) =>
        car?.Components.FirstOrDefault(
            c => c.DamageFields.Any(f => f.Label == label && f.Number != 0f));

    /// <summary>The component of one deform part, by that part's place in the prefab's list — what a fresh read
    /// of the same file reproduces, where an identity does not.</summary>
    private static CarComponent? At(Car? car, int partIndex) =>
        car?.Components.FirstOrDefault(c => c.PartIndex == partIndex);

    /// <summary>Which bit a named flag lives in — the reference toolkit's own annotation, restated here so the
    /// probe measures the aggregate against the reading rather than against itself.</summary>
    private static int Bit(string label) => label switch
    {
        "Always dynamic" => 1, "Kill part" => 4, "Snow" => 10, "AI box" => 13, _ => 18,
    };

    /// <summary>The archive's frame resource file, or null when it carries none.</summary>
    private static string? Rig(string extracted)
    {
        try { return Formats.Archive.SdsManifest.Load(extracted).GetFiles("FrameResource").FirstOrDefault(); }
        catch (Exception ex) when (ex is IOException or Formats.SdsFormatException) { return null; }
    }
}
