using System.Collections.ObjectModel;
using Illusion.Domain;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// The viewport's scene tree: folder → SDS → FrameResource → frame hierarchy → mesh. Owns the roots,
/// the folder index, the mesh counter and the Render-tab visibility filters (proxy/snow). Purely a
/// UI-thread model — the streaming pipeline builds detached subtrees and attaches them here.
/// </summary>
internal sealed class SceneTree
{
    /// <summary>Tree roots (source folders). Populated incrementally.</summary>
    public ObservableCollection<SceneNode> Roots { get; } = new();

    private readonly Dictionary<string, SceneNode> _folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of meshes currently attached to the renderer.</summary>
    public int MeshCount;

    /// <summary>Render tab filters — all off by default: proxy scenes (whole neighbor/proxy districts),
    /// proxy meshes (embedded proxy_ nodes inside a district's main scene), snow scenes (prefix Z).</summary>
    public bool ShowProxyScenes;
    public bool ShowProxyMeshes;
    public bool ShowSnowScenes;

    public SceneNode GetOrCreateFolder(string name)
    {
        if (_folders.TryGetValue(name, out SceneNode? f)) return f;
        f = new SceneNode(name, "Folder", true) { IsExpanded = true };
        _folders[name] = f;
        Roots.Add(f);
        return f;
    }

    /// <summary>Removes an SDS subtree from its folder; drops the folder itself once empty.</summary>
    public void RemoveSds(SceneNode sds, SceneNode folder)
    {
        folder.Children.Remove(sds);
        if (folder.Children.Count == 0) { Roots.Remove(folder); _folders.Remove(folder.Name); }
    }

    public static SceneNode BuildSceneTree(Assets.Sds.SdsFrameNode fn, List<SceneNode> meshLeaves)
    {
        bool hasChildren = fn.Children.Count > 0 || fn.Skeleton != null;
        var node = new SceneNode(fn.Name, fn.Kind, hasChildren) { Category = fn.Category, Source = fn.Source };
        if (fn.Mesh != null) { node.Pending = fn.Mesh; meshLeaves.Add(node); }
        if (fn.Skeleton is { } skeleton) node.AddChild(BuildSkeletonTree(skeleton));
        foreach (Assets.Sds.SdsFrameNode c in fn.Children) node.AddChild(BuildSceneTree(c, meshLeaves));
        return node;
    }

    /// <summary>
    /// The rig under its model, as a tree: one branch per root bone, children nested by the hierarchy's parent
    /// indices. For a car this is the list of its parts — <c>doorFL</c>, <c>coverF</c>, <c>axleFR</c> — which
    /// nothing else in the scene tree shows, because they are not frames but weight groups inside one mesh.
    /// </summary>
    private static SceneNode BuildSkeletonTree(SkeletonData skeleton)
    {
        var root = new SceneNode($"Skeleton ({skeleton.Bones.Count})", "Skeleton", true);
        var nodes = new SceneNode[skeleton.Bones.Count];
        for (int i = 0; i < skeleton.Bones.Count; i++)
        {
            bool hasChildren = skeleton.Bones.Any(b => b.Parent == i) ||
                               skeleton.Attachments.Any(a => a.Joint == i);
            nodes[i] = new SceneNode(skeleton.Bones[i].Name, "Bone", hasChildren)
            {
                Source = skeleton.Bones[i].Source,   // selectable and draggable like any other object
            };
        }

        // Parents come before children in every rig in the corpus, but a forward reference must not lose a
        // bone: anything whose parent is not placed yet hangs off the branch root instead of vanishing.
        for (int i = 0; i < nodes.Length; i++)
        {
            int parent = skeleton.Bones[i].Parent;
            SceneNode host = parent >= 0 && parent < i ? nodes[parent] : root;
            // The bone becomes a branch the moment it gains a child; the flag is set at construction, so
            // a parent bone is built as a container up front (a leaf just never grows one).
            host.AddChild(nodes[i]);
        }

        // What hangs off each bone, under that bone. The frames themselves appear elsewhere in the tree too,
        // in their own place in the hierarchy — but that place is a grouping node at the origin and says
        // nothing about which part they belong to, whereas this does.
        foreach (BoneAttachment a in skeleton.Attachments)
        {
            if (a.Joint < 0 || a.Joint >= nodes.Length) continue;
            nodes[a.Joint].AddChild(new SceneNode($"{a.Name}  ({a.TypeName})", "Attachment", false)
            {
                Source = a.Source,
            });
        }
        return root;
    }

    // True while every link up to a current root is still a real child — i.e. the node was not detached by a
    // district unload (which removes the SDS subtree from its folder without clearing Parent back-pointers).
    public bool IsInScene(SceneNode node)
    {
        SceneNode cur = node;
        while (cur.Parent is { } p)
        {
            if (!p.Children.Contains(cur)) return false;
            cur = p;
        }
        return Roots.Contains(cur);
    }

    public static bool IsSelfOrDescendantOf(SceneNode node, SceneNode? ancestor)
    {
        for (SceneNode? n = node; n != null; n = n.Parent)
            if (ReferenceEquals(n, ancestor)) return true;
        return false;
    }

    // Applies scene filters to all loaded SDS (when toggling a setting on the Render tab).
    // Tree layout: folder → SDS → FrameResource → scene.
    public void ApplySceneFilters()
    {
        foreach (SceneNode folder in Roots)
            foreach (SceneNode sds in folder.Children)
                foreach (SceneNode frameRes in sds.Children)
                    foreach (SceneNode sc in frameRes.Children)
                        ApplySceneFilter(sc);
    }

    // Visibility of one scene by Render tab filters: proxy/snow scenes are hidden entirely by their own
    // toggles; inside a regular scene we separately hide embedded proxy_ nodes (e.g. mesh proxy_<district>
    // in the district's main scene) via the independent "Render proxy meshes" toggle.
    public void ApplySceneFilter(SceneNode scene)
    {
        if (scene.Category == "Proxy") { scene.IsVisible = ShowProxyScenes; return; }
        if (scene.Category == "Snow") { scene.IsVisible = ShowSnowScenes; return; }
        ApplyProxyToSubtree(scene);
    }

    // Recursively: any proxy_ node (mesh or group) follows the proxy-meshes toggle; once found — hide/
    // show it entirely (cascade to the branch) and don't descend further.
    private void ApplyProxyToSubtree(SceneNode node)
    {
        if (node.IsProxy) { node.IsVisible = ShowProxyMeshes; return; }
        foreach (SceneNode c in node.Children) ApplyProxyToSubtree(c);
    }

    /// <summary>Empties the tree, folder index and counter (scene reset).</summary>
    public void Clear()
    {
        Roots.Clear();
        _folders.Clear();
        MeshCount = 0;
    }
}
