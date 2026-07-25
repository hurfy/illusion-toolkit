using System.Runtime.InteropServices;

namespace Illusion.Formats.Native.Collisions;

/// <summary>The <c>mf_col_*</c> import surface (collision: .col + cooked NXS capsules).
/// Kept 1:1 with the collision section of <c>mf_abi.h</c>.</summary>
internal static partial class ColNativeMethods
{
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_col_load")]
    internal static unsafe partial int Load(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_col_save")]
    internal static unsafe partial int Save(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_col_decode_mesh")]
    internal static unsafe partial int DecodeMesh(byte* cooked, ulong len, out MfRawBuffer decodedWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_col_validate_tail")]
    internal static unsafe partial int ValidateTail(byte* cooked, ulong len, out int trailing);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_col_mesh_layout")]
    internal static unsafe partial int MeshLayout(byte* cooked, ulong len, out MfRawBuffer layoutWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_col_tail_supported")]
    internal static unsafe partial int TailSupported(byte* cooked, ulong len, out int supported);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_col_scale_mesh")]
    internal static unsafe partial int ScaleMesh(
        byte* cooked, ulong len, float sx, float sy, float sz, out MfRawBuffer buffer);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_col_widen_mesh")]
    internal static unsafe partial int WidenMesh(byte* cooked, ulong len, out MfRawBuffer buffer);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_col_build_sections")]
    internal static unsafe partial int BuildSections(
        uint* triangleIndices, ulong indexCount, ushort* surfaceIds, ulong surfaceCount,
        out MfRawBuffer planWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_col_cooker_bin")]
    internal static unsafe partial int CookerBin(
        float* positionsXyz, ulong vertexCount, uint* triangleIndices, ulong indexCount,
        ushort* surfaceIds, ulong surfaceCount, out MfRawBuffer buffer);
}
