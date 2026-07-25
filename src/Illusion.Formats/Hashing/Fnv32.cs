using System.Text;
using Illusion.Formats.IO;

namespace Illusion.Formats.Hashing;

/// <summary>FNV-1 32-bit (multiply-then-xor) — the checksum the SDS "safe block" framing uses.
/// Strings hash through Windows-1252, matching the game's tooling.</summary>
public static class Fnv32
{
    public const uint Initial = 0x811C9DC5;
    private const uint Prime = 0x1000193;

    public static uint Hash(string? value) => Hash(value, EndianStreamExtensions.DefaultEncoding);

    public static uint Hash(string? value, Encoding encoding)
    {
        if (value == null)
        {
            return Initial;
        }
        byte[] bytes = encoding.GetBytes(value);
        return Hash(bytes, 0, bytes.Length);
    }

    public static uint Hash(byte[]? buffer, int offset, int count) => Hash(buffer, offset, count, Initial);

    public static uint Hash(byte[]? buffer, int offset, int count, uint hash)
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
