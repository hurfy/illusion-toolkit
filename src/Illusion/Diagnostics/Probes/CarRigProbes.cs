using System.Globalization;
using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The two intents that reach past the assembly layer into the RIG: adding a component whose bone would have
/// to be minted, and removing one outright, which means taking its bone away. Both are offered and both
/// refuse today, and what this measures is that the refusal is honest — that it says what a modder can do
/// instead, that it states the ceilings against this car's own numbers, and above all that it wrote NOTHING.
///
/// <para>
/// The last of those is the reason this probe exists at all. A refusal that leaves half an edit behind is
/// worse than no feature: the modder is told the change did not happen and Build writes it anyway. So the
/// working copy is hashed file by file before and after, and every refused operation has to leave every byte
/// of it where it was.
/// </para>
/// <para>
/// The car's working copy is mirrored into the temp directory first, so the game's folders are never written
/// to. Output: %TEMP%\illusion_car_rig.txt
/// </para>
/// </summary>
internal static class CarRigProbes
{
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "illusion_car_rig");

    /// <summary>A name no car ships and nothing could resolve — the bone that would have to be minted.</summary>
    private const string Unmade = "illusion_probe_bone_no_car_has";

    internal static void RunCarRigProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_rig.txt");
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
            Seam(sb, Check);

            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            if (Mirror(focus, folder, sb, Check) is { } mirror) Refusals(sb, mirror, Check);
            // Its own copy, because it grants a part and saves it — the mirror above is the one every byte of
            // which has to be untouched at the end.
            if (Mirror(focus, folder, sb, Check, "-allowed") is { } second) OneDoor(sb, second, Check);
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            sb.Insert(0, $"MINTING A BONE REFUSES WITH THE REASON ({focus}): {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // ── the seam itself ──

    /// <summary>
    /// The conditions the refusals hang off, and what the refusals say. Asserted rather than described,
    /// because both are promises to a modder: that the ceilings are stated where they are needed, and that
    /// the way out — Blender — is named rather than implied.
    /// </summary>
    private static void Seam(StringBuilder sb, Action<string, bool, string> check)
    {
        sb.AppendLine("════ the condition the rig writer flips ════");
        sb.AppendLine($"    CanMintBone {CarRig.CanMintBone}, CanRemoveBone {CarRig.CanRemoveBone}, "
            + $"ceilings {CarRig.BoneCeiling} bones / {CarRig.WidestShippedPool} per pool");

        check("minting a bone is one condition, and it is off", !CarRig.CanMintBone,
            $"CanMintBone {CarRig.CanMintBone}");
        // Deliberately its own condition. Adding a bone APPENDS and renumbers nothing; removing one renumbers
        // every vertex weight in the model and is not in the rig plan at all — so a rig writer landing must
        // not switch component removal on as a side effect of switching minting on.
        check("removing one is a condition of its own, and it is off too", !CarRig.CanRemoveBone,
            $"CanRemoveBone {CarRig.CanRemoveBone}");

        string mint = CarRig.WhyNoBone("someBone", 73, 52);
        check("the mint refusal names the bone that was asked for",
            mint.Contains("someBone", StringComparison.Ordinal), Head(mint));
        check("…and says where to make it instead",
            mint.Contains("Blender", StringComparison.Ordinal), Head(mint));
        check("…and states both ceilings, against this car's own numbers",
            mint.Contains("255", StringComparison.Ordinal) && mint.Contains("60", StringComparison.Ordinal)
            && mint.Contains("73", StringComparison.Ordinal) && mint.Contains("52", StringComparison.Ordinal),
            Head(mint));

        string gone = CarRig.WhyNoRemoval("doorFL", hasPart: true);
        check("the removal refusal says what removing a bone would renumber",
            gone.Contains("vertex weight", StringComparison.Ordinal), Head(gone));
        check("…and names the demotion, which is what a modder asking for this usually wants",
            gone.Contains("Remove deform part", StringComparison.Ordinal), Head(gone));
        sb.AppendLine();
    }

    // ── the refusals, against the bytes of a real car ──

    /// <summary>
    /// Every refused operation run on one mirrored car, with every file of the working copy hashed before and
    /// after. The archive has to come out byte for byte as it went in.
    /// </summary>
    private static void Refusals(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("════ what is refused, and what it wrote ════");

        Car? car = Car.ReadFrom(mirror);
        if (car?.Body is not { } body) { check("the mirrored car reads", false, mirror); return; }
        Dictionary<string, byte[]> was = Files(mirror);
        byte[] prefab = car.Prefab.ToBytes();
        sb.AppendLine($"    {was.Count} files in the working copy, {car.Bones.Count} bones, "
            + $"{car.Components.Count} components");

        void Refused(string what, CarEdit? edit, string? why, params string[] words)
        {
            bool said = !string.IsNullOrWhiteSpace(why)
                && Array.TrueForAll(words, w => why!.Contains(w, StringComparison.Ordinal));
            check(what, edit == null && said, why ?? "it was allowed");
        }

        // ── a bone that would have to be minted ──
        check($"\"{Unmade}\" really is no bone of this car",
            !car.Bones.ContainsKey(Fnv64.Hash(Unmade)), Unmade);
        Refused("a component whose bone does not exist is refused, with the way to make it",
            car.AddComponent(Unmade, Car.DefaultPartKind, body, out string? why), why,
            Unmade, "Blender", "255");
        // The ceilings are stated against THIS car, not as two constants recited: the count in the message has
        // to be the rig's own, which a modder can compare with what they see in Blender.
        (int bones, int pool) = Ceilings(car);
        sb.AppendLine($"    the rig holds {bones} bones, its widest remap pool {pool}");
        check("the ceilings in the refusal are this car's own numbers",
            why != null && bones > 0
            && why.Contains(bones.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && why.Contains(pool.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            $"{bones} bones, {pool} per pool");

        Refused("a component with no bone named at all is refused",
            car.AddComponent("   ", Car.DefaultPartKind, body, out string? blank), blank, "bone");

        // ── the two bones that ARE in the rig and are not components ──
        //
        // Two different accidents needing two different answers. The rig roots and the hinge bones of tracked
        // vehicles carry no geometry (101 over the corpus) and are answered by weighting some to them; a bone
        // a part crumples AROUND is a list on that part, claimed whatever it draws, and telling a modder to
        // weight geometry to that one would be advice that changes nothing.
        var handles = new HashSet<ulong>();
        foreach (CarDeformPart claiming in car.Prefab.CarDeformParts)
        {
            foreach (CarDeformHandle handle in claiming.Handles) handles.Add(handle.JointName);
        }
        string? dark = car.Bones
            .Where(b => car.ComponentOfBone(b.Key) == null && !handles.Contains(b.Key))
            .Select(b => b.Value)
            .FirstOrDefault();
        string? crumple = car.Bones.Where(b => handles.Contains(b.Key)).Select(b => b.Value).FirstOrDefault();
        if (dark != null)
        {
            Refused("a bone nothing is weighted to is refused, and told what would make it a component",
                car.AddComponent(dark, Car.DefaultPartKind, body, out string? darkWhy), darkWhy,
                dark, "weighted", "Blender");
        }
        if (crumple != null)
        {
            Refused("a bone a part crumples around is refused with the part that holds it",
                car.AddComponent(crumple, Car.DefaultPartKind, body, out string? crumpleWhy), crumpleWhy,
                crumple, "deform handle");
        }
        sb.AppendLine($"    the bone nothing is weighted to: {dark ?? "none on this car"}; "
            + $"the bone some part crumples around: {crumple ?? "none on this car"}");

        // ── and removing a component outright ──
        CarComponent? part = car.Components.FirstOrDefault(c => !c.IsBare && !ReferenceEquals(c, body));
        CarComponent? bare = car.Components.FirstOrDefault(c => c.IsBare && c.HasGeometry);
        if (part != null)
        {
            Refused("removing a component outright is refused, with the demotion offered instead",
                car.RemoveComponent(part, out string? partWhy), partWhy,
                part.Name, "vertex weight", "Remove deform part");
        }
        if (bare != null)
        {
            // A bare component has no part to take away either, so the sentence that names the demotion would
            // be a promise the menu does not keep — it says so instead.
            Refused("a bare component is refused with the truth that it has no part to take away either",
                car.RemoveComponent(bare, out string? bareWhy), bareWhy, bare.Name, "Blender");
        }
        Refused("so is the body", car.RemoveComponent(body, out string? bodyWhy), bodyWhy, body.Name);

        // ── and not one of them wrote anything ──
        check("no refusal marked the frame graph for a rewrite", !car.FramesChanged,
            car.FramesChanged ? "the frame graph was marked dirty" : "the frame graph was left alone");
        check("no refusal changed the prefab the aggregate holds",
            car.Prefab.ToBytes().AsSpan().SequenceEqual(prefab), "");
        // The whole point of the ticket: the ARCHIVE, file by file. Nothing here calls Save, and this is what
        // says so — a refusal that had written half an edit would show up as one changed file.
        check("every file of the working copy is byte for byte what it was", Same(was, Files(mirror), sb),
            $"{was.Count} files");
        sb.AppendLine();
    }

    // ── the same door, when the bone is already there ──

    /// <summary>
    /// The other half of the operation: adding a component to a bone the rig already holds goes through the
    /// SAME call and works. Which is what says the refusal is about the bone alone — not about the intent, not
    /// about the interface, and not about anything a modder would have to do differently when the writer
    /// lands.
    /// </summary>
    private static void OneDoor(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("════ the same call, on a bone that is already there ════");

        Car? car = Car.ReadFrom(mirror);
        if (car?.Body is not { } body) { check("the mirrored car reads", false, mirror); return; }
        CarComponent? bare = car.Components.FirstOrDefault(c => c.IsBare && c.HasGeometry && c.BoneResolves);
        if (bare == null) { check("the focus car has a bare component", false, ""); return; }

        string name = bare.Name;
        sb.AppendLine($"    adding \"{name}\" as a {Car.DefaultPartKind.Name} of \"{body.Name}\"");
        CarEdit? edit = car.AddComponent(name, Car.DefaultPartKind, body, out string? refusal);
        check("adding a component whose bone is already in the rig is allowed", edit != null, refusal ?? "");
        if (edit == null) return;

        CarSave saved = car.Save();
        check("it saves", saved.Ok, string.Join("; ", saved.Lost));
        if (!saved.Ok) return;

        Car? again = Car.ReadFrom(mirror);
        CarComponent? grown = again?.ComponentOfBone(bare.BoneHash);
        check("and the archive holds it as a component with a deform part of its own",
            grown is { IsBare: false } && grown.BoneHash == bare.BoneHash,
            grown == null ? "it is not in the car at all" : grown.Kind);
        sb.AppendLine();
    }

    // ── helpers ──

    /// <summary>
    /// What this car's rig holds: how many bones, and how many its widest remap pool holds.
    ///
    /// <para>
    /// Read here rather than asked of the aggregate, and deliberately a copy of what
    /// <c>Car.RigCeilings</c> does — a probe that asked the layer under test would be asserting its own
    /// reading back at itself, which is the same reason <c>CarPartProbes</c> spells the part kinds out
    /// instead of reaching for the format layer's table. What is being checked is that the numbers in the
    /// refusal came off the MODEL and are not two constants recited.
    /// </para>
    /// </summary>
    private static (int Bones, int Pool) Ceilings(Car car)
    {
        FrameObjectModel? model =
            car.Frames?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
        if (model == null) return (0, 0);

        int bones = 0, pool = 0;
        try { bones = model.GetSkeletonObject().BoneNames?.Length ?? 0; }
        catch (Exception) { /* no skeleton: the probe reports zero, the same as the aggregate does */ }
        try
        {
            foreach (var level in model.GetBlendInfoObject().BoneIndexInfos ?? [])
            {
                foreach (byte size in level.BonesPerRemapPool ?? []) pool = Math.Max(pool, size);
            }
        }
        catch (Exception) { /* likewise */ }
        return (bones, pool);
    }

    /// <summary>Every file of a working copy, by name, with the bytes it holds.</summary>
    private static Dictionary<string, byte[]> Files(string folder)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(folder))
        {
            files[Path.GetFileName(file)] = File.ReadAllBytes(file);
        }
        return files;
    }

    /// <summary>Whether two readings of a working copy hold the same files with the same bytes — and, when
    /// they do not, which ones moved, since "something changed" is not a diagnosis.</summary>
    private static bool Same(Dictionary<string, byte[]> was, Dictionary<string, byte[]> now, StringBuilder sb)
    {
        var moved = new List<string>();
        foreach ((string name, byte[] bytes) in was)
        {
            if (!now.TryGetValue(name, out byte[]? after)) { moved.Add(name + " (gone)"); continue; }
            if (!bytes.AsSpan().SequenceEqual(after))
            {
                moved.Add($"{name} ({bytes.Length} B → {after.Length} B)");
            }
        }
        foreach (string name in now.Keys)
        {
            if (!was.ContainsKey(name)) moved.Add(name + " (new)");
        }
        if (moved.Count > 0) sb.AppendLine("    changed: " + string.Join(", ", moved));
        return moved.Count == 0;
    }

    /// <summary>The first line of a refusal, for the report — the whole of one runs to a paragraph.</summary>
    private static string Head(string message)
    {
        string first = message.Split('\n')[0];
        return first.Length <= 120 ? first : first[..117] + "…";
    }

    private static string? Mirror(
        string focus, string folder, StringBuilder sb, Action<string, bool, string> check, string into = "")
    {
        string source = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, focus + ".sds")));
        if (!File.Exists(Path.Combine(source, "SDSContent.xml")))
        {
            check("the focus car is extracted", false, focus);
            return null;
        }

        string mirror = Path.Combine(Scratch, focus + into);
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

        sb.AppendLine($"\n════ the focus car, mirrored into {mirror} ════");
        return mirror;
    }
}
