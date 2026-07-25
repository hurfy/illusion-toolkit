using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using Illusion.Formats.Hashing;

namespace Illusion.Formats.Frames;

public class FrameResource
{
    /// <summary>The scene's merged buffer pools, attached by <see cref="ExtractedSds.Load"/> so
    /// mesh objects can resolve their index/vertex buffers. Null for a bare-parsed resource.</summary>
    public IndexBufferManager IndexBuffers { get; set; } = null!;
    public VertexBufferManager VertexBuffers { get; set; } = null!;

    FrameHeader _header;
    Dictionary<int, FrameHeaderScene> _frameScenes = new Dictionary<int, FrameHeaderScene>();
    Dictionary<int, FrameGeometry> _frameGeometries = new Dictionary<int, FrameGeometry>();
    Dictionary<int, FrameMaterial> _frameMaterials = new Dictionary<int, FrameMaterial>();
    Dictionary<int, FrameBlendInfo> _frameBlendInfos = new Dictionary<int, FrameBlendInfo>();
    Dictionary<int, FrameSkeleton> _frameSkeletons = new Dictionary<int, FrameSkeleton>();
    Dictionary<int, FrameSkeletonHierarchy> _frameSkeletonHierachies = new Dictionary<int, FrameSkeletonHierarchy>();
    Dictionary<int, object> _frameObjects = new Dictionary<int, object>();


    public FrameHeader Header
    {
        get { return _header; }
        set { _header = value; }
    }
    public Dictionary<int, FrameHeaderScene> FrameScenes
    {
        get { return _frameScenes; }
        set { _frameScenes = value; }
    }
    public Dictionary<int, FrameGeometry> FrameGeometries
    {
        get { return _frameGeometries; }
        set { _frameGeometries = value; }
    }
    public Dictionary<int, FrameMaterial> FrameMaterials
    {
        get { return _frameMaterials; }
        set { _frameMaterials = value; }
    }
    public Dictionary<int, FrameBlendInfo> FrameBlendInfos
    {
        get { return _frameBlendInfos; }
        set { _frameBlendInfos = value; }
    }
    public Dictionary<int, FrameSkeleton> FrameSkeletons
    {
        get { return _frameSkeletons; }
        set { _frameSkeletons = value; }
    }
    public Dictionary<int, FrameSkeletonHierarchy> FrameSkeletonHierachies
    {
        get { return _frameSkeletonHierachies; }
        set { _frameSkeletonHierachies = value; }
    }
    public Dictionary<int, object> FrameObjects
    {
        get { return _frameObjects; }
        set { _frameObjects = value; }
    }

    public int GetBlockCount
    {
        get { return _frameBlendInfos.Count + _frameGeometries.Count + _frameMaterials.Count + _frameSkeletons.Count + _frameSkeletonHierachies.Count + _frameScenes.Count; }
    }

    public int GetIndexOfObject(int refID)
    {
        for (int i = 0; i != _frameObjects.Count; i++)
        {
            if (_frameObjects.ElementAt(i).Key == refID)
                return i + (GetBlockCount);
        }
        return -1;
    }

    public FrameObjectBase GetObjectFromIndex(int index)
    {
        return (_frameObjects.ElementAt(index).Value as FrameObjectBase)!;
    }

    public FrameResource()
    {
        _header = new FrameHeader();
        _frameScenes = new Dictionary<int, FrameHeaderScene>();
        _frameGeometries = new Dictionary<int, FrameGeometry>();
        _frameMaterials = new Dictionary<int, FrameMaterial>();
        _frameBlendInfos = new Dictionary<int, FrameBlendInfo>();
        _frameSkeletons = new Dictionary<int, FrameSkeleton>();
        _frameSkeletonHierachies = new Dictionary<int, FrameSkeletonHierarchy>();
        _frameObjects = new Dictionary<int, object>();
    }

    public FrameResource(string file) : this()
    {
        using (MemoryStream reader = new MemoryStream(File.ReadAllBytes(file), false))
        {
            ReadFromFile(reader);
        }
    }

