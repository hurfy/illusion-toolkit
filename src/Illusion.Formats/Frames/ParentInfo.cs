
namespace Illusion.Formats.Frames;

public class ParentInfo
{
    int _index;
    string _name = null!;
    int _refID;

    public int Index
    {
        get { return _index; }
        set { _index = value; }
    }

    public string Name
    {
        get { return _name; }
        set { _name = value; }
    }

    public int RefID
    {
        get { return _refID; }
        set { _refID = value; }
    }

    public enum ParentType
    {
        ParentIndex1,
        ParentIndex2
    }

    public ParentInfo(int index)
    {
        _index = index;
    }

    public ParentInfo(ParentInfo other)
    {
        _index = other._index;
        _name = other._name;
        _refID = other._refID;
    }

    public void SetParent(FrameEntry ParentEntry, int IndexOfParent)
    {
        SetParent(IndexOfParent, ParentEntry.ToString(), ParentEntry.RefID);
    }

    public void SetParent(int index, string name, int refID)
    {
        _index = index;
        _name = name;
        _refID = refID;
    }

    public void RemoveParent()
    {
        _index = -1;
        _name = "root";
        _refID = 0;
    }

    public override string ToString()
    {
        if (_index == -1)
        {
            return string.Format("{0}, root", _index);
        }

        return string.Format("{0}, {1}", _index, _name);
    }
}
