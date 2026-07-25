using System.Numerics;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectBase : FrameEntry
{
    protected HashName name;
    protected int secondaryFlags;
    protected short unk3;
    protected ParentInfo parentIndex1;
    protected ParentInfo parentIndex2;
    protected short unk6;
    protected bool isOnTable;
    protected NameTableFlags nameTableFlags;

    protected Matrix4x4 worldTransform = Matrix4x4.Identity;
    protected Matrix4x4 localTransform = Matrix4x4.Identity;

    FrameObjectBase? parent;
    FrameObjectBase? root;
    List<FrameObjectBase> _children = new List<FrameObjectBase>();

    public FrameObjectBase? Parent
    {
        get { return parent; }
        set { parent = value; }
    }
    public FrameObjectBase? Root
    {
        get { return root; }
        set { root = value; }
    }
    public List<FrameObjectBase> Children
    {
        get { return _children; }
        set { _children = value; }
    }

    public HashName Name
    {
        get { return name; }
        set { name = value; }
    }
    public int SecondaryFlags
    {
        get { return secondaryFlags; }
        set { secondaryFlags = value; }
    }
    public Matrix4x4 LocalTransform
    {
        get { return localTransform; }
        set { localTransform = value; SetWorldTransform(); }
    }
    public Matrix4x4 WorldTransform
    {
        get { SetWorldTransform(); return worldTransform; }
        set { worldTransform = value; }
    }
    public short Unk3
    {
        get { return unk3; }
        set { unk3 = value; }
    }
    public ParentInfo ParentIndex1
    {
        get { return parentIndex1; }
        set { parentIndex1 = value; }
    }
    public ParentInfo ParentIndex2
    {
        get { return parentIndex2; }
        set { parentIndex2 = value; }
    }
    public short Unk6
    {
        get { return unk6; }
        set { unk6 = value; }
    }
    public NameTableFlags FrameNameTableFlags
    {
        get { return nameTableFlags; }
        set { nameTableFlags = value; }
    }
    public bool IsOnFrameTable
    {
        get { return isOnTable; }
        set { isOnTable = value; }
    }
    public string Type
    {
        get { return GetType().ToString(); }
    }
    public int Index { get; set; }

    /// <summary>Parent indices exactly as they were read from the file, so a save can tell whether an edit — or a
    /// bug — changed this object's place in the hierarchy. See <c>FrameResource.UpdateFrameData</c>.</summary>
    public int LoadedParentIndex1 { get; private set; } = -1;
    public int LoadedParentIndex2 { get; private set; } = -1;

    /// <summary>Stamps the loaded parent indices from the current ParentInfo pair — the
    /// native-boundary mapper's equivalent of the read-time capture above.</summary>
    internal void MarkLoadedParents()
    {
        LoadedParentIndex1 = parentIndex1.Index;
        LoadedParentIndex2 = parentIndex2.Index;
    }

    /// <summary>Sets the local transform without the world-transform cascade — the mapper fills
    /// fields exactly like the file reader does (the cascade runs in DefineFrameBlockParents).</summary>
    internal void SetLocalTransformRaw(Matrix4x4 transform)
    {
        localTransform = transform;
    }

    /// <summary>Runs the on-save fixups (a protected hook) for the native-boundary writer.</summary>
    internal void SanitizeForSave()
    {
        SanitizeOnSave();
    }

    public FrameObjectBase(FrameResource OwningResource) : base(OwningResource)
    {
        //do example name.
        name = new HashName("NewObject");
        secondaryFlags = 1;
        localTransform = Matrix4x4.Identity;
        worldTransform = Matrix4x4.Identity;
        unk3 = -1;
        parentIndex1 = new ParentInfo(-1);
        parentIndex2 = new ParentInfo(-1);
        unk6 = -1;
    }

    public FrameObjectBase(FrameObjectBase other) : base(other)
    {
        name = new HashName(other.name.String);
        secondaryFlags = other.secondaryFlags;
        localTransform = other.localTransform;
        worldTransform = other.worldTransform;
        unk3 = other.unk3;
        parentIndex1 = new ParentInfo(other.parentIndex1);
        parentIndex2 = new ParentInfo(other.parentIndex2);
        unk6 = -1;
        isOnTable = other.isOnTable;
        nameTableFlags = other.nameTableFlags;
    }


    protected virtual void SanitizeOnSave() { }

    public void SetWorldTransform()
    {
        //The world transform is calculated and then decomposed because some reason,
        //the renderer does not update on the first startup of the editor.
        Vector3 position, scale, newPos;
        Quaternion rotation, newRot;
        Matrix4x4 parentTransform = Matrix4x4.Identity;
        // Frame matrices are R·S (see MatrixExtensions.SetMatrix) — Matrix4x4.Decompose assumes S·R and
        // silently fails for any rotated, non-uniformly scaled transform.
        MatrixExtensions.TryDecomposeRS(localTransform, out scale, out rotation, out position);
        worldTransform = Matrix4x4.Identity;

        if (parent != null)
        {
            parentTransform = parent.worldTransform;
        }
        else if (root != null)
        {
            parentTransform = root.worldTransform;
        }

        if (parent != null || root != null)
        {
            MatrixExtensions.TryDecomposeRS(parentTransform, out _, out Quaternion parentRotation, out _);

            newRot = parentRotation * rotation;
            newPos = Vector3Extensions.TransformCoordinate(position, parentTransform);
        }
        else
        {
            newRot = rotation;
            newPos = position;
        }

        worldTransform = MatrixExtensions.SetMatrix(newRot, scale, newPos);
        // Build the message only when it is actually needed: this runs for every node of a subtree
        // on every WorldTransform read, and pre-formatting threw away two strings per node per read.
        if (worldTransform.IsNaN())
        {
            FormatAssert.Ensure(false, "Frame: {0} caused NaN()!", name);
        }
        foreach (var child in _children)
        {
            child.SetWorldTransform();
        }
    }

    /// <summary>
    /// Writes one of the two parent slots. The slots are not interchangeable:
    /// <see cref="ParentInfo.ParentType.ParentIndex1"/> is the immediate hierarchy parent and owns
    /// <see cref="Parent"/> (the transform cascade), while <see cref="ParentInfo.ParentType.ParentIndex2"/> is the
    /// anchor — the scene the object streams under, or the top of its ParentIndex1 chain — and owns
    /// <see cref="Root"/>, which never cascades. Writing one slot must therefore not disturb the other's runtime
    /// link; doing so used to corrupt the cascade list and produce parent combinations the game never ships.
    /// </summary>
    public void SetParent(ParentInfo.ParentType ParentType, FrameEntry? NewParent)
    {
        bool isPrimary = ParentType == ParentInfo.ParentType.ParentIndex1;

        // Detach only the link this slot owns.
        if (isPrimary)
        {
            if (Parent != null)
            {
                Parent._children.Remove(this);
                Parent = null;
            }
        }
        else if (Root != null)
        {
            Root._children.Remove(this);
            Root = null;
        }

        if (NewParent == null) // -1 = no parent in this slot
        {
            RemoveParent(ParentType);
            return;
        }

        int index = (NewParent is FrameHeaderScene)
            ? OwningResource.FrameScenes.IndexOfValue(NewParent.RefID)
            : OwningResource.GetIndexOfObject(NewParent.RefID);

        if (NewParent is FrameObjectBase parentObject)
        {
            if (isPrimary)
            {
                parentObject._children.Add(this);
                Parent = parentObject;
            }
            else
            {
                Root = parentObject;
                // Mirror the loader: an anchor only claims the object as a child when nothing else parents it.
                if (Parent == null) parentObject._children.Add(this);
            }
        }

        InternalSetParent(ParentType, NewParent, index);
    }

    /// <summary>Clears both parent slots, making the object a true top-level frame (-1, -1).</summary>
    public void ClearBothParents()
    {
        SetParent(ParentInfo.ParentType.ParentIndex1, null);
        SetParent(ParentInfo.ParentType.ParentIndex2, null);
    }

    private void InternalSetParent(ParentInfo.ParentType ParentType, FrameEntry NewParent, int ParentIndex)
    {
        // Get type of FrameEntryRefType we want to replace/add
        FrameEntryRefTypes ParentRef = (ParentType == ParentInfo.ParentType.ParentIndex1) ?
            FrameEntryRefTypes.Parent1 : FrameEntryRefTypes.Parent2;

        ReplaceRef(ParentRef, NewParent.RefID);

        // Update ParentInfo
        if (ParentType == ParentInfo.ParentType.ParentIndex1)
        {
            ParentIndex1.SetParent(NewParent, ParentIndex);
            ReplaceRef(FrameEntryRefTypes.Parent1, NewParent.RefID);
        }
        else
        {
            ParentIndex2.SetParent(NewParent, ParentIndex);
            ReplaceRef(FrameEntryRefTypes.Parent2, NewParent.RefID);
        }
    }

    private void RemoveParent(ParentInfo.ParentType ParentType)
    {
        // Get type of FrameEntryRefType we want to remove
        FrameEntryRefTypes ParentRef = (ParentType == ParentInfo.ParentType.ParentIndex1) ?
            FrameEntryRefTypes.Parent1 : FrameEntryRefTypes.Parent2;

        // Remove the reference
        SubRef(ParentRef);

        // Remove the parent from the desired ParentIndex
        if (ParentType == ParentInfo.ParentType.ParentIndex1)
        {
            ParentIndex1.RemoveParent();
        }
        else
        {
            ParentIndex2.RemoveParent();
        }

    }

    public bool IsFrameOwnChildren(int newParentRefID)
    {
        if (RefID == newParentRefID)
        {
            return true;
        }

        foreach (var child in _children)
        {
            if (child.IsFrameOwnChildren(newParentRefID))
            {
                return true;
            }
        }
        return false;
    }

    public bool HasMeshObject()
    {
        foreach (var child in Children)
        {
            if (child is FrameObjectSingleMesh || child.HasMeshObject())
            {
                return true;
            }
        }

        return false;
    }
}
