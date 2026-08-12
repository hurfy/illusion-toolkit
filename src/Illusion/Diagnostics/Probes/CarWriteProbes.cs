using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Domain.Properties;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// ONE WRITE PATH: no module outside the <c>Car</c> aggregate writes a car's prefab, one of its ItemDesc
/// records, or its frame resource.
///
/// <para>
/// This is the whole point of the seam, and it is the one property of it that decays in silence. A second
/// path added anywhere writes bytes that look exactly like the aggregate's, nothing fails, and the two only
/// disagree later — when one of them rewrites over the other's edit, or writes a prefab volume without the
/// ItemDesc record it names. Four modules wrote independently before ticket 13 closed the seam, and they are
/// named here as the regression to watch: <c>prefab-editing</c>, <c>climb-boxes</c>,
/// <c>car-physics-volumes</c> and <c>car-collision-builder</c>.
/// </para>
/// <para>
/// It is measured two ways, because neither is enough on its own.
/// </para>
/// <para>
/// <b>By running the surfaces.</b> The aggregate counts the files it puts bytes into
/// (<c>Car.Puts</c>/<c>Car.Deletes</c>). The probe takes a byte census of every PREFAB, ItemDesc,
/// FrameResource and FrameNameTable of a mirrored car, drives each surface that can still touch one, and
/// compares what CHANGED against what the aggregate says it wrote. A file that moved while the counter stood
/// still is a second writer, whatever it wrote.
/// </para>
/// <para>
/// <b>By reading the source.</b> A behavioural check can only run the surfaces somebody remembered to add to
/// it. So the two projects' sources are swept for the calls that put bytes on disk, and every file holding
/// one has to be on a list that says what it writes — a new writer anywhere fails until it is put there on
/// purpose. The four named modules must hold none at all.
/// </para>
/// <para>
/// NOTHING IS WRITTEN INTO THE GAME'S FOLDERS: the focus car's working copy is mirrored into the temp
/// directory and every surface is driven against the mirror.
/// Output: %TEMP%\illusion_car_writes.txt
/// </para>
/// </summary>
internal static class CarWriteProbes
{
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "illusion_car_writes");

    /// <summary>
    /// Every file in the two projects that puts bytes on disk, and what it writes.
    ///
    /// <para>
    /// The list is the contract. Anything not on it that writes is a failure by construction, which is what
    /// makes this a guard rather than a spot check — the failure mode is a NEW path, and a new path is
    /// exactly what a hand-written list of known ones catches.
    /// </para>
    /// </summary>
    private static readonly (string File, string Writes)[] Writers =
    [
        ("Illusion.Assets/AtomicFile.cs", "the temp-then-move primitive every other writer goes through"),
        ("Illusion.Assets/Cars/Car.Writing.cs",
            "A CAR: its prefab, its ItemDesc records and its frame resource — the one path"),
        ("Illusion.Assets/Sds/SdsWriter.cs",
            "the frame resource and name table of an archive that is NOT a car, and the .sds pack"),
        ("Illusion.Assets/Sds/SdsActorsSaver.cs", "an .act placement table"),
        ("Illusion.Assets/Sds/SdsCollisionSaver.cs", "a district's .col"),
        ("Illusion.Assets/Sds/SdsTranslokatorSaver.cs", "a district's Translokator table"),
        ("Illusion.Assets/Sds/ArchiveEditing.cs", "resources copied into or out of an archive"),
        ("Illusion.Assets/Sds/ResourceUnpacker.cs", "the extracted working copy of an archive"),
        ("Illusion.Assets/Bridge/SdsGeometrySaver.cs", "vertex and index buffer pools"),
        ("Illusion.Assets/Effects/EffectEditing.cs", "an .eff"),
        ("Illusion.Assets/Effects/CarEffects.cs", "an .eff"),
        ("Illusion.Assets/EntityData/TuningEditing.cs", "the entity-data storage tables"),
        ("Illusion.Assets/Import/GameMaterialCreator.cs", "a material library and its textures"),
        ("Illusion.Assets/Collisions/PhysXCooker.cs", "the cooker's own temp files"),
        ("Illusion.Formats/Archive/SdsArchive.cs", "the packed .sds"),
        ("Illusion.Formats/Archive/SdsManifest.cs", "SDSContent.xml"),
        ("Illusion.Formats/Archive/Handlers/ComplexHandlers.cs", "resources unpacked out of an archive"),
        ("Illusion.Formats/Materials/MaterialLibrary.cs", "an .mtl"),
        ("Illusion.Bridge/Payload/ExchangeWriter.cs", "the bridge's own exchange files"),
        ("Illusion.Bridge/Discovery/BridgeDiscovery.cs", "the bridge's own handshake file"),
        // Not game data at all: an opt-in trace behind ILLUSION_TRACE_PARENT, into the temp directory. Listed
        // rather than excused, because "it only writes a log" is what the next real writer will say too.
        ("Illusion.Assets/Adapters/SceneDocumentAdapter.cs", "a reparent trace into %TEMP%, behind an env var"),
        ("Illusion.Formats/Frames/FrameResource.cs", "a hierarchy trace into %TEMP%, behind an env var"),
        // The app's own files, none of them the game's.
        ("Illusion/Settings/UserSettings.cs", "the toolkit's own settings.json"),
        ("Illusion/Updates/UpdateDownloader.cs", "the downloaded release archive"),
        ("Illusion/Updates/UpdateInstaller.cs", "the toolkit's own binaries, on an update"),
    ];

    /// <summary>
    /// The four modules that wrote a car's files on their own before the seam closed, and the fifth that
    /// delegated its two writes to two of them. Four are gone; <c>car-physics-volumes</c> survives as a
    /// reader and must hold no write at all.
    /// </summary>
    private static readonly (string File, string Was)[] Retired =
    [
        ("Illusion.Assets/Prefabs/PrefabEditing.cs", "prefab-editing — wrote the prefab"),
        ("Illusion.Assets/Collisions/CarClimbBoxes.cs", "climb-boxes — wrote the prefab's climb-box rows"),
        ("Illusion.Assets/Collisions/CarPhysicsVolumes.cs",
            "car-physics-volumes — wrote the prefab and the ItemDesc records (now a reader)"),
        ("Illusion.Assets/Collisions/CarCollisionBuilder.cs",
            "car-collision-builder — wrote the ItemDesc records and the frame graph"),
        ("Illusion.Assets/Prefabs/CarPartBuilder.cs",
            "car-part-builder — mutated the frame graph and delegated its two writes to the others"),
    ];

    /// <summary>
    /// What a write looks like in source. Deliberately the primitives rather than the wrappers: a wrapper is
    /// a file on the list above, and its callers are what this is looking for. The stream openers are here
    /// too — a writer that reaches for <c>new FileStream</c> puts bytes on disk exactly as one that calls
    /// <c>WriteAllBytes</c>, and a census that only knew the convenience helpers would not see it.
    /// </summary>
    private static readonly string[] WriteCalls =
    [
        "File.WriteAllBytes(", "File.WriteAllText(", "File.WriteAllLines(", "File.AppendAllText(",
        "File.AppendAllLines(", "File.Create(", "File.CreateText(", "File.Open(", "File.OpenWrite(",
        "File.Copy(", "File.Move(", "File.Delete(", "File.Replace(",
        "new FileStream(", "new StreamWriter(", "AtomicFile.WriteAllBytes(",
    ];

    internal static void RunCarWritesProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_writes.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            Sources(sb, Check);
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            string? mirror = Mirror(focus, folder, sb, Check);
            if (mirror != null) Surfaces(sb, mirror, Check);
            ShapeRows(sb, folder, focus, Check);
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            sb.Insert(0, $"CAR WRITE PROBE ({focus}): {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // ── by reading the source ──

    private static void Sources(StringBuilder sb, Action<string, bool, string> check)
    {
        sb.AppendLine("════ who writes bytes at all ════");

        string? root = RepoRoot();
        if (root == null)
        {
            check("the source tree can be found, so the write census can be taken", false,
                "no Illusion.slnx above the running assembly — run this probe from a build made in the repo");
            return;
        }
        sb.AppendLine($"  source root: {root}");

        var allowed = Writers.ToDictionary(w => w.File, w => w.Writes, StringComparer.OrdinalIgnoreCase);
        var found = new List<(string File, int Calls)>();
        var unlisted = new List<string>();

        // EVERY project, the app included. The retired writers lived in the asset layer, but the surfaces
        // that called them lived in the app — and a view that starts writing a car's file again is exactly
        // the regression this exists for, so leaving the app out would watch the wrong half of the seam.
        foreach (string project in new[]
                 {
                     "Illusion", "Illusion.Assets", "Illusion.Formats", "Illusion.Bridge", "Illusion.Domain",
                     "Illusion.Rendering",
                 })
        {
            string dir = Path.Combine(root, "src", project);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(Path.Combine(root, "src"), file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal)
                    || relative.Contains("/bin/", StringComparison.Ordinal))
                {
                    continue;
                }
                // The probes themselves are not a surface of the app: every one writes its own report into
                // the temp directory, and several deliberately mirror an archive there to edit it. Listing
                // sixty of them would bury the one line that matters.
                if (relative.StartsWith("Illusion/Diagnostics/", StringComparison.Ordinal)) continue;

                int calls = Calls(File.ReadAllText(file));
                if (calls == 0) continue;
                found.Add((relative, calls));
                if (!allowed.ContainsKey(relative)) unlisted.Add($"{relative} ({calls})");
            }
        }

        foreach ((string file, int calls) in found.OrderBy(f => f.File, StringComparer.Ordinal))
        {
            sb.AppendLine($"    {calls,3}  {file,-58} {allowed.GetValueOrDefault(file, "NOT ON THE LIST")}");
        }

        check("every file that writes bytes is one this contract knows about",
            unlisted.Count == 0,
            unlisted.Count == 0 ? $"{found.Count} writers, all listed" : string.Join("; ", unlisted));

        // The list is a contract in both directions: an entry for a file that no longer writes is a stale
        // exception, and a stale exception is how a real one gets waved through later.
        var stale = allowed.Keys
            .Where(f => !found.Any(x => string.Equals(x.File, f, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        check("…and every file the contract allows really does write one",
            stale.Count == 0, stale.Count == 0 ? "" : string.Join("; ", stale));

        sb.AppendLine("\n  the four that wrote a car's files on their own, before the seam closed:");
        foreach ((string file, string was) in Retired)
        {
            string path = Path.Combine(root, "src", file.Replace('/', Path.DirectorySeparatorChar));
            bool gone = !File.Exists(path);
            int calls = gone ? 0 : Calls(File.ReadAllText(path));
            sb.AppendLine($"    {(gone ? "gone      " : calls == 0 ? "no writes " : $"{calls} WRITES ")} {was}");
            check($"{Path.GetFileNameWithoutExtension(file)} writes nothing",
                calls == 0, gone ? "the file is gone" : $"{calls} write calls in {file}");
        }

        check("the aggregate is the only file allowed to write a car's prefab, records or frame resource",
            allowed.Count(p => p.Value.StartsWith("A CAR:", StringComparison.Ordinal)) == 1,
            string.Join("; ", allowed.Where(p => p.Value.StartsWith("A CAR:", StringComparison.Ordinal))
                .Select(p => p.Key)));
    }

    private static int Calls(string source)
    {
        int total = 0;
        foreach (string call in WriteCalls)
        {
            for (int at = source.IndexOf(call, StringComparison.Ordinal); at >= 0;
                 at = source.IndexOf(call, at + call.Length, StringComparison.Ordinal))
            {
                int line = source.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
                int end = source.IndexOf('\n', at);
                string whole = source[line..(end < 0 ? source.Length : end)];

                // A mention inside a comment or an XML doc is prose about writing, not a write.
                if (whole[..(at - line)].Contains("//", StringComparison.Ordinal)) continue;
                // An opener is only a writer when it opens FOR writing. Both stream forms are how this
                // codebase READS a large file too, and counting those would put every reader on the list —
                // which is the fastest way to make a contract nobody keeps up to date.
                if (whole.Contains("FileAccess.Read", StringComparison.Ordinal)
                    && !whole.Contains("FileAccess.ReadWrite", StringComparison.Ordinal))
                {
                    continue;
                }
                total++;
            }
        }
        return total;
    }

    /// <summary>The umbrella's toolkit repository, found by walking up from the running assembly until the
    /// solution file appears. Null from a published build, which has no source beside it.</summary>
    private static string? RepoRoot()
    {
        var at = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; at != null && up < 8; up++, at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, "Illusion.slnx"))) return at.FullName;
        }
        return null;
    }

    // ── by running the surfaces ──

    private static void Surfaces(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n\n════ who writes when a car is edited ════");

        Car? car = Car.ReadFrom(mirror);
        FrameResource? frames = car?.Frames;
        if (car == null || frames == null) { check("the mirrored car stitches", false, mirror); return; }

        // 1) The ordinary scene save with nothing moved. It must write nothing at all: the frame resource
        //    re-serializes to the bytes it came from, and Put skips a file whose bytes are already right.
        Watch(mirror, out Dictionary<string, byte[]> before, out long puts, out long deletes);
        Car.SaveScene(mirror, frames, [], [], nameTable: false, out string? lost1);
        Report(sb, "a save with nothing moved", mirror, before, puts, deletes, check,
            expectChanges: false, lost1);

        // 2) A marker dragged. The Dummy moves in the graph, and the row the game reads has to follow — so
        //    the prefab changes, and the aggregate is what changed it.
        FrameObjectDummy? dummy = ClimbDummy(car);
        if (dummy != null)
        {
            dummy.LocalTransform = Matrix4x4.CreateTranslation(0.3f, 1.1f, 0.4f);
            Watch(mirror, out before, out puts, out deletes);
            Car.SaveScene(mirror, frames, [dummy], [], nameTable: false, out string? lost2);
            Report(sb, "a dragged climb box carried through to its row", mirror, before, puts, deletes, check,
                expectChanges: true, lost2);
        }

        // 3) A collision stub dragged. Same shape of trap, in the other list: the volume in the prefab is the
        //    copy the game reads, and the stub is only the handle.
        FrameObjectCollision? stub = frames.FrameObjects?.Values.OfType<FrameObjectCollision>().FirstOrDefault();
        if (stub != null)
        {
            stub.LocalTransform = Matrix4x4.CreateTranslation(0.2f, 0.5f, 0.9f);
            Watch(mirror, out before, out puts, out deletes);
            Car.SaveScene(mirror, frames, [], [stub], nameTable: false, out string? lost3);
            Report(sb, "a dragged collision stub carried through to its volume", mirror, before, puts,
                deletes, check, expectChanges: true, lost3);
        }

        // 4) Deleting a collision stub in the raw tree. The volume has to go with it, and through the same
        //    seam — this used to reach the prefab on its own.
        FrameObjectCollision? doomed =
            frames.FrameObjects?.Values.OfType<FrameObjectCollision>().Skip(1).FirstOrDefault();
        if (doomed != null)
        {
            Watch(mirror, out before, out puts, out deletes);
            CarStubVolume? removed = Car.DropVolumeOfStub(mirror, frames, doomed, out string? lost4);
            Report(sb, "deleting a collision stub takes its volume through the aggregate", mirror, before,
                puts, deletes, check, expectChanges: removed != null, lost4);
            if (removed != null)
            {
                Watch(mirror, out before, out puts, out deletes);
                Car.PutVolumeOfStub(mirror, frames, removed, out string? lost5);
                Report(sb, "…and putting it back goes the same way", mirror, before, puts, deletes, check,
                    expectChanges: true, lost5);
            }
        }

    }

    /// <summary>
    /// The raw property panel on a collision stub. It used to write the ItemDesc record straight out of its
    /// setter, which made the panel a second writer AND left the prefab volume beside the record stating
    /// another size. It shows the numbers now and offers no way to change them.
    ///
    /// <para>
    /// Asserted by asking the descriptors whether they CAN be set, and never by setting one — the panel has
    /// to resolve the shape against the archive's real working copy to have any rows at all, so a probe that
    /// poked a setter to find out would be writing into the player's install to discover that it should not
    /// have. Read-only either way: nothing here calls <c>Set</c>.
    /// </para>
    /// </summary>
    private static void ShapeRows(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var sds = new FileInfo(Path.Combine(folder, focus + ".sds"));
        string extracted = MafiaEnvironment.ExtractedDir(sds);
        FrameResource? frames;
        try { frames = Assets.Sds.SdsMeshLoader.OpenScene(extracted).FrameResource; }
        catch (Exception) { frames = null; }

        FrameObjectCollision? stub =
            frames?.FrameObjects?.Values.OfType<FrameObjectCollision>().FirstOrDefault();
        if (frames == null || stub == null)
        {
            check("the focus car carries a collision stub to read the panel of", false, focus);
            return;
        }

        int shown = 0, settable = 0;
        var document = new Assets.Adapters.SceneDocumentAdapter(frames, sds);
        if (document.Node(stub) is Assets.Adapters.FrameNodeAdapter node)
        {
            foreach (PropertyGroup group in node.GetPropertyGroups())
            {
                foreach (PropertyDescriptor row in group.Properties)
                {
                    if (!row.Id.StartsWith("Shape.", StringComparison.Ordinal)) continue;
                    shown++;
                    if (row.Set != null) settable++;
                }
            }
        }

        sb.AppendLine($"\n    the raw panel of \"{stub.Name}\": {shown} physics-shape rows, "
            + $"{settable} of them settable");
        check("the raw property panel still SHOWS a shape's numbers",
            shown > 0, $"{shown} rows");
        check("…and offers no way to write one — that is the component's collision row",
            settable == 0, $"{settable} settable");
    }

    private static FrameObjectDummy? ClimbDummy(Car car)
    {
        CarMarker? box = car.Markers.FirstOrDefault(m => m.Role == CarMarkerRole.ClimbBox);
        if (box == null || car.Frames?.FrameObjects == null) return null;
        foreach (FrameObjectDummy dummy in car.Frames.FrameObjects.Values.OfType<FrameObjectDummy>())
        {
            if (dummy.Name?.String is { Length: > 0 } name
                && Formats.Hashing.Fnv64.Hash(name) == box.Frame)
            {
                return dummy;
            }
        }
        return null;
    }

    // ── the witness ──

    /// <summary>The bytes of every file a car's assembly lives in, and where the aggregate's counters stood.</summary>
    private static void Watch(
        string mirror, out Dictionary<string, byte[]> census, out long puts, out long deletes)
    {
        census = Census(mirror);
        puts = Car.Puts;
        deletes = Car.Deletes;
    }

    private static void Report(
        StringBuilder sb, string what, string mirror, Dictionary<string, byte[]> before,
        long puts, long deletes, Action<string, bool, string> check, bool expectChanges, string? lost)
    {
        Dictionary<string, byte[]> after = Census(mirror);
        List<string> changed =
        [
            .. after.Where(p => !before.TryGetValue(p.Key, out byte[]? was)
                    || !was.AsSpan().SequenceEqual(p.Value))
                .Select(p => p.Key),
            .. before.Keys.Where(k => !after.ContainsKey(k)).Select(k => k + " (gone)"),
        ];
        long wrote = Car.Puts - puts, removed = Car.Deletes - deletes;

        sb.AppendLine($"    {what}: {changed.Count} files changed, "
            + $"the aggregate put {wrote} and deleted {removed}"
            + (lost == null ? "" : $"  — REFUSED: {lost}"));
        foreach (string file in changed) sb.AppendLine($"        {file}");

        // The one thing this probe exists for. Every file of the assembly that moved has to be one the
        // aggregate says it wrote; a file that moved while the counter stood still is a second writer.
        check($"{what}: nothing but the aggregate wrote",
            changed.Count <= wrote + removed,
            $"{changed.Count} changed, {wrote + removed} accounted for");
        check($"{what}: {(expectChanges ? "the change reached the file" : "the file was left alone")}",
            expectChanges ? changed.Count > 0 : changed.Count == 0,
            string.Join(", ", changed.Take(3)));
    }

    /// <summary>Every file a car's assembly is written down in: the prefab, the shape records, the frame
    /// resource and its name table. Keyed by name, because the mirror is one flat folder.</summary>
    private static Dictionary<string, byte[]> Census(string mirror)
    {
        var found = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        SdsManifest manifest;
        try { manifest = SdsManifest.Load(mirror); }
        catch (Exception) { return found; }

        foreach (string type in new[] { "PREFAB", "ItemDesc", "FrameResource", "FrameNameTable" })
        {
            foreach (string file in manifest.GetFiles(type))
            {
                try { found[Path.GetFileName(file)] = File.ReadAllBytes(file); }
                catch (Exception) { /* a file that cannot be read counts as absent */ }
            }
        }
        return found;
    }

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

        sb.AppendLine($"\n  the focus car, mirrored into {mirror}");
        return mirror;
    }
}
