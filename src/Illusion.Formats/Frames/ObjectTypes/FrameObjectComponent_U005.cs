namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectComponent_U005 : FrameObjectBase
{
    int unk01;

    public int Unk01
    {
        get { return unk01; }
        set { unk01 = value; }
    }
    public FrameObjectComponent_U005(FrameResource OwningResource) : base(OwningResource)
    {
        unk01 = 0;
    }

    public FrameObjectComponent_U005(FrameObjectComponent_U005 other) : base(other)
    {
        unk01 = other.unk01;
    }
}

