using Illusion.Formats.IO;

namespace Illusion.Formats.Sound;

/// <summary>
/// An audio-zone sector/portal graph (soundsectors_*.bin): a name, a hash table, a list of sectors
/// (primary, or normal with clipping planes) and a list of portal spheres. Ported from MafiaToolkit;
/// every field is kept at its raw stored width (MafiaToolkit's writer is lossy on the portal volume
/// factor and the scene-only flag), and a file that does not parse cleanly rides whole as an opaque
/// capsule, so it round-trips byte-exact either way.
/// </summary>
public sealed class SoundSectorFile
{
    /// <summary>The typed wire model. <see cref="IsTyped"/> is false when it rides opaque.</summary>
    internal Native.Model.SoundSectorFileW Wire { get; set; } = new();

    /// <summary>Whether the file parsed into typed fields (false = opaque capsule).</summary>
    public bool IsTyped => Wire.Typed != 0;

    /// <summary>Number of sectors (0 when the file rides opaque).</summary>
    public int SectorCount => Wire.Sectors.Count;

    /// <summary>Number of portal spheres (0 when the file rides opaque).</summary>
    public int PortalCount => Wire.Portals.Count;

    public static SoundSectorFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static SoundSectorFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadSoundSectors(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.SoundSectorsToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