    public FrameHeaderScene AddSceneFolder(string name)
    {
        FrameHeaderScene scene = ConstructFrameAssetOfType<FrameHeaderScene>();
        scene.Name = new HashName(name);
        return scene;
    }

    /// <summary>Parses the resource from the stream's current position to its end (the byte-level
    /// work runs in the native core; console big-endian resources are not supported — the toolkit
    /// is PC-only).</summary>
    public void ReadFromFile(MemoryStream reader)
    {
        byte[] bytes = new byte[reader.Length - reader.Position];
        reader.ReadExactly(bytes);
        Native.Frames.FrameResourceReader.Populate(this, Native.Frames.NativeFrames.LoadFrameResource(bytes));
    }

    public void WriteToFile(string name)
    {
        using (BinaryWriter writer = new BinaryWriter(File.Open(name, FileMode.Create)))
        {
            WriteToFile(writer);
        }
    }

    public byte[] WriteToStream()
    {
        using (MemoryStream ms = new())
        {
            using (BinaryWriter writer = new(ms))
            {
                WriteToFile(writer);
            }

            return ms.ToArray();
        }
    }

    /// <summary>Serializes through the native core.</summary>
    public void WriteToFile(BinaryWriter writer)
    {
        //BEFORE WE WRITE, WE NEED TO COMPILE AND UPDATE THE FRAME.
        UpdateFrameData();
        writer.Write(Native.Frames.NativeFrames.SaveFrameResource(
            Native.Frames.FrameResourceWriter.ToWire(this)));
    }

    public void DuplicateBlocks(FrameObjectSingleMesh mesh)
    {
        FrameMaterial material = new FrameMaterial(mesh.Material);
        mesh.ReplaceRef(FrameEntryRefTypes.Material, material.RefID);
        mesh.Material = material;
        _frameMaterials.Add(material.RefID, material);
    }

    public void DuplicateBlocks(FrameObjectModel model)
    {
        DuplicateBlocks((FrameObjectSingleMesh)model);
    }

    public bool DeleteFrame(FrameEntry EntryToDelete)
    {
        // Early return out if its invalid
        if (EntryToDelete == null)
        {
            return false;
        }

        FrameObjectBase? BaseObject = (EntryToDelete as FrameObjectBase);
        if (BaseObject == null)
        {
            return false;
        }

        // Remove Parent reference
        FrameObjectBase? ParentObject = BaseObject.Parent;
        if (ParentObject != null)
        {
            bool bDeleted = ParentObject.Children.Remove(BaseObject);
            FormatAssert.Ensure(bDeleted, "Failed to delete an object which should be in the child array.");

            BaseObject.Parent = null!;
        }

        // Remove all children
        while (BaseObject.Children.Count > 0)
        {
            DeleteFrame(BaseObject.Children[0]);
        }

        // broadcast for other systems (no subscribers is the normal case in this app)

        // Remove frame from list
        return FrameObjects.Remove(EntryToDelete.RefID);
    }

    public bool DeleteScene(FrameHeaderScene Scene)
    {
        foreach (FrameObjectBase ChildObject in Scene.Children)
        {
            DeleteFrame(ChildObject);
        }

        return _frameScenes.Remove(Scene.RefID);
    }

    public T ConstructFrameAssetOfType<T>() where T : FrameEntry
    {
        T NewFrame = (T)Activator.CreateInstance(typeof(T), this)!;

        if (NewFrame is FrameObjectBase)
        {
            FrameObjects.Add(NewFrame.RefID, NewFrame);
        }

        if (NewFrame is FrameMaterial)
        {
            _frameMaterials.Add(NewFrame.RefID, (NewFrame as FrameMaterial)!);
        }

        if (NewFrame is FrameGeometry)
        {
            _frameGeometries.Add(NewFrame.RefID, (NewFrame as FrameGeometry)!);
        }

        if (NewFrame is FrameBlendInfo)
        {
            _frameBlendInfos.Add(NewFrame.RefID, (NewFrame as FrameBlendInfo)!);
        }

        if (NewFrame is FrameSkeletonHierarchy)
        {
            _frameSkeletonHierachies.Add(NewFrame.RefID, (NewFrame as FrameSkeletonHierarchy)!);
        }

        if (NewFrame is FrameSkeleton)
        {
            _frameSkeletons.Add(NewFrame.RefID, (NewFrame as FrameSkeleton)!);
        }

        if (NewFrame is FrameHeaderScene)
        {
            _frameScenes.Add(NewFrame.RefID, (NewFrame as FrameHeaderScene)!);
            _header.SceneFolders.Add((NewFrame as FrameHeaderScene)!);
        }

        return NewFrame;
    }

