using System.Numerics;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectDummy : FrameObjectJoint
{
    private BoundingBox _bounds;

    public BoundingBox Bounds
    {
        get { return _bounds; }
        set { _bounds = value; }
    }
    public Vector3 BoundaryBoxMinimum
    {
        get { return _bounds.Min; }
        set { _bounds.SetMinimum(value); }
    }
    public Vector3 BoundaryBoxMaximum
    {
        get { return _bounds.Max; }
        set { _bounds.SetMaximum(value); }
    }

    public FrameObjectDummy(FrameObjectDummy other) : base(other)
    {
        _bounds = other._bounds;
    }

    public FrameObjectDummy(FrameResource OwningResource) : base(OwningResource)
    {
        _bounds = new BoundingBox();
    }

    public override string ToString()
    {
        return name.ToString();
    }

}
