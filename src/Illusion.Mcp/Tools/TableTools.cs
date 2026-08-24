using System.ComponentModel;
using System.Globalization;
using Illusion.Formats.Archive;
using Illusion.Formats.IO;
using Illusion.Formats.ResourceFormats;
using Illusion.Mcp.Services;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Reading the game's data tables — the rows behind weapons, cars, physics materials and most other
/// tuning, packed as "Table" resources inside <c>tables.sds</c>.
/// <para>
/// <b>Columns have no names on disk.</b> A column stores only the FNV32 of its name, so a table is
/// positional: cell 3 of every row belongs to column 3, and what column 3 <i>means</i> has to be
/// recovered by hashing candidate names until one matches (<c>hash_batch</c> is the other half of
/// that loop). Columns are therefore reported by hash and type, and rows as plain positional cells;
/// nothing here invents a name it cannot prove.
/// </para>
/// A table can be reached three ways, and every tool accepts all three: an SDS whose Table resources
/// are enumerated, a standalone <c>.tbl</c> the toolkit extracted, or the base64 of a Table-resource
/// payload straight out of <c>extract_resource</c> (which needs <c>version</c> alongside it, since
/// that lives in the archive's entry header rather than in the payload).
/// </summary>
[McpServerToolType]
public sealed class TableTools
{
    [McpServerTool(Name = "list_tables")]
    [Description("List the data tables in a source: each table's name, name hash, row and column counts, and the column hashes and types. Pass one of sdsPath, tablePath or base64Data.")]
    public static string ListTables(
        ArchiveService archives,
        [Description("An .sds archive whose Table resources are enumerated.")] string? sdsPath = null,
        [Description("A standalone .tbl file the toolkit extracted.")] string? tablePath = null,
        [Description("Base64 of a Table-resource payload from extract_resource. Needs version.")] string? base64Data = null,
        [Description("Resource format version that goes with base64Data.")] int version = 0)
    {
        try
        {
            if (!TryLoad(archives, sdsPath, tablePath, base64Data, version, out List<TableData> tables, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            return ToolResult.Json(new
            {
                success = true,
                source = sdsPath ?? tablePath ?? "base64Data",
                count = tables.Count,
                tables = tables.Select(t => new
                {
                    name = t.Name,
                    nameHash = t.NameHash,
                    nameHashHex = string.Create(CultureInfo.InvariantCulture, $"0x{t.NameHash:X16}"),
                    rowCount = t.Rows.Count,
                    columnCount = t.Columns.Count,
                    columns = t.Columns.Select(Describe),
                }),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "dump_rows")]
    [Description("Dump a data table's rows. Cells are positional — cell N belongs to column N as reported by list_tables. Give tableName when the source holds more than one table. Paginated.")]
    public static string DumpRows(
        ArchiveService archives,
        [Description("An .sds archive whose Table resources are enumerated.")] string? sdsPath = null,
        [Description("A standalone .tbl file the toolkit extracted.")] string? tablePath = null,
        [Description("Base64 of a Table-resource payload from extract_resource. Needs version.")] string? base64Data = null,
        [Description("Resource format version that goes with base64Data.")] int version = 0,
        [Description("Which table, matched case-insensitively and ignoring any path or .tbl suffix. Required when the source holds more than one.")] string? tableName = null,
        [Description("Index of the first row to return. Default 0.")] int offset = 0,
        [Description("How many rows to return. Default 100.")] int limit = 0)
    {
        try
        {
            if (!TryLoad(archives, sdsPath, tablePath, base64Data, version, out List<TableData> tables, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }
            if (!TryPick(tables, tableName, out TableData? table, out complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            (int start, int count) = Page.Clamp(offset, limit);
            List<TableData.Row> window = Page.Slice(table.Rows, start, count);

            return ToolResult.Json(new
            {
                success = true,
                table = table.Name,
                columns = table.Columns.Select(Describe),
                total = table.Rows.Count,
                offset = start,
                limit = count,
                returned = window.Count,
                rows = window.Select((r, i) => new { index = start + i, cells = r.Values }),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "lookup_by_row")]
    [Description("Read one table row by index, with each cell paired to its column hash and type. Give tableName when the source holds more than one table.")]
    public static string LookupByRow(
        ArchiveService archives,
        [Description("Zero-based row index.")] int rowIndex,
        [Description("An .sds archive whose Table resources are enumerated.")] string? sdsPath = null,
        [Description("A standalone .tbl file the toolkit extracted.")] string? tablePath = null,
        [Description("Base64 of a Table-resource payload from extract_resource. Needs version.")] string? base64Data = null,
        [Description("Resource format version that goes with base64Data.")] int version = 0,
        [Description("Which table, matched case-insensitively and ignoring any path or .tbl suffix. Required when the source holds more than one.")] string? tableName = null)
    {
        try
        {
            if (!TryLoad(archives, sdsPath, tablePath, base64Data, version, out List<TableData> tables, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }
            if (!TryPick(tables, tableName, out TableData? table, out complaint))
            {
                return ToolResult.Invalid(complaint);
            }
            if (rowIndex < 0 || rowIndex >= table.Rows.Count)
            {
                return ToolResult.Invalid(
                    $"rowIndex {rowIndex} is out of range — '{table.Name}' has {table.Rows.Count} rows");
            }

            TableData.Row row = table.Rows[rowIndex];
            var cells = new List<object>(table.Columns.Count);
            for (int i = 0; i < table.Columns.Count; i++)
            {
                TableData.Column column = table.Columns[i];
                cells.Add(new
                {
                    index = i,
                    columnHash = column.NameHash,
                    columnHashHex = string.Create(CultureInfo.InvariantCulture, $"0x{column.NameHash:X8}"),
                    type = column.Type.ToString(),
                    // Rows and columns are read from the same record, so a row shorter than the
                    // column list would mean a decode bug rather than odd data — but reporting null
                    // beats an index exception in a tool whose job is to explain a file.
                    value = i < row.Values.Count ? row.Values[i] : null,
                });
            }

            return ToolResult.Json(new { success = true, table = table.Name, rowIndex, cells });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // ── source resolution ──

    /// <summary>
    /// Turns whichever of the three source arguments was supplied into the tables it holds. Exactly
    /// one must be given: silently preferring one over another would let a caller who passed two by
    /// mistake read a different file than the one they are looking at.
    /// </summary>
    private static bool TryLoad(
        ArchiveService archives,
        string? sdsPath,
        string? tablePath,
        string? base64Data,
        int version,
        out List<TableData> tables,
        out string complaint)
    {
        tables = [];
        complaint = "";

        int supplied = (sdsPath is not null ? 1 : 0) + (tablePath is not null ? 1 : 0) + (base64Data is not null ? 1 : 0);
        if (supplied != 1)
        {
            complaint = "pass exactly one of sdsPath, tablePath or base64Data";
            return false;
        }

        if (sdsPath is not null)
        {
            CachedArchive cached = archives.Open(sdsPath);
            for (int i = 0; i < cached.Archive.Entries.Count; i++)
            {
                if (!SdsTools.TypeNameOf(cached, i).Equals("Table", StringComparison.Ordinal))
                {
                    continue;
                }

                ResourceEntry entry = cached.Archive.Entries[i];
                var resource = new TableResource();
                using var stream = new MemoryStream(entry.Data ?? []);
                resource.Deserialize(entry.Version, stream, cached.Archive.Endian);
                tables.AddRange(resource.Tables);
            }

            if (tables.Count == 0)
            {
                complaint = $"'{cached.Path}' holds no Table resources";
                return false;
            }
            return true;
        }

        if (tablePath is not null)
        {
            if (!File.Exists(tablePath))
            {
                complaint = $"no such file: {tablePath}";
                return false;
            }

            // An extracted .tbl is the entry's format version as a dword, then one table's body —
            // exactly what the archive's Table handler writes out.
            byte[] bytes = File.ReadAllBytes(tablePath);
            if (bytes.Length < sizeof(uint))
            {
                complaint = $"'{tablePath}' is too short to be a .tbl (no version dword)";
                return false;
            }

            using var stream = new MemoryStream(bytes);
            uint fileVersion = stream.ReadValueU32(Endian.Little);
            byte[] body = stream.ReadBytes(bytes.Length - sizeof(uint));
            tables.Add(TableResource.DecodeSingleTable((ushort)fileVersion, body));
            return true;
        }

        if (version <= 0)
        {
            complaint = "base64Data needs the resource's version — read it from get_resource_info";
            return false;
        }

        byte[] payload = Convert.FromBase64String(base64Data!);
        var container = new TableResource();
        using (var stream = new MemoryStream(payload))
        {
            container.Deserialize((ushort)version, stream, Endian.Little);
        }
        tables.AddRange(container.Tables);
        return true;
    }

    /// <summary>
    /// Picks the named table. With one table in the source the name is optional — the common case is
    /// a standalone .tbl, and making the caller name a file they already named is noise.
    /// </summary>
    private static bool TryPick(
        List<TableData> tables,
        string? tableName,
        out TableData table,
        out string complaint)
    {
        complaint = "";
        if (tableName is null)
        {
            if (tables.Count == 1)
            {
                table = tables[0];
                return true;
            }

            table = null!;
            complaint = "this source holds "
                + tables.Count.ToString(CultureInfo.InvariantCulture)
                + " tables — name one with tableName: "
                + string.Join(", ", tables.Select(t => t.Name));
            return false;
        }

        // Callers paste what list_tables printed, what the manifest called it, or a path off disk;
        // all three name the same table, so the comparison drops the folder and the extension.
        string wanted = Path.GetFileNameWithoutExtension(tableName.Replace('\\', '/'));
        foreach (TableData candidate in tables)
        {
            string name = Path.GetFileNameWithoutExtension(candidate.Name.Replace('\\', '/'));
            if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                table = candidate;
                return true;
            }
        }

        table = null!;
        complaint = $"no table called '{tableName}' — this source holds: "
            + string.Join(", ", tables.Select(t => t.Name));
        return false;
    }

    private static object Describe(TableData.Column column, int index) => new
    {
        index,
        nameHash = column.NameHash,
        nameHashHex = string.Create(CultureInfo.InvariantCulture, $"0x{column.NameHash:X8}"),
        type = column.Type.ToString(),
    };
}
