using Illusion.Assets.Adapters;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.Frames;

/// <summary>
/// Copies the prototype an actor places — the whole subtree, not just the node the pack names.
///
/// An actor's prototype is a holder frame parked at the origin with its meshes (and often a collision hull)
/// hanging under it; the actor supplies the world matrix. A frame is spawned by exactly one actor, so a
/// copied actor cannot share the original's object: it needs its own subtree and its own scene reference.
///
/// The meshes go through <see cref="FrameDuplicator"/> unchanged — same buffer, geometry and material deep
/// copies, same rollback when a pool is full — and are then re-parented under the cloned holder instead of
/// the original's parents. Everything the game needs to spawn the copy (name-table membership, the anchored
/// bit, LOD draw distances) rides along in the copy constructors, because the source is an object the game
/// already spawns.
/// </summary>
public static class ActorPrototypeCloner
{
    /// <summary>A cloned prototype with everything undo needs to take it out and put it back.</summary>
    public sealed class ClonedPrototype
    {
        internal FrameResource Resource = null!;
        internal readonly List<FrameObjectBase> Holders = new();          // the frames we minted ourselves
        internal readonly List<FrameDuplicator.DuplicatedObject> Meshes = new();

        /// <summary>The clone's root — what the new actor places.</summary>
        public FrameObjectBase Root { get; internal set; } = null!;

        /// <summary>The cloned meshes, each with a render-ready copy for the caller's GPU upload.</summary>
        public IReadOnlyList<(FrameObjectSingleMesh Frame, MeshData Mesh)> Renderables { get; internal set; } = [];

        /// <summary>Where <see cref="Root"/> sits in the frame resource's object list — the index a scene
        /// reference stores. Read it fresh rather than caching: an undone delete can reorder the list.</summary>
        public uint FrameIndex => IndexOf(Resource, Root);

        /// <summary>Whether the clone is currently part of the scene (false once <see cref="Detach"/> ran).</summary>
        public bool IsAttached => Resource.FrameObjects.ContainsKey(Root.RefID);

        /// <summary>Takes the clone back out of the frame resource (undo).</summary>
        public void Detach()
        {
            foreach (FrameDuplicator.DuplicatedObject mesh in Meshes) mesh.Detach();
            foreach (FrameObjectBase holder in Holders)
            {
                holder.SetParent(ParentInfo.ParentType.ParentIndex1, null);
                holder.SetParent(ParentInfo.ParentType.ParentIndex2, null);
                Resource.FrameObjects.Remove(holder.RefID);
            }
        }

        /// <summary>Puts it back (redo), holders first so the meshes have their parents again.</summary>
        public void Reattach()
        {
            foreach (FrameObjectBase holder in Holders)
            {
                if (!Resource.FrameObjects.ContainsKey(holder.RefID)) Resource.FrameObjects.Add(holder.RefID, holder);
            }
            foreach (FrameDuplicator.DuplicatedObject mesh in Meshes) mesh.Reattach();
            foreach (KeyValuePair<FrameObjectBase, FrameObjectBase> pair in _parentOf)
            {
                pair.Key.SetParent(ParentInfo.ParentType.ParentIndex1, pair.Value);
            }
            Root.SetWorldTransform();
        }

        internal readonly Dictionary<FrameObjectBase, FrameObjectBase> _parentOf = new();
    }

    /// <summary>Whether this prototype is one the cloner can copy.</summary>
    public static bool CanClone(FrameObjectBase root) => root is FrameObjectFrame or FrameObjectSingleMesh;

    /// <summary>
    /// Clones the subtree rooted at <paramref name="root"/>. Null with a reason when some part of it cannot be
    /// copied — a partial clone is never left behind, since an actor placing half an object is worse than an
    /// actor that was not copied.
    /// </summary>
    public static ClonedPrototype? TryClone(ISceneDocument document, FrameObjectBase root, out string? skipReason)
    {
        ArgumentNullException.ThrowIfNull(root);
        skipReason = null;
        if (document is not SceneDocumentAdapter adapter)
        {
            skipReason = "the object does not belong to a loaded scene";
            return null;
        }

        var clone = new ClonedPrototype { Resource = adapter.Frame };
        var renderables = new List<(FrameObjectSingleMesh, MeshData)>();

        FrameObjectBase? cloneRoot = CloneSubtree(document, adapter, root, parent: null, clone, renderables,
            new HashSet<FrameObjectBase>(), ref skipReason);
        if (cloneRoot == null)
        {
            clone.Detach();
            skipReason ??= "the object could not be copied";
            return null;
        }

        clone.Root = cloneRoot;
        clone.Renderables = renderables;
        cloneRoot.SetWorldTransform();
        return clone;
    }

