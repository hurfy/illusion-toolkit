using Illusion.Formats.IO;

namespace Illusion.Formats.Navigation;

/// <summary>
/// Ambient-animal spawn/idle paths (.atp / AnimalTrafficPaths): a header, an animal-type table
/// (hash + name) and a list of paths, each with spawn/despawn/idle index lists, a bounding box, an
/// optional hash-name, two floats and a run of 25-byte path points. Ported from MafiaToolkit; every
/// field is kept raw (MafiaToolkit's writer recomputes the bounding box and the spawn/despawn/idle
/// lists, which does not match disk), and a file that does not parse cleanly rides whole as an
/// opaque capsule, so it round-trips byte-exact either way.
/// </summary>
public sealed class AnimalTrafficPathsFile
{
    /// <summary>The typed wire model. <see cref="IsTyped"/> is false when it rides opaque.</summary>
    internal Native.Model.AtpFileW Wire { get; set; } = new();

    /// <summary>Whether the file parsed into typed fields (false = opaque capsule).</summary>
    public bool IsTyped => Wire.Typed != 0;

    /// <summary>Number of animal types declared in the file.</summary>
    public int AnimalTypeCount => Wire.AnimalTypes.Count;

    /// <summary>Number of paths.</summary>
    public int PathCount => Wire.Paths.Count;

    public static AnimalTrafficPathsFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static AnimalTrafficPathsFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadAnimalTrafficPaths(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.AnimalTrafficPathsToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}
