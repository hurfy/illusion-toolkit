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

            CompareActors(sb, extracted, order, placements);
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // The other half of a copied actor: its record in the pack, and whether the edited pack on disk still
    // says the same thing when it is read and written again.
    private static void CompareActors(StringBuilder sb, string extracted, List<FrameObjectBase> order,
        Assets.Actors.ActorPlacements placements)
    {
        foreach (string path in Directory.GetFiles(extracted, "*.act", SearchOption.AllDirectories))
        {
            byte[] onDisk = File.ReadAllBytes(path);
            Formats.Actors.ActorsFile pack = Formats.Actors.ActorsFile.Load(path);
            bool fixpoint = pack.ToBytes().AsSpan().SequenceEqual(onDisk);
            sb.AppendLine();
            sb.AppendLine($"── {Path.GetFileName(path)}: {pack.Actors.Count} actors, " +
                          $"{pack.SceneReferences.Count} references, re-writes identically: {fixpoint}");

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
