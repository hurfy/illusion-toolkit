using System.Numerics;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectArea : FrameObjectJoint
{
    int unk01;
    int planesSize;
    Vector4[] planes;
    BoundingBox bounds;

    public int Unk01
    {
        get { return unk01; }
        set { unk01 = value; }
    }
    public int PlaneSize
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

    public FrameObjectArea(FrameResource OwningResource) : base(OwningResource)
    {
        unk01 = 0;
        planesSize = 0;
        planes = new Vector4[planesSize];
        bounds = new BoundingBox();
    }

    public FrameObjectArea(FrameObjectArea other) : base(other)
    {
        unk01 = other.unk01;
        planesSize = other.planesSize;
        planes = other.planes;
        bounds = other.bounds;
    }

    public void FillPlanesArray()
    {
        planes = new Vector4[6];
        planes[0] = new Vector4(-1, 0, 0, bounds.Max.X);
        planes[1] = new Vector4(1, 0, 0, bounds.Max.X);
        planes[2] = new Vector4(0, -1, 0, bounds.Max.Y);
        planes[3] = new Vector4(0, 1, 0, bounds.Max.Y);
        planes[4] = new Vector4(0, 0, -1, bounds.Max.Z);
        planes[5] = new Vector4(0, 0, 1, bounds.Max.Z);
    }

    public override string ToString()
    {
        return Name.String;
    }

}
