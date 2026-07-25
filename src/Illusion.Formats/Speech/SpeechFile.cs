using Illusion.Formats.IO;

namespace Illusion.Formats.Speech;

/// <summary>
/// A speech table (.spe / Speech resource): a flat catalog of speech types (entity/speech/folder names
/// plus flags) and speech items (name + an opaque payload blob). Ported from MafiaToolkit's SpeechFile;
/// fully typed and byte-exact — the format is a straight sequential read/write with no offsets.
/// </summary>
public sealed class SpeechFile
{
    /// <summary>The typed wire model (types + items). Internal until a friendlier surface is needed.</summary>
    internal Native.Model.SpeechFileW Wire { get; set; } = new();

    /// <summary>Number of speech-type records.</summary>
    public int TypeCount => Wire.Types.Count;
    /// <summary>Number of speech-item records.</summary>
    public int ItemCount => Wire.Items.Count;

    public static SpeechFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static SpeechFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadSpeech(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.SpeechToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
