using System.IO;
using System.Numerics;
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

    /// <summary>
    /// What hangs off a bone, and in whose space. An <c>AttachmentReference</c> says only "frame X belongs to
    /// joint J" — it carries no transform, and the frame's own matrix cascades through
    /// <c>ParentIndex1</c>, which is never a bone. So before a bone can be made to drag its attachments
    /// along, the file has to be asked which space that matrix is in: already model space, sitting at the
    /// joint (then moving a bone means adding the same delta to every attached frame), or bone-local (then
    /// the frames need no touching at all and only the rig moves).
    /// <para>
    /// The census half runs over every car so the answer is not one archive's accident. Output:
    /// %TEMP%\illusion_attachments.txt
    /// </para>
    /// </summary>
    internal static void RunAttachmentsProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_attachments.txt");
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

            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            FileInfo[] archives = new DirectoryInfo(folder).GetFiles("*.sds");
            Array.Sort(archives, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // Census: over every car, three candidate readings of an attached frame's matrix, scored by the
            // one thing a car is guaranteed to be — symmetric. Attachments come in named left/right pairs
            // (handleFL/handleFR, polyhedron_doorBL/BR_Collision, DWHEELL/DWHEELR), and under the reading
            // that is actually right those pairs must come out mirrored about the car's centre plane.
            // The alternative test — "does it land inside the body's bounding box" — cannot tell the
            // readings apart: the box is metres larger than the shell and every reading scores ~99 %.
            int cars = 0, total = 0, unresolved = 0, noParent = 0, anchoredToModel = 0;
            int pairs = 0, undecided = 0, worldChecked = 0, worldMatchesJoint = 0;
            double[] mirrorError = new double[3];
            int[] bestReading = new int[3];
            var attachedKinds = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (FileInfo sds in archives)
            {
                string extracted = MafiaEnvironment.ExtractedDir(sds);
                if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

                FrameResource? fr;
                try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
                catch (Exception) { continue; }
                if (fr?.FrameObjects == null) continue;

                bool counted = false;
                foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
                {
                    Matrix4x4[] rest = model.RestTransform ?? [];
                    var placed = new Dictionary<string, Vector3[]>(StringComparer.OrdinalIgnoreCase);

                    foreach (FrameObjectModel.AttachmentReference r in model.AttachmentReferences ?? [])
                    {
                        total++;
                        if (!counted) { cars++; counted = true; }

                        FrameObjectBase? frame = r.Attachment;
                        if (frame == null) { unresolved++; continue; }
                        attachedKinds[KindOf(frame)] = attachedKinds.GetValueOrDefault(KindOf(frame)) + 1;
                        if (frame.ParentIndex1.Index < 0) noParent++;
                        if (frame.ParentIndex2.Index >= 0) anchoredToModel++;

                        if (r.JointIndex >= rest.Length) continue;
                        Vector3[] readings = Readings(frame.LocalTransform, rest[r.JointIndex]);
                        string? name = frame.Name?.ToString();
                        if (name != null) placed[name] = readings;

                        // What the loader itself now says. Against the model's own joint transform rather than
                        // the bare rest matrix: a model frame that is not parked at the origin carries its
                        // joints with it, and a handful of cars are exactly that.
                        worldChecked++;
                        Vector3 throughJoint = Vector3.Transform(
                            frame.LocalTransform.Translation, model.GetJointWorldTransform(r.JointIndex));
                        if (frame.AttachedTo == model && frame.AttachedJoint == r.JointIndex &&
                            (frame.WorldTransform.Translation - throughJoint).Length() < 0.001f)
                        {
                            worldMatchesJoint++;
                        }
                    }

                    foreach ((string left, string right) in MirrorPairs(placed.Keys))
                    {
                        pairs++;
                        Vector3[] l = placed[left], rr = placed[right];
                        double[] e = new double[3];
                        for (int i = 0; i < 3; i++)
                        {
                            e[i] = MirrorError(l[i], rr[i]);
                            mirrorError[i] += e[i];
                        }

                        // A pair hanging off a joint that sits at the origin with no rotation reads the same
                        // under all three; counting that as a win for any of them would be a thumb on the scale.
                        if (e.Max() - e.Min() < 1e-4) undecided++;
                        else bestReading[Array.IndexOf(e, e.Min())]++;
                    }
                }
            }

            sb.AppendLine($"ATTACHMENT CENSUS: {total} references over {cars} cars in {folder}\n");
            sb.AppendLine($"    attached frame resolved to an object     {total - unresolved}");
            sb.AppendLine($"    dangling (index resolves to nothing)     {unresolved}");
            sb.AppendLine($"    …with no hierarchy parent (ParentIndex1) {noParent}");
            sb.AppendLine($"    …anchored through ParentIndex2           {anchoredToModel}");

            sb.AppendLine($"\n  where the attached frame ends up, three readings, scored on {pairs} named\n" +
                          "  left/right pairs — how badly each reading breaks the car's own symmetry:");
            string[] labels =
            {
                "matrix is MODEL space (what the tree does today) ",
                "joint.T + matrix.T (bone offset, rotation ignored)",
                "local put THROUGH the joint (bone space)         ",
            };
            for (int i = 0; i < 3; i++)
                sb.AppendLine($"    {labels[i]}  mean error {mirrorError[i] / Math.Max(1, pairs),7:F3} m  " +
                              $"best on {bestReading[i],5} of {pairs - undecided} decidable pairs");
            sb.AppendLine($"    ({undecided} pairs read the same under all three — their joint is the origin)");

            sb.AppendLine("\n  what gets attached, by frame kind:");
            foreach ((string kind, int n) in attachedKinds.OrderByDescending(p => p.Value))
                sb.AppendLine($"    {kind,-20} {n,5}");

            // The measurement above says which reading is right; these say the loader now uses it. They are
            // the part of this probe that can FAIL, and the reason it is worth running again after a change.
            sb.AppendLine();
            Check("bone space beats model space by a clear margin",
                mirrorError[2] * 4 < mirrorError[0],
                $"{mirrorError[2] / Math.Max(1, pairs):F3} m vs {mirrorError[0] / Math.Max(1, pairs):F3} m");
            Check("…and wins on the great majority of decidable pairs",
                bestReading[2] > 2 * (bestReading[0] + bestReading[1]),
                $"{bestReading[2]} vs {bestReading[0]} + {bestReading[1]}");
            Check("every attachment reference resolves", unresolved == 0, $"{unresolved} dangling");
            Check("the loader places attached frames at their joint",
                worldMatchesJoint == worldChecked && worldChecked > 0,
                $"{worldMatchesJoint} of {worldChecked} frames within 1 mm");

            DumpAttachments(sb, folder, focus);
            sb.Insert(0, $"ATTACHMENT PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    /// <summary>
    /// The three ways an attachment's matrix could be meant, in the order they are scored. The bone-space
    /// reading puts the frame's own position THROUGH the joint exactly as the frame hierarchy puts a child
    /// through its parent (<c>FrameObjectBase.SetWorldTransform</c> → <c>TransformCoordinate</c>).
    /// A plain <c>local * joint</c> will not do: frame matrices are R·S with M44 left at 0, so the matrix
    /// product silently drops the joint's own translation.
    /// </summary>
    private static Vector3[] Readings(Matrix4x4 local, Matrix4x4 joint) =>
    [
        local.Translation,
        joint.Translation + local.Translation,
        Vector3.Transform(local.Translation, joint),
    ];

    /// <summary>
    /// Names that differ in exactly one character, an L against an R: handleFL/handleFR,
    /// polyhedron_doorBL_Collision/…BR…, DWHEELL/DWHEELR, Exhaust_Turbo_L/…_R. The convention is the modeller's,
    /// not the engine's, so a pair that does not fit the rule is simply not scored.
    /// </summary>
    private static IEnumerable<(string Left, string Right)> MirrorPairs(IEnumerable<string> names)
    {
        var all = names.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in all)
        {
            for (int i = 0; i < n.Length; i++)
            {
                if (char.ToUpperInvariant(n[i]) != 'L') continue;
                string mirrored = n[..i] + (char.IsUpper(n[i]) ? 'R' : 'r') + n[(i + 1)..];
                if (!all.Contains(mirrored, StringComparer.OrdinalIgnoreCase)) continue;
                if (seen.Add(n + " " + mirrored)) yield return (n, mirrored);
            }
        }
    }

    // A car is mirrored about x = 0: the pair should differ only in the sign of X.
    private static double MirrorError(Vector3 left, Vector3 right) =>
        Math.Abs(left.X + right.X) + Math.Abs(left.Y - right.Y) + Math.Abs(left.Z - right.Z);

    private static string KindOf(FrameObjectBase o) =>
        o.GetType().Name.Replace("FrameObject", "", StringComparison.Ordinal);

    /// <summary>One archive's attachments in full: joint, what hangs there, and where both of them are.</summary>
    private static void DumpAttachments(StringBuilder sb, string folder, string focus)
    {
        var sds = new FileInfo(Path.Combine(folder, focus + ".sds"));
        sb.AppendLine($"\n\n════ {focus}: every attachment, measured ════");
        if (!sds.Exists) { sb.AppendLine("no such archive"); return; }

        string extracted = MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) { sb.AppendLine("not extracted"); return; }

        FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
        if (fr?.FrameObjects == null) { sb.AppendLine("no frame objects"); return; }

        foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
        {
            Matrix4x4[] rest = model.RestTransform ?? [];
            var boneNames = (SkeletonOf(model)?.BoneNames ?? []).Select(n => n.ToString() ?? "?").ToList();
            var refs = (model.AttachmentReferences ?? []).OrderBy(r => r.JointIndex).ToList();

            sb.AppendLine($"\n— model \"{model.Name}\", {refs.Count} attachments —");
            sb.AppendLine($"    {"joint",-24} {"attached frame",-34} {"kind",-10} {"P1",4} {"P2",4} " +
                          $"{"[a] as model space",-26} {"[b] joint.T + local.T",-26} {"[c] local through joint",-26}");

            foreach (FrameObjectModel.AttachmentReference r in refs)
            {
                FrameObjectBase? frame = r.Attachment;
                string bone = r.JointIndex < boneNames.Count ? boneNames[r.JointIndex] : "?";
                if (frame == null)
                {
                    sb.AppendLine($"    {r.JointIndex,3} {bone,-20} #{r.AttachmentIndex} — unresolved");
                    continue;
                }

                Matrix4x4 joint = r.JointIndex < rest.Length ? rest[r.JointIndex] : Matrix4x4.Identity;
                Vector3[] c = Readings(frame.LocalTransform, joint);
                sb.AppendLine($"    {r.JointIndex,3} {bone,-20} {frame.Name,-34} {KindOf(frame),-10} " +
                              $"{frame.ParentIndex1.Index,4} {frame.ParentIndex2.Index,4} " +
                              $"{Fmt(c[0]),-26} {Fmt(c[1]),-26} {Fmt(c[2]),-26}");
            }
        }
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

    private static string Fmt(System.Numerics.Vector3 v) => $"({v.X,7:F3},{v.Y,7:F3},{v.Z,7:F3})";

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

            // Where a bone actually IS. The format offers three candidates and names none of them plainly, so
            // the only way to know which is model space is to print all three and see which one lands inside
            // the car. RestTransform may be local to the parent (then it has to be accumulated down the
            // hierarchy) or already model-space; WorldTransforms reads like an inverse bind matrix.
            if (SkeletonOf(model) is { } sk && fr.FrameSkeletonHierachies.Count > 0)
            {
                FrameSkeletonHierarchy hierarchy = model.GetSkeletonHierarchyObject();
                byte[] parents = hierarchy.ParentIndices ?? [];
                Matrix4x4[] rest = model.RestTransform ?? [];
                Matrix4x4[] world = sk.WorldTransforms ?? [];
                var names = (sk.BoneNames ?? []).Select(n => n.ToString()).ToList();

                // Accumulated: rest[i] treated as local to its parent.
                var chained = new Matrix4x4[rest.Length];
                for (int i = 0; i < rest.Length; i++)
                {
                    int p = i < parents.Length ? parents[i] : 0;
                    chained[i] = p != i && p < i ? rest[i] * chained[p] : rest[i];
                }

                sb.AppendLine("\n    where the bones are — three readings of the same data:");
                sb.AppendLine($"        {"bone",-22} {"parent",-22} {"rest.T",-26} {"chained.T",-26} inverse(world).T");
                foreach (int i in new[] { 0, 1, 2, 5, 6, 28, 29, 30, 31 }.Where(i => i < names.Count))
                {
                    string parent = i < parents.Length && parents[i] < names.Count ? names[parents[i]] : "-";
                    Matrix4x4.Invert(i < world.Length ? world[i] : Matrix4x4.Identity, out Matrix4x4 inv);
                    sb.AppendLine($"        {names[i],-22} {parent,-22} " +
                                  $"{Fmt(i < rest.Length ? rest[i].Translation : default),-26} " +
                                  $"{Fmt(i < chained.Length ? chained[i].Translation : default),-26} " +
                                  $"{Fmt(inv.Translation)}");
                }
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
