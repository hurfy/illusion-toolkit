using Illusion.Formats.IO;

namespace Illusion.Formats.FaceFx;

/// <summary>
/// A FaceFX animation-set container (.fas / FxContainer&lt;FxAnimSet&gt;): a count of self-contained
/// archive blocks, each a size-prefixed capsule holding the OC3/FaceFX RTTI table, interned string
/// table and animation-curve graph. Ported from MafiaToolkit; the container framing is typed and
/// each archive rides raw (its string table is a derived C#-traversal artifact, unsafe to
/// regenerate), so the file round-trips byte-exact.
/// </summary>
public sealed class FaceFxAnimSetFile
{
    /// <summary>The typed wire model (container framing; archive bodies are opaque capsules).</summary>
    internal Native.Model.FxContainerW Wire { get; set; } = new();

    /// <summary>Number of archive blocks in the container.</summary>
    public int ArchiveCount => Wire.Archives.Count;

    public static FaceFxAnimSetFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static FaceFxAnimSetFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadFaceFxAnimSet(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.FaceFxAnimSetToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
