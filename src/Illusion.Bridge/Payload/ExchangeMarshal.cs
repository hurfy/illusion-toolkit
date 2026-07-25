using System.Numerics;
using System.Runtime.InteropServices;

namespace Illusion.Bridge.Payload;

/// <summary>
/// Wire conversions shared by every payload codec: row-major matrix ⇄ float[16] and blittable array ⇄
/// little-endian bytes. Kept in one place so the mesh and collision codecs cannot drift into two
/// subtly different encodings of the same container fields.
/// </summary>
internal static class ExchangeMarshal
{
    public static float[] ToFloats(Matrix4x4 m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    };

    public static Matrix4x4 FromFloats(float[]? f) => f is { Length: 16 }
        ? new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7],
                        f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15])
        : Matrix4x4.Identity;

    // The toolkit only runs on little-endian x64, so a straight memory copy IS the wire format.
    public static byte[] ToBytes<T>(T[] source) where T : unmanaged =>
        MemoryMarshal.AsBytes(source.AsSpan()).ToArray();

    public static T[] FromBytes<T>(ExchangeBlock block) where T : unmanaged =>
        MemoryMarshal.Cast<byte, T>(block.Data.AsSpan()).ToArray();
}