    public void SetParentOfObject(ParentInfo.ParentType ParentType, FrameEntry childEntry, FrameEntry parentEntry)
    {
        //get the index and child object
        FrameObjectBase obj = (childEntry as FrameObjectBase)!;
        obj.SetParent(ParentType, parentEntry);

        // Update world transform
        foreach (var pair in _frameObjects)
        {
            (pair.Value as FrameObjectBase)!.SetWorldTransform();
        }
    }

    public void DefineFrameBlockParents()
    {
        // Dictionary has no positional indexer — LINQ's ElementAt re-enumerates from the start on every call,
        // which made this pass O(n²) per archive load. Materialize the enumeration order once instead
        // (the same technique ExtractedSds.LinkNameTableFlags uses).
        var objects = new object[_frameObjects.Count];
        _frameObjects.Values.CopyTo(objects, 0);
        var scenes = new FrameHeaderScene[_frameScenes.Count];
        _frameScenes.Values.CopyTo(scenes, 0);
        int blockCount = GetBlockCount;

        for (int i = 0; i < objects.Length; i++)
        {
            FrameObjectBase? obj = (objects[i] as FrameObjectBase);

            if (obj == null)
            {
                continue;
            }

            if (obj is FrameObjectModel)
            {
                FrameObjectModel model = (obj as FrameObjectModel)!;

                foreach (var attachment in model.AttachmentReferences)
                {
                    attachment.Attachment = (objects[attachment.AttachmentIndex - blockCount] as FrameObjectBase)!;
                }
            }

            if (obj.ParentIndex1.Index > -1)
            {
                if (obj.ParentIndex1.Index <= (scenes.Length - 1) && (scenes.Length - 1) != -1)
                {
                    FrameHeaderScene scene = scenes[obj.ParentIndex1.Index];
                    obj.ParentIndex1.RefID = scene.RefID;
                    obj.ParentIndex1.Name = scene.Name.ToString();
                    scene.Children.Add(obj);
                }
                else if (obj.ParentIndex1.Index >= blockCount)
                {
                    FrameObjectBase parent = (objects[obj.ParentIndex1.Index - blockCount] as FrameObjectBase)!;
                    obj.ParentIndex1.RefID = parent.RefID;
                    obj.ParentIndex1.Name = parent.Name.ToString();
                    obj.Parent = parent;
                    parent.Children.Add(obj);
                }
                else
                {
                    throw new Exception("Unhandled Frame!");
                }
                obj.AddRef(FrameEntryRefTypes.Parent1, obj.ParentIndex1.RefID);
            }

            if (obj.ParentIndex2.Index > -1)
            {
                if (obj.ParentIndex2.Index <= (scenes.Length - 1) && (scenes.Length - 1) != -1)
                {
                    FrameHeaderScene scene = scenes[obj.ParentIndex2.Index];
                    obj.ParentIndex2.RefID = scene.RefID;
                    obj.ParentIndex2.Name = scene.Name.ToString();
                    if (obj.Parent == null) scene.Children.Add(obj);
                }
                else if (obj.ParentIndex2.Index >= blockCount)
                {
                    FrameObjectBase parent = (objects[obj.ParentIndex2.Index - blockCount] as FrameObjectBase)!;
                    obj.ParentIndex2.RefID = parent.RefID;
                    obj.ParentIndex2.Name = parent.Name.ToString();
                    obj.Root = parent;
                    if (obj.Parent == null) parent.Children.Add(obj);
                }
                else
                {
                    throw new Exception("Unhandled Frame!");
                }

                obj.AddRef(FrameEntryRefTypes.Parent2, obj.ParentIndex2.RefID);
            }
            obj.SetWorldTransform();
        }
    }

