namespace Illusion.Domain.Properties;

/// <summary>
/// A scene source (see <see cref="ISceneSource"/>) that exposes its data fields as editable property groups. The
/// format adapter layer implements this over its concrete objects so the UI can render a full property panel
/// without ever depending on a format backend.
/// </summary>
public interface IPropertySource : ISceneSource
{
    /// <summary>The property groups in stable display order. Rebuilt per call; the descriptors' delegates stay
    /// bound to the same underlying object, so a descriptor held for undo/redo keeps working across calls.</summary>
    IReadOnlyList<PropertyGroup> GetPropertyGroups();

    /// <summary>The object's type label for the per-type tab header (e.g. "Mesh", "Light").</summary>
    string TypeName { get; }
}
