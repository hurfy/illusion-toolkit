namespace Illusion.Assets.World;

/// <summary>Scene line: its active asset set (loadList) + how many of them actually render.</summary>
public sealed class StreamSceneLine
{
    public string SceneName { get; init; } = null!;
    public string Name { get; init; } = null!;
    public int LineID { get; init; }
    public IReadOnlyList<StreamAsset> Assets { get; init; } = null!;
    public int RenderableCount { get; init; }
}
