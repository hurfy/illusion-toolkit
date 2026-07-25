namespace Illusion.Formats.Frames.Resources;

public class FrameSkeletonHierarchy : FrameEntry
{
    byte[] parentIndices;
    byte[] lastChildIndices;
    byte unkNum;
    byte[] unkData;

    public byte[] ParentIndices
    {
        get { return parentIndices; }
        set { parentIndices = value; }
    }
    public byte[] LastChildIndices
    {
        get { return lastChildIndices; }
        set { lastChildIndices = value; }
    }
    public byte Unk01
    {
        get { return unkNum; }
        set { unkNum = value; }
    }
    public byte[] UnkData
    {
        get { return unkData; }
        set { unkData = value; }
    }

    public FrameSkeletonHierarchy(FrameResource OwningResource) : base(OwningResource)
    {
        parentIndices = new byte[0];
        lastChildIndices = new byte[0];
        unkData = new byte[0];
    }

    public override string ToString()
    {
        return $"Skeleton Hierarchy Block";
    }
}
