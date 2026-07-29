using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Actors;

namespace Illusion.Assets.Actors;

/// <summary>
/// Turns the actors nothing draws (<see cref="ActorPlacements.Invisible"/>) into overlay line geometry: one
/// octahedral glyph per actor, coloured by <see cref="ActorCategories"/>. Sizes are in world units and fixed —
/// a screen-space size would need per-frame rebuilds, and at Mafia II's scale a third of a metre reads fine
/// from a street away without hiding the geometry behind it.
/// </summary>
public static class ActorMarkerBuilder
{
    /// <summary>Half-diagonal of a glyph, world units. Also the radius a viewport click tests against
    /// (see <c>ActorPicking</c>), so the thing you aim at is the thing you see.</summary>
    public const float Radius = 0.35f;

    // Octahedron: six poles, twelve edges. Cheap (24 vertices), unambiguous at any angle, and does not
    // resemble either the collision hulls or the navigation boxes already on screen.
    private static readonly (int A, int B)[] Edges =
    [
        (0, 2), (2, 1), (1, 3), (3, 0),   // around the equator
        (0, 4), (2, 4), (1, 4), (3, 4),   // to the top pole
        (0, 5), (2, 5), (1, 5), (3, 5),   // to the bottom pole
    ];

    /// <param name="actors">The actors to mark.</param>
    /// <param name="scale">Glyph size relative to <see cref="Radius"/> — the selection highlight draws the same
    /// glyph slightly larger so it reads over the ordinary one.</param>
    /// <param name="colorOverride">One colour for every glyph instead of the per-category colour (used by the
    /// selection highlight).</param>
    public static ActorMarkerRenderData Build(IReadOnlyList<ActorEntry> actors, float scale = 1f,
        Vector4? colorOverride = null)
    {
        if (actors.Count == 0) return new ActorMarkerRenderData();

        var positions = new Vector3[actors.Count * Edges.Length * 2];
        var colors = new Vector4[positions.Length];
        Span<Vector3> poles = stackalloc Vector3[6];
        float r = Radius * scale;
        int v = 0;

        foreach (ActorEntry actor in actors)
        {
            Vector4 color = colorOverride ?? ActorCategories.Color(ActorCategories.Of(actor.Type));
            Vector3 c = actor.Position;
            poles[0] = c + new Vector3(r, 0, 0);
            poles[1] = c - new Vector3(r, 0, 0);
            poles[2] = c + new Vector3(0, r, 0);
            poles[3] = c - new Vector3(0, r, 0);
            poles[4] = c + new Vector3(0, 0, r);
            poles[5] = c - new Vector3(0, 0, r);

            foreach ((int a, int b) in Edges)
            {
                positions[v] = poles[a];
                colors[v++] = color;
                positions[v] = poles[b];
                colors[v++] = color;
            }
        }

        return new ActorMarkerRenderData
        {
            Positions = positions,
            Colors = colors,
            MarkerCount = actors.Count,
        };
    }
}
