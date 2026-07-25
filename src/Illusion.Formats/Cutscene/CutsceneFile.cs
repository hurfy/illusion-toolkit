using Illusion.Formats.IO;

namespace Illusion.Formats.Cutscene;

/// <summary>
/// A cutscene container (.cut / CutsceneLoader): a list of cutscenes — each a name, two header
/// fields and a GCS asset block, plus an optional SPD sound block — followed by a GCR
/// (vehicle-content) list. Ported from MafiaToolkit; the top-level container is typed and the
/// GCS/SPD anim-entity payloads (~20 types) are preserved raw (deferred), so the file round-trips
/// byte-exact.
/// </summary>
public sealed class CutsceneFile
{
    /// <summary>The typed wire model. Internal until the GCS/SPD entity payloads are typed.</summary>
    internal Native.Model.CutsceneFileW Wire { get; set; } = new();

    /// <summary>Number of cutscenes in the container.</summary>
    public int CutsceneCount => Wire.Cutscenes.Count;

    /// <summary>Number of GCR (vehicle-content) records.</summary>
    public int VehicleContentCount => Wire.Gcrs.Count;

    public static CutsceneFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static CutsceneFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadCutscene(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.CutsceneToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
