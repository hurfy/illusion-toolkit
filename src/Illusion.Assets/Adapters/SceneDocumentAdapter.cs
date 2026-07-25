using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;

namespace Illusion.Assets.Adapters;

/// <summary>
/// Adapts one loaded vendor <see cref="FrameResource"/> (plus its source archive) into the Domain's
/// <see cref="ISceneDocument"/> port, and wraps its frame objects as <see cref="IFrameNode"/>s. Wrapping is
/// canonical — one adapter per frame object for the document's lifetime — because the editor's selection,
/// group-drag and delete logic key sets by reference identity (see <see cref="IFrameNode"/> remarks).
/// </summary>
public sealed class SceneDocumentAdapter : ISceneDocument
{
    private readonly FrameResource _frame;
    private readonly Dictionary<FrameObjectBase, FrameNodeAdapter> _nodes = new();
    private readonly HashSet<ulong> _dirtyVertexBuffers = new();
    private readonly HashSet<ulong> _dirtyIndexBuffers = new();
    private bool _nameTableDirty;

    public SceneDocumentAdapter(FrameResource frame, FileInfo sourceArchive)
    {
        _frame = frame;
        SourceArchive = sourceArchive;
    }

    public FileInfo SourceArchive { get; }

    /// <summary>The wrapped vendor resource — for the asset layer's own machinery (the bridge's
    /// object factory); the UI never touches it.</summary>
    internal FrameResource Frame => _frame;

    public int ObjectCount => _frame.FrameObjects?.Count ?? 0;
    public int GeometryCount => _frame.FrameGeometries?.Count ?? 0;
    public int MaterialCount => _frame.FrameMaterials?.Count ?? 0;
    public int SkeletonCount => _frame.FrameSkeletons?.Count ?? 0;
    public int SceneCount => _frame.FrameScenes?.Count ?? 0;

    /// <summary>Flags a buffer whose in-memory bytes diverged from the extracted folder — its pool
    /// file is rewritten by the next <see cref="SaveWorkingCopy"/>.</summary>
    public void MarkVertexBufferDirty(ulong hash) => _dirtyVertexBuffers.Add(hash);

    /// <inheritdoc cref="MarkVertexBufferDirty"/>
    public void MarkIndexBufferDirty(ulong hash) => _dirtyIndexBuffers.Add(hash);

    /// <inheritdoc cref="ISceneDocument.MarkNameTableDirty"/>
    public void MarkNameTableDirty() => _nameTableDirty = true;

    public string SaveWorkingCopy()
    {
        string written = SdsWriter.SaveFrameResource(_frame, SourceArchive);
        if (_nameTableDirty)
        {
            // Must run AFTER SaveFrameResource: WriteToStream ran UpdateFrameData, so FrameObjects order and the
            // scene indices the rebuild reads are final. Verified a semantic fixpoint across every district (see
            // --probe-nametable).
            SdsWriter.SaveFrameNameTable(_frame, SourceArchive);
            _nameTableDirty = false;
        }
        if (_dirtyVertexBuffers.Count > 0 || _dirtyIndexBuffers.Count > 0)
        {
            Bridge.SdsGeometrySaver.SaveDirtyPools(_frame, _dirtyVertexBuffers, _dirtyIndexBuffers);
            _dirtyVertexBuffers.Clear(); // the working copy now matches memory
            _dirtyIndexBuffers.Clear();
        }
        return written;
    }

