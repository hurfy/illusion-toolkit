using System.Numerics;
using Illusion.Formats.Actors;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Actors;

/// <summary>
/// Where a scene's actors put its frame objects.
///
/// A frame object an actor places is a PROTOTYPE: it sits at the origin with an identity matrix, and the
/// spawn transform lives in the actor pack (.act) instead — which is why such objects all pile up at (0,0,0)
/// until the pack is read. This resolves each actor to its frame object and hands out the matrix that
/// belongs in front of that object's own world transform, for the whole subtree under it.
///
/// The link is by hash, never by name: an actor names its frame, but many name one that lives in another
/// archive (ambient sounds mostly), and a scene reference's own name is the DEFINITION's, not the frame's.
/// Measured across the shipped game: 27385 actors resolve, every scene reference is claimed by exactly one
/// actor, and no frame is shared by two actors — so one matrix per subtree is enough, no instancing.
/// </summary>
public sealed class ActorPlacements
{
    private readonly Dictionary<FrameObjectBase, Matrix4x4> _byFrame;
    private readonly Dictionary<FrameObjectBase, ActorEntry> _actorByTarget;

    private ActorPlacements(Dictionary<FrameObjectBase, Matrix4x4> byFrame,
        Dictionary<FrameObjectBase, ActorEntry> actorByTarget)
    {
        _byFrame = byFrame;
        _actorByTarget = actorByTarget;
    }

    /// <summary>Placed subtree roots (one per resolved actor).</summary>
    public int PlacedCount => _actorByTarget.Count;

    /// <summary>Frame objects covered, including everything under a placed root.</summary>
    public int CoveredCount => _byFrame.Count;

    /// <summary>Actors whose frame is not in this scene — ambient sounds and the like. Nothing to place.</summary>
    public int UnresolvedCount { get; private init; }

    /// <summary>Every actor the scene's packs declare, in pack order.</summary>
    public IReadOnlyList<ActorEntry> All { get; private init; } = [];

    /// <summary>The actors nothing draws: either their frame is not in this scene, or it is but its subtree
    /// carries no mesh. These are what the viewport marks with a glyph — see <see cref="ActorMarkerBuilder"/>.</summary>
    public IReadOnlyList<ActorEntry> Invisible { get; private init; } = [];

    /// <summary>Whether this actor is one of the <see cref="Invisible"/> ones, i.e. whether it is represented by
    /// a glyph rather than by geometry.</summary>
    public bool HasGlyph(ActorEntry actor) => _invisibleSet.Contains(actor);

    private HashSet<ActorEntry> _invisibleSet = new();

    /// <summary>The frame an actor places, when this scene has it.</summary>
    public FrameObjectBase? TargetOf(ActorEntry actor) =>
        _targetByActor.TryGetValue(actor, out FrameObjectBase? frame) ? frame : null;

    private Dictionary<ActorEntry, FrameObjectBase> _targetByActor = new();

    /// <summary>The matrix to apply in front of <paramref name="frame"/>'s own world transform, or identity
    /// when no actor places it.</summary>
    public Matrix4x4 For(FrameObjectBase frame) =>
        _byFrame.TryGetValue(frame, out Matrix4x4 m) ? m : Matrix4x4.Identity;

    public bool TryGet(FrameObjectBase frame, out Matrix4x4 placement) => _byFrame.TryGetValue(frame, out placement);

    /// <summary>The actor that places <paramref name="frame"/> — set only on the subtree root it targets.</summary>
    public ActorEntry? ActorOf(FrameObjectBase frame) =>
        _actorByTarget.TryGetValue(frame, out ActorEntry? actor) ? actor : null;

    /// <summary>The actor that governs <paramref name="frame"/>, whether it is the frame the actor names or
    /// anything under it. This is the one a viewport click should select: the mesh you see is a prototype, and
    /// the actor is what puts it there.</summary>
    public ActorEntry? ActorCovering(FrameObjectBase frame) =>
        _actorByCoveredFrame.TryGetValue(frame, out ActorEntry? actor) ? actor : null;

    private Dictionary<FrameObjectBase, ActorEntry> _actorByCoveredFrame = new();

    public static ActorPlacements Empty { get; } =
        new(new Dictionary<FrameObjectBase, Matrix4x4>(), new Dictionary<FrameObjectBase, ActorEntry>());

