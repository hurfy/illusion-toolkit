using System.Numerics;
using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Translokator;

// Vendored from MafiaToolkit (ResourceTypes/FileTypes/Translokator/Translokator.cs), READ-ONLY:
// dropped write/compile/XML/grid-rebuild paths (Illusion only reads .tra to spawn instances).
// Parse layout + decompression math copied verbatim so instance transforms match the toolkit.
// MathHelper.ToRadians/ToDegrees inlined (vendored MathHelpers lacks them).

public sealed class Grid
{
    public short Key;
    public Vector3 Origin;
    public Vector2 CellSize;
    public int Width;
    public int Height;
    public ushort[] Data = null!;
}

public sealed class Instance
{
    private Vector3 _rotation;

    public Vector3 Position { get; set; }
    public Vector3 Rotation
    {
        get => _rotation;
        set { _rotation = value; UpdateQuaternion(); }
    }
    public Quaternion Quaternion { get; private set; }
    public float Scale { get; set; } = 1.0f;

    public ushort ID { get; set; }
    public ushort W0 { get; set; }
    public ushort W1 { get; set; }
    public ushort W2 { get; set; }
    public ushort D4 { get; set; }
    public int D5 { get; set; }
    public int RefID;

    private void UpdateQuaternion()
    {
        const float deg2rad = MathF.PI / 180f;
        float pitch = Rotation.X * deg2rad;
        float yaw = Rotation.Y * deg2rad;
        float roll = Rotation.Z * deg2rad;

        float v12 = pitch * 0.5f;
        float v11 = MathF.Sin(v12);
        float v10 = MathF.Cos(v12);
        float v14 = yaw * 0.5f;
        float v18 = MathF.Sin(v14);
        float v9 = MathF.Cos(v14);
        float v16 = roll * 0.5f;
        float v19 = MathF.Sin(v16);
        float v17 = MathF.Cos(v16);
        float v4 = v17 * v9;
        float v5 = v19 * v18;
        float v6 = v17 * v18;
        float v7 = v9 * v19;

        Quaternion = new Quaternion(
            v10 * v5 + v11 * v4,
            v11 * v7 + v10 * v6,
            v7 * v10 - v6 * v11,
            -(v4 * v10 - v5 * v11));
    }
}

public sealed class Object
{
    public short Unk02;
    public HashName Name = new HashName();
    public byte[] UnkBytes1 = null!;
    public float GridMax;
    public float GridMin;
    public Instance[] Instances = Array.Empty<Instance>();
}

public sealed class ObjectGroup
{
    public ActorTypes ActorType = ActorTypes.None;
    public short Unk01;
    public Object[] Objects = Array.Empty<Object>();
}

public sealed class TranslokatorLoader
{
    public Grid[] Grids { get; set; } = null!;
    public ObjectGroup[] ObjectGroups { get; set; } = null!;
    public int Version { get; set; }
    public int Unk1 { get; set; }
    public short Unk2 { get; set; }
    public BoundingBox Bounds { get; set; }

    public TranslokatorLoader() { }

    public TranslokatorLoader(FileInfo info)
    {
        using var reader = new BinaryReader(File.Open(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read));
        ReadFromFile(reader);
    }

    public void ReadFromFile(BinaryReader reader)
    {
        Stream input = reader.BaseStream;
        byte[] bytes = new byte[input.Length - input.Position];
        int at = 0;
        while (at < bytes.Length)
        {
            int got = input.Read(bytes, at, bytes.Length - at);
            if (got <= 0) break;
            at += got;
        }
        Native.Misc.NativeMiscFiles.ReadTranslokator(this, bytes);
    }

}
