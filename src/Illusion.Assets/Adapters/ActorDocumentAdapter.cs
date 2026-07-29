using Illusion.Assets.Actors;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Actors;

namespace Illusion.Assets.Adapters;

/// <summary>
/// The scene's actor packs as a unit of persistence — what the "Actors" tree node carries, the way the
/// "Collisions" node carries its .col. It has to be its own document because the tree finds what to save by
/// walking UP from the edited node to the nearest <see cref="ISceneDocument"/>, and the actors hang beside the
/// FrameResource branch, not under it: without this, moving an actor marked nothing as edited and a build
/// reported that there was nothing to pack.
/// </summary>
public sealed class ActorDocumentAdapter : ISceneDocument
{
    private readonly ActorPlacements _placements;
    private readonly SceneDocumentAdapter _scene;

    public ActorDocumentAdapter(ActorPlacements placements, FileInfo sourceArchive, SceneDocumentAdapter scene)
    {
        _placements = placements;
        _scene = scene;
        SourceArchive = sourceArchive;
    }

    /// <summary>Wraps an actor as a node — canonical, shared with the scene document, so a newly created copy
    /// gets the same adapter identity everything else keys on.</summary>
    public ActorNodeAdapter ActorNode(ActorEntry actor) => _scene.ActorNode(actor);

    /// <summary>The archive's frame document. An actor's objects live there, not here — copying an actor has
    /// to clone one, and the frame resource is what saves it.</summary>
    public SceneDocumentAdapter Scene => _scene;

    public FileInfo SourceArchive { get; }

    /// <summary>The placements these packs resolved to — the actor adapters edit them in place.</summary>
    public ActorPlacements Placements => _placements;

    public int ObjectCount => _placements.All.Count;
    public int GeometryCount => 0;
    public int MaterialCount => 0;
    public int SkeletonCount => 0;
    public int SceneCount => _placements.Packs.Count;

    /// <summary>Rewrites every pack over the file it was read from. Only fixed-size transform fields can have
    /// changed so far, so an untouched actor's bytes come back exactly as they were.</summary>
    public string SaveWorkingCopy()
    {
        IReadOnlyList<string> written = SdsActorsSaver.SaveWorkingCopy(_placements);
        return written.Count > 0 ? written[0] : SourceArchive.FullName;
    }

    /// <summary>Actors are not frame-name-table entries — nothing to flag.</summary>
    public void MarkNameTableDirty() { }

    /// <summary>An actor has no place in the frame graph to be reparented into.</summary>
    public bool Reparent(IFrameNode child, ISceneSource? newParent) => false;

    /// <summary>Whether this scene has anything an actor edit could be written to.</summary>
    public bool HasPacks => _placements.Packs.Count > 0;

    /// <summary>The actors, for the tree that lists them.</summary>
    public IReadOnlyList<ActorEntry> Actors => _placements.All;
}
