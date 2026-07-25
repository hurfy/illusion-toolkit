using System.Runtime.InteropServices;

namespace Illusion.Formats.Native.Resources;

/// <summary>The <c>mf_res_*</c> / <c>mf_sds_patch_load</c> import surface (the typed
/// envelopes inside SDS entries). Kept 1:1 with the wrapper section of <c>mf_abi.h</c>.</summary>
internal static partial class ResNativeMethods
{
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_texture_unwrap")]
    internal static unsafe partial int TextureUnwrap(
        ushort version, byte isMip, byte* data, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_texture_wrap")]
    internal static unsafe partial int TextureWrap(
        ushort version, byte isMip, byte* modelWire, ulong len, out MfRawBuffer buffer);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_sound_unwrap")]
    internal static unsafe partial int SoundUnwrap(
        ushort version, byte* data, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_sound_wrap")]
    internal static unsafe partial int SoundWrap(
        ushort version, byte* modelWire, ulong len, out MfRawBuffer buffer);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_memfile_unwrap")]
    internal static unsafe partial int MemFileUnwrap(
        ushort version, byte* data, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_memfile_wrap")]
    internal static unsafe partial int MemFileWrap(
        ushort version, byte* modelWire, ulong len, out MfRawBuffer buffer);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_script_unwrap")]
    internal static unsafe partial int ScriptUnwrap(
        ushort version, byte* data, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_script_wrap")]
    internal static unsafe partial int ScriptWrap(
        ushort version, byte* modelWire, ulong len, out MfRawBuffer buffer);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_xml_decode")]
    internal static unsafe partial int XmlDecode(
        ushort version, byte* data, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_xml_encode")]
    internal static unsafe partial int XmlEncode(
        ushort version, byte* modelWire, ulong len, out MfRawBuffer buffer);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_table_decode")]
    internal static unsafe partial int TableDecode(
        ushort version, byte* data, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_table_encode")]
    internal static unsafe partial int TableEncode(
        ushort version, byte* modelWire, ulong len, out MfRawBuffer buffer);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_table_entry_decode")]
    internal static unsafe partial int TableEntryDecode(
        ushort version, byte* data, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_res_table_entry_encode")]
    internal static unsafe partial int TableEntryEncode(
        ushort version, byte* modelWire, ulong len, out MfRawBuffer buffer);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_sds_patch_load")]
    internal static unsafe partial int PatchLoad(byte* file, ulong len, out MfRawBuffer modelWire);
}
