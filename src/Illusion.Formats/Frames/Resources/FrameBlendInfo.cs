using System.Numerics;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Frames.Resources;

public class FrameBlendInfo : FrameEntry
{
    BoneIndexInfo[] boneIndexInfos;
    BoundingBox bounds;
    BoneTransform[] boneTransforms;

    public BoneIndexInfo[] BoneIndexInfos
    {
        get { return boneIndexInfos; }
        set { boneIndexInfos = value; }
    }
    public BoneTransform[] BoneTransforms
    {
        get { return boneTransforms; }
        set { boneTransforms = value; }
    }
    public BoundingBox Bound
    {
        get { return bounds; }
        set { bounds = value; }
    }

    public FrameBlendInfo(FrameResource OwningResource) : base(OwningResource)
    {
        boneIndexInfos = new BoneIndexInfo[0];
        boneTransforms = new BoneTransform[0];
    }

    public override string ToString()
    {
        return "Blend Info Block";
    }

    public struct SkinnedMaterialInfo
    {
        // Stores the number of weights influencing the vertex within a facegroup.
        // Max number of weights per vertex is 4.
        public byte AssignedPoolIndex { get; set; }

        // Stores which pool of bones the material has been assigned to.
        // Each slot in the array is for a facegroup within the LOD
        public byte NumWeightsPerVertex { get; set; }
    }

    public struct BoneIndexInfo
    {
        // Number of bones within each Remap Pool, SkinnedMaterialInfo will refer to this.
        public byte[] BonesPerRemapPool { get; set; }

        // Remapping IDs for bones within the Skeletal Mesh for this LOD
        // Refer to @BonesPerPool to determine which range of bones is within each pool
        public byte[] BoneRemapIDs { get; set; }

        // Skinned Material data for this LOD
        public SkinnedMaterialInfo[] SkinnedMaterialInfo { get; set; }
    }

    public struct BoneTransform
    {
        Matrix4x4 transform;
        BoundingBox bounds;
        byte isValid;

        public Matrix4x4 Transform
        {
            get { return transform; }
            set { transform = value; }
        }
        public BoundingBox Bounds
        {
            get { return bounds; }
            set { bounds = value; }
        }
        public byte IsValid
        {
            get { return isValid; }
            set { isValid = value; }
        }

    }
}
