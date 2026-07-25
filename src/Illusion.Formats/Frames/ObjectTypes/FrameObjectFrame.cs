using Illusion.Formats.Hashing;

namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectFrame : FrameObjectJoint
{
    HashName actorHash;

    public HashName ActorHash
    {
        get { return actorHash; }
        set { actorHash = value; }
    }

    public FrameObjectFrame(FrameObjectFrame other) : base(other)
    {
        actorHash = other.actorHash;
    }

    public FrameObjectFrame(FrameResource OwningResource) : base(OwningResource)
    {
        actorHash = new HashName();
    }

    public override string ToString()
    {
        return string.Format("{0}", Name);
    }

}
