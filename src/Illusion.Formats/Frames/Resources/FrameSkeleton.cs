using System.Numerics;
using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Frames.Resources;

public class FrameSkeleton : FrameEntry
{
    int[] numBones = new int[4];
    int numBlendIDs;
    byte idType;

    // Name of bones
    HashName[] boneNames;

    // maybe joint space
    Matrix4x4[] jointTransforms;
    int numUnkCount2;

    // This stores if the LOD vertices use the bone - does not mean exclude bone from skeleton.
    // We'd only add whether or not the bone is used if the models vertices has any weight to the bone
    byte[] boneLODUsage = null!;

    //world space = extract position matrix, extract rotation matrix, multiply -position * rotation
    Matrix4x4[] worldTransforms;

    MappingForBlendingInfo[] mappingForBlendingInfos;

    // TODO: boneNames, boneLODUsage and jointTransforms all could be stored as same class

    public int[] NumBones
    {
        get { return numBones; }
        set { numBones = value; }
    }
    public int NumBlendIDs
    {
        get { return numBlendIDs; }
        set { numBlendIDs = value; }
    }

    // How many Remap IDs are present for the LOD. This must match LOD count.
    public int[] LodRemapIDCount { get; set; }

    public byte IDType
    {
        get { return idType; }
        set { idType = value; }
    }
    public HashName[] BoneNames
    {
        get { return boneNames; }
        set { boneNames = value; }
    }
    public Matrix4x4[] JointTransforms
    {
        get { return jointTransforms; }
        set { jointTransforms = value; }
    }
    public int NumUnkCount2
    {
        get { return numUnkCount2; }
        set { numUnkCount2 = value; }
    }
    public byte[] BoneLODUsage
    {
        get { return boneLODUsage; }
        set { boneLODUsage = value; }
    }
    public Matrix4x4[] WorldTransforms
    {
        get { return worldTransforms; }
        set { worldTransforms = value; }
    }
    public MappingForBlendingInfo[] MappingForBlendingInfos
    {
        get { return mappingForBlendingInfos; }
        set { mappingForBlendingInfos = value; }
    }

    public FrameSkeleton(FrameResource OwningResource) : base(OwningResource)
    {
        numBones = new int[4];
        LodRemapIDCount = new int[0];
        boneNames = new HashName[0];
        jointTransforms = new Matrix4x4[0];
        worldTransforms = new Matrix4x4[0];
        mappingForBlendingInfos = new MappingForBlendingInfo[0];
    }

    public override string ToString()
    {
        return "Skeleton Block";
    }

    public struct MappingForBlendingInfo
    {
        BoundingBox[] bounds;
        byte[] refToUsageArray;
        byte[] usageArray;

        public BoundingBox[] Bounds
        {
            get { return bounds; }
            set { bounds = value; }
        }

        // TODO: This is loaded using Bone Count in Skeleton
        // Can we determine what this is? My suspicion is an easy lookup between Bone -> Remapped ID.
        // This may be a code side of finding the remapped vertices using the bone ID.
        public byte[] RefToUsageArray
        {
            get { return refToUsageArray; }
            set { refToUsageArray = value; }
        }

        // TODO: This is loaded using Remap Count for each LOD.
        // Can we determine what this is? My suspicion is how many times the Remap ID is used.
        // But does that mean across Materials, or per vertex? And does it cross Material boundaries?
        public byte[] UsageArray
        {
            get { return usageArray; }
            set { usageArray = value; }
        }
    }
}
