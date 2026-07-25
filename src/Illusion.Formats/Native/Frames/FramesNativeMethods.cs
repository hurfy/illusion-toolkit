using System.Runtime.InteropServices;

namespace Illusion.Formats.Native.Frames;

/// <summary>The frames import surface, growing slice by slice through P4 (the
/// FrameNameTable first). Kept 1:1 with the frames section of <c>mf_abi.h</c>.</summary>
internal static partial class FramesNativeMethods
{
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_fnt_load")]
    internal static unsafe partial int NameTableLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_fnt_save")]
    internal static unsafe partial int NameTableSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_fr_load")]
    internal static unsafe partial int FrameResourceLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_fr_save")]
    internal static unsafe partial int FrameResourceSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_ibp_load")]
    internal static unsafe partial int IndexPoolLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_ibp_save")]
    internal static unsafe partial int IndexPoolSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_vbp_load")]
    internal static unsafe partial int VertexPoolLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_vbp_save")]
    internal static unsafe partial int VertexPoolSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_frames_rebuild_lod")]
    internal static unsafe partial int RebuildLod(byte* requestWire, ulong len, out MfRawBuffer resultWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_vtx_layout")]
    internal static unsafe partial int VertexLayout(uint declaration, out MfRawBuffer layoutWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_vtx_decompress")]
    internal static unsafe partial int VertexDecompress(
        byte* buffer, ulong len, uint declaration, ulong vertexCount,
        float offsetX, float offsetY, float offsetZ, float scale, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_vtx_decompress_channels")]
    internal static unsafe partial int VertexDecompressChannels(
        byte* buffer, ulong len, uint declaration, ulong vertexCount,
        float offsetX, float offsetY, float offsetZ, float scale,
        float* positions, float* normals, float* uv0, float* tangents, float* binormals);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_vtx_compress")]
    internal static unsafe partial int VertexCompress(
        byte* baseBytes, ulong baseLen, byte* modelWire, ulong wireLen, uint declaration,
        float offsetX, float offsetY, float offsetZ, float scale, out MfRawBuffer buffer);
}
