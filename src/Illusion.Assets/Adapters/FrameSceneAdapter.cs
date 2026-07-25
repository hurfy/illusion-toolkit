using Illusion.Domain;
using Illusion.Formats.Frames.Resources;

namespace Illusion.Assets.Adapters;

/// <summary>A vendor scene folder (<see cref="FrameHeaderScene"/>) as the Domain's <see cref="IFrameScene"/>.
/// Carries the vendor object so it can be used as a reparent target.</summary>
public sealed class FrameSceneAdapter : IFrameScene
{
    public FrameSceneAdapter(FrameHeaderScene scene)
    {
        Scene = scene;
        Name = scene.Name?.ToString() ?? "scene";
    }

    public string Name { get; }

    /// <summary>The wrapped vendor scene folder — the asset layer's reparent route; the UI never touches it.</summary>
    internal FrameHeaderScene Scene { get; }
}
