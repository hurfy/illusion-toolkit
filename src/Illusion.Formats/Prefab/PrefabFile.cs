using Illusion.Formats.IO;

namespace Illusion.Formats.Prefab;

/// <summary>
/// A prefab container (.prf / PrefabLoader): a size-header wrapped around a list of prefab entries,
/// each a hash, a type, an unknown int, a size and that many bytes of bit-packed InitData. Ported
/// from MafiaToolkit; the container is typed and the per-type InitData (~12 vehicle/door/wagon/…
/// variants) is preserved raw (deferred), so the file round-trips byte-exact — including the
/// type 0/1/11 variants MafiaToolkit cannot parse.
/// </summary>
public sealed class PrefabFile
{
    /// <summary>The typed wire model. Internal until the per-type InitData is typed.</summary>
    internal Native.Model.PrefabFileW Wire { get; set; } = new();

    /// <summary>Number of prefab entries in the container.</summary>
    public int PrefabCount => Wire.Prefabs.Count;

    /// <summary>The name hashes the container is keyed by — how an entity finds its init data.</summary>
    public IReadOnlyList<ulong> Hashes => [.. Wire.Prefabs.Select(p => p.Hash)];

    /// <summary>Each entry's type id and the size of its init-data blob, in file order. The blob itself stays
    /// inside the core; this is what a caller can ask about it while it is opaque.</summary>
    public IReadOnlyList<(int Type, int Size)> Entries => [.. Wire.Prefabs.Select(p => (p.PrefabType, p.Data.Length))];

    /// <summary>
    /// Which entries the core decoded rather than carrying opaquely, by type id (0 = still opaque). Typing is
    /// going variant by variant, so this is how much of a container is actually understood — and the number a
    /// probe asserts against so a regression shows up as coverage falling, not as silence.
    /// </summary>
    public IReadOnlyList<int> DecodedKinds => [.. Wire.Prefabs.Select(p => p.TypedKind)];

    /// <summary>
    /// The assembly of the car this container describes, or null when it holds none. Everything in it names a
    /// FRAME of the car's own model by FNV64 hash — a bone, a dummy, a point — which is what makes it the
    /// description of how the thing is put together rather than a table of numbers.
    /// </summary>
    public CarPrefab? Car
    {
        get
        {
            Native.Model.PrefabEntryW? entry = Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0);
            return entry == null ? null : new CarPrefab(entry.CarInit[0]);
        }
    }

    public static PrefabFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static PrefabFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadPrefab(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.PrefabToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
