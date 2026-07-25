using Illusion.Formats.IO;

namespace Illusion.Formats.EntityActivator;

/// <summary>
/// An entity-activator table (entityactivator.bin, magic "atne"): a small header then exactly 17
/// entity sets, each a hash, an int and a list of inner sets (a hash and a hash list). Ported from
/// MafiaToolkit; the format is flat and fully typed, so it round-trips byte-exact.
/// </summary>
public sealed class EntityActivatorFile
{
    /// <summary>The typed wire model.</summary>
    internal Native.Model.EntityActivatorW Wire { get; set; } = new();

    /// <summary>Number of top-level entity sets (fixed at 17 by the game).</summary>
    public int SetCount => Wire.Sets.Count;

    public static EntityActivatorFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static EntityActivatorFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadEntityActivator(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.EntityActivatorToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
