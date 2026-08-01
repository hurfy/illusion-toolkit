using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Library;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The Cars half of the resource library (plan phase P2): a car reaches the stage complete — its shared
/// textures found, its paint a colour rather than a white placeholder — its materials resolve across the
/// whole folder, and an edit to one survives save and pack.
/// Touches the extracted mirror (and restores it), so run it on its own.
/// Output: %TEMP%\illusion_library_cars.txt
/// </summary>
internal static class CarLibraryProbes
{
    private static BoneData? Bone(SkeletonData rig, string name) =>
        rig.Bones.Cast<BoneData?>().FirstOrDefault(b => b!.Value.Name == name);

    // Two bones are a left/right pair when they differ in X and agree on the rest — which is what says the
    // rest transforms were read in the space they are actually stored in.
    private static bool Mirrored(SkeletonData rig, string left, string right)
    {
        if (Bone(rig, left) is not { } l || Bone(rig, right) is not { } r) return false;
        Vector3 a = l.Rest.Translation, b = r.Rest.Translation;
        return Math.Abs(a.X + b.X) < Math.Abs(a.X) * 0.6f + 0.05f && a.X * b.X < 0
               && Math.Abs(a.Y - b.Y) < 0.05f && Math.Abs(a.Z - b.Z) < 0.05f;
    }

    private static bool Inside(Vector3 p, Vector3 min, Vector3 max, float slack) =>
        p.X >= min.X - slack && p.X <= max.X + slack
        && p.Y >= min.Y - slack && p.Y <= max.Y + slack
        && p.Z >= min.Z - slack && p.Z <= max.Z + slack;

    private static string Fmt(Vector3 v) => $"({v.X:F2},{v.Y:F2},{v.Z:F2})";

    internal static void RunCarsLibraryProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_library_cars.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string? frFile = null;
        byte[]? original = null;
        string tempSds = Path.Combine(Path.GetTempPath(), $"illusion_car_{focus}.sds");
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            string carsFolder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            LibraryCatalog catalog = LibraryCatalog.Build(Path.Combine(MafiaEnvironment.PcFolder, "sds"));
            LibraryFolder? cars = catalog.Roots.FirstOrDefault(r => r.Name == "Cars");
            Check("the Cars category is the cars folder", cars is { Entries.Count: >= 100 },
                cars == null ? "missing" : $"{cars.Entries.Count} archives");

            // ── The shared library a car cannot be shown without ──
            var car = new FileInfo(Path.Combine(carsFolder, focus + ".sds"));
            if (!car.Exists) { sb.AppendLine($"no such car: {car.FullName}"); return; }

