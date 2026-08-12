using Illusion.Assets.Adapters;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// The scene-tree half of a car's helper frames — where a Dummy or a Point the aggregate has just minted
/// shows up, and where it stops showing when the mint is taken back.
///
/// <para>
/// A frame the graph holds and the tree does not is one the modder cannot select, cannot see and cannot
/// drag. Minting the frame itself belongs to <see cref="Assets.Cars.Car"/> — the one path from an intent to a
/// car's bytes — and this is the view catching up with it.
/// </para>
/// </summary>
internal sealed class CarPartController
{
    private readonly D3DImageHost _host;

    internal CarPartController(D3DImageHost host) => _host = host;

    /// <summary>
    /// Puts a helper frame into the scene tree, or takes it out — for the component tree's own "Add marker"
    /// and for the undo of one.
    ///
    /// <para>
    /// A frame the graph holds and the tree does not is one the modder cannot select, cannot see and cannot
    /// drag; a frame the tree holds and the graph does not is a row that acts on nothing. Two rows go in, and
    /// they say different things — the copy under the bone says which part the helper belongs to, and the one
    /// in the hierarchy says where it sits in the graph.
    /// </para>
    /// </summary>
    /// <param name="document">The staged document the frame belongs to.</param>
    /// <param name="frame">The frame that has just joined the graph, or just left it.</param>
    /// <param name="joint">Which joint it hangs off, for the row under the bone.</param>
    /// <param name="present">Whether it is in the graph now.</param>
    internal void SyncHelperRows(
        SceneDocumentAdapter document, FrameObjectBase frame, int joint, bool present)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(frame);

        ISceneSource source = document.Node(frame);
        // Gone: both of its rows go with it, wherever they are.
        if (!present)
        {
            foreach (SceneNode root in _host.Tree.Roots) Prune(root, source);
            return;
        }

        SceneNode? bone = FindBoneRow(document, joint);
        if (bone != null && !bone.Children.Any(c => ReferenceEquals(c.Source, source)))
        {
            bone.AddChild(new SceneNode($"{frame.Name}  ({Kind(frame)})", "Attachment", false)
            {
                Source = source,
            });
            bone.IsExpanded = true;
        }

        // Beside a frame of the same type in the hierarchy — the same neighbour rule AddPart follows, and for
        // the same reason: a helper's grouping node is where the loader would have put it on a reload.
        SceneNode? sibling = FindHierarchySibling(source, Kind(frame));
        if (sibling?.Parent is { } holder
            && !holder.Children.Any(c => ReferenceEquals(c.Source, source)))
        {
            holder.AddChild(new SceneNode(frame.Name.ToString(), Kind(frame), false) { Source = source });
            holder.IsExpanded = true;
        }
    }

    /// <summary>The tree row of a model's joint, so a new attachment can join its children.</summary>
    private SceneNode? FindBoneRow(SceneDocumentAdapter document, int joint)
    {
        foreach (SceneNode root in _host.Tree.Roots)
        {
            if (Find(root, node => node.Source is BoneNodeAdapter bone && bone.Index == joint
                    && ReferenceEquals(bone.Document, document)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>A hierarchy row of the same KIND to sit beside — never the copy under a bone, and never the
    /// frame itself.</summary>
    private SceneNode? FindHierarchySibling(ISceneSource source, string kind)
    {
        foreach (SceneNode root in _host.Tree.Roots)
        {
            if (Find(root, node => !ReferenceEquals(node.Source, source)
                    && string.Equals(node.Kind, kind, StringComparison.Ordinal)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static SceneNode? Find(SceneNode node, Func<SceneNode, bool> wanted)
    {
        if (wanted(node)) return node;
        foreach (SceneNode child in node.Children)
        {
            if (Find(child, wanted) is { } found) return found;
        }
        return null;
    }

    private static void Prune(SceneNode node, ISceneSource source)
    {
        foreach (SceneNode child in node.Children.ToList())
        {
            if (ReferenceEquals(child.Source, source)) node.Children.Remove(child);
            else Prune(child, source);
        }
    }

    private static string Kind(FrameObjectBase frame) => frame switch
    {
        FrameObjectDummy => "Dummy",
        FrameObjectPoint => "Point",
        // A collision stub, which is what a minted solid volume hangs its handle on. Named the way the
        // collision path has always named one, so the two ways of adding a box produce the same row.
        FrameObjectCollision => "Collision",
        _ => "Frame",
    };
}
