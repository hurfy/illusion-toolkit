using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;

namespace Illusion.Formats.Frames.Resources;

public class FrameHeaderScene : FrameEntry
{
    HashName _name = null!;

    List<FrameObjectBase> _children = new List<FrameObjectBase>();

    public List<FrameObjectBase> Children
    {
        get { return _children; }
        set { _children = value; }
    }

    public HashName Name
    {
        get { return _name; }
        set { _name = value; }
    }

    public FrameHeaderScene(FrameResource OwningResource) : base(OwningResource) { }

    public override string ToString()
    {
        return Name.String;
    }
}
