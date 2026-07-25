namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectCollision : FrameObjectBase
{
    private ulong _hash;

    /// <summary>FNV64 of the collision mesh this frame instantiates (resolved against the streamed
    /// Collisions resource, which this toolkit does not load into the scene graph).</summary>
    public ulong Hash
    {
        get { return _hash; }
        set { _hash = value; }
    }

    public FrameObjectCollision(FrameResource OwningResource) : base(OwningResource)
    {
        _hash = 0;
    }

    public FrameObjectCollision(FrameObjectCollision other) : base(other)
    {
        _hash = other._hash;
    }

    public override string ToString()
    {
        return string.Format("{0}", Name);
    }
}
