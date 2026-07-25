using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;

namespace Illusion.Formats.Frames;

public class FrameHeader
{

    bool isScene = false;
    int numFolderNames = 0;
    int numGeometries = 0;
    int numMaterialResources = 0;
    int numBlendInfos = 0;
    int numSkeletons = 0;
    int numSkelHierachies = 0;
    int numObjects = 0;

    HashName sceneName;
    List<FrameHeaderScene> sceneFolders;

    float unk1;
    float unk2;
    bool unk3;
    float[] unkData = new float[4 * 3];

    public bool IsScene
    {
        get { return isScene; }
        set { isScene = value; }
    }
    public int NumFolderNames
    {
        get { return numFolderNames; }
        set { numFolderNames = value; }
    }
    public int NumGeometries
    {
        get { return numGeometries; }
        set { numGeometries = value; }
    }
    public int NumMaterialResources
    {
        get { return numMaterialResources; }
        set { numMaterialResources = value; }
    }
    public int NumObjects
    {
        get { return numObjects; }
        set { numObjects = value; }
    }
    public int NumBlendInfos
    {
        get { return numBlendInfos; }
        set { numBlendInfos = value; }
    }
    public int NumSkeletons
    {
        get { return numSkeletons; }
        set { numSkeletons = value; }
    }
    public int NumSkelHierachies
    {
        get { return numSkelHierachies; }
        set { numSkelHierachies = value; }
    }
    public HashName SceneName
    {
        get { return sceneName; }
        set { sceneName = value; }
    }
    public float Unk1
    {
        get { return unk1; }
        set { unk1 = value; }
    }
    public float Unk2
    {
        get { return unk2; }
        set { unk2 = value; }
    }
    public bool Unk3
    {
        get { return unk3; }
        set { unk3 = value; }
    }
    public float[] UnkFloats
    {
        get { return unkData; }
        set { unkData = value; }
    }
    public List<FrameHeaderScene> SceneFolders
    {
        get { return sceneFolders; }
        set { sceneFolders = value; }
    }

    public FrameHeader()
    {
        sceneFolders = new List<FrameHeaderScene>();
        sceneName = new HashName();
        unkData = new float[4 * 3];
    }

    public override string ToString()
    {
        return string.Format("{0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}", isScene, numFolderNames, numGeometries, numMaterialResources, numBlendInfos, numSkeletons, numSkelHierachies, numObjects);
    }
}
