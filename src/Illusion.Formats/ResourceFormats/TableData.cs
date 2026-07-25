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

namespace Illusion.Formats.ResourceFormats;

internal class TableData
{
    public ulong NameHash;
    public string Name = null!;
    public uint Unk1;
    public uint Unk2;
    public byte[]? Data;

    // TODO: Only present if Version >= 2??
    public ulong PatchedNameHash;
    public string PatchedName = null!;
    public uint PatchedUnk1;
    public uint PatchedUnk2;

    public List<Row> Rows = new List<Row>();
    public List<Column> Columns = new List<Column>();

    // Row size as read from disk — reused when serializing a zero-row table (see CalculateRowSize).
    private uint _rowSizeOnDisk;

    /// <summary>The on-disk row stride (settable by the native decode path, which carries it
    /// through the neutral model instead of the raw row blob).</summary>
    internal uint RowSizeOnDisk
    {
        get => _rowSizeOnDisk;
        set => _rowSizeOnDisk = value;
    }

    public override string ToString()
    {
        return this.Name;
    }

    public bool Validate()
    {
        bool bIsTableValid = true;

        for (int i = 0; i < Rows.Count; i++)
        {
            for (int x = 0; x < Columns.Count; x++)
            {
                Column column = Columns[x];
                object value = Rows[i].Values[x];
                bool bIsCellValid = true;

                string Message = "";

                switch (column.Type)
                {
                    case ColumnType.Boolean:
                        ConvertToType<bool>(value, ref bIsCellValid);
                        Message = string.Format("Failed to convert {0} to 'Boolean'", value.GetType().Name);
                        break;
                    case ColumnType.Float32:
                        ConvertToType<float>(value, ref bIsCellValid);
                        Message = string.Format("Failed to convert {0} to 'Float/Single'", value.GetType().Name);
                        break;
                    case ColumnType.Signed32:
                        ConvertToType<int>(value, ref bIsCellValid);
                        Message = string.Format("Failed to convert {0} to 'Int32'", value.GetType().Name);
                        break;
                    case ColumnType.Unsigned32:
                    case ColumnType.Flags32:
                        ConvertToType<uint>(value, ref bIsCellValid);
                        Message = string.Format("Failed to convert {0} to 'UInt32'", value.GetType().Name);
                        break;
                    case ColumnType.Hash64:
                        ConvertToType<ulong>(value, ref bIsCellValid);
                        Message = string.Format("Failed to convert {0} to 'ULong64'", value.GetType().Name);
                        break;
                    case ColumnType.String8:
                        bIsCellValid = DoesFitInString(value.ToString()!, 8);
                        Message = string.Format("'{0}' exceeds string limit of: 8 characters", value.ToString());
                        break;
                    case ColumnType.String16:
                        bIsCellValid = DoesFitInString(value.ToString()!, 16);
                        Message = string.Format("'{0}' exceeds string limit of: 16 characters", value.ToString());
                        break;
                    case ColumnType.String32:
                    case ColumnType.Hash64AndString32:
                        bIsCellValid = DoesFitInString(value.ToString()!, 32);
                        Message = string.Format("'{0}' exceeds string limit of: 32 characters", value.ToString());
                        break;
                    case ColumnType.String64:
                        bIsCellValid = DoesFitInString(value.ToString()!, 64);
                        Message = string.Format("'{0}' exceeds string limit of: 64 characters", value.ToString());
                        break;
                    case ColumnType.Color:
                        string[] colors = (Rows[i].Values[x] as string)!.Split(new char[1] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (colors.Length == 3)
                        {
                            foreach (var colour in colors)
                            {
                                ConvertToType<float>(colour, ref bIsCellValid);
                            }
                        }
                        break;
                    default:
                        throw new FormatException();
                }

                if (!bIsCellValid)
                {
                    string ErrorMessage = string.Format("Error validating cell X: {0} Y: {1} \nError Message: {2}", i, x, Message);
                    System.Diagnostics.Debug.WriteLine(ErrorMessage);
                }

                // TODO: Do we want to do this? is it better to iterate through them all and tell them 
                // which ones failed validation?

                // If cell is invalid break the validation, as we know the table contains invalid data
                bIsTableValid = bIsCellValid;
                if (!bIsTableValid)
                {
                    return bIsTableValid;
                }
            }
        }

        return bIsTableValid;
    }

    private T ConvertToType<T>(object ObjectToConvert, ref bool bIsValid)
    {
        // Get type we can to cast to.
        T TypeToCast = Activator.CreateInstance<T>();
        Type TypeOfObject = TypeToCast!.GetType();

        // Try and attempt to cast
        T Output = Activator.CreateInstance<T>();

        try
        {
            Output = (T)Convert.ChangeType(ObjectToConvert, TypeOfObject);
        }
        catch (Exception)
        {
            Type TypeOfPassedObject = ObjectToConvert.GetType();
            string ErrorMessage = string.Format("Failed to cast object of type {0} to {1}", TypeOfObject.Name, TypeOfPassedObject.Name);
            System.Diagnostics.Debug.WriteLine(ErrorMessage);
            bIsValid = false;
        }

        return Output;
    }

    private bool DoesFitInString(string Text, int Size)
    {
        if (Text.Length <= Size)
        {
            return true;
        }

        return false;
    }

    public class Column
    {
        public uint NameHash;
        public ColumnType Type;
        public byte Unknown2;
        public ushort Unknown3;

        public override string ToString()
        {
            return string.Format("{0:X8} : {1} ({2}, {3})",
                                 this.NameHash,
                                 this.Type,
                                 this.Unknown2,
                                 this.Unknown3);
        }
    }

    public class Row
    {
        public List<object> Values = new List<object>();

        public override string ToString()
        {
            var values = new string[this.Values.Count];
            for (int i = 0; i < this.Values.Count; i++)
            {
                values[i] = this.Values[i].ToString()!;
            }
            return string.Join(", ", values);
        }
    }

    public enum ColumnType : byte
    {
        Boolean = 1,
        Float32 = 2,
        Signed32 = 3,
        Unsigned32 = 4,
        Flags32 = 5,
        Hash64 = 6,
        String8 = 8,
        String16 = 16,
        String32 = 32,
        String64 = 64,
        Color = 66,
        Hash64AndString32 = 132,
    }

    public static Type GetValueTypeForColumnType(ColumnType type)
    {
        switch (type)
        {
            case ColumnType.Boolean:
            {
                return typeof(bool);
            }

            case ColumnType.Float32:
            {
                return typeof(float);
            }

            case ColumnType.Signed32:
            {
                return typeof(int);
            }

            case ColumnType.Unsigned32:
            {
                return typeof(uint);
            }

            case ColumnType.Flags32:
            {
                return typeof(uint);
            }

            case ColumnType.Hash64:
            {
                return typeof(ulong);
            }

            case ColumnType.String8:
            case ColumnType.String16:
            case ColumnType.String32:
            case ColumnType.String64:
            case ColumnType.Color:
            {
                return typeof(string);
            }

            case ColumnType.Hash64AndString32:
            {
                return typeof(string);
            }
        }

        throw new ArgumentException("unhandled type", "type");
    }

}
