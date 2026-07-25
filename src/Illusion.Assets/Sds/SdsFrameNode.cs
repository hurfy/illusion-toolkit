using Illusion.Domain;

namespace Illusion.Assets.Sds;

/// <summary>CPU node of the internal SDS hierarchy: frame/mesh/light/… + children. A mesh has <see cref="Mesh"/> set.</summary>
public sealed class SdsFrameNode
{
    public string Name { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public string Category { get; set; } = "Normal"; // for scenes: Proxy / Normal (filters during streaming)
    public MeshData? Mesh { get; set; }         // non-null only on mesh nodes
    /// <summary>Backing source: an <see cref="IFrameNode"/> (frame/mesh) or an <see cref="IFrameScene"/>
    /// (scene folder). Carried to the UI node — the UI only ever sees these Domain ports.</summary>
    public ISceneSource? Source { get; set; }
    public List<SdsFrameNode> Children { get; } = new();
}
