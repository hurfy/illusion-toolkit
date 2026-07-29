using System.Numerics;
using Illusion.Assets.Actors;
using Illusion.Formats.Actors;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// The glyphs of one district and the entries a click is tested against, derived together from the live actor
/// list.
///
/// This is a seam on purpose. Both used to be snapshots taken once when the district loaded, which meant a
/// deleted actor stayed clickable for the rest of the session (and an actor pick beats the geometry behind
/// it, so it swallowed clicks aimed elsewhere), a copy was never drawn, and a moved actor left its marker
/// behind. Both lists come out of one walk so that a picked index always names the glyph it was aimed at.
/// </summary>
internal static class ActorGlyphSet
{
    /// <summary>Fills the actors to draw a glyph for and the pick entry of each, in one shared order.</summary>
    /// <param name="placements">The district's placements — <see cref="ActorPlacements.Invisible"/> is the
    /// authority on who has a glyph, and it follows deletes, copies and undo.</param>
    /// <param name="nodes">Actor → its tree row, for the eye and for what a pick selects. An actor with no row
    /// is skipped rather than drawn: nothing could select it.</param>
    internal static void Collect(ActorPlacements placements, IReadOnlyDictionary<ActorEntry, SceneNode> nodes,
        List<ActorEntry> glyphs, List<(SceneNode Node, Vector3 Position)> pickables)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentNullException.ThrowIfNull(pickables);

        foreach (ActorEntry actor in placements.Invisible)
        {
            if (!nodes.TryGetValue(actor, out SceneNode? node) || !node.IsVisible) continue;
            glyphs.Add(actor);
            pickables.Add((node, actor.Position));
        }
    }
}