    /// <summary>
    /// Reads every actor pack the extracted folder lists and resolves it against <paramref name="resource"/>.
    /// A pack that cannot be read is skipped — a scene still loads without its actors, just unplaced.
    /// </summary>
    public static ActorPlacements Load(SdsManifest manifest, FrameResource resource)
    {
        var packs = new List<ActorsFile>();
        foreach (string path in manifest.GetFiles("Actors"))
        {
            try { packs.Add(ActorsFile.Load(path)); }
            catch (Exception) { /* an unreadable pack costs placement, never the scene */ }
        }
        return Build(packs, resource);
    }

    /// <summary>Resolves already-loaded packs. Separate from <see cref="Load"/> so the probes can feed
    /// packs in directly.</summary>
    public static ActorPlacements Build(IReadOnlyList<ActorsFile> packs, FrameResource resource)
    {
        if (packs.Count == 0 || resource.FrameObjects == null) return Empty;

        // FrameIndex is the position within FrameObjects — the same index space the FrameNameTable uses.
        var objects = new List<FrameObjectBase?>(resource.FrameObjects.Count);
        foreach (object value in resource.FrameObjects.Values) objects.Add(value as FrameObjectBase);

        var byFrame = new Dictionary<FrameObjectBase, Matrix4x4>();
        var actorByTarget = new Dictionary<FrameObjectBase, ActorEntry>();
        var targetByActor = new Dictionary<ActorEntry, FrameObjectBase>();
        var actorByCoveredFrame = new Dictionary<FrameObjectBase, ActorEntry>();
        var all = new List<ActorEntry>();
        var invisible = new List<ActorEntry>();
        int unresolved = 0;

        foreach (ActorsFile pack in packs)
        {
            var frameIndexByHash = new Dictionary<ulong, uint>();
            foreach (ActorSceneReference reference in pack.SceneReferences)
            {
                frameIndexByHash[reference.FrameHash] = reference.FrameIndex;
            }

            foreach (ActorEntry actor in pack.Actors)
            {
                all.Add(actor);
                if (!actor.IsTyped) { unresolved++; invisible.Add(actor); continue; }

                // An uncompressed pack stores no hashes — derive the key from the name it does store.
                ulong key = actor.FrameHash != 0
                    ? actor.FrameHash
                    : actor.LinkedFrame.Length > 0 ? Fnv64.Hash(actor.LinkedFrame) : 0;

                if (key == 0 || !frameIndexByHash.TryGetValue(key, out uint frameIndex)
                    || frameIndex >= objects.Count || objects[(int)frameIndex] is not { } target)
                {
                    unresolved++;
                    invisible.Add(actor);
                    continue;
                }

                // Two actors targeting one frame do not occur in the shipped game; if a mod ever does it,
                // the first one wins rather than the last, so the result stays deterministic.
                if (!actorByTarget.TryAdd(target, actor)) continue;
                targetByActor[actor] = target;
                Spread(target, actor.Transform, byFrame);
                Claim(target, actor, actorByCoveredFrame);

                // Resolved is not the same as visible: most items, detectors and blockers point at an empty
                // holder frame. Those get a glyph too, otherwise they are unreachable in the viewport.
                if (!HasMesh(target, new HashSet<FrameObjectBase>())) invisible.Add(actor);
            }
        }

        return new ActorPlacements(byFrame, actorByTarget)
        {
            UnresolvedCount = unresolved,
            All = all,
            Invisible = invisible,
            _invisibleSet = new HashSet<ActorEntry>(invisible),
            _targetByActor = targetByActor,
            _actorByCoveredFrame = actorByCoveredFrame,
        };
    }

    // Records the whole subtree as governed by this actor, so a click on any mesh under it resolves back to
    // the actor rather than to the prototype frame.
    private static void Claim(FrameObjectBase frame, ActorEntry actor, Dictionary<FrameObjectBase, ActorEntry> into)
    {
        if (!into.TryAdd(frame, actor)) return;
        foreach (FrameObjectBase child in frame.Children) Claim(child, actor, into);
    }

    private static bool HasMesh(FrameObjectBase frame, HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return false;
        if (frame is FrameObjectSingleMesh { Geometry: not null }) return true;
        foreach (FrameObjectBase child in frame.Children)
        {
            if (HasMesh(child, seen)) return true;
        }
        return false;
    }

    // The actor moves the whole subtree, not just the node it names: the prototype is an empty holder and
    // its meshes/collisions hang under it. Guarded against the cycles a malformed hierarchy can carry.
    private static void Spread(FrameObjectBase frame, Matrix4x4 placement, Dictionary<FrameObjectBase, Matrix4x4> into)
    {
        if (!into.TryAdd(frame, placement)) return;
        foreach (FrameObjectBase child in frame.Children) Spread(child, placement, into);
    }
}
