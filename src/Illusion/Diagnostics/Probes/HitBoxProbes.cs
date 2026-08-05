using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using System.Numerics;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Mathematics;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// A DELIBERATELY DESTRUCTIVE experiment, not an assertion probe: it grows every per-piece hit box on a car
/// until the boxes swallow anything hanging off the body, saves that into the car's extracted working copy,
/// and leaves it there for the caller to pack and shoot at.
///
/// <para>
/// What it is testing. Geometry welded onto a car takes no bullet hits — no decal, no particle, no damage —
/// while the vanilla panels around it take them normally. The prefab's collision volumes were ruled out by
/// moving a hood volume half a metre into the air: the packed archive carried the move, the hood went on
/// registering hits where the metal is, and the airspace where the volume now sits registers nothing. So the
/// volumes are the body's PHYSICS (they are also what a player walks into and sits down through), not the
/// bullet's target.
/// </para>
/// <para>
/// That leaves the per-piece box. <c>FrameObjectModel.HitBoxes</c> carries exactly one entry per split piece
/// (158 of each on ascot_baileys200_pha), and the mesh rebuild keeps the pieces exactly as they shipped and
/// only moves the face RANGES — so a welded-on cube joins a piece whose box was computed for the geometry
/// that piece used to have. If the box gates the hit test, the cube sits outside it and is skipped, which is
/// the reported symptom exactly. Growing every box is the cheapest way to find out: it needs no decoding of
/// the orientation word, because a box big enough contains the cube whichever way it is turned.
/// </para>
/// <para>
/// The write lands in the extracted mirror only, so re-extracting the archive undoes it. The caller must pack
/// WITHOUT saving the scene from the editor first — a Save re-serialises the FrameResource from memory and
/// would take the grown boxes back out.
/// </para>
/// Output: %TEMP%\illusion_hitbox_blowup.txt
/// </summary>
internal static class HitBoxProbes
{
    /// <summary>Half-extent added on every axis, in the boxes' own quantum of 10/32768 m — about 0.6 m, which
    /// is more than anything anyone welds onto a panel and still far short of the ushort ceiling.</summary>
    private const int Grow = 2000;

    /// <summary>Ceiling for a grown half-extent. Well inside ushort so no axis can wrap to a tiny box.</summary>
    private const ushort Ceiling = 30000;

    /// <summary>Which box to sabotage, and how. Growing asks "does a bigger box let new geometry in";
    /// collapsing asks the sharper question "is this box what lets ANY geometry in" — on a stock car, with
    /// nothing else touched, so a change in behaviour can only have come from this one field.</summary>
    internal enum Sabotage
    {
        /// <summary>Grow every per-piece hit box.</summary>
        GrowHitBoxes,

        /// <summary>Collapse every per-piece hit box to nothing.</summary>
        ShrinkHitBoxes,

        /// <summary>Collapse every per-bone bounding box to nothing.</summary>
        ShrinkBoneBounds,

        /// <summary>Open every per-piece hit box to the largest size the field can hold.</summary>
        MaxHitBoxes,

        /// <summary>Change nothing: SOLVE the box's arithmetic. Every earlier attempt guessed a decoding and
        /// scored it; this one pairs each box with the vertices of its own piece — now that the face ranges
        /// are known to partition the mesh — and fits raw word against true metre, per axis, by least
        /// squares. A single slope and intercept that hold across every piece IS the encoding.</summary>
        Fit,

        /// <summary>Change nothing: report how the model's faces are shared out between the split pieces,
        /// and whether any face is in no piece at all. A face no piece claims is a face nothing walks — no
        /// size of box can rescue it, so this is worth reading before the next trip into the game.</summary>
        Coverage,
    }

    internal static void RunHitBoxBlowupProbe(string focus, Sabotage what)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_hitbox_blowup.txt");
        var sb = new StringBuilder();

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            var car = new FileInfo(Path.Combine(
                MafiaEnvironment.PcFolder, "sds", "cars", focus + ".sds"));
            if (!car.Exists) { sb.AppendLine($"no such car: {car.FullName}"); return; }

            string extracted = MafiaEnvironment.ExtractedDir(car);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
            {
                sb.AppendLine($"not extracted — open the car in the editor first: {extracted}");
                return;
            }

            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            if (fr?.FrameObjects == null) { sb.AppendLine("no frame resource"); return; }

