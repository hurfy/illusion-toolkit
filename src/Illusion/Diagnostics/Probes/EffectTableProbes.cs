using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Formats.Archive;
using Illusion.Formats.ResourceFormats;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What the game's own tables say about impact effects.
///
/// <para>
/// Established in game: a shot's effect follows the deformable PART the geometry hangs on — rebinding a cube
/// from the bonnet's vertex group to the roof's, material untouched, changes it. The part's own effects block
/// was then measured and ruled out (<c>ParticleBreakID</c> is −1 on all 1698 parts of all 85 cars). So the
/// choice is made somewhere else, and the two tables named for exactly this job are
/// <c>car_particles_keys.tbl</c> and <c>materials_shots.tbl</c>.
/// </para>
/// <para>
/// Nothing here reverses a format: the table container is already read (typed columns, see
/// <see cref="TableData"/>). This dumps the columns and the rows so the tables can be looked at before
/// anything is built on top of them.
/// </para>
/// Output: %TEMP%\illusion_effect_tables.txt
/// </summary>
internal static class EffectTableProbes
{
    private const string TableResourceType = "Table";

    internal static void RunEffectTablesProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_effect_tables.txt");
        var sb = new StringBuilder();

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            string tables = Path.Combine(MafiaEnvironment.PcFolder, "sds", "tables", "tables.sds");
            if (!File.Exists(tables))
            {
                sb.AppendLine("no tables.sds at " + tables);
                return;
            }

            sb.AppendLine("EFFECT TABLES\n");
            SdsArchive archive = SdsArchive.Open(tables);
            int dumped = 0;
            foreach (ResourceEntry entry in archive.Entries)
            {
                if (entry.Data is null) continue;
                if (entry.TypeId < 0 || entry.TypeId >= archive.ResourceTypes.Count) continue;
                if (!string.Equals(archive.ResourceTypes[entry.TypeId].Name, TableResourceType,
                        StringComparison.Ordinal)) continue;

                var resource = new TableResource();
                using var stream = new MemoryStream(entry.Data, writable: false);
                resource.Deserialize(entry.Version, stream, archive.Endian);

                foreach (TableData table in resource.Tables)
                {
                    string name = table.Name ?? "";
                    bool wanted = name.Contains("car_particles", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("materials_shots", StringComparison.OrdinalIgnoreCase);
                    if (!wanted) continue;
                    dumped++;
                    Dump(sb, table);
                }
            }

            sb.AppendLine($"\ntables in this archive: "
                + string.Join(", ", AllNames(archive).Take(40)));
            Check(sb, "both effect tables are readable with the container we already have", dumped == 2,
                $"{dumped} of 2 dumped");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static void Dump(StringBuilder sb, TableData table)
    {
        sb.AppendLine($"════ {table.Name} — {table.Rows.Count} rows, {table.Columns.Count} columns ════");
        foreach (TableData.Column column in table.Columns)
        {
            // Columns are named by HASH only — the strings are not in the file. The type is what tells one
            // column from another until a name turns up.
            sb.AppendLine($"    0x{column.NameHash:X8}  {column.Type}");
        }

        sb.AppendLine();
        // Every row, not a sample: these tables are hundreds of rows at most, and the answer being looked for
        // is a NAME in one of them, which a sample would as likely hide as show.
        foreach (TableData.Row row in table.Rows)
        {
            sb.AppendLine("    " + string.Join(" | ", row.Values.Select(Cell)));
        }
        sb.AppendLine();
    }

    private static string Cell(object? value) => value switch
    {
        null => "-",
        string s => s.Trim(),
        float f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        ulong h => "0x" + h.ToString("X16"),
        _ => value.ToString() ?? "-",
    };

    private static IEnumerable<string> AllNames(SdsArchive archive)
    {
        foreach (ResourceEntry entry in archive.Entries)
        {
            if (entry.Data is null) continue;
            if (entry.TypeId < 0 || entry.TypeId >= archive.ResourceTypes.Count) continue;
            if (!string.Equals(archive.ResourceTypes[entry.TypeId].Name, TableResourceType,
                    StringComparison.Ordinal)) continue;
            var resource = new TableResource();
            using var stream = new MemoryStream(entry.Data, writable: false);
            resource.Deserialize(entry.Version, stream, archive.Endian);
            foreach (TableData table in resource.Tables) yield return table.Name ?? "?";
        }
    }

    private static void Check(StringBuilder sb, string name, bool ok, string detail) =>
        sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
}
