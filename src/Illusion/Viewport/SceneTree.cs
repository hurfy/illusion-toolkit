using System.Collections.ObjectModel;
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
        var node = new SceneNode(fn.Name, fn.Kind, fn.Children.Count > 0) { Category = fn.Category, Source = fn.Source };
        if (fn.Mesh != null) { node.Pending = fn.Mesh; meshLeaves.Add(node); }
        foreach (Assets.Sds.SdsFrameNode c in fn.Children) node.AddChild(BuildSceneTree(c, meshLeaves));
        return node;
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
