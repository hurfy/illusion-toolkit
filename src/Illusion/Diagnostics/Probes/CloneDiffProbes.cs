using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Field-by-field comparison of an editor-made copy against the object it was copied from, read back from the
/// district's working copy on disk.
///
/// This exists because a copy the editor is happy with can still be one the game refuses to load, and the only
/// specimen of that is the working copy itself. Reading it back and diffing it against its original turns
/// "the game crashed" into a list of the fields where the copy is not like anything the game ships.
/// Output: %TEMP%\illusion_clonediff.txt
/// </summary>
internal static class CloneDiffProbes
{
    internal static void RunCloneDiffProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_clonediff.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            ExtractedSds scene = SdsMeshLoader.OpenScene(extracted);
            FrameResource? fr = scene.FrameResource;
            if (fr?.FrameObjects == null) { sb.AppendLine("district carries no frame objects"); return; }
            Assets.Actors.ActorPlacements placements = Assets.Actors.ActorPlacements.Load(scene.Manifest, fr);

            var byName = new Dictionary<string, FrameObjectBase>(StringComparer.Ordinal);
            var order = new List<FrameObjectBase>();
            foreach (object value in fr.FrameObjects.Values)
            {
                if (value is not FrameObjectBase frame) continue;
                order.Add(frame);
                byName[frame.Name.String] = frame;
            }

            sb.AppendLine($"CLONE DIFF — district={district}, {order.Count} frame objects");

