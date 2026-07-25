using System.Text;
using Illusion.Formats.ResourceFormats;

namespace Illusion.Formats.Native.Resources;

/// <summary>
/// The resource-wrapper facade over the native core: every typed envelope inside an SDS entry
/// (Texture/Mipmap, Sound, MemFile, Script, XML, Table) unwraps and rewraps natively; this class
/// translates between the wire models and the managed resource types. Text rendering (extracted
/// .xml files, table cell presentation) stays managed — see <see cref="XmlText"/> and the table
/// mapping below. The managed byte codecs survive as the dual-path reference until P6.
/// </summary>
internal static class NativeResources
{
    // ── Texture / Mipmap ──

    internal static unsafe void TextureUnwrap(
        TextureResource target, ushort version, bool isMip, byte[] data)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = data)
        {
            status = ResNativeMethods.TextureUnwrap(
                version, (byte)(isMip ? 1 : 0), p, (ulong)data.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_texture_unwrap");
        Model.WrappedTexture wire = ReadWire(buffer, Model.WrappedTexture.ReadFrom);

        target.NameHash = wire.NameHash;
        target.Unknown8 = wire.Unknown8;
        target.HasMIP = wire.HasMip;
        target.Data = wire.Dds;
    }

    internal static unsafe byte[] TextureWrap(TextureResource source, ushort version, bool isMip)
    {
        var wire = new Model.WrappedTexture
        {
            NameHash = source.NameHash,
            Unknown8 = source.Unknown8,
            HasMip = source.HasMIP,
            Dds = source.Data,
        };
        byte[] wireBytes = WriteWire(wire.WriteTo);
        int status;
        MfRawBuffer raw;
        fixed (byte* p = wireBytes)
        {
            status = ResNativeMethods.TextureWrap(
                version, (byte)(isMip ? 1 : 0), p, (ulong)wireBytes.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_texture_wrap");
        return buffer.ToArray();
    }

    // ── Sound ──

    internal static unsafe void SoundUnwrap(SoundResource target, ushort version, byte[] data)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = data)
        {
            status = ResNativeMethods.SoundUnwrap(version, p, (ulong)data.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_sound_unwrap");
        Model.WrappedSound wire = ReadWire(buffer, Model.WrappedSound.ReadFrom);
        target.Name = wire.Name;
        target.Data = wire.Fsb;
    }

    internal static unsafe byte[] SoundWrap(SoundResource source, ushort version)
    {
        var wire = new Model.WrappedSound { Name = source.Name, Fsb = source.Data };
        byte[] wireBytes = WriteWire(wire.WriteTo);
        int status;
        MfRawBuffer raw;
        fixed (byte* p = wireBytes)
        {
            status = ResNativeMethods.SoundWrap(version, p, (ulong)wireBytes.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_sound_wrap");
        return buffer.ToArray();
    }

    // ── MemFile ──

    internal static unsafe void MemFileUnwrap(MemFileResource target, ushort version, byte[] data)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = data)
        {
            status = ResNativeMethods.MemFileUnwrap(version, p, (ulong)data.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_memfile_unwrap");
        Model.WrappedMemFile wire = ReadWire(buffer, Model.WrappedMemFile.ReadFrom);
        target.Unk0_V4 = wire.Unk0V4;
        target.Name = wire.Name;
        target.Unk1 = wire.Unk1;
        target.Unk2_V4 = wire.Unk2V4;
        target.Data = wire.Data;
    }

    internal static unsafe byte[] MemFileWrap(MemFileResource source, ushort version)
    {
        var wire = new Model.WrappedMemFile
        {
            Unk0V4 = source.Unk0_V4,
            Name = source.Name,
            Unk1 = source.Unk1,
            Unk2V4 = source.Unk2_V4,
            Data = source.Data,
        };
        byte[] wireBytes = WriteWire(wire.WriteTo);
        int status;
        MfRawBuffer raw;
        fixed (byte* p = wireBytes)
        {
            status = ResNativeMethods.MemFileWrap(version, p, (ulong)wireBytes.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_memfile_wrap");
        return buffer.ToArray();
    }

    // ── Script ──

    internal static unsafe void ScriptUnwrap(ScriptResource target, ushort version, byte[] data)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = data)
        {
            status = ResNativeMethods.ScriptUnwrap(version, p, (ulong)data.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_script_unwrap");
        Model.WrappedScript wire = ReadWire(buffer, Model.WrappedScript.ReadFrom);

        target.Path = wire.Path;
        target.Scripts.Clear();
        foreach (Model.ScriptItem item in wire.Scripts)
        {
            target.Scripts.Add(new ScriptData { Name = item.Name, Data = item.Data });
        }
    }

    internal static unsafe byte[] ScriptWrap(ScriptResource source, ushort version)
    {
        var wire = new Model.WrappedScript { Path = source.Path };
        foreach (ScriptData script in source.Scripts)
        {
            wire.Scripts.Add(new Model.ScriptItem { Name = script.Name, Data = script.Data });
        }
        byte[] wireBytes = WriteWire(wire.WriteTo);
        int status;
        MfRawBuffer raw;
        fixed (byte* p = wireBytes)
        {
            status = ResNativeMethods.ScriptWrap(version, p, (ulong)wireBytes.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_script_wrap");
        return buffer.ToArray();
    }

    // ── XML (binary side; text rendering lives in XmlText) ──

    internal static unsafe Model.XmlDocumentModel XmlDecode(ushort version, byte[] data)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = data)
        {
            status = ResNativeMethods.XmlDecode(version, p, (ulong)data.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_xml_decode");
        return ReadWire(buffer, Model.XmlDocumentModel.ReadFrom);
    }

    internal static unsafe byte[] XmlEncode(ushort version, Model.XmlDocumentModel model)
    {
        byte[] wireBytes = WriteWire(model.WriteTo);
        int status;
        MfRawBuffer raw;
        fixed (byte* p = wireBytes)
        {
            status = ResNativeMethods.XmlEncode(version, p, (ulong)wireBytes.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_xml_encode");
        return buffer.ToArray();
    }

    // ── Table ──

    internal static unsafe Model.TableModel TableDecode(ushort version, byte[] data)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = data)
        {
            status = ResNativeMethods.TableDecode(version, p, (ulong)data.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_table_decode");
        return ReadWire(buffer, Model.TableModel.ReadFrom);
    }

    internal static unsafe byte[] TableEncode(ushort version, Model.TableModel model)
    {
        byte[] wireBytes = WriteWire(model.WriteTo);
        int status;
        MfRawBuffer raw;
        fixed (byte* p = wireBytes)
        {
            status = ResNativeMethods.TableEncode(version, p, (ulong)wireBytes.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_table_encode");
        return buffer.ToArray();
    }

    internal static unsafe Model.TableEntry TableEntryDecode(ushort version, byte[] data)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = data)
        {
            status = ResNativeMethods.TableEntryDecode(version, p, (ulong)data.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_table_entry_decode");
        return ReadWire(buffer, Model.TableEntry.ReadFrom);
    }

    internal static unsafe byte[] TableEntryEncode(ushort version, Model.TableEntry entry)
    {
        byte[] wireBytes = WriteWire(entry.WriteTo);
        int status;
        MfRawBuffer raw;
        fixed (byte* p = wireBytes)
        {
            status = ResNativeMethods.TableEntryEncode(version, p, (ulong)wireBytes.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_res_table_entry_encode");
        return buffer.ToArray();
    }

    // ── .sds.patch (parse-only) ──

    /// <summary>Raised for the byte-flipped console patches the managed reader keeps.</summary>
    internal sealed class ConsolePatchException : Exception
    {
        public ConsolePatchException(string message) : base(message)
        {
        }
    }

    internal static unsafe Model.PatchModel PatchLoad(ReadOnlySpan<byte> file)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = file)
        {
            status = ResNativeMethods.PatchLoad(p, (ulong)file.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        if (status == NativeMethods.ErrState)
        {
            throw new ConsolePatchException(NativeFormats.LastError);
        }
        ThrowOnError(status, "mf_sds_patch_load");
        return ReadWire(buffer, Model.PatchModel.ReadFrom);
    }

    // ── plumbing ──

    private static T ReadWire<T>(MfBuffer buffer, Func<BinaryReader, T> read)
    {
        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return read(reader);
    }

    private static byte[] WriteWire(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(writer);
        }
        return stream.ToArray();
    }

    private static void ThrowOnError(int status, string entryPoint)
    {
        if (status == NativeMethods.Ok)
        {
            return;
        }
        string error = NativeFormats.LastError;
        throw new SdsFormatException(error.Length != 0 ? error : $"{entryPoint} failed ({status})");
    }
}
