using System.Buffers.Binary;
using System.Text;

namespace Illusion.Formats.IO;

/// <summary>
/// Endian-aware primitive readers/writers over <see cref="Stream"/> — the in-source replacement for the
/// Gibbed.IO binary this library used to ship. The member names and semantics match that library exactly
/// (the whole format layer was written against them); the roundtrip probe pins behavioral equivalence.
/// </summary>
internal static class EndianStreamExtensions
{
    private static Encoding? _defaultEncoding;

    /// <summary>Encoding for the string helpers when none is passed. Mafia's tools wrote strings as
    /// Windows-1252 (resolved lazily so the code-page provider is registered by then — see ModuleInit).</summary>
    public static Encoding DefaultEncoding
    {
        get => _defaultEncoding ??= Encoding.GetEncoding(1252);
        set => _defaultEncoding = value;
    }

    private static bool ShouldSwap(Endian endian) =>
        endian == Endian.Big ? BitConverter.IsLittleEndian : !BitConverter.IsLittleEndian;

    // ── Raw bytes ──

    public static byte[] ReadBytes(this Stream stream, int count)
    {
        byte[] data = new byte[count];
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(data, total, count - total);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }
            total += read;
        }
        return data;
    }

    public static byte[] ReadBytes(this Stream stream, uint count) => stream.ReadBytes((int)count);

    public static void WriteBytes(this Stream stream, byte[] data) => stream.Write(data, 0, data.Length);

    // ── Unsigned / signed integers ──

    public static byte ReadValueU8(this Stream stream)
    {
        int value = stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException();
        }
        return (byte)value;
    }

    public static void WriteValueU8(this Stream stream, byte value) => stream.WriteByte(value);

    public static ushort ReadValueU16(this Stream stream, Endian endian = Endian.Little)
    {
        Span<byte> b = stackalloc byte[2];
        stream.ReadExactly(b);
        return ShouldSwap(endian)
            ? BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt16(b))
            : BitConverter.ToUInt16(b);
    }

    public static void WriteValueU16(this Stream stream, ushort value, Endian endian = Endian.Little)
    {
        Span<byte> b = stackalloc byte[2];
        BitConverter.TryWriteBytes(b, ShouldSwap(endian) ? BinaryPrimitives.ReverseEndianness(value) : value);
        stream.Write(b);
    }

    public static short ReadValueS16(this Stream stream, Endian endian = Endian.Little) =>
        (short)stream.ReadValueU16(endian);

    public static void WriteValueS16(this Stream stream, short value, Endian endian = Endian.Little) =>
        stream.WriteValueU16((ushort)value, endian);

    public static uint ReadValueU32(this Stream stream, Endian endian = Endian.Little)
    {
        Span<byte> b = stackalloc byte[4];
        stream.ReadExactly(b);
        return ShouldSwap(endian)
            ? BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(b))
            : BitConverter.ToUInt32(b);
    }

    public static void WriteValueU32(this Stream stream, uint value, Endian endian = Endian.Little)
    {
        Span<byte> b = stackalloc byte[4];
        BitConverter.TryWriteBytes(b, ShouldSwap(endian) ? BinaryPrimitives.ReverseEndianness(value) : value);
        stream.Write(b);
    }

    public static int ReadValueS32(this Stream stream, Endian endian = Endian.Little) =>
        (int)stream.ReadValueU32(endian);

    public static void WriteValueS32(this Stream stream, int value, Endian endian = Endian.Little) =>
        stream.WriteValueU32((uint)value, endian);

    public static ulong ReadValueU64(this Stream stream, Endian endian = Endian.Little)
    {
        Span<byte> b = stackalloc byte[8];
        stream.ReadExactly(b);
        return ShouldSwap(endian)
            ? BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt64(b))
            : BitConverter.ToUInt64(b);
    }

    public static void WriteValueU64(this Stream stream, ulong value, Endian endian = Endian.Little)
    {
        Span<byte> b = stackalloc byte[8];
        BitConverter.TryWriteBytes(b, ShouldSwap(endian) ? BinaryPrimitives.ReverseEndianness(value) : value);
        stream.Write(b);
    }

    // ── Floats ──

    public static float ReadValueF32(this Stream stream, Endian endian = Endian.Little) =>
        BitConverter.UInt32BitsToSingle(stream.ReadValueU32(endian));

    public static void WriteValueF32(this Stream stream, float value, Endian endian = Endian.Little) =>
        stream.WriteValueU32(BitConverter.SingleToUInt32Bits(value), endian);

    // ── Strings (raw bytes, no length prefix / terminator — that is the caller's framing) ──

    public static string ReadString(this Stream stream, int size) =>
        stream.ReadString(size, false, DefaultEncoding);

    public static string ReadString(this Stream stream, uint size) =>
        stream.ReadString((int)size, false, DefaultEncoding);

    public static string ReadString(this Stream stream, int size, bool trailingNull) =>
        stream.ReadString(size, trailingNull, DefaultEncoding);

    public static string ReadString(this Stream stream, uint size, bool trailingNull) =>
        stream.ReadString((int)size, trailingNull, DefaultEncoding);

    public static string ReadString(this Stream stream, int size, Encoding encoding) =>
        stream.ReadString(size, false, encoding);

    public static string ReadString(this Stream stream, uint size, Encoding encoding) =>
        stream.ReadString((int)size, false, encoding);

    public static string ReadString(this Stream stream, uint size, bool trailingNull, Encoding encoding) =>
        stream.ReadString((int)size, trailingNull, encoding);

    public static string ReadString(this Stream stream, int size, bool trailingNull, Encoding encoding)
    {
        string text = encoding.GetString(stream.ReadBytes(size));
        if (trailingNull)
        {
            int nul = text.IndexOf('\0');
            if (nul >= 0)
            {
                text = text[..nul];
            }
        }
        return text;
    }

    public static void WriteString(this Stream stream, string value) =>
        stream.WriteString(value, DefaultEncoding);

    public static void WriteString(this Stream stream, string value, Encoding encoding) =>
        stream.WriteBytes(encoding.GetBytes(value));

    /// <summary>Fixed-width string cell: encoded bytes truncated or zero-padded to exactly
    /// <paramref name="size"/> bytes (table columns are stored this way).</summary>
    public static void WriteString(this Stream stream, string value, int size) =>
        stream.WriteString(value, size, DefaultEncoding);

    public static void WriteString(this Stream stream, string value, uint size) =>
        stream.WriteString(value, (int)size, DefaultEncoding);

    public static void WriteString(this Stream stream, string value, uint size, Encoding encoding) =>
        stream.WriteString(value, (int)size, encoding);

    public static void WriteString(this Stream stream, string value, int size, Encoding encoding)
    {
        byte[] data = encoding.GetBytes(value);
        Array.Resize(ref data, size);
        stream.Write(data, 0, size);
    }

    /// <summary>Reads bytes up to (and consuming) the NUL terminator. Single-byte scan — safe for the
    /// single-byte code pages and UTF-8 these formats store.</summary>
    public static string ReadStringZ(this Stream stream) => stream.ReadStringZ(DefaultEncoding);

    public static string ReadStringZ(this Stream stream, Encoding encoding)
    {
        using var buffer = new MemoryStream();
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException();
            }
            if (b == 0)
            {
                break;
            }
            buffer.WriteByte((byte)b);
        }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public static void WriteStringZ(this Stream stream, string value) =>
        stream.WriteStringZ(value, DefaultEncoding);

    public static void WriteStringZ(this Stream stream, string value, Encoding encoding)
    {
        stream.WriteBytes(encoding.GetBytes(value));
        stream.WriteByte(0);
    }

    // ── Stream-to-stream ──

    /// <summary>Reads exactly <paramref name="size"/> bytes into a fresh seekable memory stream.</summary>
    public static MemoryStream ReadToMemoryStream(this Stream stream, long size)
    {
        byte[] data = stream.ReadBytes((int)size);
        return new MemoryStream(data, 0, data.Length, writable: false, publiclyVisible: true);
    }

    /// <summary>Copies exactly <paramref name="size"/> bytes from <paramref name="input"/>.</summary>
    public static void WriteFromStream(this Stream stream, Stream input, long size)
    {
        byte[] buffer = new byte[81920];
        long remaining = size;
        while (remaining > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }
            stream.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    // ── Byte-order helpers on values ──

    public static ushort Swap(this ushort value) => BinaryPrimitives.ReverseEndianness(value);
    public static short Swap(this short value) => BinaryPrimitives.ReverseEndianness(value);
    public static uint Swap(this uint value) => BinaryPrimitives.ReverseEndianness(value);
    public static int Swap(this int value) => BinaryPrimitives.ReverseEndianness(value);
    public static ulong Swap(this ulong value) => BinaryPrimitives.ReverseEndianness(value);

    public static float Swap(this float value) =>
        BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReverseEndianness(BitConverter.SingleToUInt32Bits(value)));

    /// <summary>The value as it reads when the bytes are interpreted big-endian on this host.</summary>
    public static uint BigEndian(this uint value) => BitConverter.IsLittleEndian ? value.Swap() : value;

    /// <summary>The value as it reads when the bytes are interpreted little-endian on this host.</summary>
    public static uint LittleEndian(this uint value) => BitConverter.IsLittleEndian ? value : value.Swap();
}
