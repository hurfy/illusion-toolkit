using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Frames.Resources;

public class FrameMaterial : FrameEntry
{

    uint numLods = 0;
    int[] lodMatCount;
    BoundingBox bounds;
    List<MaterialStruct[]> materials;

    public uint NumLods
    {
        get { return numLods; }
        set { numLods = value; }
    }
    public int[] LodMatCount
    {
        get { return lodMatCount; }
        set { lodMatCount = value; }
    }
    public BoundingBox Bounds
    {
        get { return bounds; }
        set { bounds = value; }
    }
    public List<MaterialStruct[]> Materials
    {
        get { return materials; }
        set { materials = value; }
    }

    public FrameMaterial(FrameMaterial other) : base(other)
    {
        bounds = other.bounds;
        numLods = other.numLods;
        lodMatCount = other.lodMatCount;
        materials = new List<MaterialStruct[]>();
        for (int i = 0; i < numLods; i++)
        {
            MaterialStruct[] array = new MaterialStruct[lodMatCount[i]];
            for (int d = 0; d < array.Length; d++)
            {
                array[d] = new MaterialStruct(other.materials[i][d]);
            }
            materials.Add(array);
        }
    }

    public FrameMaterial(FrameResource OwningResource) : base(OwningResource)
    {
        numLods = 0;
        lodMatCount = new int[0];
        materials = new List<MaterialStruct[]>();
        bounds = new BoundingBox();
    }

    public override string ToString()
    {
        return $"Material Block";
    }
}

public class MaterialStruct
{
    int numFaces;
    int startIndex;
    ulong materialHash;
    string materialName = null!;
    int unk3;

    public int NumFaces
    {
        get { return numFaces; }
        set { numFaces = value; }
    }
    public int StartIndex
    {
        get { return startIndex; }
        set { startIndex = value; }
    }
    public ulong MaterialHash
    {
        get { return materialHash; }
        set { materialHash = value; }
    }
    public string MaterialName
    {
        get { return materialName; }
        set { SetName(value); }
    }
    public int Unk3
    {
        get { return unk3; }
        set { unk3 = value; }
    }

    public MaterialStruct(MaterialStruct other)
    {
        numFaces = other.numFaces;
        startIndex = other.startIndex;
        materialHash = other.materialHash;
        materialName = other.materialName;
        unk3 = other.unk3;
    }

    public MaterialStruct()
    {
        numFaces = 0;
        startIndex = 0;
        materialHash = 0;
        materialName = "";
        unk3 = 0;
    }

    // The display name is NOT part of the stream — only the hash is. Resolving it against a loaded
    // material collection is the caller's concern (the vendored code reached into a global manager
    // here, coupling frame parsing to process-wide material state).
    public void SetName(string name)
    {
        materialName = name;
        materialHash = Fnv64.Hash(name);
    }

    public override string ToString()
    {
        return string.Format("{0}", materialName);
    }
}
