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
