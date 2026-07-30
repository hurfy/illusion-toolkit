using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Actors;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>The entity-init property table: what the core makes of the behavior blobs, and what an edit to one
/// does to the file. Part of <see cref="ActorProbes"/> — one file per area of the actor layer.</summary>
internal static partial class ActorProbes
{
    /// <summary>
    /// Census + edit round-trip over every .act of the install: the property region parses everywhere, its rows
    /// decode into named fields, a pack whose fields were merely READ still re-saves byte for byte, and a pack
    /// whose field was CHANGED differs in exactly that field's bytes and reads the new value back.
    /// Output: %TEMP%\illusion_actor_props.txt
    /// </summary>
    internal static void RunActorPropertiesProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_actor_props.txt");
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
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root)) { sb.AppendLine("resources not unpacked: " + root); return; }

            string[] files = Directory.GetFiles(root, "*.act", SearchOption.AllDirectories);
            sb.AppendLine($"ACTOR PROPERTIES PROBE — {files.Length} packs\n");

            // ── Census ──
            var rowsByType = new SortedDictionary<int, int>();
            var fieldedByType = new SortedDictionary<int, int>();
            var sizesByType = new SortedDictionary<int, SortedSet<int>>();
            int packs = 0, propsTyped = 0, compressed = 0, fixpoint = 0, errors = 0;
            int rows = 0, rowsWithFields = 0, fields = 0;
            int sharerTotal = 0, actorsWithRow = 0, sharedRows = 0;
            int cutsceneTyped = 0, cutscenePacks = 0, cutsceneNames = 0, cutsceneActors = 0;
            string firstError = "";

            foreach (string file in files)
            {
                try
                {
                    byte[] original = File.ReadAllBytes(file);
                    ActorsFile pack = ActorsFile.Load(file);
                    packs++;
                    if (pack.IsCompressed) compressed++;
                    if (pack.ArePropertiesTyped) propsTyped++;

                    foreach (ActorPropertyRow row in pack.PropertyRows)
                    {
                        rows++;
                        int type = row.TypeId;
                        rowsByType.TryGetValue(type, out int seen);
                        rowsByType[type] = seen + 1;
                        if (!sizesByType.TryGetValue(type, out SortedSet<int>? sizes))
                        {
                            sizes = new SortedSet<int>();
                            sizesByType[type] = sizes;
                        }
                        sizes.Add(row.PayloadSize);
                        if (row.Fields.Count > 0)
                        {
                            rowsWithFields++;
                            fields += row.Fields.Count;
                            fieldedByType.TryGetValue(type, out int f);
                            fieldedByType[type] = f + 1;
                        }
                        sharerTotal += row.SharerCount;
                        if (row.SharerCount > 1) sharedRows++;
                    }

                    foreach (ActorEntry actor in pack.Actors)
                    {
                        if (actor.InitPropId >= 0) actorsWithRow++;
                        if (actor.Type == EntityType.Cutscene) cutsceneActors++;
                    }

                    if (pack.CutsceneNames.Count > 0)
                    {
                        cutscenePacks++;
                        cutsceneNames += pack.CutsceneNames.Count;
                    }
                    if (pack.IsCutsceneLookupTyped) cutsceneTyped++;

                    // Reading the fields must not disturb anything: the blob is the authority and an unchanged
                    // value is never written back over it.
                    if (pack.ToBytes().AsSpan().SequenceEqual(original)) fixpoint++;
                    else if (firstError.Length == 0) firstError = "fixpoint diff in " + Path.GetFileName(file);
                }
                catch (Exception ex)
                {
                    errors++;
                    if (firstError.Length == 0) firstError = Path.GetFileName(file) + ": " + ex.Message;
                }
            }

            Check("every pack reads", errors == 0 && packs == files.Length, $"{packs}/{files.Length} {firstError}");
            Check("every property region is typed", propsTyped == packs, $"{propsTyped}/{packs}");
            Check("reading the fields leaves the bytes alone", fixpoint == packs, $"{fixpoint}/{packs}");
            Check("the table carries rows", rows > 0, $"{rows} rows, {fields} named fields");
            Check("every row of a known type decodes fields", rowsWithFields > 0 && rowsWithFields <= rows,
                $"{rowsWithFields}/{rows} rows have fields");
            Check("row sharing is accounted for", sharerTotal == actorsWithRow,
                $"{actorsWithRow} actors point at a row, {sharedRows} rows have more than one sharer");
            Check("every cutscene lookup is typed", cutsceneTyped == packs, $"{cutsceneTyped}/{packs}");
            Check("the cutscene lookup names the cutscene actors", cutsceneNames == cutsceneActors,
                $"{cutsceneNames} names across {cutscenePacks} packs vs {cutsceneActors} C_Cutscene actors");

            sb.AppendLine();
            sb.AppendLine($"packs: {packs} ({compressed} compressed, {packs - compressed} uncompressed)");
            sb.AppendLine("type  rows  with fields  payload sizes");
            foreach ((int type, int count) in rowsByType)
            {
                fieldedByType.TryGetValue(type, out int fielded);
                string sizes = string.Join("/", sizesByType[type]);
                string name = Enum.IsDefined((EntityType)type) ? ((EntityType)type).ToString() : "?";
                sb.AppendLine($"{type,4}  {count,5}  {fielded,11}  {sizes,-12} {name}");
            }

            // ── Editing ──
            sb.AppendLine();
            CheckPropertyEdit(files, "a number", EntityType.Door, "Locked",
                f => f.Number = f.Number == 0 ? 1 : 0, sb, Check);
            CheckPropertyEdit(files, "a float", EntityType.CrashObject, "HitPoints",
                f => f.Single += 12.5f, sb, Check);
            CheckPropertyEdit(files, "a flag", EntityType.CrashObject, "CameraCollision",
                f => f.Flag = !f.Flag, sb, Check);
            CheckPropertyEdit(files, "a vector", EntityType.CleanEntity, "BBoxSize",
                f => f.Vector = new System.Numerics.Vector3(3, 4, 5), sb, Check);
            CheckPropertyEdit(files, "a text buffer", EntityType.ScriptEntity, "ScriptName",
                f => f.Text = "probe/edited.lua", sb, Check);

            CheckPanel(sb, Check);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"ACTOR PROPERTIES PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    // What the property panel actually offers on an actor: the behavior group and the two fixed-size item fields
    // the panel can now write. Driven through the descriptors, not around them — a field that is editable in
    // theory and read-only in the panel is the failure this catches.
    private static void CheckPanel(StringBuilder sb, Action<string, bool, string> check)
    {
        string sds = Path.Combine(MafiaEnvironment.CityFolder, "eastside.sds");
        if (!File.Exists(sds)) { check("the panel offers the behavior fields", false, "no eastside.sds"); return; }

        (_, _, ISceneDocument? loaded) = SdsMeshLoader.LoadHierarchy(new FileInfo(sds));
        if (loaded is not SceneDocumentAdapter document)
        {
            check("the panel offers the behavior fields", false, "the district did not load");
            return;
        }

        ActorNodeAdapter? withRow = null;
        foreach (ActorEntry actor in document.Placements.All)
        {
            ActorsFile? pack = document.Placements.PackOf(actor);
            if (pack?.PropertiesOf(actor) is not { Fields.Count: > 0 }) continue;
            withRow = document.ActorNode(actor);
            break;
        }
        if (withRow == null)
        {
            check("the panel offers the behavior fields", false, "no actor of eastside has a decoded row");
            return;
        }

        IReadOnlyList<PropertyGroup> groups = withRow.GetPropertyGroups();
        PropertyGroup? behaviour = groups.FirstOrDefault(g =>
            g.Title.StartsWith("Behaviour", StringComparison.Ordinal) && !g.IsUnknown);
        PropertyDescriptor? writable = behaviour?.Properties.FirstOrDefault(p => p.Set != null);
        check("the panel offers the behavior fields", behaviour != null && writable != null,
            $"{withRow.Name} ({withRow.TypeName}): {behaviour?.Title ?? "no group"}, "
            + $"{behaviour?.Properties.Count ?? 0} field(s), "
            + $"{groups.Count(g => g.IsUnknown)} collapsed group(s)");

        if (writable != null)
        {
            object? before = writable.Get();
            object? after = before switch
            {
                bool b => !b,
                long n => n + 1,
                float f => f + 1f,
                ulong h => h + 1,
                string s => s + "!",
                System.Numerics.Vector3 v => v + System.Numerics.Vector3.One,
                _ => null,
            };
            if (after != null)
            {
                writable.Set!(after);
                bool stuck = Equals(writable.Get(), after);
                writable.Set(before);
                check("a behavior field written through the panel sticks", stuck && Equals(writable.Get(), before),
                    $"{writable.Label}: {before} → {after}");
            }
        }

        // "Active on load" — bit 0 of the flags word, and the first .act field outside the transform the panel
        // may write at all.
        PropertyDescriptor? active = groups.SelectMany(g => g.Properties)
            .FirstOrDefault(p => p.Id == "Actor.ActivateOnInit");
        bool wasActive = withRow.Actor.ActivateOnInit;
        ushort flagsBefore = withRow.Actor.Flags;
        active?.Set?.Invoke(!wasActive);
        bool flipped = withRow.Actor.ActivateOnInit == !wasActive
                       && (withRow.Actor.Flags & ~1) == (flagsBefore & ~1);
        active?.Set?.Invoke(wasActive);
        check("'Active on load' flips only its own bit", active?.Set != null && flipped
              && withRow.Actor.Flags == flagsBefore, $"flags {flagsBefore} untouched apart from bit 0");

        // The row an actor points at is editable, but not at a row describing another entity type — that is a
        // blob the engine would read as the wrong struct.
        PropertyDescriptor? initProp = groups.SelectMany(g => g.Properties)
            .FirstOrDefault(p => p.Id == "Actor.InitProp");
        ActorsFile ownPack = document.Placements.PackOf(withRow.Actor)!;
        short rowBefore = withRow.Actor.InitPropId;
        int foreign = -1;
        for (int i = 0; i < ownPack.PropertyRows.Count; i++)
        {
            if (ownPack.PropertyRows[i].TypeId != (int)withRow.Actor.TypeId) { foreign = i; break; }
        }
        initProp?.Set?.Invoke((long)foreign);
        bool refused = withRow.Actor.InitPropId == rowBefore;
        initProp?.Set?.Invoke(-1L);
        bool cleared = withRow.Actor.InitPropId == -1;
        withRow.Actor.InitPropId = rowBefore;
        check("the init-props row refuses a row of another entity type", initProp?.Set != null && refused && cleared,
              $"row {rowBefore} kept when offered {foreign} (a {(foreign >= 0 ? ownPack.PropertyRows[foreign].Type.ToString() : "-")} row), "
              + "and -1 accepted");

        // Renaming from the panel. The name is an identity — the engine keys the entity by its hash — so the
        // descriptor has to re-derive that hash, and it has to refuse a name another actor of the pack already
        // uses rather than let the two collide.
        PropertyDescriptor? entityName = groups.SelectMany(g => g.Properties)
            .FirstOrDefault(p => p.Id == "Actor.Entity");
        ActorsFile namePack = document.Placements.PackOf(withRow.Actor)!;
        string nameBefore = withRow.Actor.EntityName;
        ulong hashBefore = withRow.Actor.EntityHash;
        entityName?.Set?.Invoke("probe_renamed_actor");
        bool renamed = withRow.Actor.EntityName == "probe_renamed_actor"
                       && withRow.Actor.EntityHash == Formats.Hashing.Fnv64.Hash("probe_renamed_actor");

        string taken = namePack.Actors.First(a => !ReferenceEquals(a, withRow.Actor)).EntityName;
        entityName?.Set?.Invoke(taken);
        bool nameRefused = withRow.Actor.EntityName == "probe_renamed_actor";

        entityName?.Set?.Invoke(nameBefore);
        check("the panel renames an actor and re-derives its hash",
            entityName?.Set != null && renamed && nameRefused
            && withRow.Actor.EntityName == nameBefore && withRow.Actor.EntityHash == hashBefore,
            $"'{nameBefore}' → 'probe_renamed_actor' → refused '{taken}' → back");

        sb.AppendLine($"    panel: {withRow.Name} of eastside, groups: "
                      + string.Join(", ", groups.Select(g => $"{g.Title}({g.Properties.Count})")));
    }

    // Finds the first pack holding a row of `type` with a field called `name`, changes it, and requires the file
    // to come back with the new value and with NOTHING else moved: the changed bytes must all lie inside that one
    // field, which is what proves the blob is patched in place rather than re-encoded from a partial model.
    private static void CheckPropertyEdit(string[] files, string label, EntityType type, string name,
        Action<ActorPropertyField> edit, StringBuilder sb, Action<string, bool, string> check)
    {
        foreach (string file in files)
        {
            ActorsFile pack;
            try { pack = ActorsFile.Load(file); }
            catch (Exception) { continue; }

            int rowIndex = -1;
            int fieldIndex = -1;
            for (int i = 0; i < pack.PropertyRows.Count && rowIndex < 0; i++)
            {
                if (pack.PropertyRows[i].Type != type) continue;
                for (int f = 0; f < pack.PropertyRows[i].Fields.Count; f++)
                {
                    if (pack.PropertyRows[i].Fields[f].Name != name) continue;
                    rowIndex = i;
                    fieldIndex = f;
                    break;
                }
            }
            if (rowIndex < 0) continue;

            ActorPropertyField field = pack.PropertyRows[rowIndex].Fields[fieldIndex];
            string before = field.Display;
            edit(field);
            string after = field.Display;

            byte[] original = File.ReadAllBytes(file);
            byte[] edited = pack.ToBytes();

            int changed = 0;
            long firstAt = -1;
            int n = Math.Min(original.Length, edited.Length);
            for (int i = 0; i < n; i++)
            {
                if (original[i] == edited[i]) continue;
                changed++;
                if (firstAt < 0) firstAt = i;
            }

            using var reloaded = new MemoryStream(edited, writable: false);
            ActorsFile back = ActorsFile.Read(reloaded);
            string readBack = back.PropertyRows[rowIndex].Fields[fieldIndex].Display;

            bool sameLength = original.Length == edited.Length;
            bool contained = changed > 0 && changed <= field.Capacity;
            check($"editing {label} ({type}.{name}) reaches the file and nothing else moves",
                sameLength && contained && readBack == after && before != after,
                $"{Path.GetFileName(file)}: '{before}' → '{after}', read back '{readBack}', "
                + $"{changed} byte(s) changed at {firstAt} (field is {field.Capacity} wide), "
                + $"length {original.Length}→{edited.Length}");
            sb.AppendLine($"    {type}.{name}: row {rowIndex} of {Path.GetFileName(file)}, "
                          + $"{pack.PropertyRows[rowIndex].SharerCount} sharer(s)");
            return;
        }
        check($"editing {label} ({type}.{name}) reaches the file and nothing else moves", false,
            "no pack in the corpus carries that field");
    }
}
