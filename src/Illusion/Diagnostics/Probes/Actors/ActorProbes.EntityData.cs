using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Formats.Actors;
using Illusion.Formats.EntityData;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>The entity-data storages (.eds): the same behavior catalog applied to what no district places —
/// the player above all. Part of <see cref="ActorProbes"/> — one file per area of the actor layer.</summary>
internal static partial class ActorProbes
{
    /// <summary>
    /// Census + edit round-trip over every .eds of the install: the tables split out of the blob, decode through
    /// the actor behavior catalog, re-save byte for byte, and an edit to the player's own table lands in exactly
    /// that field. Output: %TEMP%\illusion_eds_tables.txt
    /// </summary>
    internal static void RunEdsTablesProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_eds_tables.txt");
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

            string[] files = Directory.GetFiles(root, "*.eds", SearchOption.AllDirectories);
            sb.AppendLine($"ENTITY DATA PROBE — {files.Length} storages\n");

            var byType = new SortedDictionary<int, (int Files, int Tables, int Fielded, int Fields, int Size)>();
            int storages = 0, typed = 0, fixpoint = 0, errors = 0;
            string firstError = "";
            string? playerFile = null;

            foreach (string file in files)
            {
                try
                {
                    byte[] original = File.ReadAllBytes(file);
                    EntityDataStorageFile eds = EntityDataStorageFile.Load(file);
                    storages++;
                    if (eds.AreTablesTyped) typed++;
                    if (eds.Type == Formats.Actors.EntityType.Player2) playerFile ??= file;

                    byType.TryGetValue(eds.EntityType, out (int Files, int Tables, int Fielded, int Fields, int Size) row);
                    row.Files++;
                    row.Size = eds.TableSize;
                    foreach (EntityDataTable table in eds.Tables)
                    {
                        row.Tables++;
                        if (table.Fields.Count > 0) { row.Fielded++; row.Fields += table.Fields.Count; }
                    }
                    byType[eds.EntityType] = row;

                    if (eds.ToBytes().AsSpan().SequenceEqual(original)) fixpoint++;
                    else if (firstError.Length == 0) firstError = "fixpoint diff in " + Path.GetFileName(file);
                }
                catch (Exception ex)
                {
                    errors++;
                    if (firstError.Length == 0) firstError = Path.GetFileName(file) + ": " + ex.Message;
                }
            }

            Check("every storage reads", errors == 0 && storages == files.Length,
                $"{storages}/{files.Length} {firstError}");
            Check("every table run is split into tables", typed == storages, $"{typed}/{storages}");
            Check("reading the fields leaves the bytes alone", fixpoint == storages, $"{fixpoint}/{storages}");

            sb.AppendLine();
            sb.AppendLine("type  size  storages  tables  with fields  fields");
            foreach ((int type, (int f, int t, int fielded, int fields, int size)) in byType)
            {
                string name = Enum.IsDefined((Formats.Actors.EntityType)type)
                    ? ((Formats.Actors.EntityType)type).ToString() : "?";
                sb.AppendLine($"{type,4}  {size,4}  {f,8}  {t,6}  {fielded,11}  {fields,6}  {name}");
            }

            // The player: the entry point this whole slice exists for.
            sb.AppendLine();
            if (playerFile == null)
            {
                Check("the player's table decodes", false, "no C_Player2 storage in the install");
            }
            else
            {
                EntityDataStorageFile player = EntityDataStorageFile.Load(playerFile);
                EntityDataTable table = player.Tables[0];
                ActorPropertyField? health = table.Fields.FirstOrDefault(f => f.Name == "HealthMax");
                Check("the player's table decodes", health != null,
                    $"{Path.GetFileName(playerFile)}: {table.Fields.Count} fields, "
                    + $"HealthMax={health?.Display ?? "-"}");

                if (health != null)
                {
                    byte[] original = File.ReadAllBytes(playerFile);
                    float before = health.Single;
                    health.Single = before + 55f;
                    byte[] edited = player.ToBytes();

                    int changed = 0;
                    int n = Math.Min(original.Length, edited.Length);
                    for (int i = 0; i < n; i++)
                    {
                        if (original[i] != edited[i]) changed++;
                    }

                    using var stream = new MemoryStream(edited, writable: false);
                    EntityDataStorageFile back = EntityDataStorageFile.Read(stream);
                    float readBack = back.Tables[0].Fields.First(f => f.Name == "HealthMax").Single;

                    Check("editing the player's health reaches the file and nothing else moves",
                        original.Length == edited.Length && changed > 0 && changed <= 4
                        && Math.Abs(readBack - (before + 55f)) < 1e-3f,
                        $"{before} → {readBack}, {changed} byte(s) changed");
                }

                foreach (ActorPropertyField f in table.Fields)
                {
                    sb.AppendLine($"    {f.Name} = {f.Display}");
                }
            }

            // The car tables are the one entity type reaching EDS whose layout is not ported yet. Say so out
            // loud rather than letting the census read as full coverage.
            if (byType.TryGetValue(18, out (int Files, int Tables, int Fielded, int Fields, int Size) cars)
                && cars.Fielded == 0)
            {
                sb.AppendLine();
                sb.AppendLine($"NOT COVERED: {cars.Tables} C_Car tables of {cars.Size} bytes across {cars.Files} "
                              + "storages round-trip but have no named fields — the handling layout is the one "
                              + "MafiaToolkit struct still to port.");
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"ENTITY DATA PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }
}
