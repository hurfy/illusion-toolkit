using System.Numerics;
using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectLight : FrameObjectJoint
{
    public int Flags { get; set; }
    public float LUnk0 { get; set; }
    public float LUnk1 { get; set; }
    public float LUnk2 { get; set; }
    public float LUnk3 { get; set; }
    public float LUnk4 { get; set; }
    public float LUnk5 { get; set; }
    public float LUnk6 { get; set; }
    public int UnkInt1 { get; set; }
    public Vector3 UnkVector_0 { get; set; }
    public float LUnk7 { get; set; }
    public float LUnk8 { get; set; }
    public byte UnkByte1 { get; set; }
    public float LUnk9 { get; set; }
    public float LUnk10 { get; set; }
    public float LUnk11 { get; set; }
    public float LUnk12 { get; set; }
    public Vector3 UnkVector_1 { get; set; }
    public Vector3 UnkVector_2 { get; set; }
    public float LUnk13 { get; set; }
    public float LUnk14 { get; set; }
    public float LUnk15 { get; set; }
    public Vector3 UnkVector_3 { get; set; }
    public float LUnk16 { get; set; }
    public float LUnk17 { get; set; }
    public float LUnk18 { get; set; }
    public byte UnkByte2 { get; set; }
    public float LUnk19 { get; set; }
    public float LUnk20 { get; set; }
    public float LUnk21 { get; set; }
    public float LUnk22 { get; set; }
    public float LUnk23 { get; set; }
    public HashName ProjectionTexture { get; set; }
    public int UnkInt2 { get; set; }
    public float LUnk24 { get; set; }
    public float LUnk25 { get; set; }
    public Vector3 UnkVector_4 { get; set; }
    public float LUnk26 { get; set; }
    public float LUnk27 { get; set; }
    public float LUnk28 { get; set; }
    public float LUnk29 { get; set; }
    public float LUnk30 { get; set; }
    public Vector3 UnkVector_5 { get; set; }
    public float LUnk31 { get; set; }
    public float LUnk32 { get; set; }
    public float LUnk33 { get; set; }
    public float LUnk34 { get; set; }
    public float LUnk35 { get; set; }
    public HashName[] TextureHashes { get; set; }
    public BoundingBox UnkBox { get; set; }
    public byte UnkByte3 { get; set; }
    public Matrix4x4 UnknownMatrix { get; set; }

    public FrameObjectLight(FrameResource OwningResource) : base(OwningResource)
    {
        Flags = 0;
        UnkVector_0 = new Vector3();
        UnkVector_1 = new Vector3();
        UnkVector_2 = new Vector3();
        UnkVector_3 = new Vector3();
        UnkVector_4 = new Vector3();
        UnkVector_5 = new Vector3();
        TextureHashes = new HashName[4];
        ProjectionTexture = new HashName();
        for (int i = 0; i < 4; i++)
        {
            TextureHashes[i] = new HashName();
        }

        UnkBox = new BoundingBox(new Vector3(float.MinValue), new Vector3(float.MaxValue));
        UnkByte3 = 0;
        UnknownMatrix = Matrix4x4.Identity;
    }

    public FrameObjectLight(FrameObjectLight other) : base(other)
    {
        Flags = other.Flags;
        LUnk0 = other.LUnk0;
        LUnk1 = other.LUnk1;
        LUnk2 = other.LUnk2;
        LUnk3 = other.LUnk3;
        LUnk4 = other.LUnk4;
        LUnk5 = other.LUnk5;
        LUnk6 = other.LUnk6;
        UnkInt1 = other.UnkInt1;
        UnkVector_0 = other.UnkVector_0;
        LUnk7 = other.LUnk7;
        LUnk8 = other.LUnk8;
        UnkByte1 = other.UnkByte1;
        LUnk9 = other.LUnk9;
        LUnk10 = other.LUnk10;
        LUnk11 = other.LUnk11;
        LUnk12 = other.LUnk12;
        UnkVector_1 = other.UnkVector_1;
        UnkVector_2 = other.UnkVector_2;
        LUnk13 = other.LUnk13;
        LUnk14 = other.LUnk14;
        LUnk15 = other.LUnk15;
        UnkVector_3 = other.UnkVector_3;
        LUnk16 = other.LUnk16;
        LUnk17 = other.LUnk17;
        LUnk18 = other.LUnk18;
        UnkByte2 = other.UnkByte2;
        LUnk19 = other.LUnk19;
        LUnk20 = other.LUnk20;
        LUnk21 = other.LUnk21;
        LUnk22 = other.LUnk22;
        LUnk23 = other.LUnk23;
        ProjectionTexture = new HashName(other.ProjectionTexture);
        UnkInt2 = other.UnkInt2;
        LUnk24 = other.LUnk24;
        LUnk25 = other.LUnk25;
        UnkVector_4 = other.UnkVector_4;
        LUnk26 = other.LUnk26;
        LUnk27 = other.LUnk27;
        LUnk28 = other.LUnk28;
        LUnk29 = other.LUnk29;
        LUnk30 = other.LUnk30;
        UnkVector_5 = other.UnkVector_5;
        LUnk31 = other.LUnk31;
        LUnk32 = other.LUnk32;
        LUnk33 = other.LUnk33;
        LUnk34 = other.LUnk34;
        LUnk35 = other.LUnk35;

        TextureHashes = new HashName[4];
        for (int i = 0; i < 4; i++)
        {
            TextureHashes[i] = new HashName(other.TextureHashes[i].String);
        }

        UnkBox = other.UnkBox;
        UnkByte3 = other.UnkByte3;
        UnknownMatrix = MatrixExtensions.CopyFrom(other.UnknownMatrix);
    }


    public override string ToString()
    {
        return base.ToString();
    }
}