    public void SanitizeFrameData()
    {
        Dictionary<int, bool> isGeomUsed = new Dictionary<int, bool>(_frameGeometries.Count);
        Dictionary<int, bool> isMatUsed = new Dictionary<int, bool>(_frameMaterials.Count);
        Dictionary<int, bool> isBlendInfoUsed = new Dictionary<int, bool>(_frameBlendInfos.Count);
        Dictionary<int, bool> isSkelUsed = new Dictionary<int, bool>(_frameSkeletons.Count);
        Dictionary<int, bool> isSkelHierUsed = new Dictionary<int, bool>(_frameSkeletonHierachies.Count);

        foreach (KeyValuePair<int, FrameGeometry> entry in _frameGeometries)
        {
            isGeomUsed.Add(entry.Key, false);
        }

        foreach (KeyValuePair<int, FrameMaterial> entry in _frameMaterials)
        {
            isMatUsed.Add(entry.Key, false);
        }

        foreach (KeyValuePair<int, FrameBlendInfo> entry in _frameBlendInfos)
        {
            isBlendInfoUsed.Add(entry.Key, false);
        }

        foreach (KeyValuePair<int, FrameSkeleton> entry in _frameSkeletons)
        {
            isSkelUsed.Add(entry.Key, false);
        }

        foreach (KeyValuePair<int, FrameSkeletonHierarchy> entry in _frameSkeletonHierachies)
        {
            isSkelHierUsed.Add(entry.Key, false);
        }

        foreach (KeyValuePair<int, object> entry in _frameObjects)
        {
            if (entry.Value is FrameObjectModel)
            {
                FrameObjectModel mesh = (entry.Value as FrameObjectModel)!;
                isGeomUsed[mesh.Refs[FrameEntryRefTypes.Geometry]] = true;
                isMatUsed[mesh.Refs[FrameEntryRefTypes.Material]] = true;
                isBlendInfoUsed[mesh.Refs[FrameEntryRefTypes.BlendInfo]] = true;
                isSkelHierUsed[mesh.Refs[FrameEntryRefTypes.SkeletonHierarchy]] = true;
                isSkelUsed[mesh.Refs[FrameEntryRefTypes.Skeleton]] = true;

            }
            else if (entry.Value is FrameObjectSingleMesh)
            {
                FrameObjectSingleMesh mesh = (entry.Value as FrameObjectSingleMesh)!;

                if (mesh.MeshIndex > -1)
                {
                    isGeomUsed[mesh.Refs[FrameEntryRefTypes.Geometry]] = true;
                }

                if (mesh.MaterialIndex > -1)
                {
                    isMatUsed[mesh.Refs[FrameEntryRefTypes.Material]] = true;
                }
            }
        }

        for (int i = 0; i != isGeomUsed.Count; i++)
        {
            KeyValuePair<int, bool> pair = isGeomUsed.ElementAt(i);
            if (pair.Value != true)
            {
                _frameGeometries.Remove(pair.Key);
            }

        }
        for (int i = 0; i != isMatUsed.Count; i++)
        {
            KeyValuePair<int, bool> pair = isMatUsed.ElementAt(i);
            if (pair.Value != true)
            {
                _frameMaterials.Remove(pair.Key);
            }
        }
        for (int i = 0; i != isBlendInfoUsed.Count; i++)
        {
            KeyValuePair<int, bool> pair = isBlendInfoUsed.ElementAt(i);
            if (pair.Value != true)
            {
                _frameBlendInfos.Remove(pair.Key);
            }
        }
        for (int i = 0; i != isSkelUsed.Count; i++)
        {
            KeyValuePair<int, bool> pair = isSkelUsed.ElementAt(i);
            if (pair.Value != true)
            {
                _frameSkeletons.Remove(pair.Key);
            }
        }
        for (int i = 0; i != isSkelHierUsed.Count; i++)
        {
            KeyValuePair<int, bool> pair = isSkelHierUsed.ElementAt(i);
            if (pair.Value != true)
            {
                _frameSkeletonHierachies.Remove(pair.Key);
            }
        }

    }

