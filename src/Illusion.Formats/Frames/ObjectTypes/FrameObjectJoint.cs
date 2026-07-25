using Illusion.Formats.Hashing;

namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectJoint : FrameObjectBase
{

    byte dataSize;
    NodeStruct[] nodeData;
    public byte DataSize
    {
        get { return dataSize; }
        set { dataSize = value; }
    }
    public NodeStruct[] Data
    {
        get { return nodeData; }
        set { nodeData = value; }
    }

    public FrameObjectJoint(FrameResource OwningResource) : base(OwningResource)
    {
        dataSize = 0;
        nodeData = new NodeStruct[dataSize];

        for (int i = 0; i != dataSize; i++)
        {
            nodeData[i] = new NodeStruct();
        }
    }

    public FrameObjectJoint(FrameObjectJoint other) : base(other)
    {
        dataSize = other.dataSize;
        nodeData = other.nodeData;
    }

    public override string ToString()
    {
        return name.ToString();
    }

    public struct NodeStruct
    {

        int unk1;
        HashName unk2;
        HashName unk3;

        public int Unk_01
        {
            get { return unk1; }
            set { unk1 = value; }
        }
        public HashName Unk_02_Hash
        {
            get { return unk2; }
            set { unk2 = value; }
        }
        public HashName Unk_03_Hash
        {
            get { return unk3; }
            set { unk3 = value; }
        }

    }

}

