using System.Numerics;
using Illusion.Formats.IO;

namespace Illusion.Formats.ItemDesc;

/// <summary>Kind of item description (the file's Type/SubType selector).</summary>
public enum ItemDescType : byte
{
    SimulationScene = 1, // Type 1 / SubType 1
    RigidBody = 2,       // Type 2 / SubType = shape
}

/// <summary>Rigid-body collision shape (the SubType when <see cref="ItemDescType.RigidBody"/>).</summary>
public enum RigidBodyShape : byte
{
    Box = 1,
    Sphere = 2,
    Capsule = 3,
    Cylinder = 4,
    TriangleMesh = 5,
    ConvexPolyhedron = 7,
    Composite = 10,
}

/// <summary>
/// A physics item description (.ids): the shape/scene primitive a collision object instantiates. Ported
/// from MafiaToolkitV2's C_ItemDesc / C_RBElementDesc / C_SimulationSceneDesc. PhysX-cooked mesh bodies
/// (triangle mesh, convex hull) are preserved as opaque blobs — this library does not cook PhysX.
/// </summary>
public sealed class ItemDescFile
{
    public ulong Hash { get; set; }
    public ItemDescType Type { get; set; }
    public byte SubType { get; set; }
    public ItemDescElement? Element { get; set; }

    public static ItemDescFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static ItemDescFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeItemDesc.Read(bytes);
    }

    /// <summary>Whether this file uses a body layout this library can parse (vs. an opaque tail).</summary>
    public bool IsOpaque => Element is OpaqueElement;

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        Write(stream);
        return stream.ToArray();
    }

    public void Write(Stream output)
    {
        output.WriteBytes(Native.Misc.NativeItemDesc.ToBytes(this));
    }

}

/// <summary>Base of every item-desc body: the leading FNV64 data hash.</summary>
public abstract class ItemDescElement
{
    public ulong DataHash { get; set; }
}

/// <summary>Simulation-scene description (Type 1): scene/simulation bounds with several opaque scalars.</summary>
public sealed class SimulationSceneElement : ItemDescElement
{
    public ushort Unk1 { get; set; }
    public ushort Unk2 { get; set; }
    public ushort Unk3 { get; set; }
    public int Unk4 { get; set; }
    public int Unk5 { get; set; }
    public uint Unk6 { get; set; }
    public byte Unk7 { get; set; }
    public byte Unk8 { get; set; }
    public Vector3 BoundsMin { get; set; }
    public Vector3 BoundsMax { get; set; }
    public uint Unk9 { get; set; }
    public uint Unk10 { get; set; }
    public float Unk11 { get; set; }
    public Vector3 SimulationBoundsMin { get; set; }
    public Vector3 SimulationBoundsMax { get; set; }

}

/// <summary>Rigid-body element (Type 2): a material id, a local 3x4 transform, a collision layer and a
/// shape-specific body.</summary>
public sealed class RigidBodyElement : ItemDescElement
{
    public RigidBodyShape Shape { get; set; }
    public ushort MaterialId { get; set; }
    /// <summary>Local transform as the on-disk 3x4 row-major float matrix (12 floats).</summary>
    public float[] Transform { get; set; } = new float[12];
    public sbyte Layer { get; set; }

    // Box / Sphere / Capsule / Cylinder scalars.
    public Vector3 BoxDimensions { get; set; }
    public float Radius { get; set; }
    public float Height { get; set; }

    // Triangle mesh: opaque PhysX-cooked body + per-material triangle ranges.
    public byte[]? CookedMesh { get; set; }
    public List<(ushort StartTriangleIndex, uint NumTriangles)> MaterialInfos { get; } = new();

    // Composite: nested rigid-body elements.
    public List<RigidBodyElement> Elements { get; } = new();

}

/// <summary>An item-desc body whose layout this library does not map — the remaining file bytes, kept
/// verbatim so the file still writes back byte-for-byte.</summary>
public sealed class OpaqueElement : ItemDescElement
{
    public byte[] Body { get; set; } = Array.Empty<byte>();
}
