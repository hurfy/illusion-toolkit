namespace Illusion.Domain;

/// <summary>
/// Marker for the backing data object a scene-tree node represents. The app's tree nodes carry their source
/// as this abstraction so the UI layer never depends on a concrete format backend; the format adapter layer
/// implements the derived ports (<see cref="IFrameNode"/>, <see cref="IFrameScene"/>,
/// <see cref="ISceneDocument"/>) over its own types.
/// </summary>
public interface ISceneSource;
