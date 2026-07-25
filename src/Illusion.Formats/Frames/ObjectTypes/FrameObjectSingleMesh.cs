using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectSingleMesh : FrameObjectJoint
{
    SingleMeshFlags flags;
    BoundingBox bounds;
    int meshIndex;
    int materialIndex;
    HashName omTextureHash;
    byte unk18_1;
    byte unk18_2;
    byte unk18_3;

    private FrameMaterial? _material;
    private FrameGeometry? _geometry;

    public SingleMeshFlags SingleMeshFlags
    {
        get { return flags; }
        set { flags = value; }
    }
    public BoundingBox Boundings
    {
        get { return bounds; }
        set { bounds = value; }
    }
    public byte DeformPartIndex { get; set; }
    public int MeshIndex
    {
        get { return meshIndex; }
        set { meshIndex = value; }
    }
    public int MaterialIndex
    {
        get { return materialIndex; }
        set { materialIndex = value; }
    }
    public HashName OMTextureHash
    {
        get { return omTextureHash; }
        set { omTextureHash = value; }
    }
    public byte Unk_18_1
    {
        get { return unk18_1; }
        set { unk18_1 = value; }
    }
    public byte Unk_18_2
    {
        get { return unk18_2; }
        set { unk18_2 = value; }
    }
    public byte Unk_18_3
    {
        get { return unk18_3; }
        set { unk18_3 = value; }
    }
    public FrameGeometry Geometry
    {
        get { return GetGeometry(); }
        set { _geometry = value; }
    }
    public FrameMaterial Material
    {
        get { return GetMaterial(); }
        set { _material = value; }
    }

    public FrameObjectSingleMesh(FrameObjectSingleMesh other) : base(other)
    {
        flags = other.flags;
        bounds = other.bounds;
        DeformPartIndex = other.DeformPartIndex;
        meshIndex = other.meshIndex;
        materialIndex = other.materialIndex;
        omTextureHash = new HashName(other.omTextureHash.String);
        unk18_1 = other.unk18_1;
        unk18_2 = other.unk18_2;
        unk18_3 = other.unk18_3;
        _material = other._material;
        _geometry = other._geometry;
    }

    public FrameObjectSingleMesh(FrameResource OwningResource) : base(OwningResource)
    {
        flags = SingleMeshFlags.Unk14_Flag | SingleMeshFlags.flag_32 | SingleMeshFlags.flag_67108864;
        bounds = new BoundingBox();
        DeformPartIndex = 255;
        meshIndex = 0;
        materialIndex = 0;
        omTextureHash = new HashName();
        unk18_1 = 0;
        unk18_2 = 0;
        unk18_3 = 0;
    }


    protected override void SanitizeOnSave()
    {
        base.SanitizeOnSave();

        /* Start check regarding OM Flag */

        // Check if OM Texture is valid
        bool bIsOMTextureValid = (omTextureHash.Hash != 0);

        // Cache-Off flag
        bool bHasOMFlag = flags.HasFlag(SingleMeshFlags.OM_Flag);

        // If we have the flag but we it isn't valid
        if (bHasOMFlag && !bIsOMTextureValid)
        {
            // Remove flag
            flags &= ~SingleMeshFlags.OM_Flag;
        }

        // If we have a valid hash but don't have the flag.
        if (bIsOMTextureValid && !bHasOMFlag)
        {
            // Add flag
            flags |= SingleMeshFlags.OM_Flag;
        }
        /* End check regarding OM Flag */
    }

    protected FrameMaterial ConstructMaterialObject()
    {
        Material = OwningResource.ConstructFrameAssetOfType<FrameMaterial>();
        AddRef(FrameEntryRefTypes.Material, Material.RefID);
        return Material;
    }

    protected FrameGeometry ConstructGeometryObject()
    {
        _geometry = OwningResource.ConstructFrameAssetOfType<FrameGeometry>();
        AddRef(FrameEntryRefTypes.Geometry, _geometry.RefID);
        return _geometry;
    }

    public FrameMaterial GetMaterial()
    {
        if (_material == null)
        {
            return ConstructMaterialObject();
        }

        return _material;
    }

    public FrameGeometry GetGeometry()
    {
        if (_geometry == null)
        {
            return ConstructGeometryObject();
        }

        return _geometry;
    }

    public override string ToString()
    {
        return string.Format("{0}", Name);
    }

    public IndexBuffer? GetIndexBuffer(int lod)
    {
        return OwningResource.IndexBuffers.GetBuffer(Geometry.LOD[lod].IndexBufferRef.Hash);
    }

    public VertexBuffer? GetVertexBuffer(int lod)
    {
        return OwningResource.VertexBuffers.GetBuffer(Geometry.LOD[lod].VertexBufferRef.Hash);
    }


}
