namespace Illusion.Domain;

/// <summary>
/// A loaded scene document — the unit of persistence (one frame resource of one archive). Carried by the
/// document wrapper node of the scene tree; saving an edited object means saving its owning document.
/// </summary>
public interface ISceneDocument : ISceneSource
{
    /// <summary>The archive this document was loaded from (the file a build repacks).</summary>
    FileInfo SourceArchive { get; }

    /// <summary>Further archives this document also wrote into and that a build must therefore repack alongside
    /// <see cref="SourceArchive"/>. Empty for every ordinary document; the seasonal crash table uses it, because
    /// one edit can land in both the summer and the winter archive.</summary>
    IReadOnlyList<FileInfo> CompanionArchives => [];

    int ObjectCount { get; }
    int GeometryCount { get; }
    int MaterialCount { get; }
    int SkeletonCount { get; }
    int SceneCount { get; }

    /// <summary>
    /// Re-serializes the document (with any in-memory edits) over its extracted working copy on disk.
    /// Returns the path written. Does not repack the archive — that is the build step.
    /// </summary>
    string SaveWorkingCopy();

    /// <summary>Signals that a name-table field changed (an object's name, or whether it is on the name table),
    /// so the next <see cref="SaveWorkingCopy"/> also rewrites the FrameNameTable file — not only the
    /// FrameResource. (Those fields live in the name table, which is otherwise not rewritten.)</summary>
    void MarkNameTableDirty();

    /// <summary>Reparents <paramref name="child"/> under <paramref name="newParent"/> — another frame node
    /// (<see cref="IFrameNode"/>), a scene folder (<see cref="IFrameScene"/>) or null / this document (a scene
    /// root). Keeps the child's local transform and recomputes world transforms. Returns false when the target is
    /// invalid (itself or one of its own descendants). Persisted via the frame stream (parent indices).</summary>
    bool Reparent(IFrameNode child, ISceneSource? newParent);
}
