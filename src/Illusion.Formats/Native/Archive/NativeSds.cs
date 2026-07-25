using System.Text;
using Illusion.Formats.Archive;

namespace Illusion.Formats.Native.Archive;

/// <summary>
/// The SDS container facade over the native core. Load hands the raw file bytes across (the
/// native side detects and unwraps the XTEA layer itself); save produces the classic zlib
/// shape. Console (big-endian) archives and opt-in oodle writes stay on the managed twins —
/// the caller falls back when <see cref="ConsoleArchiveException"/> surfaces.
/// </summary>
internal static class NativeSds
{
    /// <summary>Raised when the native side refuses a console-platform archive; the managed
    /// reader handles those.</summary>
    internal sealed class ConsoleArchiveException : Exception
    {
        public ConsoleArchiveException(string message) : base(message)
        {
        }
    }

    /// <summary>Removes the XTEA wrapper when present; a plain archive comes back verbatim.</summary>
    internal static unsafe byte[] Unwrap(ReadOnlySpan<byte> file)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = file)
        {
            status = SdsNativeMethods.Unwrap(p, (ulong)file.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_sds_unwrap");
        return buffer.ToArray();
    }

    internal static unsafe SdsArchive Load(ReadOnlySpan<byte> file)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = file)
        {
            status = SdsNativeMethods.Load(p, (ulong)file.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_sds_load");

        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        Model.ArchiveModel model = Model.ArchiveModel.ReadFrom(reader);

        var archive = new SdsArchive
        {
            Version = model.Version,
            Platform = (Platform)model.Platform,
            Endian = IO.Endian.Little,
            SlotRamRequired = model.SlotRam,
            SlotVramRequired = model.SlotVram,
            OtherRamRequired = model.OtherRam,
            OtherVramRequired = model.OtherVram,
            Unknown20 = model.Unknown20,
            ResourceInfoXml = model.HasResourceInfoXml != 0 ? model.ResourceInfoXml : null,
        };
        foreach (Model.ResourceType type in model.ResourceTypes)
        {
            archive.ResourceTypes.Add(new SdsResourceTypeEntry
            {
                Id = type.Id,
                Name = type.Name,
                Parent = type.Parent,
            });
        }
        foreach (Model.ArchiveEntry entry in model.Entries)
        {
            archive.Entries.Add(new ResourceEntry
            {
                TypeId = entry.TypeId,
                Version = entry.Version,
                Data = entry.Data,
                SlotRamRequired = entry.SlotRam,
                SlotVramRequired = entry.SlotVram,
                OtherRamRequired = entry.OtherRam,
                OtherVramRequired = entry.OtherVram,
            });
        }
        return archive;
    }

    internal static unsafe byte[] Save(SdsArchive archive, SdsWriteOptions options)
    {
        var model = new Model.ArchiveModel
        {
            Version = archive.Version,
            Platform = (uint)archive.Platform,
            SlotRam = archive.SlotRamRequired,
            SlotVram = archive.SlotVramRequired,
            OtherRam = archive.OtherRamRequired,
            OtherVram = archive.OtherVramRequired,
            Unknown20 = archive.Unknown20,
            HasResourceInfoXml = (byte)(archive.ResourceInfoXml is null ? 0 : 1),
            ResourceInfoXml = archive.ResourceInfoXml ?? "",
        };
        foreach (SdsResourceTypeEntry type in archive.ResourceTypes)
        {
            model.ResourceTypes.Add(new Model.ResourceType
            {
                Id = type.Id,
                Name = type.Name,
                Parent = type.Parent,
            });
        }
        foreach (ResourceEntry entry in archive.Entries)
        {
            model.Entries.Add(new Model.ArchiveEntry
            {
                TypeId = entry.TypeId,
                Version = entry.Version,
                Data = entry.Data ?? [],
                SlotRam = entry.SlotRamRequired,
                SlotVram = entry.SlotVramRequired,
                OtherRam = entry.OtherRamRequired,
                OtherVram = entry.OtherVramRequired,
            });
        }

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
            status = SdsNativeMethods.Save(
                p, (ulong)wire.Length, (byte)(options.Compress ? 1 : 0),
                options.CompressionRatio, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_sds_save");
        return buffer.ToArray();
    }

    private static void ThrowOnError(int status, string entryPoint)
    {
        if (status == NativeMethods.Ok)
        {
            return;
        }
        string error = NativeFormats.LastError;
        if (status == NativeMethods.ErrState)
        {
            throw new ConsoleArchiveException(error);
        }
        if (error.Contains("version", StringComparison.Ordinal)
            && error.Contains("is not supported", StringComparison.Ordinal))
        {
            throw new UnsupportedVersionException(error);
        }
        throw new SdsFormatException(error.Length != 0 ? error : $"{entryPoint} failed ({status})");
    }
}
