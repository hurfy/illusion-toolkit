using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Rendering.Scene;
using Illusion.Scene;
using Illusion.Viewport;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the actor layer: a scene's .act pack, and the placement it gives the frame objects
/// it spawns.</summary>
internal static class ActorProbes
{
    /// <summary>
    /// Actor placement for one district: reads the pack, resolves it against the frame resource and checks
    /// that every placed prototype now stands where its actor says — and that nothing else moved. Also
    /// re-saves the pack and requires the bytes back unchanged, since reading now types every actor.
    /// Output: %TEMP%\illusion_actors.txt
    /// </summary>
    internal static void RunActorPlacementProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_actors.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            ExtractedSds scene = SdsMeshLoader.OpenScene(extracted);
            FrameResource? fr = scene.FrameResource;
            if (fr?.FrameObjects == null) { sb.AppendLine("district carries no frame objects"); return; }

            string[] packFiles = scene.Manifest.GetFiles("Actors").ToArray();
            sb.AppendLine($"ACTOR PLACEMENT PROBE — district={district}, packs={packFiles.Length}");
            Check("district ships an actor pack", packFiles.Length > 0, string.Join(", ", packFiles.Select(Path.GetFileName)));
            if (packFiles.Length == 0) return;

            // ── The pack itself: typed and byte-exact ──
            var packs = new List<ActorsFile>();
            int typed = 0, raw = 0, fixpoint = 0;
            foreach (string file in packFiles)
            {
                ActorsFile pack = ActorsFile.Load(file);
                packs.Add(pack);
                foreach (ActorEntry a in pack.Actors) { if (a.IsTyped) typed++; else raw++; }
                if (pack.ToBytes().AsSpan().SequenceEqual(File.ReadAllBytes(file))) fixpoint++;
            }
            Check("packs re-save byte-identically", fixpoint == packFiles.Length, $"{fixpoint}/{packFiles.Length}");
            Check("every actor is typed", raw == 0, $"typed={typed}, raw={raw}");

            // Every scene reference must land on the object whose name it hashes. This is the invariant that
            // catches a copy the editor made but never saved into the scene: the pack keeps the reference, the
            // frame resource never gets the object, and the game is handed a row that holds something else —
            // or nothing at all. Measured to hold for every reference the game ships.
            var objectsInOrder = new List<FrameObjectBase>();
            foreach (object value in fr.FrameObjects.Values)
            {
                if (value is FrameObjectBase f) objectsInOrder.Add(f);
            }
            int references = 0, offTarget = 0;
            string firstOffTarget = "";
            foreach (ActorsFile p in packs)
            {
                foreach (ActorSceneReference reference in p.SceneReferences)
                {
                    references++;
                    string landed = reference.FrameIndex < objectsInOrder.Count
                        ? objectsInOrder[(int)reference.FrameIndex].Name.String
                        : "(out of range)";
                    if (reference.FrameIndex < objectsInOrder.Count
                        && Formats.Hashing.Fnv64.Hash(landed) == reference.FrameHash) continue;

                    offTarget++;
                    if (firstOffTarget.Length == 0)
                    {
                        firstOffTarget = $"row {reference.FrameIndex} holds '{landed}', " +
                                         $"whose name does not hash to {reference.FrameHash:X16}";
                    }
                }
            }
            Check("every scene reference lands on the object it names", offTarget == 0,
                $"{references} references, {offTarget} off target {firstOffTarget}");

            // A mesh whose flag says it hangs off a second parent, with that slot empty, was suspected of being
            // what a copied object got wrong. It is not an invariant: the shipped districts contain such
            // meshes themselves (distillery's 'Glow'), so the engine tolerates it. Recorded rather than
            // checked, so the next reader does not rediscover the same dead end.
            int flagWithoutSlot = fr.FrameObjects.Values.OfType<FrameObjectSingleMesh>().Count(m =>
                m.SingleMeshFlags.HasFlag(SingleMeshFlags.ParentIndex2_Flag)
                && !m.Refs.ContainsKey(FrameEntryRefTypes.Parent2));
            sb.AppendLine($"meshes flagged as anchored with an empty slot (the game ships these too): {flagWithoutSlot}");

            // The entity-init property rows: a copy shares its original's row, which is only safe if the
            // shipped packs share them too. If every actor owned one, the table would be parallel to the
            // actor list and growing one without the other would hand the engine a mismatch.
            foreach (ActorsFile p in packs)
            {
                var ids = new List<short>();
                foreach (ActorEntry a in p.Actors) ids.Add(a.InitPropId);
                int shared = ids.Where(i => i >= 0).GroupBy(i => i).Count(g => g.Count() > 1);
                sb.AppendLine($"init-props: {ids.Count} actors, {ids.Where(i => i >= 0).Distinct().Count()} distinct rows, " +
                              $"{ids.Count(i => i < 0)} without one, {shared} row(s) shared by several actors");
            }

            // ── Resolution, on the very scene the viewport loads (a second FrameResource would be a
            //    different set of objects, and the placements would not apply to it) ──
            (List<SdsFrameNode> roots, List<MeshData> meshes, ISceneDocument? loaded) =
                SdsMeshLoader.LoadHierarchy(new FileInfo(sds));
            Check("district loads", loaded is SceneDocumentAdapter, $"{roots.Count} roots, {meshes.Count} meshes");
            if (loaded is not SceneDocumentAdapter document) return;

            ActorPlacements placements = document.Placements;
            sb.AppendLine($"actors={typed + raw}, placed={placements.PlacedCount}, " +
                          $"covered frames={placements.CoveredCount}, unresolved={placements.UnresolvedCount}");
            Check("some actors resolve to frame objects", placements.PlacedCount > 0, $"{placements.PlacedCount}");

