using System.Numerics;

namespace Illusion.Domain;

/// <summary>
/// Render-neutral marker geometry for the actors a scene cannot draw: sounds, lights, particles, script hooks,
/// triggers and the like place nothing visible, so the viewport stands a small colored glyph where each one
/// sits. Built in the asset layer (which knows the actor types) and handed to the viewport pass as a plain
/// colored line list — position pairs in the same world space the meshes use.
/// </summary>
public sealed class ActorMarkerRenderData
{
    /// <summary>Line endpoints (A,B,A,B,…), world space.</summary>
    public Vector3[] Positions { get; init; } = [];

    /// <summary>Colour per entry of <see cref="Positions"/> (rgb + alpha).</summary>
    public Vector4[] Colors { get; init; } = [];

    /// <summary>Markers represented (one glyph is several line segments).</summary>
    public int MarkerCount { get; init; }

    public int VertexCount => Positions.Length;
}
