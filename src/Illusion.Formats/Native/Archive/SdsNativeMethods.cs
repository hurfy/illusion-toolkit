using System.Runtime.InteropServices;

namespace Illusion.Formats.Native.Archive;

/// <summary>The <c>mf_sds_*</c> import surface (the v19 SDS container).
/// Kept 1:1 with the archive section of <c>mf_abi.h</c>.</summary>
internal static partial class SdsNativeMethods
{
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_sds_load")]
    internal static unsafe partial int Load(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_sds_unwrap")]
    internal static unsafe partial int Unwrap(byte* file, ulong len, out MfRawBuffer payload);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_sds_save")]
    internal static unsafe partial int Save(
        byte* modelWire, ulong len, byte compress, float compressionRatio, out MfRawBuffer fileBytes);
}
