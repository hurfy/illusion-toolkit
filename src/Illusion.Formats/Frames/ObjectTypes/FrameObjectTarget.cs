namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectTarget : FrameObjectJoint
{
    private int _unk01;
    private int _unk02;

    public int Unk01
    {
        get { return _unk01; }
        set { _unk01 = value; }
    }
    public int Unk02
    {
        get { return _unk02; }
        set { _unk02 = value; }
    }

    public FrameObjectTarget(FrameResource OwningResource) : base(OwningResource) { }

    public FrameObjectTarget(FrameObjectTarget other) : base(other)
    {
        _unk01 = other._unk01;
        _unk02 = other._unk02;
    }
}
