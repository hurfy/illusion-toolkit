using System.Globalization;
using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Assets.Prefabs;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The carry-verbatim guarantee, measured: every shipped car read through the <c>Car</c> aggregate, saved
/// again with no edit, and compared BYTE FOR BYTE with the file it came from.
///
/// <para>
/// This is the guarantee everything above the seam rests on. A large part of the deform-part struct is
/// undecoded — <c>unk2</c>, <c>unk4</c>, <c>unk5</c>, <c>unk6</c>, <c>unk14</c>, <c>unk18</c>, <c>unk20</c>,
/// <c>unk21_data</c>, <c>unk22_rel_data</c>, <c>unk23</c>, <c>unk24</c> and seven more inside its common
/// block — and every one of those has to reach the game exactly as it arrived. Anything that fails here is a
/// field the aggregate is rebuilding instead of carrying.
/// </para>
/// <para>
/// A failure names the FIELD, not a byte offset: the write is read back and compared through the prefab's
/// own field-level diff, so a dropped <c>unk14</c> reads as
/// <c>prefab[0].car.Deformation[0].DeformParts[7].Unk14: count 3 vs 0</c>.
/// </para>
/// <para>
/// NOTHING IS WRITTEN INTO THE GAME'S FOLDERS. Every save is redirected into a scratch mirror under the temp
/// directory, which is also what makes the comparison possible — the original is still there to compare with.
/// Output: %TEMP%\illusion_car_roundtrip.txt
/// </para>
/// </summary>
internal static class CarRoundTripProbes
{
    /// <summary>Where the redirected saves land. Cleared at the start of every run.</summary>
    private static readonly string Scratch =
        Path.Combine(Path.GetTempPath(), "illusion_car_roundtrip");

    internal static void RunCarRoundTripProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_roundtrip.txt");
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
            Clear();
            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");