            // Whether frame names are unique in this archive. It decides whether a copy may keep its children's
            // names: animations are bound to an object's inner frames BY NAME (this district ships
            // 19_port_plosina-download.an2), so renaming them leaves the copy unanimated — but only if the
            // shipped data insists on unique names is renaming necessary in the first place.
            var repeats = order.Where(f => !f.Name.String.Contains("_copy", StringComparison.Ordinal))
                .GroupBy(f => f.Name.String, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .ToList();
            sb.AppendLine($"frame names repeated in the shipped data: {repeats.Count} name(s), " +
                          $"worst: {string.Join(", ", repeats.Take(5).Select(g => $"'{g.Key}' ×{g.Count()}"))}");
            var copies = order.Where(f => f.Name.String.Contains("_copy", StringComparison.Ordinal)).ToList();
            sb.AppendLine($"editor-made copies found: {copies.Count}");
            if (copies.Count == 0)
            {
                sb.AppendLine("(nothing to compare — this working copy carries no copies)");
                return;
            }

            foreach (FrameObjectBase copy in copies)
            {
                string baseName = copy.Name.String;
                int cut = baseName.LastIndexOf("_copy", StringComparison.Ordinal);
                baseName = baseName[..cut];
                sb.AppendLine();
                sb.AppendLine($"── '{copy.Name}' (row {order.IndexOf(copy)}) vs '{baseName}'");
                if (!byName.TryGetValue(baseName, out FrameObjectBase? source))
                {
                    sb.AppendLine("    the original is gone — nothing to compare against");
                    continue;
                }
                Compare(sb, fr, order, source, copy);
            }

            CompareActors(sb, extracted, fr, order, placements, copies);
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // The other half of a copied actor: its record in the pack, and whether the edited pack on disk still
    // says the same thing when it is read and written again.
    private static void CompareActors(StringBuilder sb, string extracted, FrameResource fr,
        List<FrameObjectBase> order, Assets.Actors.ActorPlacements placements, List<FrameObjectBase> copiedFrames)
    {
        foreach (string path in Directory.GetFiles(extracted, "*.act", SearchOption.AllDirectories))
        {
            byte[] onDisk = File.ReadAllBytes(path);
            Formats.Actors.ActorsFile pack = Formats.Actors.ActorsFile.Load(path);
            bool fixpoint = pack.ToBytes().AsSpan().SequenceEqual(onDisk);
            sb.AppendLine();
            sb.AppendLine($"── {Path.GetFileName(path)}: {pack.Actors.Count} actors, " +
                          $"{pack.SceneReferences.Count} references, re-writes identically: {fixpoint}");

            // Every copied FRAME, and whether anything actually spawns it. Keyed off the frame rather than off
            // an actor named "_copy": the actor gets renamed the moment a modder gives their new object a real
            // name, and then a search by that convention finds nothing and reports nothing — which reads as
            // "all fine" for exactly the case that is not.
            foreach (FrameObjectBase frame in copiedFrames)
            {
                ulong hash = Formats.Hashing.Fnv64.Hash(frame.Name.String);
                Formats.Actors.ActorEntry? owner = pack.Actors.FirstOrDefault(a => a.FrameHash == hash);
                Formats.Actors.ActorSceneReference? reference =
                    pack.SceneReferences.FirstOrDefault(r => r.FrameHash == hash);
                sb.AppendLine($"   frame '{frame.Name}' (row {order.IndexOf(frame)}): "
                    + $"actor {(owner == null ? "NONE — nothing spawns it" : $"'{owner.EntityName}' (row {owner.Index})")}"
                    + $", reference {(reference == null ? "NONE — the engine cannot resolve the link" : $"→ row {reference.FrameIndex}")}");
                if (reference != null && reference.FrameIndex != order.IndexOf(frame))
                {
                    sb.AppendLine($"    ! the reference points at row {reference.FrameIndex}, but the frame is at "
                        + $"{order.IndexOf(frame)} — the engine would spawn the wrong object");
                }

                // The holder is only the handle. What the eye sees hangs UNDER it, and a holder that matches
                // its original in every field still shows nothing if its children do not.
                int cut = frame.Name.String.LastIndexOf("_copy", StringComparison.Ordinal);
                if (cut < 0) continue;
                string origin = frame.Name.String[..cut];
                FrameObjectBase? source = order.FirstOrDefault(f => f.Name.String == origin);
                if (source == null) continue;

                Formats.Actors.ActorEntry? sourceActor =
                    pack.Actors.FirstOrDefault(a => a.FrameHash == Formats.Hashing.Fnv64.Hash(origin));
                if (sourceActor != null && owner != null)
                {
                    Line(sb, "  actor type", $"{sourceActor.Type} ({sourceActor.TypeId})",
                        $"{owner.Type} ({owner.TypeId})");
                    Line(sb, "  actor flags", sourceActor.Flags.ToString(), owner.Flags.ToString());
                    Line(sb, "  active on load", sourceActor.ActivateOnInit.ToString(), owner.ActivateOnInit.ToString());
                    Line(sb, "  init prop row", sourceActor.InitPropId.ToString(), owner.InitPropId.ToString());
                    Line(sb, "  definition", sourceActor.LinkedDefinition, owner.LinkedDefinition);
                    Line(sb, "  scene sector", sourceActor.SceneSector, owner.SceneSector);
                    Line(sb, "  position", Fmt(sourceActor.Position), Fmt(owner.Position));
                }

                // Both links side by side, field for field. A copy that spawns while the ORIGINAL stops
                // spawning means the two are colliding over something, and the reference record is where the
                // engine reads the link from — its name position included, which is what names the definition.
                Formats.Actors.ActorSceneReference? sourceRef =
                    pack.SceneReferences.FirstOrDefault(r => r.FrameHash == Formats.Hashing.Fnv64.Hash(origin));
                sb.AppendLine($"    reference records (table position | frameHash | unk0 | namePos | name | frameIndex):");
                sb.AppendLine($"      origin  {Describe(pack, sourceRef)}");
                sb.AppendLine($"      copy    {Describe(pack, reference)}");
                if (sourceActor != null)
                {
                    sb.AppendLine($"      origin actor '{sourceActor.EntityName}' (row {sourceActor.Index}) "
                        + $"frameHash 0x{sourceActor.FrameHash:X16} name1 '{sourceActor.Name1}'");
                }
                if (owner != null)
                {
                    sb.AppendLine($"      copy   actor '{owner.EntityName}' (row {owner.Index}) "
                        + $"frameHash 0x{owner.FrameHash:X16} name1 '{owner.Name1}'");
                }

                // How many actors the SHIPPED archive already points at this definition. If sharing one is
                // normal, then sharing it is not what makes a copy collide with its original; if the shipped
                // data never shares one, the definition is an identity and a copy needs its own.
                if (sourceActor != null)
                {
                    string definition = sourceActor.LinkedDefinition;
                    int sharers = pack.Actors.Count(a => a.LinkedDefinition == definition);
                    var byDefinition = pack.Actors
                        .GroupBy(a => a.LinkedDefinition, StringComparer.Ordinal)
                        .OrderByDescending(g => g.Count())
                        .ToList();
                    sb.AppendLine($"    definition '{definition}' is used by {sharers} actor(s) of this pack; "
                        + $"across the pack {byDefinition.Count(g => g.Count() > 1)} of {byDefinition.Count} "
                        + $"definitions are shared, busiest: "
                        + string.Join(", ", byDefinition.Take(3).Select(g => $"'{g.Key}'×{g.Count()}")));
                }

                // Which SCENE lists the object. A district's frame resource is split into scenes, and a scene
                // owns its objects through its own child list — not through either parent slot, which is why
                // both read -1 here and say nothing. The actor names its sector, so an object no scene lists is
                // one the engine never streams in, however complete the record pointing at it.
                Line(sb, "  listed by scene", SceneOf(fr, source), SceneOf(fr, frame));

                // The archive's OTHER hash-keyed tables. An actor record is only half an entity: the engine
                // also looks the thing up in the prefab table (its init data) and in the item descriptions
                // (its physics shape), both keyed by hash. A copy the frame side and the actor side agree
                // about is still an entity nothing can build if those tables have never heard of it.
                LookUpTables(sb, extracted, sourceActor, owner, source, frame);

                sb.AppendLine($"    children of '{origin}' vs '{frame.Name}':");
                for (int i = 0; i < Math.Max(source.Children.Count, frame.Children.Count); i++)
                {
                    FrameObjectBase? a = i < source.Children.Count ? source.Children[i] : null;
                    FrameObjectBase? b = i < frame.Children.Count ? frame.Children[i] : null;
                    sb.AppendLine($"      [{i}] {Child(order, a)}   |   {Child(order, b)}");
                }
            }

            foreach (Formats.Actors.ActorEntry copy in pack.Actors)
            {
                if (!copy.EntityName.Contains("_copy", StringComparison.Ordinal)) continue;
                string baseName = copy.EntityName[..copy.EntityName.LastIndexOf("_copy", StringComparison.Ordinal)];
                Formats.Actors.ActorEntry? source = pack.Actors.FirstOrDefault(a => a.EntityName == baseName);
                sb.AppendLine($"   actor '{copy.EntityName}' (row {copy.Index}) vs '{baseName}'");
                if (source == null) { sb.AppendLine("      the original is gone"); continue; }

                Line(sb, "type", $"{source.Type} ({source.TypeId})", $"{copy.Type} ({copy.TypeId})");
                Line(sb, "type name", source.TypeName, copy.TypeName);
                Line(sb, "name1", source.Name1, copy.Name1);
                Line(sb, "scene sector", source.SceneSector, copy.SceneSector);
                Line(sb, "definition", source.LinkedDefinition, copy.LinkedDefinition);
                Line(sb, "linked frame", source.LinkedFrame, copy.LinkedFrame);
                Line(sb, "entity hash", source.EntityHash.ToString("X16"), copy.EntityHash.ToString("X16"));
                Line(sb, "frame hash", source.FrameHash.ToString("X16"), copy.FrameHash.ToString("X16"));
                Line(sb, "flags", source.Flags.ToString(), copy.Flags.ToString());
                Line(sb, "init prop row", source.InitPropId.ToString(), copy.InitPropId.ToString());
                Line(sb, "position", Fmt(source.Position), Fmt(copy.Position));

                // The link the game follows: hash → reference → row in the object list.
                Formats.Actors.ActorSceneReference? sourceRef =
                    pack.SceneReferences.FirstOrDefault(r => r.FrameHash == source.FrameHash);
                Formats.Actors.ActorSceneReference? copyRef =
                    pack.SceneReferences.FirstOrDefault(r => r.FrameHash == copy.FrameHash);
                Line(sb, "reference row", Target(order, sourceRef), Target(order, copyRef));

                // Drawn as geometry or as a glyph. A copy that came out as a glyph while its original is
                // geometry means the placements found no mesh under the copied object.
                Assets.Actors.ActorPlacements live = placements;
                Formats.Actors.ActorEntry? liveSource =
                    live.All.FirstOrDefault(a => a.EntityName == source.EntityName);
                Formats.Actors.ActorEntry? liveCopy = live.All.FirstOrDefault(a => a.EntityName == copy.EntityName);
                Line(sb, "drawn as",
                    liveSource == null ? "(not resolved)" : live.HasGlyph(liveSource) ? "glyph" : "geometry",
                    liveCopy == null ? "(not resolved)" : live.HasGlyph(liveCopy) ? "glyph" : "geometry");
                Line(sb, "places", live.TargetOf(liveSource!)?.Name.String ?? "(nothing)",
                    liveCopy == null ? "(not resolved)" : live.TargetOf(liveCopy)?.Name.String ?? "(nothing)");
            }
        }
    }

    private static string Target(List<FrameObjectBase> order, Formats.Actors.ActorSceneReference? reference)
    {
        if (reference == null) return "(no reference)";
        return reference.FrameIndex < order.Count
            ? $"{reference.FrameIndex} → '{order[(int)reference.FrameIndex].Name}'"
            : $"{reference.FrameIndex} → OUT OF RANGE ({order.Count} objects)";
    }

    // Which of the archive's hash-keyed tables know each of the two objects. Names are hashed the way every
    // resolver in this engine hashes them, and each candidate is tried: an entity is found by its own name in
    // one table and by its definition or its frame in another, and which is which is what this reveals.
    private static void LookUpTables(StringBuilder sb, string extracted,
        Formats.Actors.ActorEntry? sourceActor, Formats.Actors.ActorEntry? copyActor,
        FrameObjectBase sourceFrame, FrameObjectBase copyFrame)
    {
        var prefabs = new HashSet<ulong>();
        foreach (string path in Directory.GetFiles(extracted, "*.prf", SearchOption.AllDirectories))
        {
            try
            {
                foreach (ulong hash in Formats.Prefab.PrefabFile.Load(path).Hashes) prefabs.Add(hash);
            }
            catch (Exception) { /* a table we cannot read simply reports nothing */ }
        }

        var shapes = new HashSet<ulong>();
        foreach (string path in Directory.GetFiles(extracted, "*.ids", SearchOption.AllDirectories))
        {
            try { shapes.Add(Formats.ItemDesc.ItemDescFile.Load(path).Hash); }
            catch (Exception) { }
        }

        sb.AppendLine($"    hash-keyed tables: {prefabs.Count} prefab entr(ies), {shapes.Count} item description(s)");
        foreach ((string what, string? a, string? b) in new[]
                 {
                     ("entity name", sourceActor?.EntityName, copyActor?.EntityName),
                     ("definition", sourceActor?.LinkedDefinition, copyActor?.LinkedDefinition),
                     ("frame name", sourceFrame.Name?.String, copyFrame.Name?.String),
                 })
        {
            if (a == null || b == null) continue;
            Line(sb, $"  {what} in prefab", Found(prefabs, a), Found(prefabs, b));
            Line(sb, $"  {what} in itemdesc", Found(shapes, a), Found(shapes, b));
        }
    }

    private static string Found(HashSet<ulong> table, string name) =>
        table.Contains(Formats.Hashing.Fnv64.Hash(name)) ? $"yes ('{name}')" : $"no ('{name}')";

    private static string Describe(Formats.Actors.ActorsFile pack, Formats.Actors.ActorSceneReference? reference)
    {
        if (reference == null) return "(none)";
        int at = -1;
        for (int i = 0; i < pack.SceneReferences.Count; i++)
        {
            if (ReferenceEquals(pack.SceneReferences[i], reference)) { at = i; break; }
        }
        return $"[{at}]  0x{reference.FrameHash:X16}  unk0={reference.Unk0}  namePos={reference.NamePos}  "
             + $"'{reference.Name}'  frameIndex={reference.FrameIndex}";
    }

    // The scene whose child list names this object, or a plain "none".
    private static string SceneOf(FrameResource fr, FrameObjectBase frame)
    {
        if (fr.FrameScenes == null) return "(no scenes in this archive)";
        foreach (FrameHeaderScene scene in fr.FrameScenes.Values)
        {
            if (scene.Children.Contains(frame)) return scene.Name?.String ?? "(unnamed)";
        }
        return "NONE";
    }

    // One child of a placed holder, in the terms that decide whether the game draws it: what it is, whether the
    // spawn list names it, and which mesh it points at.
    private static string Child(List<FrameObjectBase> order, FrameObjectBase? frame)
    {
        if (frame == null) return "(missing)";
        string mesh = frame is FrameObjectSingleMesh m
            ? $" mesh={m.MeshIndex} mat={m.MaterialIndex} lods={m.Geometry?.LOD?.Length ?? -1}"
            : "";
        return $"row {order.IndexOf(frame)} {frame.GetType().Name} '{frame.Name}' "
            + $"table={frame.IsOnFrameTable} flags={frame.FrameNameTableFlags}{mesh}";
    }

    private static void Compare(StringBuilder sb, FrameResource fr, List<FrameObjectBase> order,
        FrameObjectBase source, FrameObjectBase copy)
    {
        Line(sb, "type", source.GetType().Name, copy.GetType().Name);
        Line(sb, "row", order.IndexOf(source).ToString(), order.IndexOf(copy).ToString());
        Line(sb, "parent1", Ref(fr, order, source, FrameEntryRefTypes.Parent1),
            Ref(fr, order, copy, FrameEntryRefTypes.Parent1));
        Line(sb, "parent2", Ref(fr, order, source, FrameEntryRefTypes.Parent2),
            Ref(fr, order, copy, FrameEntryRefTypes.Parent2));
        Line(sb, "parentIndex1 (written)", source.ParentIndex1.Index.ToString(), copy.ParentIndex1.Index.ToString());
        Line(sb, "parentIndex2 (written)", source.ParentIndex2.Index.ToString(), copy.ParentIndex2.Index.ToString());
        Line(sb, "on name table", source.IsOnFrameTable.ToString(), copy.IsOnFrameTable.ToString());
        Line(sb, "name table flags", source.FrameNameTableFlags.ToString(), copy.FrameNameTableFlags.ToString());
        Line(sb, "secondary flags", source.SecondaryFlags.ToString(), copy.SecondaryFlags.ToString());
        Line(sb, "local position", Fmt(source.LocalTransform.Translation), Fmt(copy.LocalTransform.Translation));
        Line(sb, "world position", Fmt(source.WorldTransform.Translation), Fmt(copy.WorldTransform.Translation));
        Line(sb, "children", source.Children.Count.ToString(), copy.Children.Count.ToString());

        if (source is FrameObjectSingleMesh a && copy is FrameObjectSingleMesh b)
        {
            Line(sb, "flags", a.SingleMeshFlags.ToString(), b.SingleMeshFlags.ToString());
            Line(sb, "mesh index", a.MeshIndex.ToString(), b.MeshIndex.ToString());
            Line(sb, "material index", a.MaterialIndex.ToString(), b.MaterialIndex.ToString());
            Line(sb, "geometry LODs", (a.Geometry?.LOD?.Length ?? -1).ToString(), (b.Geometry?.LOD?.Length ?? -1).ToString());
            Line(sb, "LOD0 distance", Lod(a), Lod(b));
            Line(sb, "LOD0 vertex buffer", Buffer(fr, a, vertex: true), Buffer(fr, b, vertex: true));
            Line(sb, "LOD0 index buffer", Buffer(fr, a, vertex: false), Buffer(fr, b, vertex: false));
            Line(sb, "material LODs", (a.Material?.NumLods ?? 0).ToString(), (b.Material?.NumLods ?? 0).ToString());
            Line(sb, "material LOD counts", string.Join('/', a.Material?.LodMatCount ?? []),
                string.Join('/', b.Material?.LodMatCount ?? []));
        }
        if (source is FrameObjectCollision c && copy is FrameObjectCollision d)
        {
            Line(sb, "collision hash", c.Hash.ToString("X16"), d.Hash.ToString("X16"));
        }
    }

    private static string Lod(FrameObjectSingleMesh mesh) =>
        mesh.Geometry?.LOD is { Length: > 0 } lods ? lods[0].Distance.ToString("G6") : "(none)";

    private static string Buffer(FrameResource fr, FrameObjectSingleMesh mesh, bool vertex)
    {
        if (mesh.Geometry?.LOD is not { Length: > 0 } lods) return "(none)";
        ulong hash = vertex ? lods[0].VertexBufferRef.Hash : lods[0].IndexBufferRef.Hash;
        bool present = vertex ? fr.VertexBuffers.GetBuffer(hash) != null : fr.IndexBuffers.GetBuffer(hash) != null;
        return $"{hash:X16} {(present ? "in pool" : "MISSING FROM POOL")}";
    }

    private static string Ref(FrameResource fr, List<FrameObjectBase> order, FrameObjectBase frame,
        FrameEntryRefTypes slot)
    {
        if (!frame.Refs.TryGetValue(slot, out int id)) return "(none)";
        if (fr.FrameScenes.TryGetValue(id, out FrameHeaderScene? scene)) return $"scene '{scene.Name}'";
        if (fr.FrameObjects.TryGetValue(id, out object? value) && value is FrameObjectBase parent)
        {
            return $"{parent.GetType().Name} '{parent.Name}' (row {order.IndexOf(parent)})";
        }
        return $"(dangling #{id})";
    }

    private static string Fmt(Vector3 v) => $"<{v.X:F2}, {v.Y:F2}, {v.Z:F2}>";

    private static void Line(StringBuilder sb, string what, string source, string copy) =>
        sb.AppendLine($"    {(source == copy ? " " : "!")} {what,-22} {source}   |   {copy}");
}
