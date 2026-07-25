using System.Numerics;
using System.Text;

namespace Illusion.Formats.Native.Model;

/// <summary>
/// Hand-written support for the generated model io (GeneratedModel.g.cs): the wire
/// helpers the generator emits calls to. Little-endian throughout (BinaryReader's
/// native shape); floats travel and compare as raw bits so NaN payloads survive.
/// </summary>
internal static class Wire
{
    /// <summary>A hostile count cannot exceed the bytes that remain (every element is at
    /// least one byte), so a corrupt stream can never demand a giant list allocation.</summary>
    internal static uint ReadCount(BinaryReader reader)
    {
        uint count = reader.ReadUInt32();
        long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        if (count > remaining)
        {
            throw new InvalidDataException($"list count {count} exceeds the remaining {remaining} bytes");
        }
        return count;
    }

    internal static void WriteCount(BinaryWriter writer, int count) => writer.Write((uint)count);

    internal static byte[] ReadBytes(BinaryReader reader)
    {
        uint length = ReadCount(reader);
        if (length == 0)
        {
            return [];
        }
        byte[] bytes = reader.ReadBytes(checked((int)length));
        return bytes.Length == length
            ? bytes
            : throw new EndOfStreamException($"byte payload truncated at {bytes.Length}/{length}");
    }

    internal static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    internal static string ReadString(BinaryReader reader) =>
        Encoding.UTF8.GetString(ReadBytes(reader));

    internal static void WriteString(BinaryWriter writer, string value) =>
        WriteBytes(writer, Encoding.UTF8.GetBytes(value));

    internal static Vector3 ReadVector3(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    internal static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    internal static void DiffVector3(string path, Vector3 a, Vector3 b, List<string> diffs)
    {
        if (BitConverter.SingleToUInt32Bits(a.X) != BitConverter.SingleToUInt32Bits(b.X)
            || BitConverter.SingleToUInt32Bits(a.Y) != BitConverter.SingleToUInt32Bits(b.Y)
            || BitConverter.SingleToUInt32Bits(a.Z) != BitConverter.SingleToUInt32Bits(b.Z))
        {
            diffs.Add($"{path}: {a} vs {b}");
        }
    }

    internal static void DiffBytes(string path, byte[] a, byte[] b, List<string> diffs)
    {
        if (a.Length != b.Length)
        {
            diffs.Add($"{path}: length {a.Length} vs {b.Length}");
            return;
        }
        if (!a.AsSpan().SequenceEqual(b))
        {
            diffs.Add($"{path}: contents differ");
        }
    }
}