            Corpus(sb, folder, Check);
            Edited(sb, folder, focus, Check);
            Frames(sb, folder, focus, Check);
            sb.Insert(0, $"CAR ROUND TRIP PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "CAR ROUND TRIP PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // ── every shipped car, out through the aggregate and back ──

    private static void Corpus(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("════ the carry-verbatim round trip over every shipped car ════");

        int cars = 0, identical = 0, savesRefused = 0, wroteOnce = 0, secondSaveQuiet = 0, agreesWithOld = 0;
        int parts = 0, volumes = 0, handles = 0;
        int rigs = 0, rigsIdentical = 0;
        long bytes = 0;
        var damage = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            Car? car;
            try { car = Car.ReadFrom(extracted); }
            catch (Exception) { continue; }
            if (car?.PrefabPath == null) continue;
            if (car.Prefab.Car is not { DeformPartCount: > 0 }) continue;
            cars++;

            string name = Path.GetFileNameWithoutExtension(sds.Name);
            Func<string, string> redirect = Redirect(extracted, name);

            // The save the whole ticket is about: no edit was made, so what lands in the mirror must be the
            // bytes that were read.
            CarSave saved = car.Save(redirect);
            if (!saved.Ok)
            {
                savesRefused++;
                damage.Add($"{name}: REFUSED — {string.Join("; ", saved.Lost.Take(4))}");
                continue;
            }
            if (saved.Written.Count == 1) wroteOnce++;

            byte[] original = File.ReadAllBytes(car.PrefabPath);
            byte[] written = saved.Written.Count == 1 ? File.ReadAllBytes(saved.Written[0]) : [];
            bytes += original.Length;
            if (original.AsSpan().SequenceEqual(written)) identical++;
            else damage.Add($"{name}: {Diagnose(car, written, original)}");

            // Saving again over a mirror that now holds exactly those bytes must write nothing at all. This is
            // "everything I did not touch is left as it was" in its strongest form: not rewritten identically,
            // not rewritten.
            CarSave again = car.Save(redirect);
            if (again.Written.Count == 0 && again.Unchanged.Count == 1) secondSaveQuiet++;

            // The path this ticket adds runs BESIDE the four that write today; it removes none. The cheapest
            // way to see that is that the old reader and the new aggregate serialize the same file the same.
            try
            {
                PrefabFile? old = PrefabEditing.OpenFirst(extracted);
                if (old != null && old.ToBytes().AsSpan().SequenceEqual(original)) agreesWithOld++;
            }
            catch (Exception) { /* counted as disagreeing */ }

            // What the round trip actually carried, so "byte-identical" is a statement about a car rather
            // than about a file that might have held nothing.
            foreach (CarDeformPart part in car.Prefab.CarDeformParts)
            {
                parts++;
                volumes += part.Volumes.Count;
                handles += part.Handles.Count;
            }

            // And the other half of the archive the aggregate holds. It is written only on request, so this
            // is the one place the corpus is asked whether re-serializing it reproduces it — the tickets that
            // edit a frame need to know, and this car is spent afterwards either way.
            if (car.Frames == null) continue;
            rigs++;
            string? rig = Rig(extracted);
            if (rig == null) continue;
            car.TouchFrames();
            CarSave saved2 = car.Save(redirect);
            string? at = saved2.Written.Concat(saved2.Unchanged).FirstOrDefault(IsRig);
            if (at != null && File.ReadAllBytes(rig).AsSpan().SequenceEqual(File.ReadAllBytes(at)))
            {
                rigsIdentical++;
            }
        }

        sb.AppendLine($"  cars read through the aggregate: {cars}");
        sb.AppendLine($"  carried through: {parts} deform parts, {volumes} collision volumes, "
            + $"{handles} deform handles, "
            + $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} prefab bytes");
        sb.AppendLine($"  saved into a scratch mirror and compared with the original:");
        sb.AppendLine($"    byte-identical      {identical}");
        sb.AppendLine($"    refused (a field did not survive)  {savesRefused}");
        sb.AppendLine($"    the save wrote exactly one file    {wroteOnce}");
        sb.AppendLine($"    saving again wrote nothing         {secondSaveQuiet}");
        sb.AppendLine($"    the old read path agrees on the bytes  {agreesWithOld}");
        sb.AppendLine($"    the frame graph re-serialized unedited is identical too  "
            + $"{rigsIdentical} of {rigs}");
        foreach (string line in damage.Take(20)) sb.AppendLine("      " + line);
        if (damage.Count > 20) sb.AppendLine($"      …and {damage.Count - 20} more, not printed");

        check("the corpus is the 85 cars the census measured", cars == 85, $"{cars} cars");
        check("every shipped car read and saved with no edit comes back byte-identical",
            cars > 0 && identical == cars,
            $"{identical} of {cars} identical, {savesRefused} refused");
        check("no field failed to survive being written and read back",
            savesRefused == 0, $"{savesRefused} cars refused their own save");
        check("a save with nothing to write leaves the file alone rather than rewriting it",
            cars > 0 && secondSaveQuiet == cars, $"{secondSaveQuiet} of {cars}");
        check("the path this ticket adds writes what the path beside it writes",
            cars > 0 && agreesWithOld == cars, $"{agreesWithOld} of {cars}");
        check("the other half of the car — its frame graph — comes back byte-identical as well",
            rigs > 0 && rigsIdentical == rigs, $"{rigsIdentical} of {rigs}");
    }

    /// <summary>The archive's frame resource file, or null when it carries none.</summary>
    private static string? Rig(string extracted)
    {
        try { return SdsManifest.Load(extracted).GetFiles("FrameResource").FirstOrDefault(); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return null; }
    }

