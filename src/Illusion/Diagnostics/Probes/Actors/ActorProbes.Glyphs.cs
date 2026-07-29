using System.Text;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

/// <summary>The glyphs of a district and the entries a click is tested against, through every edit that
/// changes them. Part of <see cref="ActorProbes"/>.</summary>
internal static partial class ActorProbes
{
    private static void CheckGlyphSet(string district, SceneDocumentAdapter document, ActorPlacements placements,
        StringBuilder sb, Action<string, bool, string> check)
    {
        var rows = new Dictionary<ActorEntry, SceneNode>();
        foreach (ActorEntry actor in placements.All)
        {
            rows[actor] = new SceneNode(actor.EntityName, "Actor", false) { Source = document.ActorNode(actor) };
        }

        List<ActorEntry> glyphs = new();
        List<(SceneNode Node, System.Numerics.Vector3 Position)> picks = new();
        void Collect()
        {
            glyphs = new List<ActorEntry>();
            picks = new List<(SceneNode, System.Numerics.Vector3)>();
            ActorGlyphSet.Collect(placements, rows, glyphs, picks);
        }

        Collect();
        int baseline = glyphs.Count;
        check("every glyph actor gets a marker and a click target",
            baseline == placements.Invisible.Count && picks.Count == baseline,
            $"{baseline} of {placements.Invisible.Count}, {picks.Count} click targets");
        if (baseline == 0) { sb.AppendLine("(district has no glyph actors — glyph-set checks skipped)"); return; }

        bool aligned = true;
        for (int i = 0; i < glyphs.Count && aligned; i++) aligned = glyphs[i].Position == picks[i].Position;
        check("a picked index names the glyph it was aimed at", aligned, $"{glyphs.Count} in step");

        // Deleting: the actor leaves the list, so nothing draws it and nothing can click it.
        ActorEntry victim = placements.Invisible[baseline / 2];
        int victimIndex = placements.All.ToList().IndexOf(victim);
        ActorsFile? victimPack = placements.PackOf(victim);
        FrameObjectBase? victimTarget = placements.Detach(victim);
        Collect();
        check("a deleted actor stops being drawn and stops being clickable",
            glyphs.Count == baseline - 1 && !glyphs.Contains(victim) && picks.Count == baseline - 1,
            $"{glyphs.Count} left, still listed: {glyphs.Contains(victim)}");

        placements.Attach(victim, victimPack!, victimTarget, victimIndex, hadGlyph: true);
        Collect();
        check("undoing the delete brings its marker back", glyphs.Contains(victim), $"{glyphs.Count} markers");

        // …and brings back everything a LATER edit of it needs. The editor skips an actor whose pack it cannot
        // find, silently — so an actor restored without one looks fine and can never be deleted again.
        check("an actor restored by undo can still be edited",
            ReferenceEquals(placements.PackOf(victim), victimPack) && victimPack != null,
            placements.PackOf(victim) == null ? "PackOf is null after undo" : "pack intact");

        // Moving: the marker and the click target travel with the actor, rather than staying where it loaded.
        // Deliberately not the actor the delete test used — its state has already been through a round trip.
        ActorEntry mover = placements.Invisible.FirstOrDefault(a => !ReferenceEquals(a, victim)) ?? victim;
        System.Numerics.Vector3 was = mover.Position;
        mover.Position = was + new System.Numerics.Vector3(12f, -7f, 3f);
        Collect();
        int moverAt = glyphs.IndexOf(mover);
        check("a moved actor takes its marker and its click target with it",
            moverAt >= 0 && picks[moverAt].Position == mover.Position,
            moverAt >= 0 ? $"{was} → {picks[moverAt].Position}" : "(not listed)");
        mover.Position = was;

        // Copying: a new actor is drawn and clickable as soon as it has a row.
        if (placements.PackOf(mover) is { } pack && pack.Duplicate(mover, out _) is { } copy)
        {
            placements.AddCopy(copy, mover, pack);
            Collect();
            check("a copy is drawn and clickable only once it has a row",
                !glyphs.Contains(copy), "no row yet");

            rows[copy] = new SceneNode(copy.EntityName, "Actor", false) { Source = document.ActorNode(copy) };
            Collect();
            check("a copy with a row is drawn and clickable",
                glyphs.Contains(copy) && picks.Count == glyphs.Count, $"{glyphs.Count} markers");

            placements.Detach(copy);
            pack.RemoveCopy(copy);
            rows.Remove(copy);
        }

        // Hiding a row takes its glyph with it — the eye has nothing else to act on for a glyph actor.
        rows[mover].IsVisible = false;
        Collect();
        check("hiding an actor's row hides its glyph", !glyphs.Contains(mover), $"{glyphs.Count} markers");
        rows[mover].IsVisible = true;

        Collect();
        check("the district ends where it started", glyphs.Count == baseline, $"{glyphs.Count} vs {baseline}");
    }

}
