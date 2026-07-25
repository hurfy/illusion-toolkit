/* Copyright (c) 2017 Rick (rick 'at' gibbed 'dot' us)
 * 
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 * 
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 * 
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 * 
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 * 
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System.Text;
using Illusion.Formats.IO;
using Illusion.Formats.Native.Resources;
using Wire = Illusion.Formats.Native.Model;

namespace Illusion.Formats.ResourceFormats;

internal class TableResource : IResourceFormat
{
    public List<TableData> Tables = new List<TableData>();

    // The row cell codec runs in the native core; cell presentation (code pages, the
    // color string) stays managed.

    public void Serialize(ushort version, Stream input, Endian endian) =>
        input.WriteBytes(NativeResources.TableEncode(version, ToWire(version)));

    public void Deserialize(ushort version, Stream input, Endian endian)
    {
        Wire.TableModel model = NativeResources.TableDecode(
            version, input.ReadBytes((int)(input.Length - input.Position)));
        this.Tables.Clear();
        foreach (Wire.TableEntry entry in model.Tables)
        {
            this.Tables.Add(FromWire(entry));
        }
    }

    /// <summary>One table outside the container — the shape an extracted .tbl file stores after
    /// its version dword (the codec is native).</summary>
    internal static TableData DecodeSingleTable(ushort version, byte[] body) =>
        FromWire(NativeResources.TableEntryDecode(version, body));

    internal static byte[] EncodeSingleTable(ushort version, TableData table) =>
        NativeResources.TableEntryEncode(version, ToWireEntry(version, table));

    // ── wire mapping ──

    private static Encoding CellEncoding(byte type) =>
        type == 32 ? Encoding.GetEncoding(1250) : EndianStreamExtensions.DefaultEncoding;

    private static TableData FromWire(Wire.TableEntry entry)
    {
        var table = new TableData
        {
            NameHash = entry.NameHash,
            Name = entry.Name,
            PatchedNameHash = entry.PatchedNameHash,
            PatchedName = entry.PatchedName,
            PatchedUnk1 = entry.PatchedUnk1,
            PatchedUnk2 = entry.PatchedUnk2,
            Unk1 = entry.Unk1,
            Unk2 = entry.Unk2,
            RowSizeOnDisk = entry.RowSize,
        };
        foreach (Wire.TableColumn column in entry.Columns)
        {
            table.Columns.Add(new TableData.Column
            {
                NameHash = column.NameHash,
                Type = (TableData.ColumnType)column.Type,
                Unknown2 = column.Unknown2,
                Unknown3 = column.Unknown3,
            });
        }
        foreach (Wire.TableRow row in entry.Rows)
        {
            var target = new TableData.Row();
            foreach (Wire.TableCell cell in row.Cells)
            {
                target.Values.Add(CellToValue(cell));
            }
            table.Rows.Add(target);
        }
        return table;
    }

    /// <summary>The managed presentation of one cell — same .NET types and formatting the
    /// pre-port reader produced (bool/float/int/uint/ulong, strings via the code pages,
    /// the color triple as a current-culture string).</summary>
    private static object CellToValue(Wire.TableCell cell) => (TableData.ColumnType)cell.Kind switch
    {
        TableData.ColumnType.Boolean => cell.U32Value != 0,
        TableData.ColumnType.Float32 => cell.F32X,
        TableData.ColumnType.Signed32 => (int)cell.U32Value,
        TableData.ColumnType.Unsigned32 or TableData.ColumnType.Flags32 => cell.U32Value,
        TableData.ColumnType.Hash64 => cell.U64Value,
        TableData.ColumnType.Color => string.Format("{0} {1} {2}", cell.F32X, cell.F32Y, cell.F32Z),
        _ => TrimmedString(cell),
    };

    private static string TrimmedString(Wire.TableCell cell)
    {
        int nul = Array.IndexOf(cell.Raw, (byte)0);
        int length = nul >= 0 ? nul : cell.Raw.Length;
        return CellEncoding(cell.Kind).GetString(cell.Raw, 0, length);
    }

    private Wire.TableModel ToWire(ushort version)
    {
        var model = new Wire.TableModel();
        foreach (TableData table in Tables)
        {
            model.Tables.Add(ToWireEntry(version, table));
        }
        return model;
    }

    private static Wire.TableEntry ToWireEntry(ushort version, TableData table)
    {
        var entry = new Wire.TableEntry
        {
            NameHash = table.NameHash,
            Name = table.Name,
            PatchedNameHash = table.PatchedNameHash,
            PatchedName = version >= 2 ? table.PatchedName : "",
            PatchedUnk1 = table.PatchedUnk1,
            PatchedUnk2 = table.PatchedUnk2,
            Unk1 = table.Unk1,
            Unk2 = table.Unk2,
            RowSize = table.RowSizeOnDisk,
        };
        foreach (TableData.Column column in table.Columns)
        {
            entry.Columns.Add(new Wire.TableColumn
            {
                NameHash = column.NameHash,
                Type = (byte)column.Type,
                Unknown2 = column.Unknown2,
                Unknown3 = column.Unknown3,
            });
        }
        foreach (TableData.Row row in table.Rows)
        {
            var wireRow = new Wire.TableRow();
            for (int i = 0; i < table.Columns.Count; i++)
            {
                wireRow.Cells.Add(ValueToCell(table.Columns[i].Type, row.Values[i]));
            }
            entry.Rows.Add(wireRow);
        }
        return entry;
    }

    private static Wire.TableCell ValueToCell(TableData.ColumnType type, object value)
    {
        var cell = new Wire.TableCell { Kind = (byte)type };
        switch (type)
        {
            case TableData.ColumnType.Boolean:
                cell.U32Value = Convert.ToUInt32(value);
                break;
            case TableData.ColumnType.Float32:
                cell.F32X = (float)value;
                break;
            case TableData.ColumnType.Signed32:
                cell.U32Value = (uint)(int)value;
                break;
            case TableData.ColumnType.Unsigned32:
            case TableData.ColumnType.Flags32:
                cell.U32Value = (uint)value;
                break;
            case TableData.ColumnType.Hash64:
                cell.U64Value = (ulong)value;
                break;
            case TableData.ColumnType.Color:
            {
                // Same parse the managed writer performs (current culture, split on spaces).
                string[] colors = ((string)value).Split([' '], StringSplitOptions.RemoveEmptyEntries);
                cell.F32X = float.Parse(colors[0]);
                cell.F32Y = float.Parse(colors[1]);
                cell.F32Z = float.Parse(colors[2]);
                break;
            }
            case TableData.ColumnType.String8:
                cell.Raw = FixedBytes(value, 8, CellEncoding((byte)type));
                break;
            case TableData.ColumnType.String16:
                cell.Raw = FixedBytes(value, 16, CellEncoding((byte)type));
                break;
            case TableData.ColumnType.String32:
                cell.Raw = FixedBytes(value, 32, CellEncoding((byte)type));
                break;
            case TableData.ColumnType.String64:
                cell.Raw = FixedBytes(value, 64, CellEncoding((byte)type));
                break;
            case TableData.ColumnType.Hash64AndString32:
                cell.Raw = FixedBytes(value, 32, CellEncoding((byte)type));
                break;
            default:
                throw new FormatException($"unhandled table column type {type}");
        }
        return cell;
    }

    /// <summary>Fixed-width cell bytes: encoded, then truncated or zero-padded to exactly
    /// <paramref name="size"/> — the managed WriteString(value, size) semantics.</summary>
    private static byte[] FixedBytes(object value, int size, Encoding encoding)
    {
        byte[] data = encoding.GetBytes(value.ToString()!);
        Array.Resize(ref data, size);
        return data;
    }
}
