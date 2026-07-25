namespace Illusion.Assets.World;

/// <summary>Scene = StreamMap line-group (namespace header) with the list of its lines.</summary>
public sealed class StreamScene
{
    public string Name { get; init; } = null!;
    public IReadOnlyList<StreamSceneLine> Lines { get; init; } = null!;
}
