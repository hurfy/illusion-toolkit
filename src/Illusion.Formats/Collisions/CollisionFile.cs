using System.Numerics;

namespace Illusion.Formats.Collisions;

/// <summary>
/// A streamed collision resource (.col): placed instances of collision meshes plus the meshes themselves
/// (opaque PhysX-cooked triangle-mesh blobs + per-material triangle sections). Ported from
/// MafiaToolkitV2's CollisionResource; the cooked mesh bodies are preserved verbatim.
/// </summary>
public sealed class CollisionFile
{
    /// <summary>Format version — Mafia II ships 17 (0x11).</summary>
    public const uint SupportedVersion = 0x11;

    public uint Version { get; set; } = SupportedVersion;
    public uint Platform { get; set; }
    public List<CollisionInstance> Instances { get; } = new();
    public List<CollisionMesh> Meshes { get; } = new();

    public static CollisionFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    /// <summary>Parses a .col from the stream's current position to its end (the byte-level
    /// work runs in the native core).</summary>
    public static CollisionFile Read(Stream input)
    {
        byte[] remaining = new byte[input.Length - input.Position];
        input.ReadExactly(remaining);
        return Native.Collisions.NativeCollision.Load(remaining);
    }

    /// <summary>Serializes through the native core.</summary>
    public byte[] ToBytes() => Native.Collisions.NativeCollision.Save(this);

    public void Write(Stream output)
    {
        byte[] bytes = ToBytes();
        output.Write(bytes, 0, bytes.Length);
    }
}

/// <summary>A placed instance of a collision mesh (position + euler rotation + the mesh hash it uses).</summary>
public sealed class CollisionInstance
{
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public ulong Hash { get; set; }
    public int Unk4 { get; set; }
    public byte Group { get; set; }
}

/// <summary>A collision mesh: its FNV64 hash, the opaque PhysX-cooked triangle mesh, and material sections.</summary>
public sealed class CollisionMesh
{
    public ulong Hash { get; set; }
    public byte[]? CookedMesh { get; set; }
    public List<CollisionSection> Sections { get; } = new();
}

/// <summary>A per-material triangle range within a collision mesh.</summary>
public sealed class CollisionSection
{
    public uint Start { get; set; }
    public uint NumEdges { get; set; }
    public uint Material { get; set; }
    public uint Unk2 { get; set; }
}
