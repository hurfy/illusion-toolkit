namespace Illusion.Domain;

/// <summary>A scene folder inside a loaded scene document (a named grouping, not transformable).</summary>
public interface IFrameScene : ISceneSource
{
    string Name { get; }
}
