using System.Runtime.InteropServices;

namespace Illusion.Formats.Native;

/// <summary>
/// The raw P/Invoke surface of <c>Mafia.Formats.dll</c> — the native core the byte-level codecs
/// migrate into. Kept 1:1 with <c>src/Mafia.Formats/src/abi/mf_abi.h</c>; everything above this
/// level goes through <see cref="NativeFormats"/>.
/// </summary>
internal static partial class NativeMethods
{
    private const string DllName = "Mafia.Formats.dll";

    /// <summary>Success code of the boundary (<c>MF_OK</c>); failures are negative and explained
    /// by <c>mf_last_error</c>.</summary>
    internal const int Ok = 0;

    [LibraryImport(DllName, EntryPoint = "mf_abi_rev")]
    internal static partial uint AbiRev();

    [LibraryImport(DllName, EntryPoint = "mf_version")]
    internal static partial nint VersionPtr();

    [LibraryImport(DllName, EntryPoint = "mf_last_error")]
    internal static partial nint LastErrorPtr();

    [LibraryImport(DllName, EntryPoint = "mf_echo")]
    internal static unsafe partial int Echo(byte* data, ulong len, out MfRawBuffer buffer);

    [LibraryImport(DllName, EntryPoint = "mf_free")]
    internal static partial int Free(ref MfRawBuffer buffer);

    // ── Core primitives (the dual-path parity surface; see mf_abi.h) ──

    [LibraryImport(DllName, EntryPoint = "mf_core_fnv32")]
    internal static unsafe partial uint CoreFnv32(byte* data, ulong len);

    [LibraryImport(DllName, EntryPoint = "mf_core_fnv64")]
    internal static unsafe partial ulong CoreFnv64(byte* data, ulong len);

    [LibraryImport(DllName, EntryPoint = "mf_core_xtea_decrypt")]
    internal static unsafe partial int CoreXteaDecrypt(
        byte* data, ulong len, uint sum, uint delta, uint rounds, out MfRawBuffer buffer);

    [LibraryImport(DllName, EntryPoint = "mf_core_inflate")]
    internal static unsafe partial int CoreInflate(
        byte* src, ulong srcLen, ulong expectedLen, out MfRawBuffer buffer);

    [LibraryImport(DllName, EntryPoint = "mf_core_deflate")]
    internal static unsafe partial int CoreDeflate(byte* src, ulong srcLen, out MfRawBuffer buffer);

    [LibraryImport(DllName, EntryPoint = "mf_core_oodle_bind", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int CoreOodleBind(string path);

    [LibraryImport(DllName, EntryPoint = "mf_core_oodle_decompress")]
    internal static unsafe partial int CoreOodleDecompress(
        byte* src, ulong srcLen, ulong expectedLen, out MfRawBuffer buffer);

    // ── Generated-model wire (see Native/Model + tools/mf-schema-gen) ──

    [LibraryImport(DllName, EntryPoint = "mf_collision_model_echo")]
    internal static unsafe partial int CollisionModelEcho(
        byte* wire, ulong len, out MfRawBuffer buffer);

    /// <summary>A required dependency refused the input (mirrors <c>MF_ERR_STATE</c>) — e.g.
    /// a console-endian archive the managed reader keeps.</summary>
    internal const int ErrState = -5;

    /// <summary>The library name, for the per-area import classes (<c>Collisions/</c>,
    /// <c>Archive/</c> — one file of imports per format area, not one dump).</summary>
    internal const string LibraryName = DllName;
}
