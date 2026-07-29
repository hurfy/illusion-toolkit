using System.IO;
using System.Text;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Assets.Frames;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Copying an actor together with the object it places: the clone, its shape, and the link the
/// game follows to it. Part of <see cref="ActorProbes"/> — one file per area of the actor layer.</summary>
internal static partial class ActorProbes
{
    // A copied object has to have the SAME SHAPE as the one it was copied from: the same parent slots filled,
    // with links that pointed inside the subtree redirected at the copy and links that pointed outside left
    // alone. The one that bites is the anchored-mesh flag — a mesh whose flag says it hangs off a second
    // parent, with that slot cleared, writes an anchor index of -1 into the file and the game follows it on
    // load. Nothing in the toolkit notices; the district just stops opening.
    private static void CheckCloneShape(ActorPlacements placements, ActorPrototypeCloner.ClonedPrototype clone,
        StringBuilder sb, Action<string, bool, string> check)
    {
        int compared = 0, shapeOff = 0, danglingAnchor = 0;
        string firstOff = "";
        foreach (KeyValuePair<FrameObjectBase, FrameObjectBase> pair in clone.Pairs)
        {
            compared++;
            bool sourceP1 = pair.Key.Refs.ContainsKey(FrameEntryRefTypes.Parent1);
            bool sourceP2 = pair.Key.Refs.ContainsKey(FrameEntryRefTypes.Parent2);
            bool cloneP1 = pair.Value.Refs.ContainsKey(FrameEntryRefTypes.Parent1);
            bool cloneP2 = pair.Value.Refs.ContainsKey(FrameEntryRefTypes.Parent2);
            if (sourceP1 != cloneP1 || sourceP2 != cloneP2)
            {
                shapeOff++;
                if (firstOff.Length == 0)
                {
                    firstOff = $"{pair.Key.Name}: source has ({sourceP1},{sourceP2}), copy has ({cloneP1},{cloneP2})";
                }
            }

            if (pair.Value is FrameObjectSingleMesh mesh
                && mesh.SingleMeshFlags.HasFlag(SingleMeshFlags.ParentIndex2_Flag) && !cloneP2)
            {
                danglingAnchor++;
            }
        }
        check("the copy has the same parent shape as the object it came from", shapeOff == 0 && compared > 0,
            $"{compared} frames compared, {shapeOff} off {firstOff}");
        check("no copied mesh claims an anchor it does not have", danglingAnchor == 0,
            danglingAnchor == 0 ? "" : $"{danglingAnchor} mesh(es) flagged as anchored with an empty slot");
        foreach (KeyValuePair<FrameObjectBase, FrameObjectBase> pair in clone.Pairs)
        {
            sb.AppendLine($"    {pair.Key.GetType().Name} '{pair.Key.Name}' → '{pair.Value.Name}'");
            sb.AppendLine($"        source: parent1={Describe(placements, pair.Key, FrameEntryRefTypes.Parent1)}" +
                          $", parent2={Describe(placements, pair.Key, FrameEntryRefTypes.Parent2)}" +
                          $", flags={(pair.Key as FrameObjectSingleMesh)?.SingleMeshFlags.ToString() ?? "-"}");
            sb.AppendLine($"        copy:   parent1={Describe(placements, pair.Value, FrameEntryRefTypes.Parent1)}" +
                          $", parent2={Describe(placements, pair.Value, FrameEntryRefTypes.Parent2)}");
        }

        int withP1 = clone.Pairs.Keys.Count(f => f.Refs.ContainsKey(FrameEntryRefTypes.Parent1));
        int withP2 = clone.Pairs.Keys.Count(f => f.Refs.ContainsKey(FrameEntryRefTypes.Parent2));
        int anchored = clone.Pairs.Keys.OfType<FrameObjectSingleMesh>()
            .Count(m => m.SingleMeshFlags.HasFlag(SingleMeshFlags.ParentIndex2_Flag));
        sb.AppendLine($"    clone: {compared} frames, on the name table: {clone.IsOnNameTable}; " +
                      $"sources with parent1: {withP1}, with parent2: {withP2}, flagged as anchored: {anchored}");
    }

