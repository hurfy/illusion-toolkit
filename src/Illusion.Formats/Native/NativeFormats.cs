using System.Runtime.InteropServices;

namespace Illusion.Formats.Native;

/// <summary>
/// The managed facade over the native core. P0 exposes only the handshake surface (version and
/// ABI revision) plus the buffer-protocol smoke path; real format entry points accrue here phase
/// by phase, starting with collisions.
/// </summary>
internal static class NativeFormats
{
    /// <summary>The boundary revision this facade was written against; must equal the DLL's
    /// <c>mf_abi_rev()</c> (mirrors <c>MF_ABI_REV</c>).</summary>
    internal const uint ExpectedAbiRev = 32;

    /// <summary>The native core version this tree builds (mirrors the CMake project version).</summary>
    internal const string ExpectedVersion = "0.1.0";

    internal static uint AbiRev => NativeMethods.AbiRev();

    internal static string Version => Marshal.PtrToStringUTF8(NativeMethods.VersionPtr()) ?? "";

    /// <summary>The failure text of the most recent native call on this thread ("" after a success).</summary>
    internal static string LastError => Marshal.PtrToStringUTF8(NativeMethods.LastErrorPtr()) ?? "";

    /// <summary>Round-trips bytes through the native allocator (the buffer-protocol smoke path).</summary>
    internal static unsafe (int Status, MfBuffer Buffer) Echo(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            int status = NativeMethods.Echo(p, (ulong)data.Length, out MfRawBuffer raw);
            return (status, new MfBuffer(raw));
        }
    }

    internal static unsafe uint Fnv32(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            return NativeMethods.CoreFnv32(p, (ulong)data.Length);
        }
    }

    internal static unsafe ulong Fnv64(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
        {
            return NativeMethods.CoreFnv64(p, (ulong)data.Length);
        }
    }

    /// <summary>Decrypts every full 8-byte XTEA group of a copy of <paramref name="data"/>
    /// (the partial tail rides through untouched, as the game's reader leaves it).</summary>
    internal static unsafe (int Status, MfBuffer Buffer) XteaDecrypt(
        ReadOnlySpan<byte> data, uint sum, uint delta, uint rounds)
    {
        fixed (byte* p = data)
        {
            int status = NativeMethods.CoreXteaDecrypt(
                p, (ulong)data.Length, sum, delta, rounds, out MfRawBuffer raw);
            return (status, new MfBuffer(raw));
        }
    }

    internal static unsafe (int Status, MfBuffer Buffer) Inflate(
        ReadOnlySpan<byte> src, ulong expectedLength)
    {
        fixed (byte* p = src)
        {
            int status = NativeMethods.CoreInflate(
                p, (ulong)src.Length, expectedLength, out MfRawBuffer raw);
            return (status, new MfBuffer(raw));
        }
    }

    internal static unsafe (int Status, MfBuffer Buffer) Deflate(ReadOnlySpan<byte> src)
    {
        fixed (byte* p = src)
        {
            int status = NativeMethods.CoreDeflate(p, (ulong)src.Length, out MfRawBuffer raw);
            return (status, new MfBuffer(raw));
        }
    }

    /// <summary>Binds the native oodle shim to the game's own <c>oo2core</c> DLL (idempotent).</summary>
    internal static int OodleBind(string path) => NativeMethods.CoreOodleBind(path);

    internal static unsafe (int Status, MfBuffer Buffer) OodleDecompress(
        ReadOnlySpan<byte> src, ulong expectedLength)
    {
        fixed (byte* p = src)
        {
            int status = NativeMethods.CoreOodleDecompress(
                p, (ulong)src.Length, expectedLength, out MfRawBuffer raw);
            return (status, new MfBuffer(raw));
        }
    }

    /// <summary>Parses a collision-model wire image natively and serializes it back —
    /// the generated-code cycle proof (trailing bytes are refused).</summary>
    internal static unsafe (int Status, MfBuffer Buffer) CollisionModelEcho(ReadOnlySpan<byte> wire)
    {
        fixed (byte* p = wire)
        {
            int status = NativeMethods.CollisionModelEcho(p, (ulong)wire.Length, out MfRawBuffer raw);
            return (status, new MfBuffer(raw));
        }
    }
}