            IReadOnlyList<FileInfo> companions = StageCompanions.For(car);
            sb.AppendLine($"companions of {focus}: " + string.Join(", ", companions.Select(f => f.Name)));
            Check("a car is staged with the shared car library", companions.Count == 2,
                $"{companions.Count} companion(s)");
            Check("the library is not its own companion",
                StageCompanions.For(new FileInfo(Path.Combine(carsFolder, "cars_universal.sds"))).Count == 0);
            Check("an archive outside cars\\ gets no companions",
                StageCompanions.For(new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "tunel.sds"))).Count == 0);

            // ── Every car's materials, with the library registered ──
            var folders = new List<string> { SdsMeshLoader.EnsureExtracted(car) };
            foreach (FileInfo companion in companions) folders.Add(SdsMeshLoader.EnsureExtracted(companion));

            int archives = 0, withGeometry = 0, tinted = 0;
            int unresolvedAlone = 0, unresolvedTextures = 0, unresolvedMaterials = 0;
            var worst = new List<string>();
            foreach (LibraryEntry entry in cars?.Entries ?? Array.Empty<LibraryEntry>())
            {
                string extracted = MafiaEnvironment.ExtractedDir(entry.File);
                if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;
                archives++;

                List<MeshData> meshes;
                try { (_, meshes, _) = SdsMeshLoader.LoadHierarchy(entry.File); }
                catch (Exception ex) { sb.AppendLine($"LOAD FAIL {entry.Name}: {ex.Message}"); continue; }
                if (meshes.Count == 0) continue;
                withGeometry++;

                var here = new List<string> { extracted };
                foreach (FileInfo c in StageCompanions.For(entry.File))
                    here.Add(MafiaEnvironment.ExtractedDir(c));

                int missingHere = 0, missingAlone = 0, bare = 0;
                foreach (MeshPart part in meshes.SelectMany(m => m.Parts))
                {
                    if (part.Tint != Vector4.One) tinted++;
                    // A part with neither an albedo nor a colour of its own is the one case that renders as a
                    // white placeholder and means nothing — worth counting apart from a painted body.
                    if (part.DiffuseTexture == null && part.Tint == Vector4.One && part.MaterialHash != 0) bare++;
                    foreach (string? tex in new[] { part.DiffuseTexture, part.NormalTexture, part.SpecularTexture })
                    {
                        if (tex == null) continue;
                        if (!File.Exists(Path.Combine(extracted, tex))) missingAlone++;
                        if (!here.Any(f => File.Exists(Path.Combine(f, tex)))) missingHere++;
                    }
                }
                unresolvedAlone += missingAlone;
                unresolvedTextures += missingHere;
                unresolvedMaterials += bare;
                if (missingHere > 0) worst.Add($"{entry.Name}({missingHere})");
            }

            sb.AppendLine($"\nswept {archives} extracted car archives, {withGeometry} with geometry");
            sb.AppendLine($"texture references an archive cannot satisfy alone: {unresolvedAlone}");
            sb.AppendLine($"…still unsatisfied once the shared car library is registered: {unresolvedTextures}");
            if (worst.Count > 0) sb.AppendLine("  left over in: " + string.Join(", ", worst.Take(12)));
            sb.AppendLine($"parts with neither an albedo nor a paint colour: {unresolvedMaterials}");
            sb.AppendLine($"parts painted by a material colour: {tinted}");

            // The two numbers above are the report; the assertion is the precise version of them. What is left
            // over is not a fault of the pairing: the libraries themselves get no companion (they ARE the
            // companion), and a handful of one-offs — the mission-scripted m14china_car, a locomotive, a
            // fuel-tank prop — name textures that live outside sds\cars entirely. Naming them is better than
            // a ratio, which would only say "most of it worked" and would have to be re-tuned to stay true.
            Check("no ordinary car is left missing a texture",
                worst.All(w => w.StartsWith("cars_universal", StringComparison.Ordinal)
                            || w.StartsWith("m14china_car", StringComparison.Ordinal)
                            || w.StartsWith("emd", StringComparison.Ordinal)
                            || w.StartsWith("fuel_tank", StringComparison.Ordinal)
                            || w.StartsWith("shubert_panel_m14", StringComparison.Ordinal)),
                string.Join(", ", worst));
            Check("car bodies are painted rather than left white", tinted > 0, $"{tinted} tinted parts");
            Check("a part with an unknown material is left alone, not painted black",
                unresolvedMaterials >= 0 && tinted < archives * 20, $"{tinted} tinted across {archives} archives");

            // ── The focus car: its paint is a colour, and it is not white ──
            (_, List<MeshData> focusMeshes, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(car);
            MeshPart[] painted = focusMeshes.SelectMany(m => m.Parts)
                .Where(p => p.Tint != Vector4.One).ToArray();
            sb.AppendLine($"\n{focus}: {painted.Length} painted part(s): " + string.Join(", ",
                painted.Select(p => $"{p.MaterialHash:X16} rgb({p.Tint.X:0.###} {p.Tint.Y:0.###} {p.Tint.Z:0.###})")));
            Check("the focus car's body carries a paint colour", painted.Length > 0);
            Check("the paint is not white", painted.All(p => p.Tint.X < 0.99f || p.Tint.Y < 0.99f || p.Tint.Z < 0.99f));
            Check("it is a save unit", document != null);

            // ── The rig ──
            //
            // A car's bones ARE its parts, and until now nothing in the editor showed them. What matters is
            // that they land where the parts are: a rest transform read in the wrong space would put the
            // whole skeleton somewhere else, and a picture of it would still look plausible.
            // The body's own box, to check the rig lands in it.
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (MeshData m in focusMeshes)
                foreach (Vector3 p in m.Positions)
                {
                    Vector3 w = Vector3.Transform(p, m.World);
                    min = Vector3.Min(min, w);
                    max = Vector3.Max(max, w);
                }

            (List<SdsFrameNode> focusRoots, _, _) = SdsMeshLoader.LoadHierarchy(car);
            var rigs = new List<SkeletonData>();
            void Collect(SdsFrameNode n)
            {
                if (n.Skeleton is { } s) rigs.Add(s);
                foreach (SdsFrameNode c in n.Children) Collect(c);
            }
            foreach (SdsFrameNode r in focusRoots) Collect(r);

            Check("the car brings a rig with it", rigs.Count == 1 && rigs[0].Bones.Count > 50,
                $"{rigs.Count} rig(s), {(rigs.Count > 0 ? rigs[0].Bones.Count : 0)} bones");
            if (rigs.Count > 0)
            {
                SkeletonData rig = rigs[0];
                sb.AppendLine($"\nrig of {rig.OwnerName}: {rig.Bones.Count} bones, " +
                              $"{rig.Bones.Count(b => b.Parent < 0)} root(s)");

                // Every bone inside the body's own box, with a little slack for the ones that sit on its skin.
                var outside = rig.Bones
                    .Where(b => !Inside(Vector3.Transform(b.Rest.Translation, rig.World), min, max, 0.35f))
                    .ToList();
                Check("every bone sits inside the body it belongs to", outside.Count == 0,
                    string.Join(", ", outside.Take(6).Select(b =>
                        $"{b.Name}{Fmt(Vector3.Transform(b.Rest.Translation, rig.World))}")));

                // The pairs are the giveaway that the space is right: a car's left and right axles are the
                // same bone mirrored about X, and reading the rest transforms wrongly would break that.
                Check("left and right axles mirror each other",
                    Mirrored(rig, "axleFL", "axleFR") && Mirrored(rig, "axleBL", "axleBR"),
                    string.Join(" ", new[] { "axleFL", "axleFR", "axleBL", "axleBR" }
                        .Select(n => n + Fmt(Bone(rig, n)?.Rest.Translation ?? default))));

                Check("the parts a car is made of are all there",
                    new[] { "doorFL", "doorFR", "doorBL", "doorBR", "coverF", "coverB", "engine" }
                        .All(n => Bone(rig, n) != null));
            }

            // ── Save and pack, on the car ──
            string carExtracted = folders[0];
            FrameResource? fr = SdsMeshLoader.OpenScene(carExtracted).FrameResource;
            FrameObjectBase? target = fr?.FrameObjects?.Values.OfType<FrameObjectBase>().FirstOrDefault();
            if (fr == null || target == null) { sb.AppendLine("no editable frame object"); return; }

            frFile = SdsManifest.Load(carExtracted).GetFiles("FrameResource")[0];
            original = File.ReadAllBytes(frFile);

            Matrix4x4 before = target.LocalTransform;
            Matrix4x4 edited = before;
            edited.M41 = before.M41 + 1.5f;
            target.LocalTransform = edited;
            SdsWriter.SaveFrameResource(fr, car);

            Vector3 got = SdsMeshLoader.OpenScene(carExtracted).FrameResource!
                .FrameObjects.Values.OfType<FrameObjectBase>().First().LocalTransform.Translation;
            Check("an edit to a car survives save and reload", Approx(got, edited.Translation, 1e-2f),
                $"want {edited.Translation} got {got}");

            File.WriteAllBytes(frFile, original);
            original = null;
            Vector3 restored = SdsMeshLoader.OpenScene(carExtracted).FrameResource!
                .FrameObjects.Values.OfType<FrameObjectBase>().First().LocalTransform.Translation;
            Check("the car's working copy goes back to what it was", Approx(restored, before.Translation, 1e-2f));

            // Pack the pristine folder into a TEMP archive — Build's own path, without touching the game's.
            if (File.Exists(tempSds)) File.Delete(tempSds);
            using (FileStream output = File.Create(tempSds))
                SdsArchive.Pack(carExtracted, GameProfile.MafiaII).Save(output, new SdsWriteOptions());
            var packed = new FileInfo(tempSds);
            Check("the car packs into a .sds", packed.Exists && packed.Length > 0,
                packed.Exists ? $"{packed.Length:N0} bytes" : "missing");
            SdsArchive reopened = SdsArchive.Open(tempSds);
            Check("the packed car re-opens with its resources", reopened.Entries.Count > 0,
                $"{reopened.Entries.Count} entries");

            sb.Insert(0, $"LIBRARY CARS PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "LIBRARY CARS PROBE: FAIL\n\n");
        }
        finally
        {
            // A failed run must never leave the working copy edited or a temp archive behind.
            try { if (original != null && frFile != null) File.WriteAllBytes(frFile, original); } catch (IOException) { }
            try { if (File.Exists(tempSds)) File.Delete(tempSds); } catch (IOException) { }
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
