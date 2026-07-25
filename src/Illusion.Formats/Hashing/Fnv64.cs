using System.Text;
using Illusion.Formats.IO;

namespace Illusion.Formats.Hashing;

/// <summary>FNV-1 64-bit (multiply-then-xor) — the name hash used throughout Mafia II's formats
/// (frame names, texture names, actor references). Strings hash through Windows-1252.</summary>
public static class Fnv64
{
    public const ulong Initial = 0xCBF29CE484222325;
    private const ulong Prime = 0x00000100000001B3;

    public static ulong Hash(string? value) => Hash(value, EndianStreamExtensions.DefaultEncoding);

    public static ulong Hash(string? value, Encoding encoding)
    {
        if (value == null)
        {
            return Initial;
        }
        byte[] bytes = encoding.GetBytes(value);
        return Hash(bytes, 0, bytes.Length);
    }

    public static ulong Hash(byte[]? buffer, int offset, int count) => Hash(buffer, offset, count, Initial);

    public static ulong Hash(byte[]? buffer, int offset, int count, ulong hash)
    {
        if (buffer == null)
        {
            return hash;
        }
        for (int i = offset; i < offset + count; i++)
        {
            hash *= Prime;
            hash ^= buffer[i];
        }
        return hash;
    }
}