    // Meshes reachable from a holder through the child links — the same walk the placements use to decide
    // whether an actor has geometry or is drawn as a glyph.
    private static int MeshCountUnder(FrameObjectBase frame) => MeshCountUnder(frame, new HashSet<FrameObjectBase>());

    private static int MeshCountUnder(FrameObjectBase frame, HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return 0;
        int count = frame is FrameObjectSingleMesh { Geometry: not null } ? 1 : 0;
        foreach (FrameObjectBase child in frame.Children) count += MeshCountUnder(child, seen);
        return count;
    }

    // Prefers the object whose mesh sits DEEPEST under it. A mesh hanging straight off the holder and one two
    // levels down (an animated platform puts its mesh under an anim node) are different cases for everything
    // that walks the subtree, and the deep one is the one that has gone wrong in practice.
    private static bool Deeper(FrameObjectBase candidate, FrameObjectBase current) =>
        MeshDepth(candidate, 0, new HashSet<FrameObjectBase>()) >
        MeshDepth(current, 0, new HashSet<FrameObjectBase>());

    private static int MeshDepth(FrameObjectBase frame, int depth, HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return -1;
        if (frame is FrameObjectSingleMesh { Geometry: not null }) return depth;
        int best = -1;
        foreach (FrameObjectBase child in frame.Children)
        {
            best = Math.Max(best, MeshDepth(child, depth + 1, seen));
        }
        return best;
    }

    // What a parent slot actually points at — a scene folder, another object, or nothing.
    private static string Describe(ActorPlacements placements, FrameObjectBase frame, FrameEntryRefTypes slot)
    {
        if (!frame.Refs.TryGetValue(slot, out int id)) return "(none)";
        return placements.DescribeRef(id);
    }