            var nodes = new List<FrameNodeAdapter>();
            void Walk(SdsFrameNode n)
            {
                if (n.Source is FrameNodeAdapter adapter) nodes.Add(adapter);
                foreach (SdsFrameNode c in n.Children) Walk(c);
            }
            foreach (SdsFrameNode r in roots) Walk(r);

            // ── The prototypes an actor places are parked at the origin; with the placement folded in they
            //    report the actor's own position — which is what the gizmo and the renderer read ──
            int placedAtOrigin = 0, placedElsewhere = 0, matched = 0, mismatched = 0;
            string firstMismatch = "";
            foreach (FrameNodeAdapter node in nodes)
            {
                ActorEntry? actor = placements.ActorOf(node.Frame);
                if (actor == null) continue;

                if (node.Frame.WorldTransform.Translation.LengthSquared() < 1e-8f) placedAtOrigin++;
                else placedElsewhere++;

                if (Approx(node.WorldTransform.Translation, actor.Position, 1e-2f)) matched++;
                else
                {
                    mismatched++;
                    if (firstMismatch == "")
                        firstMismatch = $"{node.Frame.Name} → {node.WorldTransform.Translation} vs actor {actor.Position}";
                }
            }
            Check("placed prototypes sit at the origin in the frame resource", placedElsewhere == 0,
                $"atOrigin={placedAtOrigin}, elsewhere={placedElsewhere}");
            Check("scene nodes report the actor's position", mismatched == 0 && matched > 0,
                $"matched={matched}, off={mismatched} {firstMismatch}");

            // ── Nothing outside the placed subtrees moved ──
            int untouched = 0, moved = 0;
            foreach (FrameNodeAdapter node in nodes)
            {
                if (placements.TryGet(node.Frame, out _)) continue;
                if (Approx(node.WorldTransform.Translation, node.Frame.WorldTransform.Translation)) untouched++;
                else moved++;
            }
            Check("frames no actor places keep their own world transform", moved == 0, $"untouched={untouched}");

            // ── The meshes under a placed prototype travel with it: the render matrix must agree with the node ──
            int placedMeshes = 0, meshOff = 0;
            string firstMeshOff = "";
            foreach (FrameNodeAdapter node in nodes)
            {
                if (node.Frame is not FrameObjectSingleMesh) continue;
                if (!placements.TryGet(node.Frame, out _)) continue;
                SdsFrameNode? treeNode = FindTreeNode(roots, node);
                if (treeNode?.Mesh == null) continue;

                placedMeshes++;
                if (!Approx(treeNode.Mesh.World.Translation, node.WorldTransform.Translation))
                {
                    meshOff++;
                    if (firstMeshOff == "")
                        firstMeshOff = $"{node.Frame.Name}: mesh {treeNode.Mesh.World.Translation} vs node {node.WorldTransform.Translation}";
                }
            }
            Check("placed meshes render at their node's placed transform", meshOff == 0 && placedMeshes > 0,
                $"meshes={placedMeshes}, off={meshOff} {firstMeshOff}");

            // ── A click on placed geometry must resolve to the ACTOR, not to the prototype frame ──
            int governed = 0, ungoverned = 0, wrongOwner = 0;
            string firstWrongOwner = "";
            foreach (FrameNodeAdapter node in nodes)
            {
                ActorEntry? covering = placements.ActorCovering(node.Frame);
                bool placed = placements.TryGet(node.Frame, out _);

                if (placed)
                {
                    if (covering == null)
                    {
                        wrongOwner++;
                        if (firstWrongOwner == "") firstWrongOwner = $"{node.Frame.Name} is placed but has no actor";
                        continue;
                    }
                    // The placement folded into this node must be the covering actor's own transform: the node's
                    // world is the frame's own world followed by that actor's matrix (not a plain offset — the
                    // actor's rotation turns the whole subtree around it).
                    FrameObjectBase? target = placements.TargetOf(covering);
                    System.Numerics.Matrix4x4 expected = node.Frame.WorldTransform * covering.Transform;
                    if (target == null || !Approx(node.WorldTransform.Translation, expected.Translation, 1e-2f))
                    {
                        wrongOwner++;
                        if (firstWrongOwner == "")
                            firstWrongOwner = $"{node.Frame.Name} → {covering.EntityName}: " +
                                              $"{node.WorldTransform.Translation} vs {expected.Translation}";
                        continue;
                    }
                    governed++;
                }
                else if (covering == null) ungoverned++;
                else
                {
                    wrongOwner++;
                    if (firstWrongOwner == "") firstWrongOwner = $"{node.Frame.Name} claims {covering.EntityName} without a placement";
                }
            }
            Check("every placed frame resolves back to its actor", wrongOwner == 0,
                $"governed={governed}, plain={ungoverned}, wrong={wrongOwner} {firstWrongOwner}");

            // ── The actors nothing draws: census, glyphs, property panel ──
            sb.AppendLine($"invisible actors: {placements.Invisible.Count} of {placements.All.Count}");
            foreach (var g in placements.All.GroupBy(a => ActorCategories.Of(a.Type)).OrderByDescending(g => g.Count()))
            {
                int hidden = g.Count(placements.Invisible.Contains);
                sb.AppendLine($"    {ActorCategories.Label(g.Key),-18} {g.Count(),5}   without geometry: {hidden}");
            }

            ActorMarkerRenderData markers = ActorMarkerBuilder.Build(placements.Invisible);
            Check("a glyph is built for every actor with nothing to draw",
                markers.MarkerCount == placements.Invisible.Count,
                $"{markers.MarkerCount}/{placements.Invisible.Count}");
            Check("glyph geometry is well formed",
                markers.Positions.Length == markers.Colors.Length
                && markers.Positions.Length == markers.MarkerCount * 24
                && markers.Positions.Length % 2 == 0,
                $"{markers.Positions.Length} vertices, {markers.Colors.Length} colours");

