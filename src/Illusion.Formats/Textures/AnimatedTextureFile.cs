using Illusion.Formats.IO;

namespace Illusion.Formats.Textures;

/// <summary>
/// An animated texture (.ifl / Animated Texture): a flipbook that cycles a set of textures — a header
/// (hash + name) followed by a frame array of (texture hash, texture name, flag). Ported from MafiaToolkit;
/// fully typed and byte-exact (a flat sequential read/write with no offsets).
/// </summary>
public sealed class AnimatedTextureFile
{
    /// <summary>The typed wire model. Internal until a friendlier surface is needed.</summary>
    internal Native.Model.AnimTexFileW Wire { get; set; } = new();

    /// <summary>The flipbook's name.</summary>
    public string Name => Wire.FileName;
    /// <summary>Number of texture frames in the flipbook.</summary>
    public int FrameCount => Wire.Textures.Count;

    public static AnimatedTextureFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static AnimatedTextureFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadAnimTex(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.AnimTexToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
