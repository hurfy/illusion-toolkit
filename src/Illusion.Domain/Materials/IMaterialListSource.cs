namespace Illusion.Domain.Materials;

/// <summary>
/// A scene source (see <see cref="ISceneSource"/>) that carries a list of materials — a mesh. The format adapter
/// layer resolves the mesh's material assignments against the MTL library and returns them as engine-neutral
/// <see cref="MaterialInfo"/>, so the UI can render a Materials tab without depending on a format backend.
/// </summary>
public interface IMaterialListSource : ISceneSource
{
    /// <summary>The materials of this object's LOD0 (empty when it has none — i.e. not a mesh).</summary>
    IReadOnlyList<MaterialInfo> GetMaterials();
}
