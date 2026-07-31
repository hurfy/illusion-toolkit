using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What a Mafia II car is made of. Not an assertion probe — a census: it reads every archive in
/// <c>sds\cars</c> and writes down what each one carries, so the shape of a car can be argued about from
/// data rather than from one opened example. One archive can be dumped in full detail as well.
/// Reads the extracted mirror only; nothing is unpacked, parsed twice or written.
/// Output: %TEMP%\illusion_cars.txt
/// </summary>
internal static class CarProbes
{
    /// <param name="focus">Archive stem to dump in full (frame tree, bones, attachments, hit boxes).</param>
    internal static void RunCarsProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_cars.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            FileInfo[] archives = new DirectoryInfo(folder).GetFiles("*.sds");
            Array.Sort(archives, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            sb.AppendLine($"CARS CENSUS: {archives.Length} archives in {folder}\n");

            var rows = new List<Row>();
            var resourceTypes = new Dictionary<string, int>(StringComparer.Ordinal);
            var kindTotals = new Dictionary<string, int>(StringComparer.Ordinal);
            int skipped = 0;

            foreach (FileInfo sds in archives)
            {
                string extracted = MafiaEnvironment.ExtractedDir(sds);
                if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) { skipped++; continue; }

                Row row;
                try { row = Read(sds, extracted); }
                catch (Exception ex) { sb.AppendLine($"READ FAIL {sds.Name}: {ex.Message}"); continue; }

                rows.Add(row);
                foreach (string t in row.ResourceTypes) resourceTypes[t] = resourceTypes.GetValueOrDefault(t) + 1;
                foreach ((string k, int n) in row.Kinds) kindTotals[k] = kindTotals.GetValueOrDefault(k) + n;
            }

            // Per archive, one line. The columns are the questions worth asking of a car: how big it is, how
            // much of it is geometry, whether it is skinned and how deep the rig goes.
            sb.AppendLine($"{"archive",-28} {"MB",5} {"frames",6} {"mesh",4} {"verts",7} {"tris",7} " +
                          $"{"bones",5} {"att",4} {"hit",4} {"lods",4}  resources");
            foreach (Row r in rows)
            {
                sb.AppendLine($"{r.Name,-28} {r.Megabytes,5:F1} {r.FrameCount,6} {r.MeshCount,4} " +
                              $"{r.Vertices,7} {r.Triangles,7} {r.Bones,5} {r.Attachments,4} {r.HitBoxes,4} " +
                              $"{r.Lods,4}  {string.Join(" ", r.ResourceTypes)}");
            }
            if (skipped > 0) sb.AppendLine($"\n{skipped} archive(s) not extracted — unpack the game to include them");

            sb.AppendLine("\n── what the folder holds ──");
            sb.AppendLine($"archives read: {rows.Count}; with geometry: {rows.Count(r => r.MeshCount > 0)}; " +
                          $"skinned (a Model): {rows.Count(r => r.Bones > 0)}");
            sb.AppendLine("resource types, by how many archives carry them:");
            foreach ((string type, int n) in resourceTypes.OrderByDescending(p => p.Value))
                sb.AppendLine($"    {type,-22} {n,4}");
            sb.AppendLine("frame kinds, totalled over every archive:");
            foreach ((string kind, int n) in kindTotals.OrderByDescending(p => p.Value))
                sb.AppendLine($"    {kind,-22} {n,6}");

            // Naming conventions are the folder's own index: which of the 107 are variants of something else.
            sb.AppendLine("\n── naming ──");
            Convention(sb, rows, "_z", "winter variant");
            Convention(sb, rows, "_destr", "destroyed/wrecked variant");
            Convention(sb, rows, "_pha", "unclear suffix (appears on many stock cars)");
            Convention(sb, rows, "_s", "unclear suffix (appears on rolling stock)");
            Convention(sb, rows, "_fmv", "cutscene variant");

            sb.AppendLine("\n── outliers (no geometry, or no skinned model) ──");
            foreach (Row r in rows.Where(r => r.MeshCount == 0 || r.Bones == 0))
                sb.AppendLine($"    {r.Name,-28} meshes={r.MeshCount} bones={r.Bones}  {string.Join(" ", r.ResourceTypes)}");

            DumpOne(sb, folder, focus);
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private sealed record Row(string Name, double Megabytes, int FrameCount, int MeshCount, long Vertices,
        long Triangles, int Bones, int Attachments, int HitBoxes, int Lods,
        IReadOnlyList<string> ResourceTypes, IReadOnlyList<(string Kind, int Count)> Kinds);

    private static Row Read(FileInfo sds, string extracted)
    {
        SdsManifest manifest = SdsManifest.Load(extracted);
        var types = manifest.Entries.Select(e => e.Type).Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal).ToList();

        FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        int meshes = 0, bones = 0, attachments = 0, hitBoxes = 0, lods = 0;
        long verts = 0, tris = 0;

