using System.Runtime.InteropServices;

namespace Illusion.Formats.Native.Materials;

/// <summary>The material-library import surface (P5). Kept 1:1 with the materials
/// section of <c>mf_abi.h</c>.</summary>
internal static partial class MtlNativeMethods
{
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_mtl_load")]
    internal static unsafe partial int MtlLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_mtl_save")]
    internal static unsafe partial int MtlSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);
}
