using Illusion.Formats.Archive;

namespace Illusion.Formats.ResourceFormats;

/// <summary>
/// Reads the game's own physics-surface table, <c>pc/sds/tables/tables.sds → /tables/MaterialsPhysics.tbl</c> —
/// the authoritative source of collision material indices and names.
/// </summary>
/// <remarks>
/// Each row carries the material's token and a 64-bit guid whose low dword is the material index the cooked
/// collision meshes refer to. The archive is XTEA-wrapped, which <see cref="SdsArchive.Open"/> already handles.
/// <para/>
/// Nothing depends on this at load time: the toolkit ships a verified copy of the table, and this reader exists so
/// that copy can be checked against the installed game rather than trusted forever.
/// </remarks>
public static class MaterialsPhysicsTable
{
    /// <summary>Archive path relative to the game root.</summary>
    public const string ArchiveRelativePath = @"pc\sds\tables\tables.sds";

    private const string TableSuffix = "MaterialsPhysics.tbl";
    private const string TableResourceType = "Table";

    /// <summary>Absolute path of the archive holding the table for a game installation.</summary>
    public static string ArchivePath(string gameRoot) => Path.Combine(gameRoot, ArchiveRelativePath);

    /// <summary>
    /// Reads material index → token from the installed game. Returns null when the archive is missing; throws only
    /// if the archive exists but cannot be understood.
    /// </summary>
    public static IReadOnlyDictionary<int, string>? TryReadFromGame(string gameRoot)
    {
        string path = ArchivePath(gameRoot);
        return File.Exists(path) ? Read(path) : null;
    }

    /// <summary>Reads material index → token from a <c>tables.sds</c> archive.</summary>
    /// <exception cref="InvalidDataException">The archive holds no readable MaterialsPhysics table.</exception>
    public static IReadOnlyDictionary<int, string> Read(string tablesSdsPath)
    {
        SdsArchive archive = SdsArchive.Open(tablesSdsPath);
        foreach (ResourceEntry entry in archive.Entries)
        {
            if (entry.Data is null) continue;
            if (entry.TypeId < 0 || entry.TypeId >= archive.ResourceTypes.Count) continue;
            if (!string.Equals(archive.ResourceTypes[entry.TypeId].Name, TableResourceType, StringComparison.Ordinal)) continue;

            var resource = new TableResource();
            using var stream = new MemoryStream(entry.Data, writable: false);
            resource.Deserialize(entry.Version, stream, archive.Endian);

            foreach (TableData table in resource.Tables)
            {
                if (table.Name is null || !table.Name.EndsWith(TableSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                return ReadRows(table, tablesSdsPath);
            }
        }

        throw new InvalidDataException($"'{tablesSdsPath}' contains no {TableSuffix} table.");
    }

    private static Dictionary<int, string> ReadRows(TableData table, string source)
    {
        int nameColumn = ColumnOfType(table, TableData.ColumnType.String32);
        int guidColumn = ColumnOfType(table, TableData.ColumnType.Hash64);
        if (nameColumn < 0 || guidColumn < 0)
        {
            throw new InvalidDataException(
                $"{TableSuffix} in '{source}' has no name/guid column pair (String32 + Hash64).");
        }

        var byIndex = new Dictionary<int, string>(table.Rows.Count);
        foreach (TableData.Row row in table.Rows)
        {
            if (row.Values.Count <= nameColumn || row.Values.Count <= guidColumn) continue;
            ulong guid = Convert.ToUInt64(row.Values[guidColumn]);
            uint index = (uint)(guid & 0xFFFFFFFF);
            if (index == uint.MaxValue) continue; // the table's own "undefined" row
            byIndex[(int)index] = (row.Values[nameColumn] as string ?? string.Empty).Trim();
        }
        return byIndex;
    }

    private static int ColumnOfType(TableData table, TableData.ColumnType type)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (table.Columns[i].Type == type) return i;
        }
        return -1;
    }
}
