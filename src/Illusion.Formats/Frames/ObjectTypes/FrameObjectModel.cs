using System.Numerics;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectModel : FrameObjectSingleMesh
{
    private FrameSkeleton? _skeleton;
    private FrameBlendInfo? _blendInfo;
    private FrameSkeletonHierarchy? _hierarchy;

    int blendInfoIndex;
    int skeletonIndex;
    int skeletonHierarchyIndex;
    Matrix4x4[] restTransform = null!;
    Matrix4x4 unkTransform;
    AttachmentReference[] attachmentReferences = null!;
    uint unkFlags;
    int physSplitSize;
    int hitBoxSize;
    short nPhysSplits;
    WeightedByMeshSplit[] blendMeshSplits = null!;
    HitBoxInfo[] hitBoxInfo = null!;
    public int BlendInfoIndex
    {
        get { return blendInfoIndex; }
        set { blendInfoIndex = value; }
    }
    public int SkeletonIndex
    {
        get { return skeletonIndex; }
        set { skeletonIndex = value; }
    }
    public int SkeletonHierarchyIndex
    {
        get { return skeletonHierarchyIndex; }
        set { skeletonHierarchyIndex = value; }
    }
    public WeightedByMeshSplit[] BlendMeshSplits
    {
        get { return blendMeshSplits; }
        set { blendMeshSplits = value; }
    }
    public Matrix4x4[] RestTransform
    {
        get { return restTransform; }
        set { restTransform = value; }
    }
    public Matrix4x4 UnkTransform
    {
        get { return unkTransform; }
        set { unkTransform = value; }
    }
    public HitBoxInfo[] HitBoxes
    {
        get { return hitBoxInfo; }
        set { hitBoxInfo = value; }
    }
    public AttachmentReference[] AttachmentReferences
    {
        get { return attachmentReferences; }
        set { attachmentReferences = value; }
    }
    public uint UnkFlags
    {
        get { return unkFlags; }
        set { unkFlags = value; }
    }
    public FrameSkeleton Skeleton
    {
        get { return GetSkeletonObject(); }
        set { _skeleton = value; }
    }
    public FrameBlendInfo BlendInfo
    {
        get { return GetBlendInfoObject(); }
        set { _blendInfo = value; }
    }
    public FrameSkeletonHierarchy SkeletonHierarchy
    {
        get { return GetSkeletonHierarchyObject(); }
        set { _hierarchy = value; }
    }

    /// <summary>The three private on-disk counters, exposed for the native-boundary mapper
    /// (they are read/written verbatim; nPhysSplits is only stored when physSplitSize > 0).</summary>
    internal (int PhysSplitSize, int HitBoxSize, short NPhysSplits) SplitCounters
    {
        get { return (physSplitSize, hitBoxSize, nPhysSplits); }
        set { physSplitSize = value.PhysSplitSize; hitBoxSize = value.HitBoxSize; nPhysSplits = value.NPhysSplits; }
    }

    public FrameObjectModel(FrameResource OwningResource) : base(OwningResource) { }

    public FrameObjectModel(FrameObjectSingleMesh other) : base(other)
    {
        restTransform = new Matrix4x4[0];
        attachmentReferences = new AttachmentReference[0];
        blendMeshSplits = new WeightedByMeshSplit[0];
        hitBoxInfo = new HitBoxInfo[0];
    }

    public FrameObjectModel(FrameObjectModel other) : base(other)
    {
        blendInfoIndex = other.blendInfoIndex;
        skeletonIndex = other.skeletonIndex;
        skeletonHierarchyIndex = other.skeletonHierarchyIndex;
        _skeleton = other._skeleton;
        _blendInfo = other._blendInfo;

        restTransform = new Matrix4x4[_skeleton!.NumBones[0]];
        for (int i = 0; i != restTransform.Length; i++)
        {
            restTransform[i] = MatrixExtensions.CopyFrom(other.restTransform[i]);
        }

        unkTransform = MatrixExtensions.CopyFrom(other.unkTransform);

        attachmentReferences = new AttachmentReference[other.attachmentReferences.Length];
        for (int i = 0; i != attachmentReferences.Length; i++)
        {
            attachmentReferences[i] = new AttachmentReference(other.attachmentReferences[i]);
        }

        unkFlags = other.unkFlags;
        physSplitSize = other.physSplitSize;
        hitBoxSize = other.hitBoxSize;
        nPhysSplits = other.nPhysSplits;

        blendMeshSplits = new WeightedByMeshSplit[nPhysSplits];
        for (int i = 0; i != blendMeshSplits.Length; i++)
        {
            blendMeshSplits[i] = new WeightedByMeshSplit(other.blendMeshSplits[i]);
        }

        hitBoxInfo = new HitBoxInfo[other.hitBoxInfo.Length];
        for (int i = 0; i != hitBoxInfo.Length; i++)
        {
            hitBoxInfo[i] = new HitBoxInfo(other.hitBoxInfo[i]);
        }
    }

    protected FrameBlendInfo ConstructBlendInfoObject()
    {
        _blendInfo = OwningResource.ConstructFrameAssetOfType<FrameBlendInfo>();
        AddRef(FrameEntryRefTypes.BlendInfo, _blendInfo.RefID);
        return _blendInfo;
    }
    protected FrameSkeletonHierarchy ConstructSkeletonHierarchyObject()
    {
        SkeletonHierarchy = OwningResource.ConstructFrameAssetOfType<FrameSkeletonHierarchy>();
        AddRef(FrameEntryRefTypes.SkeletonHierarchy, SkeletonHierarchy.RefID);
        return SkeletonHierarchy;
    }

    protected FrameSkeleton ConstructSkeletonObject()
    {
        _skeleton = OwningResource.ConstructFrameAssetOfType<FrameSkeleton>();
        AddRef(FrameEntryRefTypes.Skeleton, _skeleton.RefID);
        return _skeleton;
    }

    public FrameBlendInfo GetBlendInfoObject()
    {
        if (_blendInfo == null)
        {
            return ConstructBlendInfoObject();
        }

        return _blendInfo;
    }

    public FrameSkeletonHierarchy GetSkeletonHierarchyObject()
    {
        if (_hierarchy == null)
        {
            return ConstructSkeletonHierarchyObject();
        }

        return _hierarchy;
    }

    public FrameSkeleton GetSkeletonObject()
    {
        if (_skeleton == null)
        {
            return ConstructSkeletonObject();
        }

        return _skeleton;
    }

    public override string ToString()
    {
        return string.Format("{0}", Name.ToString());
    }

    public class AttachmentReference
    {
        int attachmentIndex;
        byte jointIndex;

        //not saved
        string jointName = null!;
        FrameObjectBase? attachment;
        public int AttachmentIndex
        {
            get { return attachmentIndex; }
            set { attachmentIndex = value; }
        }
        public byte JointIndex
        {
            get { return jointIndex; }
            set { jointIndex = value; }
        }
        public string JointName
        {
            get { return jointName; }
            set { jointName = value; }
        }
        public FrameObjectBase? Attachment
        {
            get { return attachment; }
            set { attachment = value; }
        }

        public AttachmentReference()
        {
            attachmentIndex = -1;
            jointIndex = 0;
            jointName = string.Empty;
            attachment = null;
        }

        public AttachmentReference(AttachmentReference other)
        {
            attachmentIndex = other.attachmentIndex;
            jointIndex = other.jointIndex;
        }
    }


    public class HitBoxInfo
    {
        uint unk;
        Short3 pos = null!;
        Short3 size = null!;

        public uint Unk
        {
            get { return unk; }
            set { unk = value; }
        }
        public Short3 Position
        {
            get { return pos; }
            set { pos = value; }
        }
        public Short3 Size
        {
            get { return size; }
            set { size = value; }
        }

        /// <summary>Empty hit box for the native-boundary mapper.</summary>
        internal HitBoxInfo()
        {
        }

        public HitBoxInfo(HitBoxInfo other)
        {
            unk = other.unk;
            pos = new Short3(other.pos);
            size = new Short3(other.size);
        }
    }
    public class WeightedByMeshSplit
    {
        ushort blendIndex;
        BlendMeshSplitInfo[] data = null!;
        string jointName;

        public ushort BlendIndex
        {
            get { return blendIndex; }
            set { blendIndex = value; }
        }
        public BlendMeshSplitInfo[] Data
        {
            get { return data; }
            set { data = value; }
        }
        public string JointName
        {
            get { return jointName; }
            set { jointName = value; }
        }

        /// <summary>Empty split for the native-boundary mapper.</summary>
        internal WeightedByMeshSplit()
        {
            jointName = "";
        }

        public WeightedByMeshSplit(WeightedByMeshSplit other)
        {
            blendIndex = other.blendIndex;
            data = new BlendMeshSplitInfo[other.data.Length];
            for (int i = 0; i != other.data.Length; i++)
                data[i] = other.data[i];
            jointName = other.jointName;
        }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(jointName))
            {
                return jointName.ToString();
            }

            return "";
        }
    }

    public class BlendMeshSplitInfo
    {
        MiniMaterialBurst[] data = null!;

        public MiniMaterialBurst[] Data
        {
            get { return data; }
            set { data = value; }
        }

        /// <summary>Empty info for the native-boundary mapper.</summary>
        internal BlendMeshSplitInfo()
        {
        }

        public BlendMeshSplitInfo(BlendMeshSplitInfo other)
        {
            data = new MiniMaterialBurst[other.data.Length];
            for (int i = 0; i != other.data.Length; i++)
                data[i] = other.data[i];
        }
    }

    public class MiniMaterialBurst
    {
        ushort materialIndex;
        FacesBurst[] data = null!;

        public ushort MaterialIndex
        {
            get { return materialIndex; }
            set { materialIndex = value; }
        }
        public FacesBurst[] Data
        {
            get { return data; }
            set { data = value; }
        }

        /// <summary>Empty burst for the native-boundary mapper.</summary>
        internal MiniMaterialBurst()
        {
        }

        public MiniMaterialBurst(MiniMaterialBurst other)
        {
            materialIndex = other.materialIndex;
            data = new FacesBurst[other.data.Length];
            for (int i = 0; i != other.data.Length; i++)
                data[i] = other.data[i];
        }
    }

    public class FacesBurst
    {
        ushort startIndex;
        ushort numFaces;

        public ushort StartIndex
        {
            get { return startIndex; }
            set { startIndex = value; }
        }
        public ushort NumFaces
        {
            get { return numFaces; }
            set { numFaces = value; }
        }

        /// <summary>Empty burst for the native-boundary mapper.</summary>
        internal FacesBurst()
        {
        }

        public FacesBurst(FacesBurst other)
        {
            startIndex = other.startIndex;
            numFaces = other.numFaces;
        }
    }
}