        if (fr?.FrameObjects != null)
        {
            foreach (object o in fr.FrameObjects.Values)
            {
                string kind = o.GetType().Name.Replace("FrameObject", "", StringComparison.Ordinal);
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;

                if (o is not FrameObjectSingleMesh mesh || mesh.Geometry == null) continue;
                meshes++;
                lods += mesh.Geometry.NumLods;
                if (mesh.Geometry.LOD is { Length: > 0 } lodList && lodList[0] is { } lod0) verts += lod0.NumVerts;
                // Triangles come off the material block's LOD 0 slots — the geometry block only counts
                // vertices, and decoding the index buffer for a census would be work for nothing.
                if (mesh.Material?.Materials is { Count: > 0 } slots)
                    tris += slots[0].Sum(s => (long)s.NumFaces);
                if (o is not FrameObjectModel model) continue;
                attachments += model.AttachmentReferences?.Length ?? 0;
                hitBoxes += model.HitBoxes?.Length ?? 0;
                // Through the model's own accessor, not the dictionary: SkeletonIndex is a block index, and
                // the resource dictionaries are keyed by RefID.
                bones += SkeletonOf(model)?.BoneNames?.Length ?? 0;
            }
        }

        return new Row(Path.GetFileNameWithoutExtension(sds.Name), sds.Length / 1048576.0,
            fr?.FrameObjects?.Count ?? 0, meshes, verts, tris, bones, attachments, hitBoxes, lods, types,
            kinds.OrderByDescending(p => p.Value).Select(p => (p.Key, p.Value)).ToList());
    }

    // Best-effort: a model whose skeleton block is missing or mis-indexed must not stop the census.
    private static FrameSkeleton? SkeletonOf(FrameObjectModel model)
    {
        try { return model.GetSkeletonObject(); }
        catch (Exception) { return null; }
    }

    private static void Convention(StringBuilder sb, List<Row> rows, string suffix, string meaning)
    {
        var hits = rows.Where(r => r.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
        sb.AppendLine($"{suffix,-8} {hits.Count,4}  {meaning}");
        if (hits.Count > 0)
            sb.AppendLine("         " + string.Join(", ", hits.Take(8).Select(r => r.Name))
                          + (hits.Count > 8 ? ", …" : ""));
    }

    /// <summary>
    /// One archive, in full: what it announces, what its frame tree looks like, and — the point of the whole
    /// exercise — what its skeleton's bones are CALLED. The bone names are the vocabulary a car is built in,
    /// and nothing else in the data says as plainly which part is which.
    /// </summary>
    private static void DumpOne(StringBuilder sb, string folder, string focus)
    {
        var sds = new FileInfo(Path.Combine(folder, focus + ".sds"));
        sb.AppendLine($"\n\n════ {focus} in full ════");
        if (!sds.Exists) { sb.AppendLine("no such archive"); return; }

        string extracted = MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) { sb.AppendLine("not extracted"); return; }

        sb.AppendLine("\n— what it announces (SDSContent.xml) —");
        foreach ((string type, string file) in SdsManifest.Load(extracted).Entries)
            sb.AppendLine($"    {type,-22} {file}");

        FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
        if (fr?.FrameObjects == null) { sb.AppendLine("\nno frame objects"); return; }

        sb.AppendLine($"\n— resource blocks — scenes {fr.FrameScenes.Count}, geometries {fr.FrameGeometries.Count}, " +
                      $"materials {fr.FrameMaterials.Count}, skeletons {fr.FrameSkeletons.Count}, " +
                      $"blend infos {fr.FrameBlendInfos.Count}, hierarchies {fr.FrameSkeletonHierachies.Count}");

        sb.AppendLine("\n— frame objects, by kind and name —");
        foreach (IGrouping<string, FrameObjectBase> group in fr.FrameObjects.Values.OfType<FrameObjectBase>()
                     .GroupBy(o => o.GetType().Name.Replace("FrameObject", "", StringComparison.Ordinal))
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var names = group.Select(o => o.Name?.ToString() ?? "?").ToList();
            sb.AppendLine($"    {group.Key,-14} {names.Count,3}  {string.Join(", ", names.Take(24))}"
                          + (names.Count > 24 ? ", …" : ""));
        }

        foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
        {
            sb.AppendLine($"\n— the skinned model \"{model.Name}\" —");
            sb.AppendLine($"    skeleton {model.SkeletonIndex}, blend info {model.BlendInfoIndex}, " +
                          $"hierarchy {model.SkeletonHierarchyIndex}, rest transforms " +
                          $"{model.RestTransform?.Length ?? 0}, hit boxes {model.HitBoxes?.Length ?? 0}");

            if (SkeletonOf(model) is { } skeleton)
            {
                var names = (skeleton.BoneNames ?? []).Select(n => n.ToString()).ToList();
                sb.AppendLine($"    bones ({names.Count}), in order:");
                for (int i = 0; i < names.Count; i += 6)
                    sb.AppendLine("        " + string.Join("  ", names.Skip(i).Take(6).Select(n => n.PadRight(20))));
            }

            var refs = model.AttachmentReferences ?? [];
            sb.AppendLine($"    attachment references ({refs.Length}): each names a joint index and the frame it hangs there");
            foreach (FrameObjectModel.AttachmentReference r in refs.Take(48))
            {
                string target = r.Attachment?.Name?.ToString() ?? $"#{r.AttachmentIndex}";
                sb.AppendLine($"        joint {r.JointIndex,3}  →  {target}");
            }
            if (refs.Length > 48) sb.AppendLine("        …");
        }
    }
}