            sb.AppendLine($"HIT BOX SABOTAGE ({focus}, {what})\n");
            sb.AppendLine($"working copy: {extracted}\n");

            if (what == Sabotage.Coverage) { ReportCoverage(sb, fr); return; }
            if (what == Sabotage.Fit) { ReportFit(sb, fr); FitCorpus(sb); return; }

            int models = 0, boxes = 0;
            foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
            {
                if (what == Sabotage.ShrinkBoneBounds)
                {
                    FrameSkeleton skeleton;
                    try { skeleton = model.GetSkeletonObject(); }
                    catch (Exception) { continue; }
                    foreach (FrameSkeleton.MappingForBlendingInfo mapping in skeleton.MappingForBlendingInfos ?? [])
                    {
                        BoundingBox[]? bounds = mapping.Bounds;
                        if (bounds is not { Length: > 0 }) continue;
                        models++;
                        for (int i = 0; i < bounds.Length; i++)
                        {
                            if (i < 3)
                            {
                                sb.AppendLine($"    [{i}] {bounds[i].Min} .. {bounds[i].Max}"
                                    + " → collapsed to a point at its own centre");
                            }
                            Vector3 centre = (bounds[i].Min + bounds[i].Max) * 0.5f;
                            bounds[i] = new BoundingBox(centre, centre);
                            boxes++;
                        }
                    }
                    continue;
                }

                FrameObjectModel.HitBoxInfo[]? hit = model.HitBoxes;
                if (hit is not { Length: > 0 }) continue;
                models++;

                sb.AppendLine($"  {model.Name.String ?? "(unnamed)"} — {hit.Length} boxes");
                for (int i = 0; i < hit.Length; i++)
                {
                    Short3 size = hit[i].Size;
                    if (i < 4)
                    {
                        sb.AppendLine($"    [{i}] {size.S1},{size.S2},{size.S3}"
                            + $" → {Resized(size.S1, what)},{Resized(size.S2, what)},{Resized(size.S3, what)}");
                    }
                    size.S1 = Resized(size.S1, what);
                    size.S2 = Resized(size.S2, what);
                    size.S3 = Resized(size.S3, what);
                    boxes++;
                }
                if (hit.Length > 4) sb.AppendLine($"    … and {hit.Length - 4} more, all changed the same way");
            }

            if (boxes == 0)
            {
                sb.AppendLine("this car carries no such boxes at all — nothing to test");
                return;
            }

            SdsWriter.SaveFrameResource(fr, car);

            // Read it back off disk: a change that did not survive serialisation would send the caller into
            // the game to test nothing at all.
            int checkedBoxes = 0, changed = 0;
            foreach (FrameObjectModel model in SdsMeshLoader.OpenScene(extracted).FrameResource!
                         .FrameObjects.Values.OfType<FrameObjectModel>())
            {
                if (what == Sabotage.ShrinkBoneBounds)
                {
                    FrameSkeleton reread;
                    try { reread = model.GetSkeletonObject(); }
                    catch (Exception) { continue; }
                    foreach (FrameSkeleton.MappingForBlendingInfo mapping in reread.MappingForBlendingInfos ?? [])
                    {
                        foreach (BoundingBox box in mapping.Bounds ?? [])
                        {
                            checkedBoxes++;
                            if ((box.Max - box.Min).Length() < 1e-4f) changed++;
                        }
                    }
                    continue;
                }
                foreach (FrameObjectModel.HitBoxInfo box in model.HitBoxes ?? [])
                {
                    checkedBoxes++;
                    bool ok = what is Sabotage.GrowHitBoxes or Sabotage.MaxHitBoxes
                        ? box.Size.S1 >= Grow && box.Size.S2 >= Grow && box.Size.S3 >= Grow
                        : box.Size.S1 == 0 && box.Size.S2 == 0 && box.Size.S3 == 0;
                    if (ok) changed++;
                }
            }

            sb.AppendLine($"\nchanged {boxes} boxes on {models} model(s)/mapping(s), saved into the working copy");
            Check(sb, "the change survived the save", checkedBoxes > 0 && changed == checkedBoxes,
                $"{changed} of {checkedBoxes} read back changed");