    /// <inheritdoc cref="ISceneDocument.Reparent"/>
    public bool Reparent(IFrameNode child, ISceneSource? newParent)
    {
        if (child is not FrameNodeAdapter childAdapter) return false;
        FrameObjectBase childFrame = childAdapter.Frame;
        TraceReparent(childFrame, newParent);

        FrameEntry? parentEntry = newParent switch
        {
            FrameNodeAdapter fna => fna.Frame,
            FrameSceneAdapter fsa => fsa.Scene,
            _ => null, // ISceneDocument / null → a scene root
        };

        // Reject self / a descendant (would make a cycle). SetParent itself has no such guard.
        if (parentEntry is FrameObjectBase pf &&
            (ReferenceEquals(pf, childFrame) || childFrame.IsFrameOwnChildren(pf.RefID)))
            return false;

        // Detach from any old scene folder's runtime children — SetParent only detaches the link its own slot
        // owns, and a scene-parented object is held by the folder, not by a frame.
        if (_frame.FrameScenes != null)
            foreach (FrameHeaderScene s in _frame.FrameScenes.Values) s.Children.Remove(childFrame);

        // The two slots mean different things, so each target shape writes a different pair. These are the only
        // three shapes the game ships: a scene-anchored root (-1, scene), a nested object (object, chain root),
        // and a true top-level frame (-1, -1). Writing a scene index into ParentIndex1 — which is what this used
        // to do for every target — produces a combination that occurs nowhere in the stock game and that the
        // engine refuses to stream.
        switch (parentEntry)
        {
            case FrameHeaderScene scene:
                childFrame.SetParent(ParentInfo.ParentType.ParentIndex2, scene);
                childFrame.SetParent(ParentInfo.ParentType.ParentIndex1, null);
                scene.Children.Add(childFrame);
                break;

            case FrameObjectBase parentFrame:
                childFrame.SetParent(ParentInfo.ParentType.ParentIndex1, parentFrame);
                childFrame.SetParent(ParentInfo.ParentType.ParentIndex2, AnchorOf(parentFrame));
                break;

            default: // (root)
                childFrame.ClearBothParents();
                break;
        }

        childFrame.SetWorldTransform();
        return true;
    }

    /// <summary>
    /// The anchor a child of <paramref name="parent"/> must record in ParentIndex2: the top of the parent's
    /// ParentIndex1 chain, or — when that top is itself scene-anchored — the scene folder it lives in. Null when
    /// the chain ends at a rootless frame, which is the (object, -1) shape.
    /// </summary>
    private FrameEntry? AnchorOf(FrameObjectBase parent)
    {
        FrameObjectBase top = parent;
        var seen = new HashSet<FrameObjectBase> { top };
        while (top.Parent is { } next && seen.Add(next)) top = next; // seen guards a malformed cycle
        return top.Root as FrameEntry ?? FindScene(top);
    }

    /// <summary>The scene folder that holds <paramref name="frame"/>, by runtime membership.</summary>
    private FrameHeaderScene? FindScene(FrameObjectBase frame) =>
        _frame.FrameScenes?.Values.FirstOrDefault(s => s.Children.Contains(frame));

    // Diagnostic: records every reparent with a stack trace when ILLUSION_TRACE_PARENT=1. A reparent rewrites the
    // object's ParentIndex1, so an unintended one silently corrupts the FrameResource; this is how an unintended
    // caller gets identified rather than guessed at. Off (and free) unless the variable is set.
    private static void TraceReparent(FrameObjectBase child, ISceneSource? newParent)
    {
        if (Environment.GetEnvironmentVariable("ILLUSION_TRACE_PARENT") != "1") return;
        try
        {
            string parentName = newParent switch
            {
                FrameNodeAdapter fna => "object " + fna.Frame.Name,
                FrameSceneAdapter fsa => "scene " + fsa.Scene.Name,
                null => "(root)",
                _ => newParent.GetType().Name,
            };
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "illusion_parent_trace.txt"),
                $"REPARENT '{child.Name}' (ParentIndex1={child.ParentIndex1.Index}, ParentIndex2={child.ParentIndex2.Index})"
                + $" -> {parentName}\n{new System.Diagnostics.StackTrace(true)}\n\n");
        }
        catch (Exception) { /* tracing must never break an edit */ }
    }

    /// <summary>The canonical <see cref="IFrameNode"/> adapter for a frame object of this document.</summary>
    public FrameNodeAdapter Node(FrameObjectBase frame)
    {
        if (!_nodes.TryGetValue(frame, out FrameNodeAdapter? node))
        {
            _nodes[frame] = node = new FrameNodeAdapter(frame, this);
        }
        return node;
    }
}
