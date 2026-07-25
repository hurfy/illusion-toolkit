using Illusion.Formats.IO;

namespace Illusion.Formats.EntityData;

/// <summary>
/// An entity-data storage table (.eds / EntityDataStorage): a header (entity type, hash, fixed per-table
/// size), a table-hash array, and that many equally-sized entity-data table blobs. Ported from MafiaToolkit;
/// the header and table hashes are typed and each table blob is preserved raw (the per-type extra-data
/// layout reuses the Actors typing, deferred), so the file round-trips byte-exact.
/// </summary>
public sealed class EntityDataStorageFile
{
    /// <summary>The typed wire model. Internal until the per-table extra-data is typed.</summary>
    internal Native.Model.EdsFileW Wire { get; set; } = new();

    /// <summary>The entity type this storage is for.</summary>
    public int EntityType => Wire.EntityType;
    /// <summary>Number of entity-data tables.</summary>
    public int TableCount => Wire.TableHashes.Count;

    public static EntityDataStorageFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static EntityDataStorageFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadEds(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.EdsToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