            // Packed here rather than left for the editor to pack. The editor has this very car open, and a
            // Save from it would re-serialise the FrameResource from memory and take the grown boxes straight
            // back out — the tester would then shoot at an unchanged car and read the result as a refutation.
            // PackSds takes the usual versioned backup first, so this is undone from the app's Restore.
            SdsWriter.PackResult packed = SdsWriter.PackSds(car);
            sb.AppendLine($"\npacked  {packed.Archive}");
            sb.AppendLine($"backup  {packed.Backup ?? "(none)"}");
            sb.AppendLine("\nNEXT: launch the game and shoot the welded-on geometry. Hits there = the");
            sb.AppendLine("per-piece hit box is what gates them. Undo from the app's Restore backup.");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    /// <summary>
    /// SOLVES the box encoding instead of guessing at it.
    /// <para>
    /// Every earlier attempt proposed a decoding (a fixed quantum, the mesh's own quantization, half or full
    /// extents, two spaces) and scored how much of a piece it covered; the best reached 2.5 %. This pairs
    /// each box with the vertices of ITS OWN piece — reachable now that the face ranges are known to
    /// partition the mesh — and fits the raw word against the true metre by least squares, per axis. If one
    /// slope and intercept hold across all pieces, that IS the encoding, and no guessing was involved.
    /// </para>
    /// </summary>
    private static void ReportFit(StringBuilder sb, FrameResource fr)
    {
        foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
        {
            FrameObjectModel.HitBoxInfo[] boxes = model.HitBoxes ?? [];
            FrameObjectModel.WeightedByMeshSplit[] splits = model.BlendMeshSplits ?? [];
            if (boxes.Length == 0 || splits.Length == 0) continue;

            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, 0);
            if (mesh?.Indices is not { Length: > 0 } indices) continue;
            Vector3[] pos = mesh.Positions;

            sb.AppendLine($"  {model.Name.String ?? "(unnamed)"}: {boxes.Length} boxes, {splits.Length} splits");
            sb.AppendLine($"    the mesh's own quantization: offset {mesh.DecompressionOffset} "
                + $"factor {mesh.DecompressionFactor:G9}");

            // Piece order is assumed to be splits-then-pieces; the fit is what proves or breaks it.
            var truthMin = new List<Vector3>();
            var truthMax = new List<Vector3>();
            foreach (FrameObjectModel.WeightedByMeshSplit split in splits)
            {
                foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
                {
                    var lo = new Vector3(float.MaxValue);
                    var hi = new Vector3(float.MinValue);
                    foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                    {
                        foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                        {
                            int from = range.StartIndex;
                            for (int i = from; i < from + (range.NumFaces * 3) && i < indices.Length; i++)
                            {
                                uint v = indices[i];
                                if (v >= pos.Length) continue;
                                lo = Vector3.Min(lo, pos[v]);
                                hi = Vector3.Max(hi, pos[v]);
                            }
                        }
                    }
                    truthMin.Add(lo);
                    truthMax.Add(hi);
                }
            }

            sb.AppendLine($"    pieces walked: {truthMin.Count} (boxes: {boxes.Length})"
                + (truthMin.Count == boxes.Length ? " — one to one" : " — DO NOT MATCH, order is not this"));
            int n = Math.Min(truthMin.Count, boxes.Length);
            if (n == 0) continue;

            // What the BUILDER would write, against what the game shipped. The centre is the claim being
            // checked; the size is deliberately larger than shipped (a sphere radius, so the box holds under
            // the turn we cannot read), and how much larger is worth knowing rather than assuming.
            FrameObjectModel.HitBoxInfo[]? rebuilt = HitBoxBuilder.Compute(model);
            if (rebuilt != null && rebuilt.Length == boxes.Length)
            {
                double worst = 0, total = 0;
                double biggest = 0, sumRatio = 0;
                int counted = 0, contains = 0;
                for (int i = 0; i < n; i++)
                {
                    if (truthMin[i].X > truthMax[i].X) continue;
                    counted++;
                    var shipped = new Vector3(
                        Signed(Word(boxes[i].Position, 0)), Signed(Word(boxes[i].Position, 1)),
                        Signed(Word(boxes[i].Position, 2))) * (10f / 32768f);
                    var mine = new Vector3(
                        Signed(Word(rebuilt[i].Position, 0)), Signed(Word(rebuilt[i].Position, 1)),
                        Signed(Word(rebuilt[i].Position, 2))) * (10f / 32768f);
                    float off = (shipped - mine).Length();
                    worst = Math.Max(worst, off);
                    total += off;

                    // The size the builder writes must actually hold the piece, under any turn.
                    float radius = Word(rebuilt[i].Size, 0) * (10f / 32768f);
                    if (radius + 1e-3f >= ((truthMax[i] - truthMin[i]) * 0.5f).Length()) contains++;

                    float shippedDiagonal = new Vector3(
                        Word(boxes[i].Size, 0), Word(boxes[i].Size, 1), Word(boxes[i].Size, 2)).Length()
                        * (10f / 32768f);
                    if (shippedDiagonal > 1e-4f)
                    {
                        double ratio = radius * Math.Sqrt(3.0) / shippedDiagonal;
                        biggest = Math.Max(biggest, ratio);
                        sumRatio += ratio;
                    }
                }
                sb.AppendLine($"    REBUILT vs SHIPPED over {counted} pieces:");
                sb.AppendLine($"      centre error: mean {total / Math.Max(1, counted) * 1000:F2} mm, "
                    + $"worst {worst * 1000:F2} mm");
                sb.AppendLine($"      the rebuilt size holds its own piece: {contains} of {counted}");
                sb.AppendLine($"      rebuilt box vs shipped, by diagonal: mean ×"
                    + $"{sumRatio / Math.Max(1, counted):F2}, worst ×{biggest:F2}");

                // How big these things are on screen, against the car they sit on. Asked because the drawn
                // layer looks far heavier than "a box round a panel" suggests, and the answer decides
                // whether that is the data or the drawing.
                var radii = new List<float>(n);
                for (int i = 0; i < n; i++)
                {
                    radii.Add(Math.Max(Word(boxes[i].Size, 0),
                        Math.Max(Word(boxes[i].Size, 1), Word(boxes[i].Size, 2))) * (10f / 32768f));
                }
                radii.Sort();
                var carLo = new Vector3(float.MaxValue);
                var carHi = new Vector3(float.MinValue);
                foreach (Vector3 p in mesh.Positions) { carLo = Vector3.Min(carLo, p); carHi = Vector3.Max(carHi, p); }
                sb.AppendLine($"      shipped radius as DRAWN (max axis): median {radii[radii.Count / 2]:F2} m, "
                    + $"90th {radii[(int)(radii.Count * 0.9)]:F2} m, largest {radii[^1]:F2} m");
                sb.AppendLine($"      …the car itself is {(carHi - carLo).X:F2} × {(carHi - carLo).Y:F2}"
                    + $" × {(carHi - carLo).Z:F2} m");
                sb.AppendLine($"      boxes wider than half the car: "
                    + $"{radii.Count(r => r > (carHi - carLo).Length() * 0.25f)} of {radii.Count}");
            }

            // Four readings of the same words, fitted rather than scored. A reading is right when one line
            // fits every piece — a wrong reading shows as a scatter, not as a slightly worse line.
            foreach ((string name, Func<int, int, float> raw, Func<int, int, float> truth) in
                     new (string, Func<int, int, float>, Func<int, int, float>)[]
                     {
                         ("centre ← Position, unsigned", (i, a) => Word(boxes[i].Position, a),
                             (i, a) => Axis((truthMin[i] + truthMax[i]) * 0.5f, a)),
                         ("centre ← Position, signed", (i, a) => Signed(Word(boxes[i].Position, a)),
                             (i, a) => Axis((truthMin[i] + truthMax[i]) * 0.5f, a)),
                         ("minimum ← Position, signed", (i, a) => Signed(Word(boxes[i].Position, a)),
                             (i, a) => Axis(truthMin[i], a)),
                         ("half-extent ← Size", (i, a) => Word(boxes[i].Size, a),
                             (i, a) => Axis((truthMax[i] - truthMin[i]) * 0.5f, a)),
                         ("full extent ← Size", (i, a) => Word(boxes[i].Size, a),
                             (i, a) => Axis(truthMax[i] - truthMin[i], a)),
                     })
            {
                sb.AppendLine($"    {name}:");
                for (int axis = 0; axis < 3; axis++)
                {
                    var xs = new List<float>(n);
                    var ys = new List<float>(n);
                    for (int i = 0; i < n; i++)
                    {
                        if (truthMin[i].X > truthMax[i].X) continue; // a piece with no vertices at all
                        xs.Add(raw(i, axis));
                        ys.Add(truth(i, axis));
                    }
                    (double slope, double intercept, double r2) = Fit(xs, ys);
                    sb.AppendLine($"      {"XYZ"[axis]}: metre = raw × {slope:G6} + {intercept:G6}"
                        + $"   R² {r2:F4}   over {xs.Count} pieces"
                        + (r2 > 0.999 ? "   ← FITS" : ""));
                }
            }
        }
    }

    /// <summary>
    /// The same claim, asserted over every car the install has extracted rather than shown on one. Three
    /// things have to hold everywhere, because the builder writes on all of them: the walk pairs one box
    /// with one piece, the rebuilt centre lands where the shipped one is, and the rebuilt size really holds
    /// the piece it was derived from.
    /// </summary>
    private static void FitCorpus(StringBuilder sb)
    {
        string root = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
        if (!Directory.Exists(root)) return;

        int cars = 0, models = 0, pairedCars = 0;
        int pieces = 0, held = 0;
        double worstMean = 0, worstOne = 0;
        var offenders = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(root).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            if (fr?.FrameObjects == null) continue;
            cars++;

            foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
            {
                FrameObjectModel.HitBoxInfo[] shipped = model.HitBoxes ?? [];
                if (shipped.Length == 0) continue;
                models++;

                FrameObjectModel.HitBoxInfo[]? mine;
                try { mine = HitBoxBuilder.Compute(model); }
                catch (Exception) { mine = null; }
                if (mine == null || mine.Length != shipped.Length)
                {
                    offenders.Add($"{Path.GetFileNameWithoutExtension(sds.Name)}"
                        + $" ({mine?.Length.ToString() ?? "no"} vs {shipped.Length})");
                    continue;
                }
                pairedCars++;

                DecodedMesh? decoded = SdsMeshLoader.DecodeLod(model, 0);
                double sum = 0;
                int counted = 0;
                for (int i = 0; i < mine.Length; i++)
                {
                    pieces++;
                    float off = (Decode(shipped[i].Position) - Decode(mine[i].Position)).Length();
                    // A piece the walk found no vertices for keeps its shipped box, so it agrees exactly and
                    // says nothing about the claim — those are left out of the average rather than flattering it.
                    if (off > 0 || Word(mine[i].Size, 0) != Word(shipped[i].Size, 0))
                    {
                        sum += off;
                        counted++;
                        worstOne = Math.Max(worstOne, off);
                    }
                    if (decoded != null && Holds(decoded, model, i, mine[i])) held++;
                }
                if (counted > 0) worstMean = Math.Max(worstMean, sum / counted);
            }
        }

        sb.AppendLine($"\n── the same claim over the whole cars folder ──");
        sb.AppendLine($"    {cars} extracted cars, {models} models carrying boxes, {pieces} pieces");
        Check(sb, "one box per piece, in the split-then-piece walk, on every car",
            models > 0 && offenders.Count == 0,
            offenders.Count == 0 ? $"{pairedCars} of {models} models paired"
                : "mismatched: " + string.Join(", ", offenders.Take(5)));
        Check(sb, "a rebuilt centre lands where the shipped one is", worstMean < 0.05,
            $"worst per-car mean {worstMean * 1000:F1} mm, worst single piece {worstOne * 1000:F1} mm");
        Check(sb, "a rebuilt box holds every vertex of the piece it came from", pieces > 0 && held == pieces,
            $"{held} of {pieces}");
    }

    private static Vector3 Decode(Short3 raw) => new Vector3(
        Signed(raw.S1), Signed(raw.S2), Signed(raw.S3)) * (10f / 32768f);

    // Whether the rebuilt box's radius really reaches every vertex of its piece — the one property the
    // sphere trick is bought with, and the one that would fail silently in game.
    private static bool Holds(DecodedMesh mesh, FrameObjectModel model, int ordinal,
        FrameObjectModel.HitBoxInfo box)
    {
        int seen = 0;
        foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
        {
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
            {
                if (seen++ != ordinal) continue;
                Vector3 centre = Decode(box.Position);
                float radius = (Word(box.Size, 0) * (10f / 32768f)) + 1e-3f;
                foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                {
                    foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                    {
                        int to = Math.Min(range.StartIndex + (range.NumFaces * 3), mesh.Indices.Length);
                        for (int i = range.StartIndex; i < to; i++)
                        {
                            uint v = mesh.Indices[i];
                            if (v < mesh.Positions.Length
                                && (mesh.Positions[v] - centre).Length() > radius) return false;
                        }
                    }
                }
                return true;
            }
        }
        return true;
    }

    private static float Word(Short3 s, int axis) => axis == 0 ? s.S1 : axis == 1 ? s.S2 : s.S3;

    private static float Signed(float unsignedWord) => unsignedWord >= 32768f ? unsignedWord - 65536f : unsignedWord;

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static (double Slope, double Intercept, double R2) Fit(List<float> xs, List<float> ys)
    {
        int n = xs.Count;
        if (n < 2) return (0, 0, 0);
        double mx = xs.Average(), my = ys.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = xs[i] - mx, dy = ys[i] - my;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        if (sxx <= 0 || syy <= 0) return (0, my, 0);
        double slope = sxy / sxx;
        return (slope, my - (slope * mx), sxy * sxy / (sxx * syy));
    }

    /// <summary>
    /// Who claims which faces. The split table is per bone → per material → a list of face RANGES, and the
    /// game walks those ranges. New geometry is folded into an existing range by the rebuild; if the fold
    /// missed, the new faces sit past the end of every range and nothing ever reaches them.
    /// </summary>
    private static void ReportCoverage(StringBuilder sb, FrameResource fr)
    {
        foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
        {
            FrameObjectModel.WeightedByMeshSplit[] splits = model.BlendMeshSplits ?? [];
            if (splits.Length == 0) continue;

            sb.AppendLine($"  {model.Name.String ?? "(unnamed)"} — {splits.Length} splits, "
                + $"{model.HitBoxes?.Length ?? 0} hit boxes");

            // A split is a BONE and its Data are the PIECES; a burst's StartIndex is an index-buffer offset
            // and NumFaces a triangle count. Together the ranges are meant to partition LOD 0's triangle
            // list — so a face outside all of them is a face the damage system never walks.
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, 0);
            int faces = mesh?.Indices is { } idx ? idx.Length / 3 : 0;
            if (faces == 0) { sb.AppendLine("    LOD 0 would not decode"); continue; }

            var claimed = new bool[faces];
            int pieces = 0, ranges = 0, past = 0;
            foreach (FrameObjectModel.WeightedByMeshSplit split in splits)
            {
                foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
                {
                    pieces++;
                    foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                    {
                        foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                        {
                            ranges++;
                            int from = range.StartIndex / 3;
                            for (int f = from; f < from + range.NumFaces; f++)
                            {
                                if (f >= 0 && f < faces) claimed[f] = true; else past++;
                            }
                        }
                    }
                }
            }

            int held = claimed.Count(c => c);
            int firstFree = Array.IndexOf(claimed, false);
            int lastFree = Array.LastIndexOf(claimed, false);
            sb.AppendLine($"    LOD 0: {faces} faces, {held} claimed by {pieces} pieces over {ranges} ranges"
                + (past > 0 ? $", {past} range slots point past the end of the mesh" : ""));
            if (held == faces)
            {
                sb.AppendLine("    ⇒ every face belongs to a piece");
                continue;
            }
            sb.AppendLine($"    ⇒ {faces - held} FACES BELONG TO NO PIECE — first {firstFree}, last {lastFree}"
                + (firstFree >= 0 && lastFree == faces - 1
                    ? "; they run to the very end of the buffer, which is where newly added geometry lands"
                    : ""));
        }
    }

    private static ushort Resized(ushort was, Sabotage what) =>
        what switch
        {
            Sabotage.GrowHitBoxes => (ushort)Math.Min(Ceiling, was + Grow),
            Sabotage.MaxHitBoxes => Ceiling,
            _ => 0,
        };

    private static void Check(StringBuilder sb, string name, bool ok, string detail) =>
        sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
}
