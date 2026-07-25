using Illusion.Formats.IO;

namespace Illusion.Formats.Effects;

/// <summary>
/// An effects definition tree (.eff): a fixed 0x40 header of constant ids and self-referential sizes
/// then a reflected particle/FX property tree that no tool decodes. Ported from a corpus survey; the
/// header is typed and the body rides as an opaque capsule, so the file round-trips byte-exact.
/// </summary>
public sealed class EffectsFile
{
    /// <summary>The typed wire model (header typed; the property tree is an opaque capsule).</summary>
    internal Native.Model.EffFileW Wire { get; set; } = new();

    /// <summary>The container magic (always 666 in the retail corpus).</summary>
    public uint Magic => Wire.Magic;

    public static EffectsFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static EffectsFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadEffects(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.EffectsToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