    private static bool IsRig(string path) =>
        Path.GetFileName(path).StartsWith("FrameResource", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the write lost, named as fields — the byte offset is context, not the answer.</summary>
    private static string Diagnose(Car car, byte[] written, byte[] original)
    {
        long at = FirstDiff(original, written);
        string where = $"{original.Length} bytes vs {written.Length}, first differing at {at}";
        if (written.Length == 0) return where;

        try
        {
            using var buffer = new MemoryStream(written, writable: false);
            IReadOnlyList<string> fields = car.Prefab.Diff(PrefabFile.Read(buffer));
            return fields.Count == 0
                ? $"{where}, yet no field differs — the difference is in the encoding, not the model"
                : $"{where}; {string.Join("; ", fields.Take(6))}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return $"{where}; what was written cannot be read back — {ex.Message}";
        }
    }

    // ── a save that DID change something: exactly one field moves, and it is named ──

    private static void Edited(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ a save that DID change something ════");

        string extracted = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, focus + ".sds")));
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
        {
            check("the focus car is extracted", false, focus);
            return;
        }
        Car? read = Car.ReadFrom(extracted);
        if (read?.PrefabPath == null || read.Frames == null)
        {
            check("the focus car has a prefab and a frame graph", false, focus);
            return;
        }
        string path = read.PrefabPath;
        FrameResource frames = read.Frames;
        Func<string, string> redirect = Redirect(extracted, focus + "_edited");

        // ── one number changed on one part ──
        //
        // Every other field of that part — including the eleven the toolkit never reads — is carried through
        // the same write. If the aggregate were rebuilding the part rather than patching it, this is where
        // they would go missing, and the diff would name them.
        CarComponent? part = read.Components.FirstOrDefault(c => !c.IsBare && c.Damage != null);
        if (part?.Damage == null) { check("the focus car has a deform part to edit", false, focus); return; }

        Car car = Reopen(path, frames, extracted);
        byte was = part.Damage.EffectGroup;
        byte now = (byte)(was == 7 ? 8 : 7);
        bool set = car.Prefab.SetCarValue(CarValueSlot.DeformPartEffectGroup, part.PartIndex, 0, now);
        CarSave saved = car.Save(redirect);

        IReadOnlyList<string> moved = Compare(path, saved);
        sb.AppendLine($"  {focus}: part {part.PartIndex} (\"{part.Name}\", {part.Kind}) effect group "
            + $"{was} → {now}");
        foreach (string field in moved.Take(8)) sb.AppendLine("    " + field);
        check("an edit reaches the file, and the fields around it do not move",
            set && saved.Ok && saved.Written.Count == 1 && moved.Count == 1
            && moved[0].Contains($"DeformParts[{part.PartIndex}].Unk19", StringComparison.Ordinal)
            && moved[0].EndsWith($": {was} vs {now}", StringComparison.Ordinal),
            moved.Count == 0 ? "nothing changed in the file at all" : string.Join("; ", moved.Take(3)));

        // ── a row dropped ──
        //
        // The other shape a failure takes: not a value that moved but a row that is no longer there. It has to
        // read as a named field with a count, because "the file got 8 bytes shorter" is not something anybody
        // can act on.
        (CarItemKind kind, string label) = Droppable(read.Prefab);
        if (label == "")
        {
            check("the focus car carries a row that can be dropped", false, focus);
            return;
        }
        Car dropping = Reopen(path, frames, extracted);
        int before = dropping.Prefab.CarItemCount(kind);
        byte[]? taken = dropping.Prefab.TakeCarItem(kind, before - 1);
        CarSave dropped = dropping.Save(Redirect(extracted, focus + "_dropped"));

        IReadOnlyList<string> gone = Compare(path, dropped);
        sb.AppendLine($"  {focus}: dropped the last of {before} {label} rows");
        foreach (string field in gone.Take(8)) sb.AppendLine("    " + field);
        check("a row that is no longer there is named as a field, not as a byte offset",
            taken != null && dropped.Ok && gone.Count == 1
            && gone[0].Contains($"count {before} vs {before - 1}", StringComparison.Ordinal),
            gone.Count == 0 ? "the drop did not reach the file" : string.Join("; ", gone.Take(3)));

        // ── and a save that cannot happen at all ──
        //
        // A car stitched in memory is what a bridge push produces. It has no working copy behind it, and the
        // one thing it must not do is report a quiet success — the modder would go on editing a car that is
        // not being saved.
        Car homeless = Car.Stitch(PrefabFile.Load(path), frames);
        CarSave refused = homeless.Save();
        sb.AppendLine($"  a car stitched in memory: {string.Join("; ", refused.Lost)}");
        check("a car with nowhere to write refuses and says why, rather than reporting success",
            !refused.Ok && refused.Written.Count == 0 && refused.Lost.Count == 1,
            refused.Ok ? "it reported success" : string.Join("; ", refused.Lost));
    }

    /// <summary>Which of the plain frame-hash lists this car has one of to drop. Deliberately one of the
    /// lists that is a bare hash: dropping a seat renumbers the ones after it, which is a second change.</summary>
    private static (CarItemKind Kind, string Label) Droppable(PrefabFile prefab)
    {
        if (prefab.CarItemCount(CarItemKind.Wiper) > 0) return (CarItemKind.Wiper, "wiper");
        if (prefab.CarItemCount(CarItemKind.FuelTank) > 0) return (CarItemKind.FuelTank, "fuel tank");
        if (prefab.CarItemCount(CarItemKind.Exhaust) > 0) return (CarItemKind.Exhaust, "exhaust");
        return (CarItemKind.Wiper, "");
    }

    /// <summary>The fields the written file holds differently from the one on disk — read back through the
    /// same field-level diff the aggregate refuses a save with.</summary>
    private static IReadOnlyList<string> Compare(string original, CarSave saved)
    {
        if (saved.Written.Count != 1) return [];
        try
        {
            using var buffer = new MemoryStream(File.ReadAllBytes(saved.Written[0]), writable: false);
            return PrefabFile.Load(original).Diff(PrefabFile.Read(buffer));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return [ex.Message];
        }
    }

    // ── the frame graph: written only when the car says it changed ──

    private static void Frames(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ the frame graph is written only when something changed it ════");

        string extracted = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, focus + ".sds")));
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) return;

        Car? car = Car.ReadFrom(extracted);
        if (car?.Frames == null) { check("the focus car has a frame graph", false, focus); return; }

        if (Rig(extracted) == null) { check("the focus car has a frame resource", false, focus); return; }

        // The frame resource is the heaviest thing in the archive and the aggregate holds it open on every
        // car it reads. Writing one nobody edited is not a harmless no-op: serializing it runs
        // UpdateFrameData over the live resource, renumbering indices and pruning unreferenced blocks.
        Func<string, string> redirect = Redirect(extracted, focus + "_frames");
        CarSave quiet = car.Save(redirect);
        bool untouched = !quiet.Written.Concat(quiet.Unchanged).Any(IsRig);
        sb.AppendLine("  a save nobody asked for the frame graph in wrote: "
            + $"{(quiet.Written.Count == 0 ? "nothing" : string.Join(", ", quiet.Written.Select(Path.GetFileName)))}");
        check("a car nobody edited does not have its frame resource rewritten", untouched && quiet.Ok,
            untouched ? "" : "the frame resource was written anyway");

        // This spends the car — serializing mutates the live resource — so nothing below reuses it.
        car.TouchFrames();
        CarSave frames = car.Save(redirect);
        string? at = frames.Written.Concat(frames.Unchanged).FirstOrDefault(IsRig);
        sb.AppendLine($"  after TouchFrames: {string.Join(", ", frames.Written.Select(Path.GetFileName))}");
        check("a car that says its frame graph changed has it written", at != null && frames.Ok,
            at == null ? "no frame resource was written" : Path.GetFileName(at));
    }

    // ── the scratch mirror ──

    /// <summary>Reopens the car's prefab from disk against a frame graph that is already open — a fresh,
    /// unedited aggregate without paying for the scene again.</summary>
    private static Car Reopen(string prefabPath, FrameResource frames, string extracted) =>
        Car.Stitch(PrefabFile.Load(prefabPath), frames, lod: 0, previous: null, prefabPath, extracted);

    /// <summary>Sends a save into this run's mirror of one car, keeping the layout inside the extracted
    /// folder so a written file is recognisable by its name.</summary>
    private static Func<string, string> Redirect(string extracted, string name) =>
        path => Path.Combine(Scratch, name, Path.GetRelativePath(extracted, path));

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