    // Copying an actor that PLACES an object: it gets its own clone of that object and its own scene
    // reference, and the whole thing has to survive a trip through the writer — the copy's link is a hash of
    // a name that did not exist a moment ago, pointing at a row that did not exist either.
    private static void CheckPlacingDuplicate(SceneDocumentAdapter document, ActorPlacements placements,
        StringBuilder sb, Action<string, bool, string> check)
    {
        ActorEntry? placing = null, physical = null;
        foreach (ActorEntry a in placements.All)
        {
            if (placements.TargetOf(a) is not { Children.Count: > 0 } target) continue;
            if (placements.PackOf(a) is not { } p || !p.SceneReferences.Any(r => r.FrameHash == a.FrameHash)) continue;

            if (!ActorPrototypeCloner.CanClone(target)) continue; // a skinned character is not copyable at all
            if (ActorPrototypeCloner.HullsOf(target) == 0) placing ??= a;
            else if (physical == null || Deeper(target, placements.TargetOf(physical)!)) physical = a;
        }

        // An object built on collision copies like any other — it is only flagged to the user, because one such
        // copy crashed the game on load and why is still unknown (see ActorPrototypeCloner.HullsOf).
        if (physical != null && placements.TargetOf(physical) is { } physicalTarget)
        {
            ActorPrototypeCloner.ClonedPrototype? physicalClone =
                ActorPrototypeCloner.TryClone(document, physicalTarget, out string? physicalReason);
            check("an object built on collision copies, and its hulls come with it",
                physicalClone != null
                && ActorPrototypeCloner.HullsOf(physicalClone.Root) == ActorPrototypeCloner.HullsOf(physicalTarget),
                physicalClone == null
                    ? $"{physical.EntityName}: {physicalReason}"
                    : $"{physical.EntityName}: {ActorPrototypeCloner.HullsOf(physicalClone.Root)} hull(s)");

            // The editor re-applies a copy after making it (and again on every redo), which runs the re-linking
            // a second time. A HOLDER between the object's root and its mesh — an animated platform puts its
            // mesh under an anim node — used to fall out of the root's child list on that second pass, and
            // since that list is what finds the geometry, the copy turned into an actor with nothing under it,
            // drawn as a glyph. The meshes themselves never showed it: their own re-link puts them back.
            if (physicalClone != null)
            {
                int deepBefore = MeshCountUnder(physicalClone.Root);
                physicalClone.Reattach();
                physicalClone.Reattach();
                check("re-applying a copy leaves a deep object intact",
                    MeshCountUnder(physicalClone.Root) == deepBefore && deepBefore > 0,
                    $"{physical.EntityName}: {deepBefore} mesh(es) under {physicalClone.Root.Name}, " +
                    $"{MeshCountUnder(physicalClone.Root)} after re-applying");
            }

            // A copy whose object has geometry must be drawn as that geometry. Landing in the glyph list means
            // the placements could not see a mesh under the clone — which is what a copy showing up as a
            // diamond looks like from the outside.
            if (physicalClone != null && placements.PackOf(physical) is { } physicalPack)
            {
                ActorEntry? physicalCopy = physicalPack.Duplicate(physical,
                    new ActorPlacedFrame(physicalClone.Root.Name.String, physicalClone.FrameIndex), out _);
                if (physicalCopy != null)
                {
                    placements.AddCopy(physicalCopy, physical, physicalPack, physicalClone.Root);
                    bool sourceDrawn = !placements.HasGlyph(physical);
                    check("a copy is drawn the way its original is",
                        placements.HasGlyph(physicalCopy) == placements.HasGlyph(physical),
                        $"{physical.EntityName}: original {(sourceDrawn ? "geometry" : "glyph")}, " +
                        $"copy {(placements.HasGlyph(physicalCopy) ? "glyph" : "geometry")}");
                    placements.Detach(physicalCopy);
                    physicalPack.RemoveCopy(physicalCopy);
                }
            }
            physicalClone?.Detach();
        }

        if (placing == null || placements.PackOf(placing) is not { } pack)
        {
            sb.AppendLine("(no copyable actor placing an object in this district — copy-with-object checks skipped)");
            return;
        }

        FrameObjectBase source = placements.TargetOf(placing)!;
        byte[] before = pack.ToBytes();
        int refsBefore = pack.SceneReferences.Count;

        ActorPrototypeCloner.ClonedPrototype? clone =
            ActorPrototypeCloner.TryClone(document, source, out string? cloneReason);
        check("the object an actor places can be cloned", clone != null, cloneReason ?? placing.EntityName);
        if (clone == null) return;

        check("the clone is its own object, named apart from the original",
            !ReferenceEquals(clone.Root, source) && clone.Root.Name.String != source.Name.String
            && clone.FrameIndex != uint.MaxValue,
            $"{source.Name} → {clone.Root.Name} at row {clone.FrameIndex}");

        // Only the root may be renamed. An animation is bound to an object's inner frames by name, so a
        // renamed child is one the animation cannot find — and the shipped data has no problem with repeated
        // names (eight wanted posters share one child name over eight differently-named roots).
        int renamedChildren = clone.Pairs.Count(p =>
            !ReferenceEquals(p.Key, source) && p.Key.Name.String != p.Value.Name.String);
        check("a copy renames its root and nothing else",
            renamedChildren == 0 && clone.Root.Name.String != source.Name.String,
            $"{clone.Pairs.Count - 1} child frame(s), {renamedChildren} renamed");

        CheckCloneShape(placements, clone, sb, check);

        // The editor re-applies a copy after making it (and again on every redo), which runs the re-linking a
        // second time. That pass has to leave the tree alone: a node whose two parent slots name the same
        // holder used to fall out of its holder's child list on the second pass, and since that list is what
        // finds the geometry, the copy turned into an actor with nothing under it — drawn as a glyph.
        int meshesBefore = MeshCountUnder(clone.Root);
        clone.Reattach();
        clone.Reattach();
        // An empty holder legitimately has none — what matters is that the count does not change.
        check("re-applying a copy leaves its object intact",
            MeshCountUnder(clone.Root) == meshesBefore,
            $"{meshesBefore} mesh(es) under {clone.Root.Name}, {MeshCountUnder(clone.Root)} after re-applying");

        var placed = new ActorPlacedFrame(clone.Root.Name.String, clone.FrameIndex);
        ActorEntry? copy = pack.Duplicate(placing, placed, out string? why);
        check("an actor with a clone of its object copies", copy != null, why ?? "");
        if (copy == null) { clone.Detach(); return; }

        check("the copy points at its own object, not the original's",
            copy.FrameHash != placing.FrameHash
            && copy.FrameHash == Formats.Hashing.Fnv64.Hash(clone.Root.Name.String)
            && copy.LinkedFrame == clone.Root.Name.String,
            copy.LinkedFrame);
        check("the copy brings its own scene reference",
            pack.SceneReferences.Count == refsBefore + 1
            && pack.SceneReferences.Any(r => r.FrameHash == copy.FrameHash && r.FrameIndex == clone.FrameIndex),
            $"{refsBefore} → {pack.SceneReferences.Count} references");

        // A copy has to be copyable in turn: everything the editor needs to copy an actor — the pack it
        // belongs to, the object it places — has to be registered for a copy exactly as it is for a row that
        // came off disk, or the second copy is refused with a reason about the first.
        placements.AddCopy(copy, placing, pack, clone.Root);
        check("a copy belongs to the same pack as its original",
            ReferenceEquals(placements.PackOf(copy), pack), copy.EntityName);
        check("a copy is registered as placing its own object",
            ReferenceEquals(placements.TargetOf(copy), clone.Root),
            placements.TargetOf(copy)?.Name.String ?? "(nothing)");

        // The clone's geometry has to stand where the COPY stands. It is built at the prototype's own place —
        // the origin — and only the placement moves it, so the copy's meshes have to be re-pushed once the
        // placement exists. A copy whose geometry stayed at the origin looks, from the viewport, like a copy
        // that was never made.
        FrameObjectBase geometry = clone.Renderables.Count > 0 ? clone.Renderables[0].Frame : clone.Root;
        System.Numerics.Matrix4x4 placement = placements.For(geometry);
        check("the copy's geometry carries the copy's placement, not the prototype's",
            !placement.IsIdentity && Approx(placement.Translation, copy.Position, 1e-3f),
            $"{geometry.Name} placed at {placement.Translation}, copy at {copy.Position}");

        ActorPrototypeCloner.ClonedPrototype? second =
            ActorPrototypeCloner.TryClone(document, clone.Root, out string? secondReason);
        check("the copy's object can be cloned again", second != null, secondReason ?? "");
        ActorEntry? copyOfCopy = second == null
            ? null
            : pack.Duplicate(copy, new ActorPlacedFrame(second.Root.Name.String, second.FrameIndex), out secondReason);
        check("a copy can be copied", copyOfCopy != null, secondReason ?? copyOfCopy?.EntityName ?? "");
        if (copyOfCopy != null)
        {
            check("the second copy gets its own name and object",
                copyOfCopy.EntityName != copy.EntityName && copyOfCopy.FrameHash != copy.FrameHash,
                $"{copyOfCopy.EntityName} → {copyOfCopy.LinkedFrame}");
            pack.RemoveCopy(copyOfCopy);
        }
        second?.Detach();
        placements.Detach(copy);

        // The pack has to survive the writer, and the copy has to still find its own object afterwards —
        // resolution is by hash through the reference table, exactly as the game does it.
        byte[] grown = pack.ToBytes();
        using var stream = new MemoryStream(grown, writable: false);
        ActorsFile reread = ActorsFile.Read(stream);
        check("the grown pack re-reads and is a fixpoint",
            reread.Actors.Count == pack.Actors.Count && reread.ToBytes().AsSpan().SequenceEqual(grown),
            $"{reread.Actors.Count} actors, {grown.Length} bytes");

        ActorPlacements resolved = placements.ResolveAgain([reread]);
        ActorEntry? rereadCopy = reread.Actors.FirstOrDefault(a => a.EntityName == copy.EntityName);
        check("the copy resolves to its own clone after a round trip",
            rereadCopy != null && ReferenceEquals(resolved.TargetOf(rereadCopy), clone.Root),
            rereadCopy == null ? "(copy missing)" : $"{resolved.TargetOf(rereadCopy)?.Name}");
        check("the original still places its own object",
            resolved.All.FirstOrDefault(a => a.EntityName == placing.EntityName) is { } original
            && ReferenceEquals(resolved.TargetOf(original), source),
            source.Name.String);

        // Undo: the row, its reference and the cloned object all go, and the pack is what it was.
        pack.RemoveCopy(copy);
        clone.Detach();
        check("undoing the copy restores the pack byte for byte",
            pack.ToBytes().AsSpan().SequenceEqual(before) && pack.SceneReferences.Count == refsBefore,
            $"{pack.SceneReferences.Count} references");
        check("undoing the copy takes its object out of the scene", !clone.IsAttached, clone.Root.Name.String);
    }

}
