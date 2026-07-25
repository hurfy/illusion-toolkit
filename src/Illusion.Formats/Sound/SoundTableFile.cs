using Illusion.Formats.IO;

namespace Illusion.Formats.Sound;

/// <summary>
/// A sound table (.stbl / SoundTable): five sequential sections — curves, tagged records, fixed
/// records, sound groups (nested variants → FSB entries) and a name buffer. Ported from MafiaToolkit;
/// fully typed with counts/lengths re-derived from the stored arrays, and a file whose re-emit does
/// not match rides whole as an opaque capsule, so it round-trips byte-exact either way.
/// </summary>
public sealed class SoundTableFile
{
    /// <summary>The typed wire model. <see cref="IsTyped"/> is false when it rides opaque.</summary>
    internal Native.Model.StblFileW Wire { get; set; } = new();

    /// <summary>Whether the file parsed into typed sections (false = opaque capsule).</summary>
    public bool IsTyped => Wire.Typed != 0;

    /// <summary>Number of sound groups (0 when the file rides opaque).</summary>
    public int GroupCount => Wire.Groups.Count;

    public static SoundTableFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static SoundTableFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadSoundTable(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.SoundTableToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