    // Diagnostic: reports any object whose parent indices are about to be written differently from how they were
    // read, with a stack trace. Enabled by ILLUSION_TRACE_PARENT=1; free otherwise. A save that silently moves an
    // object in the hierarchy produces a district the game refuses to stream in, and this names the object and the
    // call path that did it instead of leaving it to inference.
    private static void TraceHierarchyChange(FrameObjectBase block)
    {
        if (Environment.GetEnvironmentVariable("ILLUSION_TRACE_PARENT") != "1") return;
        if (block.ParentIndex1.Index == block.LoadedParentIndex1
            && block.ParentIndex2.Index == block.LoadedParentIndex2) return;
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "illusion_parent_trace.txt"),
                $"HIERARCHY CHANGED on save: '{block.Name}' "
                + $"ParentIndex1 {block.LoadedParentIndex1} -> {block.ParentIndex1.Index}, "
                + $"ParentIndex2 {block.LoadedParentIndex2} -> {block.ParentIndex2.Index}\n"
                + new System.Diagnostics.StackTrace(true) + "\n\n");
        }
        catch (Exception) { /* tracing must never break a save */ }
    }

    /// <summary>
    /// Turns a parent reference into the on-disk parent index: a scene folder's ordinal, or the object block
    /// offset plus the object's ordinal. Returns -1 ("no parent") when the reference resolves to neither.
    /// </summary>
    /// <remarks>
    /// The −1 fallback is load-bearing. <c>IndexOfValue</c> returns −1 for an unknown key, so adding it blindly to
    /// the object-block offset used to yield <c>offset − 1</c> — a wrong index that still points at a real block,
    /// so nothing downstream could tell it was garbage. The game reads it as a genuine parent and dies. A stale
    /// reference must degrade to "no parent", never to some other object.
    /// </remarks>
    private int ResolveParentIndex(int parentRefId, int objectBlockOffset)
    {
        if (_frameScenes.ContainsKey(parentRefId))
        {
            return _frameScenes.IndexOfValue(parentRefId);
        }
        if (_frameObjects.ContainsKey(parentRefId))
        {
            return objectBlockOffset + _frameObjects.IndexOfValue(parentRefId);
        }
        return -1;
    }

    public void UpdateFrameData()
    {
        SanitizeFrameData();

        int[] offsets = new int[7];
        offsets[0] = 0;
        offsets[1] = offsets[0] + _frameScenes.Count;
        offsets[2] = offsets[1] + _frameGeometries.Count;
        offsets[3] = offsets[2] + _frameMaterials.Count;
        offsets[4] = offsets[3] + _frameBlendInfos.Count;
        offsets[5] = offsets[4] + _frameSkeletons.Count;
        offsets[6] = offsets[5] + _frameSkeletonHierachies.Count;

        for (int i = 0; i < _frameObjects.Count; i++)
        {
            FrameObjectBase block = (_frameObjects.ElementAt(i).Value as FrameObjectBase)!;

            if (block.Refs.ContainsKey(FrameEntryRefTypes.Parent1))
            {
                int parent1Ref = block.Refs[FrameEntryRefTypes.Parent1];
                // ParentIndex1 is the hierarchy parent and is never a scene folder in the shipped game — scene
                // membership belongs in ParentIndex2. Writing a scene ordinal here produces a combination the
                // engine refuses to stream, so refuse to write it instead of shipping a district that crashes.
                FormatAssert.Ensure(!_frameScenes.ContainsKey(parent1Ref),
                    $"Frame '{block.Name}' has a scene folder in ParentIndex1; scene membership belongs in ParentIndex2.");
                block.ParentIndex1.Index = ResolveParentIndex(parent1Ref, offsets[6]);
            }

            if (block.Refs.ContainsKey(FrameEntryRefTypes.Parent2))
            {
                block.ParentIndex2.Index = ResolveParentIndex(block.Refs[FrameEntryRefTypes.Parent2], offsets[6]);
            }

            TraceHierarchyChange(block);


            if (block.Type == typeof(FrameObjectSingleMesh).ToString())
            {
                FrameObjectSingleMesh mesh = (block as FrameObjectSingleMesh)!;
                if (mesh.MeshIndex != -1) mesh.MeshIndex = offsets[1] + _frameGeometries.IndexOfValue(mesh.Refs[FrameEntryRefTypes.Geometry]);
                if (mesh.MaterialIndex != -1) mesh.MaterialIndex = offsets[2] + _frameMaterials.IndexOfValue(mesh.Refs[FrameEntryRefTypes.Material]);
                block = mesh;
            }
            if (block.Type == typeof(FrameObjectModel).ToString())
            {
                FrameObjectModel mesh = (block as FrameObjectModel)!;
                if (mesh.MeshIndex != -1) mesh.MeshIndex = offsets[1] + _frameGeometries.IndexOfValue(mesh.Refs[FrameEntryRefTypes.Geometry]);
                if (mesh.MaterialIndex != -1) mesh.MaterialIndex = offsets[2] + _frameMaterials.IndexOfValue(mesh.Refs[FrameEntryRefTypes.Material]);
                if (mesh.BlendInfoIndex != -1) mesh.BlendInfoIndex = offsets[3] + _frameBlendInfos.IndexOfValue(mesh.Refs[FrameEntryRefTypes.BlendInfo]);
                if (mesh.SkeletonIndex != -1) mesh.SkeletonIndex = offsets[4] + _frameSkeletons.IndexOfValue(mesh.Refs[FrameEntryRefTypes.Skeleton]);
                if (mesh.SkeletonHierarchyIndex != -1) mesh.SkeletonHierarchyIndex = offsets[5] + _frameSkeletonHierachies.IndexOfValue(mesh.Refs[FrameEntryRefTypes.SkeletonHierarchy]);

                foreach (var attachment in mesh.AttachmentReferences)
                {
                    attachment.AttachmentIndex = offsets[6] + _frameObjects.IndexOfValue(attachment.Attachment!.RefID);
                }

                block = mesh;
            }
        }

        _header.SceneFolders = _frameScenes.Values.ToList();

        _header.NumFolderNames = _frameScenes.Count;
        _header.NumGeometries = _frameGeometries.Count;
        _header.NumMaterialResources = _frameMaterials.Count;
        _header.NumBlendInfos = _frameBlendInfos.Count;
        _header.NumSkeletons = _frameSkeletons.Count;
        _header.NumSkelHierachies = _frameSkeletonHierachies.Count;
        _header.NumObjects = _frameObjects.Count;
        _header.NumFolderNames = _frameScenes.Count;
    }

    public static bool IsFrameType(object entry)
    {
        if (entry.GetType() == typeof(FrameObjectPoint) ||
            entry.GetType() == typeof(FrameObjectSingleMesh) ||
            entry.GetType() == typeof(FrameObjectFrame) ||
            entry.GetType() == typeof(FrameObjectLight) ||
            entry.GetType() == typeof(FrameObjectCamera) ||
            entry.GetType() == typeof(FrameObjectComponent_U005) ||
            entry.GetType() == typeof(FrameObjectSector) ||
            entry.GetType() == typeof(FrameObjectDummy) ||
            entry.GetType() == typeof(FrameObjectDeflector) ||
            entry.GetType() == typeof(FrameObjectArea) ||
            entry.GetType() == typeof(FrameObjectTarget) ||
            entry.GetType() == typeof(FrameObjectModel) ||
            entry.GetType() == typeof(FrameObjectCollision))
            return true;

        return false;
    }

    public T? GetObjectByHash<T>(ulong hashname) where T : FrameObjectBase
    {
        foreach (FrameObjectBase Object in _frameObjects.Values)
        {
            if (Object.Name.Hash == hashname)
            {
                return Object as T;
            }
        }

        return null;
    }
}
