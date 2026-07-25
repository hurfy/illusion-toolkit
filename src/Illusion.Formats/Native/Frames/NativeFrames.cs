using System.Text;

namespace Illusion.Formats.Native.Frames;

/// <summary>The frames facade over the native core (P4, slice by slice). The editable
/// document stays managed; this class moves the byte images across the boundary.</summary>
internal static class NativeFrames
{
    internal static unsafe Model.NameTableModel LoadNameTable(ReadOnlySpan<byte> file)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = file)
        {
            status = FramesNativeMethods.NameTableLoad(p, (ulong)file.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_fnt_load");

        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return Model.NameTableModel.ReadFrom(reader);
    }

    internal static unsafe byte[] SaveNameTable(Model.NameTableModel model)
    {
        using var wireStream = new MemoryStream();
        using (var writer = new BinaryWriter(wireStream, Encoding.UTF8, leaveOpen: true))
        {
            model.WriteTo(writer);
        }
        byte[] wire = wireStream.ToArray();

        int status;
        MfRawBuffer raw;
        fixed (byte* p = wire)
        {
            status = FramesNativeMethods.NameTableSave(p, (ulong)wire.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_fnt_save");
        return buffer.ToArray();
    }

    internal static unsafe Model.FrameModel LoadFrameResource(ReadOnlySpan<byte> file)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = file)
        {
            status = FramesNativeMethods.FrameResourceLoad(p, (ulong)file.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_fr_load");

        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return Model.FrameModel.ReadFrom(reader);
    }

    internal static unsafe byte[] SaveFrameResource(Model.FrameModel model)
    {
        using var wireStream = new MemoryStream();
        using (var writer = new BinaryWriter(wireStream, Encoding.UTF8, leaveOpen: true))
        {
            model.WriteTo(writer);
        }
        byte[] wire = wireStream.ToArray();

        int status;
        MfRawBuffer raw;
        fixed (byte* p = wire)
        {
            status = FramesNativeMethods.FrameResourceSave(p, (ulong)wire.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_fr_save");
        return buffer.ToArray();
    }

    internal static unsafe Model.IndexPoolModel LoadIndexPool(ReadOnlySpan<byte> file)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = file)
        {
            status = FramesNativeMethods.IndexPoolLoad(p, (ulong)file.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_ibp_load");
        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return Model.IndexPoolModel.ReadFrom(reader);
    }

    internal static unsafe byte[] SaveIndexPool(Model.IndexPoolModel model)
    {
        using var wireStream = new MemoryStream();
        using (var writer = new BinaryWriter(wireStream, Encoding.UTF8, leaveOpen: true))
        {
            model.WriteTo(writer);
        }
        byte[] wire = wireStream.ToArray();
        int status;
        MfRawBuffer raw;
        fixed (byte* p = wire)
        {
            status = FramesNativeMethods.IndexPoolSave(p, (ulong)wire.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_ibp_save");
        return buffer.ToArray();
    }

    internal static unsafe Model.VertexPoolModel LoadVertexPool(ReadOnlySpan<byte> file)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = file)
        {
            status = FramesNativeMethods.VertexPoolLoad(p, (ulong)file.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_vbp_load");
        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return Model.VertexPoolModel.ReadFrom(reader);
    }

    internal static unsafe byte[] SaveVertexPool(Model.VertexPoolModel model)
    {
        using var wireStream = new MemoryStream();
        using (var writer = new BinaryWriter(wireStream, Encoding.UTF8, leaveOpen: true))
        {
            model.WriteTo(writer);
        }
        byte[] wire = wireStream.ToArray();
        int status;
        MfRawBuffer raw;
        fixed (byte* p = wire)
        {
            status = FramesNativeMethods.VertexPoolSave(p, (ulong)wire.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_vbp_save");
        return buffer.ToArray();
    }

    internal static unsafe Model.LodRebuildResultW RebuildLod(Model.LodRebuildRequestW request)
    {
        using var wireStream = new MemoryStream();
        using (var writer = new BinaryWriter(wireStream, Encoding.UTF8, leaveOpen: true))
        {
            request.WriteTo(writer);
        }
        byte[] wire = wireStream.ToArray();

        int status;
        MfRawBuffer raw;
        fixed (byte* p = wire)
        {
            status = FramesNativeMethods.RebuildLod(p, (ulong)wire.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_frames_rebuild_lod");
        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return Model.LodRebuildResultW.ReadFrom(reader);
    }

    /// <summary>The packing plan of a vertex declaration — which channels it carries, where each
    /// one starts and how wide it is, plus the packed stride.</summary>
    internal static unsafe Model.VertexLayoutW VertexLayout(uint declaration)
    {
        int status = FramesNativeMethods.VertexLayout(declaration, out MfRawBuffer raw);
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_vtx_layout");
        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return Model.VertexLayoutW.ReadFrom(reader);
    }

    internal static unsafe Model.DecodedVertexBufferW DecompressVertexBuffer(
        ReadOnlySpan<byte> buffer, uint declaration, ulong vertexCount,
        System.Numerics.Vector3 offset, float scale)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = buffer)
        {
            status = FramesNativeMethods.VertexDecompress(
                p, (ulong)buffer.Length, declaration, vertexCount,
                offset.X, offset.Y, offset.Z, scale, out raw);
        }
        using var wire = new MfBuffer(raw);
        ThrowOnError(status, "mf_vtx_decompress");
        using var stream = new MemoryStream(wire.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return Model.DecodedVertexBufferW.ReadFrom(reader);
    }

    internal static unsafe byte[] CompressVertexBuffer(
        ReadOnlySpan<byte> baseBytes, Model.DecodedVertexBufferW vertices, uint declaration,
        System.Numerics.Vector3 offset, float scale)
    {
        using var wireStream = new MemoryStream();
        using (var writer = new BinaryWriter(wireStream, Encoding.UTF8, leaveOpen: true))
        {
            vertices.WriteTo(writer);
        }
        byte[] wireBytes = wireStream.ToArray();

        int status;
        MfRawBuffer raw;
        fixed (byte* baseP = baseBytes)
        fixed (byte* wireP = wireBytes)
        {
            status = FramesNativeMethods.VertexCompress(
                baseP, (ulong)baseBytes.Length, wireP, (ulong)wireBytes.Length, declaration,
                offset.X, offset.Y, offset.Z, scale, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_vtx_compress");
        return buffer.ToArray();
    }

    private static void ThrowOnError(int status, string entryPoint)
    {
        if (status == NativeMethods.Ok)
        {
            return;
        }
        string error = NativeFormats.LastError;
        throw new InvalidDataException(error.Length != 0 ? error : $"{entryPoint} failed ({status})");
    }
}
