using System.Numerics;
using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectSector : FrameObjectJoint
{
    int unk_08_int;
    int planesSize;
    Vector4[] planes = null!;
    BoundingBox bounds;
    Vector3 unk_13_vector3;
    Vector3 unk_14_vector3;
    HashName sectorName;

    public int Unk08
    {
        get { return unk_08_int; }
        set { unk_08_int = value; }
    }
    public int PlanesSize
    {
        get { return planesSize; }
        set { planesSize = value; }
    }
    public Vector4[] Planes
    {
        get { return planes; }
        set { planes = value; }
    }
    public BoundingBox Bounds
    {
        get { return bounds; }
        set { bounds = value; }
    }
    public Vector3 BoundaryBoxMinimum
    {
        get { return bounds.Min; }
        set { bounds.SetMinimum(value); }
    }
    public Vector3 BoundaryBoxMaximum
    {
        get { return bounds.Max; }
        set { bounds.SetMaximum(value); }
    }
    public Vector3 Unk13
    {
        get { return unk_13_vector3; }
        set { unk_13_vector3 = value; }
    }
    public Vector3 Unk14
    {
        get { return unk_14_vector3; }
        set { unk_14_vector3 = value; }
    }
    public HashName SectorName
    {
        get { return sectorName; }
        set { sectorName = value; }
    }

    public FrameObjectSector(FrameResource OwningResource) : base(OwningResource)
    {
        bounds = new BoundingBox();
        unk_13_vector3 = new Vector3(0);
        unk_14_vector3 = new Vector3(0);
        sectorName = new HashName();
    }

    public FrameObjectSector(FrameObjectSector other) : base(other)
    {
        bounds = other.bounds;
        unk_08_int = other.unk_08_int;
        planesSize = other.planesSize;
        planes = new Vector4[planesSize];
        for (int i = 0; i < planesSize; i++)
        {
            planes[i] = other.planes[i];
        }
        unk_13_vector3 = other.unk_13_vector3;
        unk_14_vector3 = other.unk_14_vector3;
        sectorName = new HashName(other.sectorName.String);
    }

    public void FillPlanesArray()
    {
        planes = new Vector4[6];
        planes[0] = new Vector4(0, 0, 1, Math.Abs(bounds.Min.Z));
        planes[1] = new Vector4(1, 0, 0, Math.Abs(bounds.Max.X));
        planes[2] = new Vector4(0, -1, 0, Math.Abs(bounds.Max.Y));
        planes[3] = new Vector4(0, 1, 0, Math.Abs(bounds.Max.Y));
        planes[4] = new Vector4(0, 0, -1, Math.Abs(bounds.Max.Z));
        planes[5] = new Vector4(-1, 0, 0, Math.Abs(bounds.Max.X));
        //planes[0] = new Vector4(-1, 0, 0, bounds.Max.X);
        //planes[1] = new Vector4(1, 0, 0, bounds.Max.X);
        //planes[2] = new Vector4(0, -1, 0, bounds.Max.Y);
        //planes[3] = new Vector4(0, 1, 0, bounds.Max.Y);
        //planes[4] = new Vector4(0, 0, -1, bounds.Max.Z);
        //planes[5] = new Vector4(0, 0, 1, bounds.Max.Z);
        ////planes[0] = new Vector4(0, 1, 0, Math.Abs(bounds.Min.X));
        ////planes[1] = new Vector4(1, 0, 0, Math.Abs(bounds.Min.Y));
        ////planes[2] = new Vector4(0, 0, -1, bounds.Max.Z);
        ////planes[3] = new Vector4(0, 0, 1, Math.Abs(bounds.Min.Y));
        ////planes[4] = new Vector4(-1, 0, 0, bounds.Max.X);
        ////planes[5] = new Vector4(0, -1, 0, bounds.Max.Y);
    }

    public override string ToString()
    {
        return Name.String;
    }

}
