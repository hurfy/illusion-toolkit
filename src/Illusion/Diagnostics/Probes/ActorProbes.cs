using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Rendering.Scene;
using Illusion.Scene;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the actor layer: a scene's .act pack, and the placement it gives the frame objects
/// it spawns.</summary>
internal static partial class ActorProbes
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
                // What the panel may write: the flags word, the row the actor points at, that row's behavior
                // fields — and, since the structural writer landed, the entity name (the pack rebuilds its
                // offset tables, so a name may change length). What stays read-only is what the panel has no
                // safe story for yet: the type, the definition and sector strings, and the frame link, whose
                // change has to move the scene reference AND re-resolve the placements.
                string[] writable = [.. groups.SelectMany(g => g.Properties).Where(p => p.Set != null).Select(p => p.Id)];
                string[] frozen = ["Actor.Type", "Actor.TypeId", "Actor.Definition",
                    "Actor.Sector", "Actor.Name1", "Actor.Frame", "Actor.FrameHash", "Actor.Index"];
                Check("the fields with no safe edit story stay read-only",
                    frozen.All(id => !writable.Contains(id)),
                    $"{writable.Length} writable: {string.Join(", ", writable.Take(6))}");
                Check("the entity name is editable", writable.Contains("Actor.Entity"));
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
