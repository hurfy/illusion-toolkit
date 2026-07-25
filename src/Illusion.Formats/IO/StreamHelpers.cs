namespace Illusion.Formats.IO;

/// <summary>
/// Numeric read/write extensions over a raw <see cref="Stream"/> — what BinaryReader/BinaryWriter
/// give you, minus the wrapper object, for the places that hand buffers to the native core. The
/// game's own encodings (length-prefixed strings, packed values) are the core's business.
/// </summary>
internal static class StreamHelpers
{
    // ReadBytes lives in EndianStreamExtensions (exact-read + EOF check) — the short-read-prone
    // legacy copy was dropped when both classes joined this namespace.
    public static float ReadSingle(this Stream stream, bool bigEndian)
    {
        byte[] data = new byte[4];
        stream.ReadExactly(data, 0, 4);
        if (bigEndian) Array.Reverse(data);
        return BitConverter.ToSingle(data, 0);
    }
    public static int ReadInt32(this Stream stream, bool bigEndian)
    {
        byte[] data = new byte[sizeof(int)];
        stream.ReadExactly(data, 0, 4);
        if (bigEndian) Array.Reverse(data);
        return BitConverter.ToInt32(data, 0);
    }
    public static uint ReadUInt32(this Stream stream, bool bigEndian)
    {
        byte[] data = new byte[sizeof(int)];
        stream.ReadExactly(data, 0, 4);
        if (bigEndian) Array.Reverse(data);
        return BitConverter.ToUInt32(data, 0);
    }
    public static short ReadInt16(this Stream stream, bool bigEndian)
    {
        byte[] data = new byte[sizeof(short)];
        stream.ReadExactly(data, 0, 2);
        if (bigEndian) Array.Reverse(data);
        return BitConverter.ToInt16(data, 0);
    }
    public static ushort ReadUInt16(this Stream stream, bool bigEndian)
    {
        byte[] data = new byte[sizeof(short)];
        stream.ReadExactly(data, 0, 2);
        if (bigEndian) Array.Reverse(data);
        return BitConverter.ToUInt16(data, 0);
    }
    public static ulong ReadUInt64(this Stream stream, bool bigEndian)
    {
        byte[] data = new byte[sizeof(long)];
        stream.ReadExactly(data, 0, 8);
        if (bigEndian) Array.Reverse(data);
        return BitConverter.ToUInt64(data, 0);
    }
    public static long ReadInt64(this Stream stream, bool bigEndian)
    {
        byte[] data = new byte[sizeof(long)];
        stream.ReadExactly(data, 0, 8);
        if (bigEndian) Array.Reverse(data);
        return BitConverter.ToInt64(data, 0);
    }

    public static void Write(this Stream stream, byte[] data)
    {
        stream.Write(data, 0, data.Length);
    }

    public static void Write(this Stream stream, float value, bool bigEndian)
    {
        byte[] data = BitConverter.GetBytes(value);
        if (bigEndian) Array.Reverse(data);
        stream.Write(data);
    }
    public static void Write(this Stream stream, int value, bool bigEndian)
    {
        byte[] data = BitConverter.GetBytes(value);
        if (bigEndian) Array.Reverse(data);
        stream.Write(data);
    }
    public static void Write(this Stream stream, uint value, bool bigEndian)
    {
        byte[] data = BitConverter.GetBytes(value);
        if (bigEndian) Array.Reverse(data);
        stream.Write(data);
    }
    public static void Write(this Stream stream, short value, bool bigEndian)
    {
        byte[] data = BitConverter.GetBytes(value);
        if (bigEndian) Array.Reverse(data);
        stream.Write(data);
    }
    public static void Write(this Stream stream, ushort value, bool bigEndian)
    {
        byte[] data = BitConverter.GetBytes(value);
        if (bigEndian) Array.Reverse(data);
        stream.Write(data);
    }
    public static void Write(this Stream stream, long value, bool bigEndian)
    {
        byte[] data = BitConverter.GetBytes(value);
        if (bigEndian) Array.Reverse(data);
        stream.Write(data);
    }
    public static void Write(this Stream stream, ulong value, bool bigEndian)
    {
        byte[] data = BitConverter.GetBytes(value);
        if (bigEndian) Array.Reverse(data);
        stream.Write(data);
    }
}