    // One node and everything under it. A mesh goes through the frame duplicator (which registers its own
    // blocks); every other type is copy-constructed and registered here. Either way the copy is re-parented
    // under the cloned parent rather than the source's — that is the whole difference from a plain duplicate.
    private static FrameObjectBase? CloneSubtree(ISceneDocument document, SceneDocumentAdapter adapter,
        FrameObjectBase source, FrameObjectBase? parent, ClonedPrototype into,
        List<(FrameObjectSingleMesh, MeshData)> renderables, HashSet<FrameObjectBase> seen, ref string? skipReason)
    {
        if (!seen.Add(source)) return null; // a malformed hierarchy can loop

        FrameObjectBase? copy;
        if (source.GetType() == typeof(FrameObjectSingleMesh))
        {
            FrameDuplicator.DuplicatedObject? duplicated =
                FrameDuplicator.TryDuplicate(document, adapter.Node(source), out skipReason);
            if (duplicated == null) return null;
            into.Meshes.Add(duplicated);

            copy = ((FrameNodeAdapter)duplicated.Node).Frame;
            renderables.Add(((FrameObjectSingleMesh)copy, duplicated.Mesh));
        }
        else
        {
            copy = CopyOf(source, adapter.Frame);
            if (copy == null)
            {
                skipReason = $"'{source.Name}' is a {source.GetType().Name}, which cannot be copied yet";
                return null;
            }
            into.Holders.Add(copy);
        }

        // The clone hangs under the cloned parent; the root keeps the shape its source had — parked at the
        // origin and unanchored, because what spawns it is the actor, not a parent in the frame graph.
        copy.SetParent(ParentInfo.ParentType.ParentIndex1, parent);
        copy.SetParent(ParentInfo.ParentType.ParentIndex2, null);
        if (parent != null) into._parentOf[copy] = parent;

        foreach (FrameObjectBase child in source.Children.ToList())
        {
            if (CloneSubtree(document, adapter, child, copy, into, renderables, seen, ref skipReason) == null)
            {
                return null;
            }
        }
        return copy;
    }

    // A copy-constructed frame of the same type, named uniquely and registered. Only the types an actor's
    // prototype is actually built from are listed — anything else is refused by name rather than silently
    // dropped, so a prototype that carries one never turns into a half-copy.
    private static FrameObjectBase? CopyOf(FrameObjectBase source, FrameResource resource)
    {
        FrameObjectBase? copy = source switch
        {
            FrameObjectFrame frame => new FrameObjectFrame(frame),
            FrameObjectCollision collision => new FrameObjectCollision(collision),
            FrameObjectDummy dummy => new FrameObjectDummy(dummy),
            FrameObjectArea area => new FrameObjectArea(area),
            FrameObjectPoint point => new FrameObjectPoint(point),
            _ => null,
        };
        if (copy == null) return null;

        copy.Name = new Formats.Hashing.HashName(UniqueName(resource, source.Name.String));
        resource.FrameObjects.Add(copy.RefID, copy);
        return copy;
    }

    // Same rule the mesh duplicator uses: the frame name table is keyed by name, so no copy may collide.
    private static string UniqueName(FrameResource resource, string sourceName)
    {
        for (int i = 1; ; i++)
        {
            string candidate = i == 1 ? $"{sourceName}_copy" : $"{sourceName}_copy{i}";
            bool taken = resource.FrameObjects.Values.OfType<FrameObjectBase>()
                .Any(o => string.Equals(o.Name.String, candidate, StringComparison.OrdinalIgnoreCase));
            if (!taken) return candidate;
        }
    }

    /// <summary>
    /// Where a frame object sits in the resource's object list. This is the index a scene reference and the
    /// frame name table both store — the plain ordinal, NOT <c>GetIndexOfObject</c>, which offsets by the
    /// block count and belongs to the parent-index space instead.
    /// </summary>
    public static uint IndexOf(FrameResource resource, FrameObjectBase frame)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(frame);
        uint index = 0;
        foreach (int key in resource.FrameObjects.Keys)
        {
            if (key == frame.RefID) return index;
            index++;
        }
        return uint.MaxValue;
    }
}