            // A glyph must stand where its actor does: the octahedron's poles average back to the centre.
            bool centred = true;
            string firstOff = "";
            for (int i = 0; i < placements.Invisible.Count && centred; i++)
            {
                var sum = System.Numerics.Vector3.Zero;
                for (int v = i * 24; v < (i + 1) * 24; v++) sum += markers.Positions[v];
                System.Numerics.Vector3 centre = sum / 24f;
                if (!Approx(centre, placements.Invisible[i].Position, 1e-2f))
                {
                    centred = false;
                    firstOff = $"{placements.Invisible[i].EntityName}: {centre} vs {placements.Invisible[i].Position}";
                }
            }
            Check("each glyph is centred on its actor", centred, firstOff);

            // ── Ray-picking the glyphs: aim at each one from a few metres away and expect that one back ──
            if (placements.Invisible.Count > 0)
            {
                var points = new List<System.Numerics.Vector3>(placements.Invisible.Count);
                foreach (ActorEntry a in placements.Invisible) points.Add(a.Position);

                // The contract is "nearest glyph along the ray", not "the one aimed at": in a dense interior
                // another marker can genuinely stand between the camera and the target, and picking that one is
                // correct. So each pick is checked against a naive nearest-hit reference.
                int exact = 0, occluded = 0, wrong = 0, misses = 0;
                string firstBad = "";
                var dir = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.6f, 0.5f, -0.62f));
                for (int i = 0; i < points.Count; i++)
                {
                    System.Numerics.Vector3 origin = points[i] - dir * 8f;
                    int hit = ActorPicking.Pick(points, origin, dir, ActorMarkerBuilder.Radius, out float t);

                    if (hit < 0)
                    {
                        misses++;
                        if (firstBad == "") firstBad = $"missed {placements.Invisible[i].EntityName}";
                        continue;
                    }
                    if (hit == i) { exact++; continue; }

                    // A different marker is only acceptable when it really is on the ray and in front.
                    System.Numerics.Vector3 toHit = points[hit] - origin;
                    float along = System.Numerics.Vector3.Dot(toHit, dir);
                    float perp = MathF.Sqrt(MathF.Max(0f, toHit.LengthSquared() - along * along));
                    float allowance = MathF.Max(ActorMarkerBuilder.Radius, along * 0.011f);
                    if (perp <= allowance && t <= 8f + 1e-3f) occluded++;
                    else
                    {
                        wrong++;
                        if (firstBad == "")
                            firstBad = $"{placements.Invisible[i].EntityName} → {placements.Invisible[hit].EntityName} " +
                                       $"(t={t:F2}, off-ray by {perp:F2})";
                    }
                }
                Check("a click on a glyph picks the nearest glyph on the ray", misses == 0 && wrong == 0,
                    $"exact={exact}, in-front={occluded}, wrong={wrong}, missed={misses} {firstBad}");

                // Aiming away from everything must report a miss, not the nearest marker.
                int stray = ActorPicking.Pick(points, points[0] + new System.Numerics.Vector3(0, 0, 5000f),
                    System.Numerics.Vector3.UnitZ, ActorMarkerBuilder.Radius, out _);
                Check("aiming at nothing picks nothing", stray < 0);
            }

            // The property panel reads through the same adapter the tree hands the UI.
            if (placements.All.Count > 0)
            {
                ActorEntry sample = placements.All[0];
                ActorNodeAdapter adapter = document.ActorNode(sample);
                Check("the actor adapter is canonical", ReferenceEquals(adapter, document.ActorNode(sample)));

                IReadOnlyList<PropertyGroup> groups = adapter.GetPropertyGroups();
                int fields = groups.Sum(g => g.Properties.Count);
                Check("the property panel has the actor's fields", groups.Count >= 2 && fields >= 12,
                    $"{groups.Count} groups, {fields} fields");

                // The transform is not a catalog field — the adapter is an IFrameNode, so the Object tab and the
                // gizmo read it straight off the actor.
                Check("the actor reports its spawn transform as a frame node",
                    Approx(adapter.LocalTransform.Translation, sample.Position)
                    && adapter.ParentWorldTransform.IsIdentity
                    && Approx(adapter.WorldTransform.Translation, sample.Position),
                    $"{adapter.Name} @ {sample.Position}");
                Check("the actor's identity fields stay read-only",
                    groups.SelectMany(g => g.Properties).All(p => p.IsReadOnly && p.Set == null));
            }

            // ── Moving an actor: the subtree follows, the pack survives a round trip, nothing else shifts ──
            ActorEntry? movable = null;
            foreach (ActorEntry a in placements.All)
            {
                if (placements.TargetOf(a) != null && placements.PackOf(a) is { } p && p.Actors.Count > 1)
                {
                    movable = a;
                    break;
                }
            }
            if (movable != null && placements.PackOf(movable) is { } pack2)
            {
                FrameObjectBase target = placements.TargetOf(movable)!;
                ActorNodeAdapter mover = document.ActorNode(movable);
                System.Numerics.Vector3 before = movable.Position;
                var offset = new System.Numerics.Vector3(3f, -4f, 5f);

                // Snapshot a neighbour's bytes: moving one actor must not disturb any other.
                ActorEntry neighbour = pack2.Actors.First(a => !ReferenceEquals(a, movable));
                System.Numerics.Vector3 neighbourBefore = neighbour.Position;
                byte[] packBefore = pack2.ToBytes();

                mover.LocalTransform = mover.LocalTransform * System.Numerics.Matrix4x4.CreateTranslation(offset);

                Check("moving an actor moves its own position",
                    Approx(movable.Position, before + offset, 1e-2f), $"{before} → {movable.Position}");
                Check("the placed subtree follows the actor",
                    Approx(document.Node(target).WorldTransform.Translation, movable.Position, 1e-2f),
                    $"{target.Name} at {document.Node(target).WorldTransform.Translation}");
                Check("no other actor moved", Approx(neighbour.Position, neighbourBefore));

                // Write the edited pack out and read it back: the move must survive, and the file must stay the
                // same size — the transform is fixed-width, so no offset in the pack can have shifted.
                string temp = Path.Combine(Path.GetTempPath(), "illusion_actor_move.act");
                SdsActorsSaver.Save(pack2, temp);
                byte[] packAfter = File.ReadAllBytes(temp);
                Check("an edited pack keeps its size", packAfter.Length == packBefore.Length,
                    $"{packBefore.Length} → {packAfter.Length}");

                ActorsFile reread = ActorsFile.Load(temp);
                ActorEntry roundTripped = reread.Actors[movable.Index];
                Check("the move survives a save/load round trip",
                    Approx(roundTripped.Position, movable.Position, 1e-3f)
                    && QApprox(roundTripped.Rotation, movable.Rotation)
                    && Approx(roundTripped.Scale, movable.Scale, 1e-3f),
                    $"{roundTripped.EntityName} @ {roundTripped.Position}");

                // Everything except this actor's own item bytes must be identical to the original pack.
                int diffs = 0;
                for (int i = 0; i < packBefore.Length && i < packAfter.Length; i++) if (packBefore[i] != packAfter[i]) diffs++;
                Check("only the moved actor's bytes changed", diffs > 0 && diffs <= 40, $"{diffs} bytes differ");

                // Put it back, so the probe leaves the working copy as it found it.
                mover.LocalTransform = mover.LocalTransform * System.Numerics.Matrix4x4.CreateTranslation(-offset);
                Check("restoring the actor restores the pack byte for byte",
                    pack2.ToBytes().AsSpan().SequenceEqual(packBefore));
                File.Delete(temp);
            }
            else
            {
                sb.AppendLine("(no movable actor with a frame in this district — move checks skipped)");
            }

            // ── An actor edit must reach persistence: the tree enlists an edit by walking UP to the nearest
            //    ISceneDocument, and the actors hang beside the FrameResource branch, not under it. Without a
            //    document on the Actors node, moving an actor marked nothing and a build had nothing to pack.
            var actorsDoc = new ActorDocumentAdapter(placements, new FileInfo(sds), document);
            var actorsNode = new SceneNode("Actors", "Actors", true) { Source = actorsDoc };
            var actorLeaf = new SceneNode("leaf", "Actor", false) { Source = document.ActorNode(placements.All[0]) };
            actorsNode.AddChild(actorLeaf);

            Check("an edited actor finds the document that saves it",
                ReferenceEquals(actorLeaf.OwningDocumentNode(), actorsNode));

            // A row the editor CREATES has to arrive with its parent set, or it can never reach that document:
            // an actor row added straight to the children list could not be copied again, or deleted, because
            // the walk upwards ended at the row itself.
            var insertedLeaf = new SceneNode("copy", "Actor", false)
            {
                Source = document.ActorNode(placements.All[^1]),
            };
            actorsNode.InsertChild(0, insertedLeaf);
            Check("a row the editor inserts finds it too",
                ReferenceEquals(insertedLeaf.OwningDocumentNode(), actorsNode),
                insertedLeaf.Parent == null ? "the inserted row has no parent" : "");
            Check("that document points at this district's archive",
                string.Equals(actorsDoc.SourceArchive.FullName, sds, StringComparison.OrdinalIgnoreCase),
                actorsDoc.SourceArchive.Name);
            Check("the packs it would write are on disk",
                placements.Packs.Count > 0 && placements.Packs.All(p => File.Exists(p.Path)),
                string.Join(", ", placements.Packs.Select(p => Path.GetFileName(p.Path))));

            // ── Deleting an actor: the record leaves the pack, the file re-reads with one actor fewer, and
            //    undo restores it byte for byte. This is what proves the recomputed offset table: the removal
            //    shifts every item after it, so a stale table would corrupt the file immediately.
            if (placements.Packs.Count > 0)
            {
                (ActorsFile pack3, _) = placements.Packs[0];
                if (pack3.Actors.Count >= 2)
                {
                    byte[] original = pack3.ToBytes();
                    int countBefore = pack3.Actors.Count;
                    int refsBefore = pack3.SceneReferences.Count;

                    // Prefer an actor that actually owns a scene reference — that is the case where removal has
                    // to clean up more than the item — but never the last row, so the offset shift is exercised.
                    int victimRow = countBefore / 2;
                    for (int i = 0; i < countBefore - 1; i++)
                    {
                        if (pack3.SceneReferences.Any(r => r.FrameHash == pack3.Actors[i].FrameHash)) { victimRow = i; break; }
                    }
                    ActorEntry victim = pack3.Actors[victimRow];
                    string victimName = victim.EntityName;
                    ulong victimHash = victim.FrameHash;
                    bool hadReference = pack3.SceneReferences.Any(r => r.FrameHash == victimHash);
                    ActorEntry after = pack3.Actors[victimRow + 1];

                    ActorRemoval removal = pack3.Remove(victim);
                    byte[] shortened = pack3.ToBytes();

                    Check("removing an actor drops exactly one row",
                        pack3.Actors.Count == countBefore - 1, $"{countBefore} → {pack3.Actors.Count}");
                    Check("the pack shrinks", shortened.Length < original.Length,
                        $"{original.Length} → {shortened.Length} bytes");
                    Check("its scene reference goes with it",
                        pack3.SceneReferences.Count == refsBefore - (hadReference ? 1 : 0),
                        $"{refsBefore} → {pack3.SceneReferences.Count} (had one: {hadReference})");
                    Check("the rows after it renumber",
                        after.Index == victimRow, $"{after.EntityName} is row {after.Index}");

                    // The shortened pack must be readable, and read back one actor fewer with the rest intact.
                    using var shortStream = new MemoryStream(shortened, writable: false);
                    ActorsFile reloaded = ActorsFile.Read(shortStream);
                    Check("the shortened pack re-reads",
                        reloaded.Actors.Count == countBefore - 1
                        && reloaded.Actors.All(a => a.IsTyped)
                        && reloaded.Actors.All(a => a.EntityName != victimName),
                        $"{reloaded.Actors.Count} actors, all typed");
                    Check("the shortened pack is a fixpoint",
                        reloaded.ToBytes().AsSpan().SequenceEqual(shortened));

                    pack3.Restore(removal);
                    Check("undo restores the pack byte for byte",
                        pack3.ToBytes().AsSpan().SequenceEqual(original), $"{pack3.Actors.Count} actors");

                    // Deleting a multi-actor selection is one edit, and its undo has to put the rows back in
                    // the OPPOSITE order: each removal recorded the index the list had at that moment, so
                    // restoring them in the order they were taken lands every row after the first one slot
                    // short. This is the contract ActorEditController's delete and duplicate edits follow.
                    if (pack3.Actors.Count >= 4)
                    {
                        ActorEntry firstVictim = pack3.Actors[1];
                        ActorEntry secondVictim = pack3.Actors[3];
                        ActorRemoval firstRemoval = pack3.Remove(firstVictim);
                        ActorRemoval secondRemoval = pack3.Remove(secondVictim);
                        Check("deleting two actors drops exactly two rows",
                            pack3.Actors.Count == countBefore - 2, $"{countBefore} → {pack3.Actors.Count}");

                        pack3.Restore(secondRemoval);
                        pack3.Restore(firstRemoval);
                        Check("undoing a two-actor delete restores the pack byte for byte",
                            pack3.ToBytes().AsSpan().SequenceEqual(original),
                            $"rows {firstVictim.Index} and {secondVictim.Index}");
                    }

                    // ── Duplicating: a glyph-only actor copies; one that places a scene object is refused,
                    //    because that copy would need its own clone of the object.
                    ActorEntry? loose = pack3.Actors.FirstOrDefault(a =>
                        a.IsTyped && !pack3.SceneReferences.Any(r => r.FrameHash == a.FrameHash));
                    ActorEntry? placing = pack3.Actors.FirstOrDefault(a =>
                        a.IsTyped && pack3.SceneReferences.Any(r => r.FrameHash == a.FrameHash));

                    if (loose != null)
                    {
                        int before = pack3.Actors.Count;
                        ActorEntry? copy = pack3.Duplicate(loose, out string? why);
                        Check("an actor with no scene object copies", copy != null, why ?? "");
                        if (copy != null)
                        {
                            Check("the copy lands right after the original and renumbers the rest",
                                copy.Index == loose.Index + 1 && pack3.Actors.Count == before + 1
                                && pack3.Actors[copy.Index].EntityName == copy.EntityName,
                                $"row {copy.Index} of {pack3.Actors.Count}");
                            Check("the copy gets a fresh name and a matching hash",
                                copy.EntityName != loose.EntityName
                                && copy.EntityHash == Illusion.Formats.Hashing.Fnv64.Hash(copy.EntityName),
                                copy.EntityName);
                            Check("the copy stands where the original does",
                                Approx(copy.Position, loose.Position) && QApprox(copy.Rotation, loose.Rotation));

                            byte[] grown = pack3.ToBytes();
                            using var grownStream = new MemoryStream(grown, writable: false);
                            ActorsFile regrown = ActorsFile.Read(grownStream);
                            Check("the grown pack re-reads and is a fixpoint",
                                regrown.Actors.Count == before + 1
                                && regrown.Actors.All(a => a.IsTyped)
                                && regrown.ToBytes().AsSpan().SequenceEqual(grown),
                                $"{regrown.Actors.Count} actors, {grown.Length} bytes");

                            pack3.RemoveCopy(copy);
                            Check("undoing the copy restores the pack byte for byte",
                                pack3.ToBytes().AsSpan().SequenceEqual(original));
                        }
                    }

                    if (placing != null)
                    {
                        ActorEntry? refused = pack3.Duplicate(placing, out string? why);
                        Check("an actor that places a scene object is refused without a clone of it",
                            refused == null && !string.IsNullOrEmpty(why), why ?? "(no reason given)");
                    }
                }
            }

            CheckPlacingDuplicate(document, placements, sb, Check);
            CheckPinnedOrientations(district, placements, nodes, sb, Check);
            CheckGlyphSet(district, document, placements, sb, Check);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            sb.Insert(0, $"ACTOR PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

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

    /// <summary>
    /// Which way an actor really turns the object it places — measured, not assumed.
    ///
    /// The oracle is the district's collision file. An actor's prototype often carries a FrameObjectCollision
    /// child, and the .col ships that same hull with its own absolute world placement. The game collides with
    /// the hull, so the hull's placement IS where and how the object stands in the game; if our actor placement
    /// disagreed, you would walk through the visible object and bump into thin air. That makes it independent
    /// evidence about the .act convention — unlike comparing raw quaternions between the two formats, which
    /// only ever showed that they are two formats.
    ///
    /// The comparison is over full world matrices: the collision child's own transform inside the prototype,
    /// followed by the actor's placement. A wrong rotation then shows up twice — the hull faces the wrong way,
    /// and (whenever the child sits off the prototype's origin) it also stands in the wrong place, which no
    /// symmetry can hide.
    /// Output: %TEMP%\illusion_actor_orient.txt
    /// </summary>
    internal static void RunActorOrientationProbe(string district, string? nameFilter)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_actor_orient.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"ACTOR ORIENTATION ORACLE — district={district}" +
                          (nameFilter == null ? "" : $", filter='{nameFilter}'"));
            if (colPath == null) { sb.AppendLine("district ships no .col — nothing to measure against"); return; }

            Formats.Collisions.CollisionFile collision = Formats.Collisions.CollisionFile.Load(colPath);
            var byHash = new Dictionary<ulong, List<Formats.Collisions.CollisionInstance>>();
            foreach (Formats.Collisions.CollisionInstance inst in collision.Instances)
            {
                if (!byHash.TryGetValue(inst.Hash, out List<Formats.Collisions.CollisionInstance>? list))
                {
                    byHash[inst.Hash] = list = new List<Formats.Collisions.CollisionInstance>();
                }
                list.Add(inst);
            }

            (_, _, ISceneDocument? loaded) = SdsMeshLoader.LoadHierarchy(new FileInfo(sds));
            if (loaded is not SceneDocumentAdapter document) { sb.AppendLine("district did not load"); return; }
            ActorPlacements placements = document.Placements;
            sb.AppendLine($".col: {collision.Instances.Count} instances, {byHash.Count} hulls; " +
                          $"actors: {placements.All.Count}, placed: {placements.PlacedCount}");

            int paired = 0, asIs = 0, inverted = 0, either = 0, neither = 0;
            int withTarget = 0, withHullChild = 0, hullInCol = 0;
            var samples = new List<string>();
            foreach (ActorEntry actor in placements.All)
            {
                if (nameFilter != null && !actor.EntityName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (placements.TargetOf(actor) is not { } target) continue;
                withTarget++;

                var hulls = new List<FrameObjectCollision>();
                CollectCollisions(target, hulls, new HashSet<FrameObjectBase>());
                if (hulls.Count > 0) withHullChild++;
                foreach (FrameObjectCollision hull in hulls)
                {
                    if (byHash.ContainsKey(hull.Hash)) hullInCol++;
                    if (!byHash.TryGetValue(hull.Hash, out List<Formats.Collisions.CollisionInstance>? instances)) continue;

                    System.Numerics.Matrix4x4 asStored = hull.WorldTransform * actor.Transform;
                    System.Numerics.Matrix4x4 asFlipped = hull.WorldTransform *
                        TransformMath.Compose(System.Numerics.Quaternion.Conjugate(actor.Rotation),
                            actor.Scale, actor.Position);

                    // The nearest hull copy — several identical hulls can share one hash across the district.
                    Formats.Collisions.CollisionInstance? best = null;
                    float bestD = float.MaxValue;
                    foreach (Formats.Collisions.CollisionInstance inst in instances)
                    {
                        float d = (inst.Position - actor.Position).Length();
                        if (d < bestD) { bestD = d; best = inst; }
                    }
                    if (best == null || bestD > 3f) continue;

                    paired++;
                    System.Numerics.Matrix4x4 truth = TransformMath.Compose(
                        TransformMath.CollisionEulerToQuaternion(best.Rotation),
                        System.Numerics.Vector3.One, best.Position);

                    float errStored = PoseError(asStored, truth);
                    float errFlipped = PoseError(asFlipped, truth);
                    bool storedFits = errStored < 0.05f;
                    bool flippedFits = errFlipped < 0.05f;

                    if (storedFits && flippedFits) either++;        // a half turn or no turn at all — says nothing
                    else if (storedFits) asIs++;
                    else if (flippedFits) inverted++;
                    else neither++;

                    if (samples.Count < 12 && !(storedFits && flippedFits))
                    {
                        samples.Add($"    {actor.EntityName} → {hull.Name}: as stored {errStored:F3}, " +
                                    $"inverted {errFlipped:F3} → {(storedFits ? "AS STORED" : flippedFits ? "INVERTED" : "neither")}");
                    }
                }
            }

            sb.AppendLine($"actors placing a frame: {withTarget}; of those, carrying a collision child: " +
                          $"{withHullChild}; those children found in the .col: {hullInCol}");

            // Which actors place an object that carries collision, by type — the shape a copy has not been
            // tried on in the game yet, and the shape most of the world's props have.
            var byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (ActorEntry actor in placements.All)
            {
                if (placements.TargetOf(actor) is not { } target) continue;
                var hulls = new List<FrameObjectCollision>();
                CollectCollisions(target, hulls, new HashSet<FrameObjectBase>());
                if (hulls.Count == 0) continue;

                string type = actor.TypeName.Length > 0 ? actor.TypeName : actor.Type.ToString();
                if (!byType.TryGetValue(type, out List<string>? names)) byType[type] = names = new List<string>();
                if (names.Count < 4) names.Add($"{actor.EntityName} ({hulls.Count} hull(s))");
            }
            foreach (KeyValuePair<string, List<string>> pair in byType.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"    {pair.Key,-20} {string.Join(", ", pair.Value)}");
            }
            sb.AppendLine($"paired with a hull: {paired}");
            sb.AppendLine($"    the actor's quaternion as stored fits the game's hull : {asIs}");
            sb.AppendLine($"    only its INVERSE fits                                 : {inverted}");
            sb.AppendLine($"    both fit (half turn / no turn — no evidence either way): {either}");
            sb.AppendLine($"    neither fits (hull is not this object's, or scaled)    : {neither}");
            if (samples.Count > 0)
            {
                sb.AppendLine("samples (error in metres, over the hull's own corners):");
                foreach (string s in samples) sb.AppendLine(s);
            }

            string verdict = asIs > 0 && inverted == 0 ? "the pack stores the orientation the game uses"
                : inverted > 0 && asIs == 0 ? "the pack stores the INVERSE of the game's orientation"
                : asIs == 0 && inverted == 0 ? "no decisive pair — nothing measured"
                : "MIXED — the pairs disagree with each other, so neither reading is safe";
            sb.AppendLine($"VERDICT: {verdict}");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // How far the two poses put the same body, in metres: the worst corner of a 1 m box carried by each.
    private static float PoseError(System.Numerics.Matrix4x4 a, System.Numerics.Matrix4x4 b)
    {
        float worst = 0f;
        for (int i = 0; i < 8; i++)
        {
            var corner = new System.Numerics.Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f,
                (i & 4) == 0 ? -1f : 1f);
            float d = (TransformMath.TransformCoordinate(corner, a) - TransformMath.TransformCoordinate(corner, b))
                .Length();
            if (d > worst) worst = d;
        }
        return worst;
    }

    private static void CollectCollisions(FrameObjectBase frame, List<FrameObjectCollision> into,
        HashSet<FrameObjectBase> seen)
    {
        if (!seen.Add(frame)) return;
        if (frame is FrameObjectCollision hull) into.Add(hull);
        foreach (FrameObjectBase child in frame.Children) CollectCollisions(child, into, seen);
    }

    /// <summary>
    /// Vanilla orientations, pinned.
    ///
    /// A rotation convention flip is invisible to every other check in this file: no translation moves, the
    /// pack still re-saves byte for byte (an inversion applied on both read and write is byte-neutral), and the
    /// round-trip check compares the flipped value against itself. It shows up only as objects standing turned
    /// the wrong way in the GAME — and the convention flipped twice in one day before anyone looked there.
    /// These lines are that comparison, frozen: they are the state in which an untouched gate in uppertown
    /// faces the same way in the viewport as it does in the game.
    ///
    /// Regenerate deliberately — never to turn a red check green. The probe prints "PIN" lines for a district
    /// it has none for; those belong here only once the viewport has been compared against the game again.
    /// </summary>
    private static readonly Dictionary<string, string[]> PinnedOrientations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["distillery"] =
        [
            "DE_lahev33|-0.0000,-0.0000,-0.5792,0.8152|-1560.28,-113.48,2.63",
            "2D_box38|-0.0000,-0.0000,0.1147,0.9934|-1560.29,-114.47,4.98",
            "2D_zidle14|-0.0000,-0.0000,0.5927,0.8054|-1563.07,-107.86,-9.65",
            "2D_zidle04|-0.0000,-0.0000,-0.8115,0.5844|-1566.38,-90.56,-14.58",
            "DE_lahev44|-0.0000,-0.0000,0.0860,0.9963|-1560.46,-114.61,4.26",
            "2D_ELECTR_38|-0.0000,-0.0000,-0.7071,0.7071|-1561.03,-100.79,-12.84",
            "X2D_box71|-0.0000,-0.0000,0.7071,0.7071|-1558.59,-116.30,-13.66",
            "X2D_box69|-0.0000,-0.0000,-0.0872,0.9962|-1553.70,-115.55,-13.66",
            "DE_bedna01>box_dI|-1561.82,-113.89,-0.17",
            "DE_bedna10>box_dI|-1557.92,-120.68,-8.38",
            "DE_bedna12>box_dI|-1561.91,-118.87,-11.58",
            "DE_bedna16>box_dI|-1568.29,-108.67,-4.28",
        ],
        ["eastside"] =
        [
            "AmbiRV_city10_train_whistle100|-0.0000,-0.0000,0.7076,0.7066|150.85,145.35,-8.88",
            "AmbiRV_city10_car_horn05|-0.0000,-0.0000,0.6921,0.7218|-474.03,188.99,42.00",
            "wanted57|-0.0000,-0.0000,0.4617,0.8870|-18.90,82.09,-8.89",
            "wanted58|-0.0000,-0.0000,0.7065,0.7077|-51.21,420.02,-9.37",
            "wanted61|0.0267,-0.0267,0.7066,0.7066|91.35,262.83,-16.62",
            "wanted62|-0.0204,0.0204,0.7068,0.7068|-11.10,148.30,-9.71",
            "wanted63|-0.0000,-0.0000,-0.7066,0.7077|-116.18,410.67,-9.24",
            "wanted64|0.0158,0.0158,-0.7069,0.7069|-339.44,253.09,0.16",
        ],
        ["port"] =
        [
            "jachta01|-0.0000,-0.0000,-0.1634,0.9866|-613.27,-856.79,-24.15",
            "jachta02|-0.0000,-0.0000,0.5884,0.8086|-470.61,-982.85,-24.15",
            "jachta05|-0.0000,-0.0000,0.7136,0.7006|-519.73,-821.65,-24.15",
            "jachta06|-0.0000,-0.0000,0.9872,0.1595|-634.01,-833.82,-24.15",
            "jachta04|-0.0000,-0.0000,-0.6903,0.7235|-487.03,-787.63,-24.15",
            "jachta09|-0.0000,-0.0000,-0.1634,0.9866|-655.35,-842.23,-24.15",
            "boatXXL01|-0.0000,-0.0000,0.7012,0.7130|-469.47,-788.49,-24.50",
            "jachta10|-0.0000,-0.0000,-0.7215,0.6924|-465.47,-918.70,-24.15",
            "jachta00>teziste|-516.80,-921.68,-26.67",
            "jachta01>teziste|-614.90,-858.42,-26.67",
            "jachta04>teziste|-489.15,-786.71,-26.67",
            "jachta05>teziste|-517.64,-822.63,-26.67",
        ],
        ["prisone"] =
        [
            "CDi_light__02|-0.7071,0.0000,-0.0000,0.7071|63.12,22.21,292.06",
            "CDi_light__01|-0.7071,0.0000,-0.0000,0.7071|63.12,32.65,292.04",
            "bedna2|-0.0000,-0.0000,0.7071,0.7071|-16.70,-40.21,303.02",
            "basketBall|-0.0000,-0.0000,-0.6635,0.7481|23.67,-2.98,303.11",
            "bedna1|-0.0000,-0.0000,0.7071,0.7071|-16.66,-42.01,303.02",
            "playBallPickUpPos|-0.0000,-0.0000,0.7476,0.6642|3.47,-1.22,303.00",
            "playBallThrowPos|-0.0000,-0.0000,0.7388,0.6739|6.78,-1.27,303.00",
            "playerBallPickUpPos|-0.0000,-0.0000,-0.6635,0.7481|23.13,-3.01,303.00",
            "bush09>C_bush03_Collision|15.47,-95.21,301.68",
            "celtis08>celtis01 trunk|41.60,-55.61,306.48",
            "celtis06>celtis01 trunk|32.14,-181.27,301.37",
            "celtis03>celtis01 trunk|25.48,-103.96,307.41",
        ],
    };

    // A rotated actor as a comparable line: the quaternion it stores, and where its turn puts a point of the
    // prototype it places. The point is what makes this catch a composition-order or scale error too — an
    // inverted, differently-ordered or unscaled transform lands it somewhere else.
    private static string PinOf(ActorEntry actor)
    {
        System.Numerics.Vector3 probe =
            TransformMath.TransformCoordinate(new System.Numerics.Vector3(1f, 2f, 3f), actor.Transform);
        return $"{actor.EntityName}|{actor.Rotation.X:F4},{actor.Rotation.Y:F4},{actor.Rotation.Z:F4}," +
               $"{actor.Rotation.W:F4}|{probe.X:F2},{probe.Y:F2},{probe.Z:F2}";
    }

    // A turn a convention flip would actually MOVE something with. Half turns are excluded on purpose: the
    // conjugate of a 180° rotation is the same rotation negated, which is the same orientation — pinning those
    // would compare a number that changes against geometry that does not, and prove nothing about the viewport.
    private static bool IsSensitiveTurn(ActorEntry actor) =>
        actor.IsTyped && MathF.Abs(actor.Rotation.W) is > 0.05f and < 0.999f;

    private static List<ActorEntry> RotatedActors(ActorPlacements placements, int count)
    {
        var picked = new List<ActorEntry>(count);
        foreach (ActorEntry actor in placements.All)
        {
            if (!IsSensitiveTurn(actor)) continue;
            picked.Add(actor);
            if (picked.Count == count) break;
        }
        return picked;
    }

    private static void CheckPinnedOrientations(string district, ActorPlacements placements,
        List<FrameNodeAdapter> nodes, StringBuilder sb, Action<string, bool, string> check)
    {
        List<ActorEntry> rotated = RotatedActors(placements, 8);
        var lines = new List<string>(rotated.Count + 4);
        foreach (ActorEntry actor in rotated) lines.Add(PinOf(actor));

        // Plus a few real placed children: their world transform runs through the scene adapter, so these pin
        // the whole path the renderer reads, not just the arithmetic on the actor's own record.
        var pinnedActors = new HashSet<string>(StringComparer.Ordinal);
        foreach (FrameNodeAdapter node in nodes)
        {
            if (pinnedActors.Count == 4) break;
            if (node.Frame.WorldTransform.Translation.LengthSquared() < 1e-4f) continue;
            if (placements.ActorCovering(node.Frame) is not { } covering) continue;
            if (!IsSensitiveTurn(covering) || !pinnedActors.Add(covering.EntityName)) continue;

            System.Numerics.Vector3 w = node.WorldTransform.Translation;
            lines.Add($"{covering.EntityName}>{node.Frame.Name}|{w.X:F2},{w.Y:F2},{w.Z:F2}");
        }

        if (!PinnedOrientations.TryGetValue(district, out string[]? expected))
        {
            sb.AppendLine($"(no pinned orientations for '{district}' — add these to PinnedOrientations)");
            foreach (string line in lines) sb.AppendLine($"    PIN  \"{line}\",");
            return;
        }

        int matched = 0;
        string firstOff = "";
        for (int i = 0; i < expected.Length; i++)
        {
            if (i < lines.Count && string.Equals(lines[i], expected[i], StringComparison.Ordinal)) { matched++; continue; }
            if (firstOff.Length == 0)
            {
                firstOff = $"expected \"{expected[i]}\", got \"{(i < lines.Count ? lines[i] : "(nothing)")}\"";
            }
        }
        check("vanilla actors are turned the way they were pinned", matched == expected.Length && lines.Count == expected.Length,
            $"{matched}/{expected.Length} {firstOff}");
    }

    // The glyphs and the click targets of a district, which used to be snapshots taken once at load: a deleted
    // actor stayed clickable (and an actor pick beats the geometry behind it, so it swallowed clicks meant for
    // something else), a copy was never drawn, and a moved actor left its marker behind.
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

    // The tree node that wraps a given frame adapter (the mesh lives on the tree node, not on the adapter).
    private static SdsFrameNode? FindTreeNode(List<SdsFrameNode> roots, FrameNodeAdapter adapter)
    {
        foreach (SdsFrameNode root in roots)
        {
            SdsFrameNode? found = FindTreeNode(root, adapter);
            if (found != null) return found;
        }
        return null;
    }

    private static SdsFrameNode? FindTreeNode(SdsFrameNode node, FrameNodeAdapter adapter)
    {
        if (ReferenceEquals(node.Source, adapter)) return node;
        foreach (SdsFrameNode child in node.Children)
        {
            SdsFrameNode? found = FindTreeNode(child, adapter);
            if (found != null) return found;
        }
        return null;
    }
}
