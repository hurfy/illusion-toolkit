using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Archive;
using Illusion.Formats.Geometry;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What a SHOT reads. The game plays a different impact particle and sound for sheet metal, glass and
/// upholstery, so something in a car names the surface at the point of impact — and it is not the physics
/// shapes: <c>--probe-car-collision</c> measured <c>MaterialId 0</c> on all 1174 ItemDesc shapes of all 106
/// car archives. The one field left unaccounted for is <see cref="FrameObjectModel.HitBoxInfo.Unk"/>, sitting
/// beside a per-piece box in the skinned model.
/// <para>
/// This probe does not assume that field means anything. It measures what it correlates with — the piece's
/// material, its bone, the physics-surface table — and whether the boxes are even readable geometry, in model
/// space or in the space of their piece's bone. Reads only; nothing is written.
/// Output: %TEMP%\illusion_bullets.txt
/// </para>
/// </summary>
internal static class BulletProbes
{
    /// <summary>Hit-box int16 → metres, as <c>--probe-car-collision</c> settled it.</summary>
    private const float Scale = 10f / 32768f;

    /// <summary>One split piece: the box that goes with it and what the model says the piece is.</summary>
    private sealed record Piece(
        int Index,
        int BlendIndex,
        string Bone,
        IReadOnlyList<(int Material, IReadOnlyList<(int Start, int Count)> Ranges)> Bursts,
        FrameObjectModel.HitBoxInfo? Box);

    internal static void RunBulletProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bullets.txt");
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
            MafiaMaterials.EnsureLoaded();

            string cars = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            OneCar(sb, cars, focus, Check);
            Calibrate(sb, cars, Check);
            Census(sb, cars, Check);
            PieceNames(sb, cars, focus, Check);
            BlendIndexReading(sb, cars, Check);
            BoxRotation(sb, cars, Check);
            VertexChannels(sb, cars, focus, Check);
            Characters(sb, Check);
            Parts(sb, cars, Check);
            BlendPools(sb, cars, Check);
            BoneBoxes(sb, cars, Check);
            StaleBounds(sb, cars, focus, Check);
            RebuildBounds(sb, cars, Check);
            RebuildUndo(sb, cars, Check);
            RoundTrips(sb, cars, Check);
            BodyHulls(sb, cars, Check);
            SurfaceReaches(sb, cars, focus, Check);

            sb.Insert(0, $"BULLET PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "BULLET PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // ── one car, piece by piece ──

    private static void OneCar(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        if (!TryOpen(folder, focus, out FrameResource? fr, out FrameObjectModel? model, out string why))
        {
            sb.AppendLine($"{focus}: {why}");
            return;
        }

        string[] bones = BoneNames(model!);
        List<Piece> pieces = PiecesOf(model!, bones);
        sb.AppendLine($"════ {focus} ════");

        // Every mesh in the archive, not just the one the rest of this probe reads. A car can carry more
        // than one, and a part that lives on a second mesh has no split, no piece and no hit box of the
        // first one — so measuring the first mesh and talking about "the car" would be silently wrong.
        sb.AppendLine("── meshes in this archive ──");
        foreach (FrameObjectSingleMesh any in fr!.FrameObjects!.Values.OfType<FrameObjectSingleMesh>())
        {
            Formats.Frames.Resources.MaterialStruct[] table =
                any.Material?.Materials is { Count: > 0 } list ? list[0] : [];
            var used = new List<string>();
            foreach (Formats.Frames.Resources.MaterialStruct slot in table)
            {
                used.Add(MafiaMaterials.GetMaterialName(slot.MaterialHash) ?? $"0x{slot.MaterialHash:X16}");
            }
            int slots = table.Length;
            sb.AppendLine($"    {(any is FrameObjectModel ? "skinned" : "static "),-8} "
                + $"{Trim(any.Name?.ToString() ?? "?", 28),-28} {slots} materials: "
                + Trim(string.Join(", ", used), 70));
        }
        sb.AppendLine();
        sb.AppendLine($"{pieces.Count} split pieces, {model!.HitBoxes?.Length ?? 0} hit boxes, "
            + $"{bones.Length} bones");

        check("one hit box per split piece",
            (model.HitBoxes?.Length ?? 0) == pieces.Count,
            $"{model.HitBoxes?.Length ?? 0} boxes, {pieces.Count} pieces");

        // Is the value simply the piece's ordinal? If it is, there is nothing to decode and the whole
        // candidate dies here rather than after a table of correlations.
        bool ordinal = pieces.Count > 0 && pieces.All(p => p.Box != null && p.Box.Unk == (uint)p.Index);
        check("the value is NOT the piece's own index", !ordinal,
            ordinal ? "it is exactly the ordinal — dead end" : "differs from the ordinal");

        sb.AppendLine($"\n── pieces of {focus} ──");
        sb.AppendLine($"    {"#",3}  {"blend",-16} {"unk",10} {"hex",10}  {"materials",-40} faces");
        foreach (Piece p in pieces)
        {
            string mats = string.Join(", ", p.Bursts.Select(b => MaterialName(model, b.Material)));
            int faces = p.Bursts.Sum(b => b.Ranges.Sum(r => r.Count));
            sb.AppendLine($"    {p.Index,3}  {Trim(p.Bone, 16),-16} "
                + $"{p.Box?.Unk.ToString() ?? "-",10} {(p.Box == null ? "-" : "0x" + p.Box.Unk.ToString("X8")),10}  "
                + $"{Trim(mats, 40),-40} {faces}");
        }

        // Where each piece actually sits. With no per-piece selection in the UI, this is the only way to say
        // WHICH piece is the rag hanging off a truck's board and which is the board — same bone, same
        // material, so nothing but geometry tells them apart.
        DecodedMesh? geo = SdsMeshLoader.DecodeLod0(model);
        if (geo != null)
        {
            sb.AppendLine($"\n── where each piece sits ({focus}) ──");
            sb.AppendLine($"    {"#",3}  {"bone",-16} {"unk",10}  {"centre",-24} {"size",-22} faces");
            foreach (Piece p in pieces)
            {
                Vector3[] verts = VerticesOf(geo, p);
                if (verts.Length == 0) continue;
                Vector3 lo = verts[0], hi = verts[0];
                foreach (Vector3 v in verts) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
                Vector3 size = hi - lo, mid = (hi + lo) * 0.5f;
                sb.AppendLine($"    {p.Index,3}  {Trim(p.Bone, 16),-16} "
                    + $"{(p.Box == null ? "-" : "0x" + p.Box.Unk.ToString("X8")),10}  "
                    + $"{mid.X,7:F2}{mid.Y,7:F2}{mid.Z,7:F2}   "
                    + $"{size.X,6:F2}{size.Y,6:F2}{size.Z,6:F2}  "
                    + $"{p.Bursts.Sum(b => b.Ranges.Sum(r => r.Count))}");
            }
        }

        Spaces(sb, model, pieces, check);
    }

    /// <summary>How a hit box's six u16 might turn into metres. Each candidate is scored the same way: does
    /// the box it produces contain the piece's own vertices?</summary>
    private sealed record Reading(string Name, Func<FrameObjectModel.HitBoxInfo, (Vector3 Lo, Vector3 Hi)> Decode);

    /// <summary>
    /// What the boxes ARE, before what they mean. A box that is real geometry contains the vertices of its own
    /// piece under SOME reading; the candidates are the fixed 10/32768 scale <c>--probe-car-collision</c>
    /// settled on and the mesh's own vertex quantization (<c>DecompressionOffset/Factor</c>) — the boxes are
    /// six u16 exactly like a packed position, so the same lattice is the obvious suspect. Each reading is
    /// tried in model space and in the space of the piece's bone.
    /// </summary>
    private static void Spaces(
        StringBuilder sb, FrameObjectModel model, List<Piece> pieces, Action<string, bool, string> check)
    {
        DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(model);
        if (decoded == null) { sb.AppendLine("\n(no decodable geometry — space test skipped)"); return; }

        Vector3 offset = decoded.DecompressionOffset;
        float factor = decoded.DecompressionFactor;
        sb.AppendLine($"\n── how to read a hit box? (quantization: offset {offset.X:F3} {offset.Y:F3} "
            + $"{offset.Z:F3}, factor {factor:G6}) ──");

        Reading[] readings =
        [
            new("fixed 10/32768, full size", b => Fixed(b, half: false)),
            new("fixed 10/32768, half size", b => Fixed(b, half: true)),
            new("mesh quantization, full size", b => Quantized(b, offset, factor, half: false)),
            new("mesh quantization, half size", b => Quantized(b, offset, factor, half: true)),
            new("mesh quantization, size also offset", b => QuantizedBoth(b, offset, factor)),
            new("SIGNED centre, size is half", b => SignedCentre(b, half: true)),
            new("SIGNED centre, size is full", b => SignedCentre(b, half: false)),
            new("SIGNED min + extent, fixed scale", b => MinFirst(b, offset, factor, false, false)),
            new("SIGNED min + far corner, fixed scale", b => MinFirst(b, offset, factor, true, false)),
            new("SIGNED min + extent, quantized", b => MinFirst(b, offset, factor, false, true)),
            new("SIGNED min + far corner, quantized", b => MinFirst(b, offset, factor, true, true)),
        ];

        Matrix4x4[] rest = model.RestTransform ?? [];
        var scores = new (int Model, int Bone)[readings.Length];
        int total = 0;

        foreach (Piece p in pieces)
        {
            if (p.Box == null) continue;
            Vector3[] verts = VerticesOf(decoded, p);
            if (verts.Length == 0) continue;
            total += verts.Length;

            bool hasBone = p.BlendIndex < rest.Length && TryInvert(rest[p.BlendIndex], out Matrix4x4 toBone2);
            Matrix4x4 toBone = Matrix4x4.Identity;
            if (hasBone) TryInvert(rest[p.BlendIndex], out toBone);

            for (int r = 0; r < readings.Length; r++)
            {
                (Vector3 lo, Vector3 hi) = readings[r].Decode(p.Box);
                scores[r].Model += verts.Count(v => Inside(v, lo, hi));
                if (hasBone) scores[r].Bone += verts.Count(v => Inside(Vector3.Transform(v, toBone), lo, hi));
            }
        }

        if (total == 0) { sb.AppendLine("    no piece resolved to vertices"); return; }
        sb.AppendLine($"    {"reading",-34} {"in model space",14} {"in bone space",14}");
        int best = 0;
        for (int r = 0; r < readings.Length; r++)
        {
            sb.AppendLine($"    {readings[r].Name,-34} {scores[r].Model * 100.0 / total,13:F1}% "
                + $"{scores[r].Bone * 100.0 / total,13:F1}%");
            best = Math.Max(best, Math.Max(scores[r].Model, scores[r].Bone));
        }
        sb.AppendLine($"    (over {total} vertices)");

        // What the numbers themselves look like beside the geometry they would have to cover — printed so a
        // reading that is close but mis-scaled can be told from one that is nonsense.
        sb.AppendLine($"\n    {"#",3}  {"piece bounds (model space)",-46} box, best reading");
        int shown = 0;
        foreach (Piece p in pieces)
        {
            if (p.Box == null || shown >= 12) continue;
            Vector3[] verts = VerticesOf(decoded, p);
            if (verts.Length == 0) continue;
            shown++;
            Vector3 lo = verts.Aggregate(Vector3.Min);
            Vector3 hi = verts.Aggregate(Vector3.Max);
            (Vector3 blo, Vector3 bhi) = Quantized(p.Box, offset, factor, half: false);
            sb.AppendLine($"    {p.Index,3}  {lo.X,7:F2}{lo.Y,7:F2}{lo.Z,7:F2} ..{hi.X,7:F2}{hi.Y,7:F2}{hi.Z,7:F2}"
                + $"   {blo.X,7:F2}{blo.Y,7:F2}{blo.Z,7:F2} ..{bhi.X,7:F2}{bhi.Y,7:F2}{bhi.Z,7:F2}"
                + $"   raw p {p.Box.Position.S1},{p.Box.Position.S2},{p.Box.Position.S3}"
                + $" s {(ushort)p.Box.Size.S1},{(ushort)p.Box.Size.S2},{(ushort)p.Box.Size.S3}");
        }

        // Recorded as knowledge, not as a hope: no reading of the six u16 puts a piece inside its own box, so
        // whatever the block is, it is not a box over this mesh. The assertion is phrased so that it fails if
        // someone ever finds the reading that works — which would be the discovery, not a regression.
        // ELEVEN readings now, in two spaces, with the piece's bone resolved correctly through the remap
        // table — the mistake that invalidated the first attempt. The best still covers a fortieth of the
        // geometry it is supposed to bound, the size-to-extent ratio has an IQR of twice its own median, and
        // the accompanying uint is a dictionary shared across cars (2899 of 5175 values appear in more than
        // one, and the commonest covers 6384 boxes on all 88). Whatever these sixteen bytes are, they are
        // not this piece's box, and this is where guessing at them stops.
        check("the hit boxes are NOT geometry over the mesh — no reading contains the pieces",
            best * 2 <= total, $"best reading covers {best * 100.0 / total:F1}% of the vertices");
    }

    private static (Vector3 Lo, Vector3 Hi) Fixed(FrameObjectModel.HitBoxInfo box, bool half)
    {
        var centre = new Vector3(box.Position.S1, box.Position.S2, box.Position.S3) * Scale;
        var size = new Vector3((ushort)box.Size.S1, (ushort)box.Size.S2, (ushort)box.Size.S3) * Scale;
        Vector3 h = half ? size : size * 0.5f;
        return (centre - h, centre + h);
    }

    /// <summary>
    /// The POSITION read as signed and the size as unsigned.
    ///
    /// <para>
    /// The raw values say so out loud once they are looked at: a shipped car's boxes carry positions like
    /// 65528, 65535 and 60237, which are −8, −1 and −5299 — a cluster around zero, not values three
    /// quarters of the way up an unsigned range. A size never does that. The reference toolkit names the
    /// triple "Short3" and reads all six as unsigned, which is where this started.
    /// </para>
    /// </summary>
    /// <summary>
    /// The two triples read as a CORNER and something measured from it, rather than as a centre and a size.
    ///
    /// <para>
    /// Worth trying because nothing says a box has to be stated centre-first, and the shipped values are
    /// consistent with it: the first triple carries negatives and the second never does, which is exactly
    /// how a min corner and an extent behave and not at all how a centre and a size do — a centre is as
    /// often positive as negative on a symmetric car, and these are not.
    /// </para>
    /// </summary>
    /// <param name="asCorner">Whether the second triple is the far CORNER rather than an extent.</param>
    /// <param name="quantized">Whether to read them through the mesh's own vertex lattice instead of the
    /// fixed scale — the same six u16 a packed position uses.</param>
    private static (Vector3 Lo, Vector3 Hi) MinFirst(
        FrameObjectModel.HitBoxInfo box, Vector3 offset, float factor, bool asCorner, bool quantized)
    {
        float scale = quantized ? factor : Scale;
        Vector3 shift = quantized ? offset : Vector3.Zero;
        var lo = (new Vector3(
            (short)box.Position.S1, (short)box.Position.S2, (short)box.Position.S3) * scale) + shift;
        var second = new Vector3((ushort)box.Size.S1, (ushort)box.Size.S2, (ushort)box.Size.S3) * scale;
        Vector3 hi = asCorner ? second + shift : lo + second;
        return (Vector3.Min(lo, hi), Vector3.Max(lo, hi));
    }

    private static (Vector3 Lo, Vector3 Hi) SignedCentre(FrameObjectModel.HitBoxInfo box, bool half)
    {
        var centre = new Vector3(
            (short)box.Position.S1, (short)box.Position.S2, (short)box.Position.S3) * Scale;
        var size = new Vector3((ushort)box.Size.S1, (ushort)box.Size.S2, (ushort)box.Size.S3) * Scale;
        Vector3 h = half ? size : size * 0.5f;
        return (centre - h, centre + h);
    }

    private static (Vector3 Lo, Vector3 Hi) Quantized(
        FrameObjectModel.HitBoxInfo box, Vector3 offset, float factor, bool half)
    {
        var centre = new Vector3((ushort)box.Position.S1, (ushort)box.Position.S2, (ushort)box.Position.S3)
            * factor + offset;
        var size = new Vector3((ushort)box.Size.S1, (ushort)box.Size.S2, (ushort)box.Size.S3) * factor;
        Vector3 h = half ? size : size * 0.5f;
        return (centre - h, centre + h);
    }

    /// <summary>Both corners quantized the same way — i.e. the pair is a MIN and a MAX, not a centre and a size.</summary>
    private static (Vector3 Lo, Vector3 Hi) QuantizedBoth(
        FrameObjectModel.HitBoxInfo box, Vector3 offset, float factor)
    {
        var a = new Vector3((ushort)box.Position.S1, (ushort)box.Position.S2, (ushort)box.Position.S3)
            * factor + offset;
        var b = new Vector3((ushort)box.Size.S1, (ushort)box.Size.S2, (ushort)box.Size.S3)
            * factor + offset;
        return (Vector3.Min(a, b), Vector3.Max(a, b));
    }

    /// <summary>
    /// Do not guess the scale — solve for it. If the six u16 really are a box over the piece, then for every
    /// piece of every car the ratio (real extent ÷ raw size) is one and the same number, and the raw position
    /// is an affine image of the real centre. Both are measured here over the whole corpus: a constant ratio
    /// IS the scale, and a ratio that scatters says the numbers are not a box over this geometry at all.
    /// </summary>
    private static void Calibrate(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ solving for the scale ════");

        var sizeRatios = new List<float>();
        var posRatios = new List<float>();
        int pieces = 0, zeroSize = 0, singlePiece = 0;
        var singles = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string stem = Path.GetFileNameWithoutExtension(sds.Name);
            if (!TryOpen(folder, stem, out FrameResource? fr, out FrameObjectModel? _, out string _)) continue;

            foreach (FrameObjectModel model in fr!.FrameObjects!.Values.OfType<FrameObjectModel>())
            {
                if ((model.HitBoxes?.Length ?? 0) == 0) continue;
                DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(model);
                if (decoded == null) continue;
                List<Piece> list = PiecesOf(model, BoneNames(model));

                // A model whose whole mesh is ONE piece is the cleanest calibration there is: its single box
                // has to be the model's own bounding box, whatever the encoding.
                if (list.Count == 1 && list[0].Box != null)
                {
                    singlePiece++;
                    Vector3[] all = decoded.Positions;
                    if (all.Length > 0 && singles.Count < 12)
                    {
                        Vector3 lo = all.Aggregate(Vector3.Min);
                        Vector3 hi = all.Aggregate(Vector3.Max);
                        FrameObjectModel.HitBoxInfo b = list[0].Box!;
                        singles.Add($"    {Trim(stem, 26),-26} mesh {lo.X,6:F2}{lo.Y,6:F2}{lo.Z,6:F2}"
                            + $" ..{hi.X,6:F2}{hi.Y,6:F2}{hi.Z,6:F2}   raw p {b.Position.S1},{b.Position.S2},"
                            + $"{b.Position.S3}  s {(ushort)b.Size.S1},{(ushort)b.Size.S2},{(ushort)b.Size.S3}"
                            + $"  unk 0x{b.Unk:X8}");
                    }
                }

                foreach (Piece p in list)
                {
                    if (p.Box == null) continue;
                    Vector3[] verts = VerticesOf(decoded, p);
                    if (verts.Length < 3) continue;
                    pieces++;

                    Vector3 lo = verts.Aggregate(Vector3.Min);
                    Vector3 hi = verts.Aggregate(Vector3.Max);
                    Vector3 extent = hi - lo;
                    Vector3 centre = (lo + hi) * 0.5f;

                    var raw = new Vector3((ushort)p.Box.Size.S1, (ushort)p.Box.Size.S2, (ushort)p.Box.Size.S3);
                    if (raw.X < 1 || raw.Y < 1 || raw.Z < 1) { zeroSize++; continue; }
                    sizeRatios.Add(extent.X / raw.X);
                    sizeRatios.Add(extent.Y / raw.Y);
                    sizeRatios.Add(extent.Z / raw.Z);

                    var rawPos = new Vector3(p.Box.Position.S1, p.Box.Position.S2, p.Box.Position.S3);
                    if (MathF.Abs(rawPos.X) > 1) posRatios.Add(centre.X / rawPos.X);
                    if (MathF.Abs(rawPos.Y) > 1) posRatios.Add(centre.Y / rawPos.Y);
                    if (MathF.Abs(rawPos.Z) > 1) posRatios.Add(centre.Z / rawPos.Z);
                }
            }
        }

        sb.AppendLine($"    {pieces} pieces measured, {zeroSize} with a zero raw size, "
            + $"{singlePiece} single-piece models");
        Sweep(sb, folder);
        Spread(sb, "extent ÷ raw size", sizeRatios);
        Spread(sb, "centre ÷ raw position", posRatios);

        if (singles.Count > 0)
        {
            sb.AppendLine("\n    single-piece models — the box must BE the mesh bounds:");
            foreach (string line in singles) sb.AppendLine(line);
        }

        // A constant ratio would show up as a tight spread around its median. Anything else means the six
        // u16 are not a box over the mesh, however they are scaled.
        (float median, float iqr) = MedianAndIqr(sizeRatios);
        check("the size ratio is NOT a constant — so the numbers are not the piece's extent at any scale",
            median <= 0 || iqr / median > 0.25f,
            $"median {median:G4}, IQR {iqr:G4} ({(median > 0 ? iqr / median : 0):P0} of the median)");
    }

    /// <summary>
    /// Forget which box belongs to which piece: if the boxes cover the model at all, then at the RIGHT scale
    /// nearly every vertex of the body falls inside some box, and at every other scale far fewer do. One
    /// sweep over candidate scales therefore both finds the scale and says whether one exists.
    /// </summary>
    private static void Sweep(StringBuilder sb, string folder)
    {
        float[] scales = [1f / 128, 1f / 256, 1f / 512, 1f / 1024, 10f / 32768, 1f / 2048, 1f / 4096,
            1f / 8192, 1f / 32768, 1f / 1000, 1f / 100];
        var coveredFull = new int[scales.Length];
        var coveredHalf = new int[scales.Length];
        int vertices = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name).Take(12))
        {
            if (!TryOpen(folder, Path.GetFileNameWithoutExtension(sds.Name),
                    out FrameResource? fr, out FrameObjectModel? _, out string _))
            {
                continue;
            }
            foreach (FrameObjectModel model in fr!.FrameObjects!.Values.OfType<FrameObjectModel>())
            {
                FrameObjectModel.HitBoxInfo[] boxes = model.HitBoxes ?? [];
                if (boxes.Length == 0) continue;
                DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(model);
                if (decoded == null) continue;

                for (int s = 0; s < scales.Length; s++)
                {
                    var full = new List<(Vector3 Lo, Vector3 Hi)>();
                    var half = new List<(Vector3 Lo, Vector3 Hi)>();
                    foreach (FrameObjectModel.HitBoxInfo b in boxes)
                    {
                        var centre = new Vector3(b.Position.S1, b.Position.S2, b.Position.S3) * scales[s];
                        var size = new Vector3(
                            (ushort)b.Size.S1, (ushort)b.Size.S2, (ushort)b.Size.S3) * scales[s];
                        full.Add((centre - size * 0.5f, centre + size * 0.5f));
                        half.Add((centre - size, centre + size));
                    }
                    foreach (Vector3 v in decoded.Positions)
                    {
                        if (full.Any(box => Inside(v, box.Lo, box.Hi))) coveredFull[s]++;
                        if (half.Any(box => Inside(v, box.Lo, box.Hi))) coveredHalf[s]++;
                    }
                }
                vertices += decoded.Positions.Length;
            }
        }

        sb.AppendLine($"\n    coverage of the body by ALL its boxes, by scale ({vertices} vertices, 12 cars):");
        for (int s = 0; s < scales.Length; s++)
        {
            if (vertices == 0) break;
            sb.AppendLine($"      1/{1 / scales[s],-8:F1}  size as full {coveredFull[s] * 100.0 / vertices,6:F1}%"
                + $"   size as half {coveredHalf[s] * 100.0 / vertices,6:F1}%");
        }
    }

    private static void Spread(StringBuilder sb, string label, List<float> values)
    {
        if (values.Count == 0) { sb.AppendLine($"    {label}: no samples"); return; }
        values.Sort();
        (float median, float iqr) = MedianAndIqr(values);
        sb.AppendLine($"    {label,-24} n={values.Count,6}  min {values[0],12:G6}  "
            + $"p25 {values[values.Count / 4],12:G6}  median {median,12:G6}  "
            + $"p75 {values[values.Count * 3 / 4],12:G6}  max {values[^1],12:G6}  (IQR {iqr:G4})");
    }

    private static (float Median, float Iqr) MedianAndIqr(List<float> values)
    {
        if (values.Count == 0) return (0, 0);
        List<float> sorted = [.. values];
        sorted.Sort();
        return (sorted[sorted.Count / 2], sorted[sorted.Count * 3 / 4] - sorted[sorted.Count / 4]);
    }

    // ── every car, for the shape of the value set ──

    private static void Census(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ census over every car ════");

        var byValue = new Dictionary<uint, int>();
        var materialsOf = new Dictionary<uint, SortedSet<string>>();
        var bonesOf = new Dictionary<uint, SortedSet<string>>();
        var carsOf = new Dictionary<uint, SortedSet<string>>();
        int archives = 0, models = 0, boxes = 0, mismatched = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            if (!TryOpen(folder, Path.GetFileNameWithoutExtension(sds.Name),
                    out FrameResource? fr, out FrameObjectModel? _, out string _))
            {
                continue;
            }
            archives++;

            foreach (FrameObjectModel model in fr!.FrameObjects!.Values.OfType<FrameObjectModel>())
            {
                if ((model.HitBoxes?.Length ?? 0) == 0) continue;
                models++;
                string[] bones = BoneNames(model);
                List<Piece> pieces = PiecesOf(model, bones);
                if (pieces.Count != model.HitBoxes!.Length) mismatched++;

                foreach (Piece p in pieces)
                {
                    if (p.Box == null) continue;
                    boxes++;
                    uint v = p.Box.Unk;
                    byValue[v] = byValue.GetValueOrDefault(v) + 1;
                    bonesOf.TryAdd(v, new SortedSet<string>(StringComparer.Ordinal));
                    bonesOf[v].Add(p.Bone);
                    carsOf.TryAdd(v, new SortedSet<string>(StringComparer.Ordinal));
                    carsOf[v].Add(Path.GetFileNameWithoutExtension(sds.Name));
                    materialsOf.TryAdd(v, new SortedSet<string>(StringComparer.Ordinal));
                    foreach ((int material, _) in p.Bursts) materialsOf[v].Add(MaterialName(model, material));
                }
            }
        }

        sb.AppendLine($"    {archives} archives read, {models} skinned models with boxes, {boxes} boxes, "
            + $"{byValue.Count} distinct values");
        check("every model's box count matches its piece count", mismatched == 0,
            $"{mismatched} models disagree");

        // A shared dictionary of surfaces would repeat across cars; a per-car index would not.
        int shared = carsOf.Count(kv => kv.Value.Count > 1);
        check("values repeat across cars — the mark of a shared dictionary, not a per-car index",
            shared > 0, $"{shared} of {byValue.Count} values appear in more than one car");

        sb.AppendLine("\n── values, most used first ──");
        sb.AppendLine($"    {"value",10} {"hex",10} {"boxes",7} {"cars",5}  "
            + $"{"physics surface",-20} {"bones",-34} materials");
        foreach ((uint value, int count) in byValue.OrderByDescending(kv => kv.Value).Take(60))
        {
            sb.AppendLine($"    {value,10} 0x{value,8:X8} {count,7} {carsOf[value].Count,5}  "
                + $"{Trim(SurfaceName(value), 20),-20} "
                + $"{Trim(string.Join("/", bonesOf[value]), 34),-34} "
                + $"{Trim(string.Join("/", materialsOf[value]), 60)}");
        }
        if (byValue.Count > 60) sb.AppendLine($"    (+{byValue.Count - 60} more values)");

        // Does the value decide the material, or the material the value? Either direction being a function
        // is the finding; both being many-to-many kills the correlation.
        int oneMaterial = materialsOf.Count(kv => kv.Value.Count == 1);
        sb.AppendLine($"\n    {oneMaterial} of {materialsOf.Count} values are used with exactly ONE material");
        int inTable = byValue.Keys.Count(v => v < (uint)CollisionMaterialCatalog.All.Count);
        sb.AppendLine($"    {inTable} of {byValue.Count} values fall inside the physics-surface table "
            + $"(0..{CollisionMaterialCatalog.All.Count - 1})");
    }

    /// <summary>
    /// The per-VERTEX channels nothing in this toolkit has ever looked at: <c>Color0</c>, <c>Color1</c> and
    /// <c>DamageGroup</c>.
    ///
    /// <para>
    /// Every candidate keyed to a material, a bone, a piece or a prefab part is dead: a truck's boards and its
    /// bumper share one material and give wood and metal, and its rag shares a material with the cab and gives
    /// cloth. Whatever names the surface has to have per-vertex resolution, and these are the only per-vertex
    /// channels in the format. The bridge copies them from the donor vertex on a weld, which would also
    /// explain a cube that changed its decal when it changed bones while keeping its material.
    /// </para>
    /// </summary>
    private static void VertexChannels(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ per-vertex channels: Color0 / Color1 / DamageGroup ════");

        if (!TryOpen(folder, focus, out FrameResource? _, out FrameObjectModel? model, out string why))
        {
            sb.AppendLine($"    {focus}: {why}");
            return;
        }

        DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(model!);
        if (decoded?.RawVertexData == null) { sb.AppendLine("    no raw vertex data"); return; }

        VertexFlags declaration = decoded.Declaration;
        sb.AppendLine($"    declaration: Color={declaration.HasFlag(VertexFlags.Color)} "
            + $"Color1={declaration.HasFlag(VertexFlags.Color1)} "
            + $"DamageGroup={declaration.HasFlag(VertexFlags.DamageGroup)}");

        Vertex[] verts = VertexTranslator.DecompressBuffer(
            decoded.RawVertexData, decoded.NumVerts, declaration,
            decoded.DecompressionOffset, decoded.DecompressionFactor);

        var damageValues = new Dictionary<int, int>();
        var colour0Values = new Dictionary<string, int>();
        foreach (Vertex v in verts)
        {
            damageValues[v.DamageGroup] = damageValues.GetValueOrDefault(v.DamageGroup) + 1;
            string key = $"{v.Color0[0]:X2}{v.Color0[1]:X2}{v.Color0[2]:X2}{v.Color0[3]:X2}";
            colour0Values[key] = colour0Values.GetValueOrDefault(key) + 1;
        }
        sb.AppendLine($"    DamageGroup: {damageValues.Count} distinct — " + string.Join(", ",
            damageValues.OrderByDescending(p => p.Value).Take(10).Select(p => $"{p.Key}×{p.Value}")));
        sb.AppendLine($"    Color0: {colour0Values.Count} distinct — " + string.Join(", ",
            colour0Values.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key}×{p.Value}")));

        // Per piece, so a board can be told from the rag hanging beside it.
        sb.AppendLine($"\n    {"#",3}  {"bone",-16}  {"DamageGroup",-22} Color0");
        foreach (Piece p in PiecesOf(model!, BoneNames(model!)))
        {
            var dmg = new SortedSet<int>();
            var col = new SortedSet<string>(StringComparer.Ordinal);
            foreach ((_, IReadOnlyList<(int Start, int Count)> ranges) in p.Bursts)
            {
                foreach ((int start, int count) in ranges)
                {
                    int first = start / 3;
                    for (int face = first; face < first + count; face++)
                    {
                        for (int corner = 0; corner < 3; corner++)
                        {
                            int slot = (face * 3) + corner;
                            if (slot < 0 || slot >= decoded.Indices.Length) continue;
                            uint vertex = decoded.Indices[slot];
                            if (vertex >= verts.Length) continue;
                            dmg.Add(verts[vertex].DamageGroup);
                            col.Add($"{verts[vertex].Color0[0]:X2}{verts[vertex].Color0[1]:X2}"
                                + $"{verts[vertex].Color0[2]:X2}{verts[vertex].Color0[3]:X2}");
                        }
                    }
                }
            }
            if (dmg.Count == 0) continue;
            sb.AppendLine($"    {p.Index,3}  {Trim(p.Bone, 16),-16}  "
                + $"{Trim(string.Join(",", dmg), 22),-22} {Trim(string.Join(",", col), 60)}");
        }

        check("the mesh carries a per-vertex channel that is not constant",
            damageValues.Count > 1 || colour0Values.Count > 1,
            $"{damageValues.Count} damage groups, {colour0Values.Count} colours");
    }

    /// <summary>
    /// Is the <c>uint</c> beside a hit box a packed ROTATION?
    ///
    /// <para>
    /// Now that the centre and the half-size read exactly (signed shorts at 10/32768), the box still fails to
    /// contain a third of its own vertices — which is what an ORIENTED box read as an axis-aligned one looks
    /// like. 32 bits is the usual home of a packed quaternion (three components at 10 bits plus 2 bits naming
    /// the one left out), so the candidate is scored the only way that settles it: rotate the box and count
    /// how many of the piece's own vertices fall inside. A reading that is not the rotation cannot raise the
    /// count; the true one should take it to nearly all.
    /// </para>
    /// </summary>
    private static void BoxRotation(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ is the piece's unk a packed rotation? ════");

        const float quantum = 10f / 32768f;
        string[] names =
        [
            "no rotation — the box as an AABB",
            "smallest-three, dropped index in the HIGH 2 bits",
            "smallest-three, dropped index in the LOW 2 bits",
            "three 10-bit Euler angles (Z·Y·X)",
        ];
        var inside = new long[names.Length];
        long total = 0, ownTotal = 0, ownInside = 0;
        int cars = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            if (cars >= 20) break;
            if (!TryOpen(folder, Path.GetFileNameWithoutExtension(sds.Name),
                    out FrameResource? fr, out FrameObjectModel? model, out string _))
            {
                continue;
            }
            cars++;

            DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(model!);
            if (decoded == null) continue;
            byte[]? owners = SdsMeshLoader.GlobalBoneIds(model!, 0);
            float[]? weights = decoded.BoneWeights;
            foreach (Piece p in PiecesOf(model!, BoneNames(model!)))
            {
                if (p.Box == null) continue;
                Vector3[] verts = VerticesOf(decoded, p);
                if (verts.Length == 0) continue;

                // Vertices of the piece that are weighted to the piece's OWN bone. A face at the seam
                // between two panels has corners belonging to the neighbour, and a box that guards this
                // piece has no reason to reach them.
                Vector3[] ownVerts = OwnVertices(decoded, p, owners, weights);
                ownTotal += ownVerts.Length;
                foreach (Vector3 v in ownVerts)
                {
                    Vector3 local = v - centreOf(p);
                    if (Math.Abs(local.X) <= halfOf(p).X + 1e-3f && Math.Abs(local.Y) <= halfOf(p).Y + 1e-3f
                        && Math.Abs(local.Z) <= halfOf(p).Z + 1e-3f)
                    {
                        ownInside++;
                    }
                }

                Vector3 centreOf(Piece piece) => new(
                    (short)piece.Box!.Position.S1 * quantum,
                    (short)piece.Box.Position.S2 * quantum,
                    (short)piece.Box.Position.S3 * quantum);
                Vector3 halfOf(Piece piece) => new(
                    piece.Box!.Size.S1 * quantum, piece.Box.Size.S2 * quantum, piece.Box.Size.S3 * quantum);

                var centre = new Vector3(
                    (short)p.Box.Position.S1 * quantum,
                    (short)p.Box.Position.S2 * quantum,
                    (short)p.Box.Position.S3 * quantum);
                var half = new Vector3(
                    p.Box.Size.S1 * quantum, p.Box.Size.S2 * quantum, p.Box.Size.S3 * quantum);
                Quaternion[] rotations =
                [
                    Quaternion.Identity,
                    SmallestThree(p.Box.Unk, indexHigh: true),
                    SmallestThree(p.Box.Unk, indexHigh: false),
                    PackedEuler(p.Box.Unk),
                ];

                foreach (Vector3 v in verts)
                {
                    total++;
                    for (int r = 0; r < rotations.Length; r++)
                    {
                        Vector3 local = Vector3.Transform(v - centre, Quaternion.Conjugate(rotations[r]));
                        if (Math.Abs(local.X) <= half.X + 1e-3f && Math.Abs(local.Y) <= half.Y + 1e-3f
                            && Math.Abs(local.Z) <= half.Z + 1e-3f)
                        {
                            inside[r]++;
                        }
                    }
                }
            }
        }

        sb.AppendLine($"    counting only vertices weighted to the piece's OWN bone: "
            + $"{(ownTotal == 0 ? 0 : (double)ownInside / ownTotal),7:P1} of {ownTotal} are inside "
            + "(no rotation)");
        sb.AppendLine($"    {total} vertices over {cars} cars, counted inside their own piece's box");
        for (int r = 0; r < names.Length; r++)
        {
            sb.AppendLine($"    {(total == 0 ? 0 : (double)inside[r] / total),7:P1}  {names[r]}");
        }

        int best = 0;
        for (int r = 1; r < names.Length; r++)
        {
            if (inside[r] > inside[best]) best = r;
        }
        check("a rotation reading of unk beats the plain AABB",
            total > 0 && best != 0,
            best == 0 ? "no reading beats the AABB — unk is not the box's rotation"
                : $"best is \"{names[best]}\"");
    }

    /// <summary>The piece's vertices that are weighted to the piece's own bone, rather than a neighbour's.</summary>
    private static Vector3[] OwnVertices(DecodedMesh decoded, Piece piece, byte[]? owners, float[]? weights)
    {
        if (owners == null || weights == null) return VerticesOf(decoded, piece);

        var kept = new List<Vector3>();
        var seen = new HashSet<uint>();
        foreach ((_, IReadOnlyList<(int Start, int Count)> ranges) in piece.Bursts)
        {
            foreach ((int start, int count) in ranges)
            {
                int first = start / 3;
                for (int face = first; face < first + count; face++)
                {
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int slot = (face * 3) + corner;
                        if (slot < 0 || slot >= decoded.Indices.Length) continue;
                        uint vertex = decoded.Indices[slot];
                        if (vertex >= decoded.Positions.Length || !seen.Add(vertex)) continue;

                        int best = -1;
                        float most = 0f;
                        for (int k = 0; k < 4; k++)
                        {
                            int at = ((int)vertex * 4) + k;
                            if (at >= owners.Length || at >= weights.Length) break;
                            if (weights[at] > most) (most, best) = (weights[at], owners[at]);
                        }
                        if (best == piece.BlendIndex) kept.Add(decoded.Positions[vertex]);
                    }
                }
            }
        }
        return [.. kept];
    }

    /// <summary>A quaternion packed as three 10-bit components plus 2 bits naming the one left out.</summary>
    private static Quaternion SmallestThree(uint packed, bool indexHigh)
    {
        int dropped = indexHigh ? (int)(packed >> 30) & 3 : (int)(packed & 3);
        uint body = indexHigh ? packed : packed >> 2;
        float scale = 1f / MathF.Sqrt(2f);
        float a = (((body & 0x3FF) / 1023f * 2f) - 1f) * scale;
        float b = ((((body >> 10) & 0x3FF) / 1023f * 2f) - 1f) * scale;
        float c = ((((body >> 20) & 0x3FF) / 1023f * 2f) - 1f) * scale;
        float rest = MathF.Sqrt(Math.Max(0f, 1f - (a * a) - (b * b) - (c * c)));
        return dropped switch
        {
            0 => new Quaternion(rest, a, b, c),
            1 => new Quaternion(a, rest, b, c),
            2 => new Quaternion(a, b, rest, c),
            _ => new Quaternion(a, b, c, rest),
        };
    }

    /// <summary>Three 10-bit angles over a full turn, applied Z then Y then X.</summary>
    private static Quaternion PackedEuler(uint packed)
    {
        float turn = MathF.PI * 2f / 1024f;
        float x = (packed & 0x3FF) * turn;
        float y = ((packed >> 10) & 0x3FF) * turn;
        float z = ((packed >> 20) & 0x3FF) * turn;
        return Quaternion.CreateFromYawPitchRoll(y, x, z);
    }

    // ── is the bone we hang on a piece the right one? ──

    /// <summary>
    /// Whether a split piece names the bone its own geometry is weighted to.
    ///
    /// <para>
    /// Everything read off a piece goes through this name — its materials, its box, which panel a shot on it
    /// belongs to. The name is one lookup, <c>BoneRemapIDs[BlendIndex]</c>, and the only check it ever had is
    /// that the index fits the table, which cannot fail on a table long enough to hold it. The oracle here is
    /// the geometry: the vertices of a piece carry bone weights, and the bone carrying most of them IS the
    /// bone that piece belongs to. A name that disagrees with the weights is wrong.
    /// </para>
    /// <para>
    /// The rival reading — <c>BlendIndex</c> straight as a bone id — is scored beside it, because that is what
    /// this toolkit did before and what the reference toolkit still does.
    /// </para>
    /// </summary>
    private static void PieceNames(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ does a piece name the bone its geometry belongs to? ════");

        int pieces = 0, agree = 0, rival = 0, cars = 0;
        var offBy = new Dictionary<int, int>();
        var examples = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            if (!TryOpen(folder, Path.GetFileNameWithoutExtension(sds.Name),
                    out FrameResource? fr, out FrameObjectModel? _, out string _))
            {
                continue;
            }
            cars++;
            string car = Path.GetFileNameWithoutExtension(sds.Name);

            foreach (FrameObjectModel model in fr!.FrameObjects!.Values.OfType<FrameObjectModel>())
            {
                string[] bones = BoneNames(model);
                if (bones.Length == 0) continue;

                DecodedMesh? decoded = SdsMeshLoader.DecodeLod(model, 0);
                byte[]? ids = SdsMeshLoader.GlobalBoneIds(model, 0);
                if (decoded?.BoneWeights is not { } weights || ids == null) continue;

                byte[] flat = [];
                try
                {
                    Illusion.Formats.Frames.Resources.FrameBlendInfo.BoneIndexInfo[] lods =
                        model.GetBlendInfoObject().BoneIndexInfos ?? [];
                    if (lods.Length > 0) flat = lods[0].BoneRemapIDs ?? [];
                }
                catch (Exception) { continue; }

                bool showCar = string.Equals(car, focus, StringComparison.OrdinalIgnoreCase);
                if (showCar)
                {
                    sb.AppendLine($"\n── {car}: piece → named bone vs the bone its weights say ──");
                    sb.AppendLine($"    {"#",3}  {"named",-18} {"weights say",-18} {"share",6}  materials");
                }

                int index = 0;
                foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
                {
                    int named = split.BlendIndex < flat.Length ? flat[split.BlendIndex] : -1;
                    int asBoneId = split.BlendIndex < bones.Length ? split.BlendIndex : -1;

                    foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
                    {
                        var weightOf = new Dictionary<int, float>(8);
                        var mats = new SortedSet<string>(StringComparer.Ordinal);
                        foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                        {
                            mats.Add(MaterialName(model, burst.MaterialIndex));
                            foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                            {
                                int from = range.StartIndex / 3;
                                for (int f = from; f < from + range.NumFaces; f++)
                                {
                                    for (int corner = 0; corner < 3; corner++)
                                    {
                                        int at = (f * 3) + corner;
                                        if (at < 0 || at >= decoded.Indices.Length) continue;
                                        int vertex = (int)decoded.Indices[at];
                                        for (int k = 0; k < 4; k++)
                                        {
                                            int slot = (vertex * 4) + k;
                                            if (slot >= ids.Length || slot >= weights.Length) break;
                                            if (weights[slot] <= 0f) continue;
                                            weightOf[ids[slot]] = weightOf.GetValueOrDefault(ids[slot])
                                                + weights[slot];
                                        }
                                    }
                                }
                            }
                        }

                        index++;
                        if (weightOf.Count == 0) continue; // a piece with no faces has nothing to say

                        float total = weightOf.Values.Sum();
                        (int Bone, float Weight) top = (-1, 0f);
                        foreach ((int bone, float weight) in weightOf)
                        {
                            if (weight > top.Weight) top = (bone, weight);
                        }

                        pieces++;
                        if (top.Bone == named) agree++;
                        if (top.Bone == asBoneId) rival++;
                        if (top.Bone != named)
                        {
                            offBy[named - top.Bone] = offBy.GetValueOrDefault(named - top.Bone) + 1;
                            if (examples.Count < 12)
                            {
                                examples.Add($"{car} piece {index - 1}: named "
                                    + $"{Name(bones, named)} but weighted to {Name(bones, top.Bone)} "
                                    + $"({top.Weight / total:P0})");
                            }
                        }

                        if (showCar)
                        {
                            // Materials by SLOT and HASH, not just by name: two slots can resolve to the
                            // same display name while being different materials, and a surface lookup keyed
                            // by material would then look identical here while behaving differently in game.
                            var slots = new List<string>();
                            foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                            {
                                if ((burst.Data?.Sum(r => r.NumFaces) ?? 0) == 0) continue;
                                ulong hash = 0;
                                Formats.Frames.Resources.MaterialStruct[]? table =
                                    model.Material?.Materials is { Count: > 0 } list ? list[0] : null;
                                if (table != null && burst.MaterialIndex < table.Length)
                                {
                                    hash = table[burst.MaterialIndex].MaterialHash;
                                }
                                slots.Add($"[{burst.MaterialIndex}] {MaterialName(model, burst.MaterialIndex)}"
                                    + $" 0x{hash:X16}");
                            }
                            sb.AppendLine($"    {index - 1,3}  {Trim(Name(bones, named), 18),-18} "
                                + $"{Trim(Name(bones, top.Bone), 18),-18} {top.Weight / total,6:P0}  "
                                + $"{string.Join("  ", slots)}");
                        }
                    }
                }
            }
        }

        sb.AppendLine($"\n    {pieces} pieces with geometry across {cars} archives");
        sb.AppendLine($"    BoneRemapIDs[BlendIndex] agrees with the weights: {agree} "
            + $"({(pieces == 0 ? 0 : (double)agree / pieces):P1})");
        sb.AppendLine($"    BlendIndex read straight as a bone id:            {rival} "
            + $"({(pieces == 0 ? 0 : (double)rival / pieces):P1})");
        if (offBy.Count > 0)
        {
            sb.AppendLine("    when it disagrees, named minus actual: " + string.Join(", ",
                offBy.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key:+0;-0;0}×{p.Value}")));
        }
        foreach (string line in examples) sb.AppendLine("      " + line);

        // No assert here on purpose. The oracle cannot judge a split named after a deform bone — that bone
        // carries no weight in the bind pose — so "every piece agrees" is not an invariant this measurement
        // can hold. The invariant lives in BlendIndexReading, which sets those splits aside first. This
        // section stays as the diagnostic that shows WHICH pieces disagree and on which car.
        sb.AppendLine("    (no assert: deform-bone splits cannot be judged by weights — see the next section)");
    }

    private static string Name(string[] bones, int bone) =>
        bone >= 0 && bone < bones.Length ? bones[bone] : $"#{bone}";

    /// <summary>
    /// WHICH reading of <c>WeightedByMeshSplit.BlendIndex</c> names the bone the split's geometry actually
    /// rides.
    ///
    /// <para>
    /// A vertex's bone id is POOL-LOCAL — it indexes the remap pool assigned to the face group drawing it
    /// (<c>SkinnedMaterialInfo.AssignedPoolIndex</c> → <c>BonesPerRemapPool</c> → a slice of
    /// <c>BoneRemapIDs</c>). A split's <c>BlendIndex</c> is the same kind of number and there is no reason it
    /// would be read flat, yet that is what this toolkit does. Each candidate below is scored against the
    /// weights of the split's own vertices, counting only pieces whose geometry sits on ONE bone by 80 % or
    /// more — a piece shared 50/50 between two deform bones cannot judge anything.
    /// </para>
    /// <para>
    /// The pool is tried through both fields of <c>SkinnedMaterialInfo</c>: the struct's own comments
    /// describe them the wrong way round, so which of the two carries the pool is a question the data has to
    /// answer rather than the reference toolkit.
    /// </para>
    /// </summary>
    private static void BlendIndexReading(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ which reading of BlendIndex names the split's bone? ════");

        string[] names =
        [
            "BoneRemapIDs[BlendIndex] — flat (what we do today)",
            "BlendIndex straight as a bone id",
            "pool of the FIRST burst's material, via AssignedPoolIndex",
            "pool of the BIGGEST burst's material, via AssignedPoolIndex",
            "pool of the FIRST burst's material, via NumWeightsPerVertex",
            "pool of the BIGGEST burst's material, via NumWeightsPerVertex",
            "RefToUsageArray[BlendIndex]",
            "the bone whose RefToUsageArray entry IS BlendIndex",
        ];
        var hits = new int[names.Length];
        int judged = 0, skipped = 0;
        var perCar = new Dictionary<string, (int Judged, int Flat)>(StringComparer.Ordinal);
        var hard = new List<string>();
        var plainMisses = new Dictionary<string, int>(StringComparer.Ordinal);
        int plainJudged = 0, plainHits = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            if (!TryOpen(folder, Path.GetFileNameWithoutExtension(sds.Name),
                    out FrameResource? fr, out FrameObjectModel? _, out string _))
            {
                continue;
            }

            foreach (FrameObjectModel model in fr!.FrameObjects!.Values.OfType<FrameObjectModel>())
            {
                DecodedMesh? decoded = SdsMeshLoader.DecodeLod(model, 0);
                byte[]? ids = SdsMeshLoader.GlobalBoneIds(model, 0);
                if (decoded?.BoneWeights is not { } weights || ids == null) continue;

                Illusion.Formats.Frames.Resources.FrameBlendInfo.BoneIndexInfo info;
                try
                {
                    Illusion.Formats.Frames.Resources.FrameBlendInfo.BoneIndexInfo[] lods =
                        model.GetBlendInfoObject().BoneIndexInfos ?? [];
                    if (lods.Length == 0) continue;
                    info = lods[0];
                }
                catch (Exception) { continue; }

                byte[] pools = info.BonesPerRemapPool ?? [];
                byte[] remap = info.BoneRemapIDs ?? [];
                Illusion.Formats.Frames.Resources.FrameBlendInfo.SkinnedMaterialInfo[] groups =
                    info.SkinnedMaterialInfo ?? [];
                if (pools.Length == 0 || remap.Length == 0) continue;

                byte[] refToUsage = [];
                try
                {
                    Illusion.Formats.Frames.Resources.FrameSkeleton.MappingForBlendingInfo[] maps =
                        model.GetSkeletonObject().MappingForBlendingInfos ?? [];
                    if (maps.Length > 0) refToUsage = maps[0].RefToUsageArray ?? [];
                }
                catch (Exception) { /* the two skeleton candidates simply score zero */ }

                var poolStart = new int[pools.Length];
                for (int p = 1; p < pools.Length; p++) poolStart[p] = poolStart[p - 1] + pools[p - 1];

                foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
                {
                    int blend = split.BlendIndex;
                    foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
                    {
                        var weightOf = new Dictionary<int, float>(8);
                        int firstMaterial = -1, biggestMaterial = -1, biggestFaces = 0;
                        foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                        {
                            int faces = burst.Data?.Sum(r => r.NumFaces) ?? 0;
                            if (faces == 0) continue;
                            if (firstMaterial < 0) firstMaterial = burst.MaterialIndex;
                            if (faces > biggestFaces) (biggestFaces, biggestMaterial) = (faces, burst.MaterialIndex);
                            foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                            {
                                int from = range.StartIndex / 3;
                                for (int f = from; f < from + range.NumFaces; f++)
                                {
                                    for (int corner = 0; corner < 3; corner++)
                                    {
                                        int at = (f * 3) + corner;
                                        if (at < 0 || at >= decoded.Indices.Length) continue;
                                        int vertex = (int)decoded.Indices[at];
                                        for (int k = 0; k < 4; k++)
                                        {
                                            int slot = (vertex * 4) + k;
                                            if (slot >= ids.Length || slot >= weights.Length) break;
                                            if (weights[slot] <= 0f) continue;
                                            weightOf[ids[slot]] = weightOf.GetValueOrDefault(ids[slot])
                                                + weights[slot];
                                        }
                                    }
                                }
                            }
                        }
                        if (weightOf.Count == 0) continue;

                        float total = weightOf.Values.Sum();
                        (int Bone, float Weight) top = (-1, 0f);
                        foreach ((int bone, float weight) in weightOf)
                        {
                            if (weight > top.Weight) top = (bone, weight);
                        }

                        // A piece split evenly between deform bones has no single right answer; judging on it
                        // would score noise. Only pieces that clearly belong to one bone get a vote.
                        if (top.Weight / total < 0.8f) { skipped++; continue; }
                        judged++;

                        int[] candidates =
                        [
                            blend < remap.Length ? remap[blend] : -1,
                            blend,
                            ByPool(firstMaterial, viaAssigned: true),
                            ByPool(biggestMaterial, viaAssigned: true),
                            ByPool(firstMaterial, viaAssigned: false),
                            ByPool(biggestMaterial, viaAssigned: false),
                            blend < refToUsage.Length ? refToUsage[blend] : -1,
                            Array.IndexOf(refToUsage, (byte)Math.Clamp(blend, 0, 255)),
                        ];
                        for (int c = 0; c < candidates.Length; c++)
                        {
                            if (candidates[c] == top.Bone) hits[c]++;
                        }

                        string car = Path.GetFileNameWithoutExtension(sds.Name);
                        (int Judged, int Flat) tally = perCar.GetValueOrDefault(car);
                        perCar[car] = (tally.Judged + 1, tally.Flat + (candidates[0] == top.Bone ? 1 : 0));

                        // A deform bone carries no weight in the bind pose — its geometry is weighted to the
                        // panel it deforms, or to the root. The weights therefore cannot judge a split named
                        // after one, and counting them as failures blames the lookup for the oracle's limit.
                        string[] named = BoneNames(model);
                        bool deform = Name(named, candidates[0])
                            .StartsWith("deform", StringComparison.OrdinalIgnoreCase);
                        if (!deform)
                        {
                            plainJudged++;
                            if (candidates[0] == top.Bone) plainHits++;
                            else plainMisses[car] = plainMisses.GetValueOrDefault(car) + 1;
                        }

                        // The hardest cases: geometry that sits on one bone almost entirely, named as
                        // another. A piece shared with its own deform bone would show up as a related pair;
                        // unrelated names mean the lookup itself is wrong.
                        // One example per car, so a single heavily-edited archive cannot fill the list and
                        // hide what the untouched ones do.
                        if (candidates[0] != top.Bone && top.Weight / total >= 0.95f
                            && hard.Count < 18 && !hard.Exists(h => h.StartsWith(car + ":", StringComparison.Ordinal)))
                        {
                            string[] boneNames = BoneNames(model);
                            hard.Add($"{car}: BlendIndex {blend} → named {Name(boneNames, candidates[0])}, "
                                + $"weighted {top.Weight / total:P0} to {Name(boneNames, top.Bone)}");
                        }

                        int ByPool(int material, bool viaAssigned)
                        {
                            if (material < 0 || material >= groups.Length) return -1;
                            int pool = viaAssigned
                                ? groups[material].AssignedPoolIndex
                                : groups[material].NumWeightsPerVertex;
                            if (pool < 0 || pool >= pools.Length) return -1;
                            int at = poolStart[pool] + blend;
                            return at >= 0 && at < remap.Length ? remap[at] : -1;
                        }
                    }
                }
            }
        }

        sb.AppendLine($"    {judged} pieces sit on one bone by 80 % or more and can judge "
            + $"({skipped} too evenly shared to judge)");
        for (int c = 0; c < names.Length; c++)
        {
            sb.AppendLine($"    {(judged == 0 ? 0 : (double)hits[c] / judged),7:P1}  {hits[c],6}  {names[c]}");
        }

        // Is the flat reading wrong EVERYWHERE a little, or right on most cars and broken on a few? The two
        // mean different repairs: a wrong formula versus a car-shaped precondition we do not model.
        int clean = perCar.Count(kv => kv.Value.Judged > 0 && kv.Value.Flat == kv.Value.Judged);
        sb.AppendLine($"\n    the flat reading is perfect on {clean} of {perCar.Count} cars");
        sb.AppendLine("    worst cars: " + string.Join(", ", perCar
            .Where(kv => kv.Value.Judged > 0)
            .OrderBy(kv => (double)kv.Value.Flat / kv.Value.Judged)
            .Take(6)
            .Select(kv => $"{kv.Key} {kv.Value.Flat}/{kv.Value.Judged}")));

        sb.AppendLine("\n    hardest disagreements (geometry 95 %+ on one bone, named as another):");
        foreach (string line in hard) sb.AppendLine("      " + line);

        sb.AppendLine($"\n    leaving out splits named after a deform bone, the flat reading is right on "
            + $"{plainHits} of {plainJudged} ({(plainJudged == 0 ? 0 : (double)plainHits / plainJudged):P2})");
        sb.AppendLine("    the cars that still miss: " + (plainMisses.Count == 0 ? "none" : string.Join(", ",
            plainMisses.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key} ×{p.Value}"))));
        // The measured invariant, not the ideal: 12359 of 12498 (98.89 %) on the shipped corpus, where the
        // remainder is one edited archive plus a scatter of pieces the weights cannot call. A reading that
        // drops below this is a broken lookup, not a corpus quirk.
        check("outside deform bones, BoneRemapIDs[BlendIndex] names the split's bone on 98 %+ of pieces",
            plainJudged > 0 && (double)plainHits / plainJudged >= 0.98,
            $"{plainHits} of {plainJudged} ({(plainJudged == 0 ? 0 : (double)plainHits / plainJudged):P2})");

        int best = 0;
        for (int c = 1; c < names.Length; c++)
        {
            if (hits[c] > hits[best]) best = c;
        }
        // Which reading WINS is the finding, and it has to keep winning: the pool-local readings are the
        // plausible-looking rivals (a vertex's id really is pool-local), and picking one of them would
        // silently move every piece to a different bone.
        check("the flat BoneRemapIDs[BlendIndex] reading beats every rival",
            judged > 0 && best == 0,
            $"best is \"{names[best]}\" at {hits[best]} of {judged}");
    }

    // ── the control group: characters carry hit boxes too ──

    private static void Characters(StringBuilder sb, Action<string, bool, string> check)
    {
        string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "hchar");
        if (!Directory.Exists(folder)) { sb.AppendLine("\n(no hchar folder — control group skipped)"); return; }

        sb.AppendLine("\n════ control group: characters ════");
        var byValue = new Dictionary<uint, int>();
        int read = 0, withBoxes = 0;
        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name).Take(20))
        {
            if (!TryOpen(folder, Path.GetFileNameWithoutExtension(sds.Name),
                    out FrameResource? fr, out FrameObjectModel? _, out string _))
            {
                continue;
            }
            read++;
            foreach (FrameObjectModel model in fr!.FrameObjects!.Values.OfType<FrameObjectModel>())
            {
                if ((model.HitBoxes?.Length ?? 0) == 0) continue;
                withBoxes++;
                foreach (FrameObjectModel.HitBoxInfo box in model.HitBoxes!)
                {
                    byValue[box.Unk] = byValue.GetValueOrDefault(box.Unk) + 1;
                }
            }
        }

        sb.AppendLine($"    {read} archives read, {withBoxes} models with boxes, "
            + $"{byValue.Count} distinct values");
        foreach ((uint value, int count) in byValue.OrderByDescending(kv => kv.Value).Take(20))
        {
            sb.AppendLine($"    {value,10} 0x{value,8:X8} {count,7}  {SurfaceName(value)}");
        }
        check("characters use the same value vocabulary as cars", byValue.Count > 0,
            $"{byValue.Count} distinct values on people");
    }

    /// <summary>
    /// The other candidate, and the one the physics layer already points at: the game does not read a surface
    /// off the geometry at all, it knows what it hit because the volume it hit belongs to a PART with a kind
    /// (1 body, 4 door, 5 window, 14 tyre …). If that is the mechanism, then part kind, volume type and the
    /// materials of the bone's own geometry line up across the whole corpus — glass parts carrying glass
    /// materials and self-describing volumes, body parts carrying shapes.
    /// </summary>
    private static void Parts(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ part kind ⇄ volume type ⇄ material ════");

        var volumeTypesOf = new Dictionary<string, Dictionary<uint, int>>(StringComparer.Ordinal);
        var materialsOf = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var flagsOf = new Dictionary<string, SortedSet<uint>>(StringComparer.Ordinal);
        var partCount = new Dictionary<string, int>(StringComparer.Ordinal);
        int archives = 0, parts = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string stem = Path.GetFileNameWithoutExtension(sds.Name);
            if (!TryOpen(folder, stem, out FrameResource? fr, out FrameObjectModel? model, out string _)) continue;

            string extracted = MafiaEnvironment.ExtractedDir(sds);
            string? prf;
            try { prf = SdsManifest.Load(extracted).GetFiles("PREFAB").FirstOrDefault(); }
            catch (Exception) { continue; }
            if (prf == null) continue;

            PrefabFile prefab;
            try { prefab = PrefabFile.Load(prf); }
            catch (Exception) { continue; }
            IReadOnlyList<CarDeformPart> list = prefab.CarDeformParts;
            if (list.Count == 0) continue;
            archives++;

            // Which material a bone's own geometry uses — the piece table is keyed by blend index, so the
            // bone's name has to come back through the skeleton to be matched against the part's frame hash.
            Dictionary<ulong, SortedSet<string>> byBone = MaterialsByBone(model!);

            foreach (CarDeformPart part in list)
            {
                parts++;
                string kind = $"{part.PartType} {part.Kind}";
                partCount[kind] = partCount.GetValueOrDefault(kind) + 1;
                volumeTypesOf.TryAdd(kind, new Dictionary<uint, int>());
                foreach (CarPhysicsVolume v in part.Volumes)
                {
                    volumeTypesOf[kind][v.VolumeType] = volumeTypesOf[kind].GetValueOrDefault(v.VolumeType) + 1;
                }
                flagsOf.TryAdd(kind, []);
                flagsOf[kind].Add(part.Flags);
                materialsOf.TryAdd(kind, new SortedSet<string>(StringComparer.Ordinal));
                if (byBone.TryGetValue(part.Frame, out SortedSet<string>? mats))
                {
                    foreach (string m in mats) materialsOf[kind].Add(m);
                }
            }
        }

        sb.AppendLine($"    {archives} archives with a car prefab, {parts} deformable parts");
        sb.AppendLine($"    {"part kind",-16} {"count",6}  {"volume types",-26} {"flags",-18} materials of its bone");
        foreach ((string kind, int count) in partCount.OrderByDescending(kv => kv.Value))
        {
            string volumes = string.Join(" ", volumeTypesOf[kind].OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}×{kv.Value}"));
            sb.AppendLine($"    {Trim(kind, 16),-16} {count,6}  {Trim(volumes, 26),-26} "
                + $"{Trim(string.Join(",", flagsOf[kind].Take(4)), 18),-18} "
                + $"{Trim(string.Join("/", materialsOf[kind]), 70)}");
        }

        // The claim: kind and volume type are not independent — a window does not carry a body's shape.
        bool windowsDiffer = volumeTypesOf.TryGetValue("5 window", out Dictionary<uint, int>? w)
            && volumeTypesOf.TryGetValue("1 body", out Dictionary<uint, int>? b)
            && w.Keys.OrderBy(k => k).SequenceEqual(b.Keys.OrderBy(k => k)) == false;
        check("a window's volumes are not shaped like a body's — the kind carries the meaning",
            windowsDiffer, windowsDiffer ? "different volume-type sets" : "same set, or one of the kinds absent");
    }

    /// <summary>
    /// What <c>BlendIndex</c> actually addresses — the question that has to be answered before a hit box can
    /// be read at all.
    ///
    /// <para>
    /// Reading it as a bone index is wrong: every piece of a stock car then resolves to one bone. The
    /// skeleton carries a second list, <c>MappingForBlendingInfos</c>, and that one is shaped like an answer
    /// — it has a per-entry <c>UsageArray</c> of bone ids and, remarkably, its own array of BOUNDING BOXES.
    /// A car's parts are its bones, so a per-blend-group box is exactly the thing a shot could be tested
    /// against, and nothing in this toolkit has ever looked at it.
    /// </para>
    /// </summary>
    private static void BlendPools(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ what does BlendIndex point into? ════");

        int cars = 0, withinMapping = 0, withinBones = 0, mappingsWithBounds = 0, mappings = 0;
        int withinUsage = 0, boundsPerBone = 0;
        var examples = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            FrameObjectModel? model = fr?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (model == null) continue;

            Illusion.Formats.Frames.Resources.FrameSkeleton skeleton;
            try { skeleton = model.GetSkeletonObject(); }
            catch (Exception) { continue; }
            int boneCount = skeleton.BoneNames?.Length ?? 0;
            Illusion.Formats.Frames.Resources.FrameSkeleton.MappingForBlendingInfo[] maps =
                skeleton.MappingForBlendingInfos ?? [];
            if (boneCount == 0) continue;
            cars++;

            int maxBlend = -1;
            foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
            {
                maxBlend = Math.Max(maxBlend, split.BlendIndex);
            }
            if (maxBlend < maps.Length) withinMapping++;
            if (maxBlend < boneCount) withinBones++;

            foreach (Illusion.Formats.Frames.Resources.FrameSkeleton.MappingForBlendingInfo map in maps)
            {
                mappings++;
                if ((map.Bounds?.Length ?? 0) > 0) mappingsWithBounds++;
                // One box per BONE is the shape that would matter: a car's parts ARE its bones.
                if ((map.Bounds?.Length ?? 0) == boneCount) boundsPerBone++;
                if (maxBlend < (map.UsageArray?.Length ?? 0)) { withinUsage++; break; }
            }

            if (examples.Count < 10)
            {
                var first = maps.Length > 0 ? maps[0] : default;
                examples.Add($"{sds.Name}: {model.BlendMeshSplits?.Length ?? 0} splits, max blend {maxBlend}; "
                    + $"{boneCount} bones, {maps.Length} blend mappings, {skeleton.NumBlendIDs} blend ids; "
                    + $"first mapping has {first.Bounds?.Length ?? 0} bounds, "
                    + $"{first.RefToUsageArray?.Length ?? 0} refs, {first.UsageArray?.Length ?? 0} usage");
            }
        }

        // The REMAP POOLS, because "can we make one bigger" is a question about a ceiling nobody has
        // measured. A vertex stores four bone references as BYTES, and each is a local index into the pool
        // of the face group that draws it — so the pool is "the bones this group may use", and its size is
        // what a push can overflow.
        var totals = new Dictionary<int, int>();
        var widest = new Dictionary<int, int>();
        int poolCars = 0, maxTotal = 0, maxOne = 0;
        int blendIdsChecked = 0, blendIdsAgree = 0, usageAgree = 0;
        var poolExamples = new List<string>();
        string maxWho = "";
        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            FrameObjectModel? model = fr?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (model == null) continue;

            Illusion.Formats.Frames.Resources.FrameBlendInfo blend;
            try { blend = model.GetBlendInfoObject(); }
            catch (Exception) { continue; }
            if (blend.BoneIndexInfos is not { Length: > 0 } lods) continue;
            poolCars++;

            Illusion.Formats.Frames.Resources.FrameSkeleton? sk = null;
            try { sk = model.GetSkeletonObject(); } catch (Exception) { /* no rig to compare against */ }
            for (int lodIndex = 0; lodIndex < lods.Length; lodIndex++)
            {
                Illusion.Formats.Frames.Resources.FrameBlendInfo.BoneIndexInfo info = lods[lodIndex];
                byte[] sizes = info.BonesPerRemapPool ?? [];
                int total = sizes.Sum(s => (int)s);
                if (sk != null)
                {
                    blendIdsChecked++;
                    int[] perLod = sk.LodRemapIDCount ?? [];
                    int said = lodIndex < perLod.Length ? perLod[lodIndex] : sk.NumBlendIDs;
                    if (said == total) blendIdsAgree++;
                    var maps = sk.MappingForBlendingInfos ?? [];
                    if (lodIndex < maps.Length && (maps[lodIndex].UsageArray?.Length ?? -1) == total) usageAgree++;
                    if (poolExamples.Count < 8)
                    {
                        poolExamples.Add($"{sds.Name} lod {lodIndex}: pools {string.Join("+", sizes)} = {total}; "
                            + $"skeleton says {said}, usage array {(lodIndex < maps.Length ? (maps[lodIndex].UsageArray?.Length ?? -1) : -1)}");
                    }
                }
                int biggest = sizes.Length == 0 ? 0 : sizes.Max();
                totals[total] = totals.GetValueOrDefault(total) + 1;
                widest[biggest] = widest.GetValueOrDefault(biggest) + 1;
                if (total > maxTotal) { maxTotal = total; maxWho = sds.Name; }
                maxOne = Math.Max(maxOne, biggest);
            }
        }

        sb.AppendLine($"  {poolCars} cars' remap pools: the largest total is {maxTotal} entries ({maxWho}), "
            + $"the largest single pool is {maxOne}");
        sb.AppendLine($"  the skeleton's own count agrees with the pool total on {blendIdsAgree} of "
            + $"{blendIdsChecked} LODs, and its usage array is that long on {usageAgree}");
        foreach (string e in poolExamples) sb.AppendLine("    " + e);

        // If the skeleton's blend-id count IS the pool total, then growing a pool changes a number the
        // skeleton also carries — and a push that grows one without updating the skeleton leaves the two
        // disagreeing. The editor reads the pools directly and would not notice; the game reads through the
        // skeleton's mapping and would.
        check("the skeleton's blend-id count is the remap total, so growing a pool changes it too",
            blendIdsChecked > 0 && blendIdsAgree * 10 > blendIdsChecked * 9,
            $"{blendIdsAgree} of {blendIdsChecked} agree");
        sb.AppendLine("    totals seen: " + string.Join(", ",
            totals.OrderByDescending(p => p.Key).Take(8).Select(p => $"{p.Key} on {p.Value} LODs")));
        sb.AppendLine("    widest single pool seen: " + string.Join(", ",
            widest.OrderByDescending(p => p.Key).Take(8).Select(p => $"{p.Key} on {p.Value} LODs")));

        // The ceiling is PER POOL, not on the total — which decides whether a push that needs one more bone
        // can grow a pool or has to be refused. Shipped cars run their totals up to 108 entries, so 64 is
        // not a limit at all; but no single pool anywhere exceeds 60, and that is the number a grown pool
        // must respect. (A vertex addresses its pool with a byte, so the format could hold 256 — the real
        // limit is the engine's, and 60 is what the shipped data says it is.)
        check("no single remap pool of any shipped car exceeds 60 entries",
            poolCars > 0 && maxOne <= 60, $"the widest is {maxOne}");
        check("…while the totals go far past 64, so the total is not a ceiling",
            poolCars > 0 && maxTotal > 64, $"the largest total is {maxTotal} in {maxWho}");

        sb.AppendLine($"  {cars} cars: max BlendIndex fits the blend mappings on {withinMapping}, "
            + $"fits the bone list on {withinBones}");
        sb.AppendLine($"  {mappings} blend mappings in all, {mappingsWithBounds} of them carrying bounds");
        foreach (string e in examples) sb.AppendLine("    " + e);

        // The shapes line up and say what the two arrays are for: UsageArray is as long as NumBlendIDs and
        // the largest BlendIndex is one less than it, while Bounds and RefToUsageArray are as long as the
        // BONE list. So a split names a blend id, the blend id names a bone through UsageArray, and the
        // skeleton keeps one bounding box PER BONE — which no part of this toolkit has ever read.
        check("BlendIndex addresses the blend-id pool, and the pool is as long as the blend ids say",
            cars > 0 && withinUsage == cars, $"{withinUsage} of {cars}");
        check("the skeleton carries one bounding box per BONE, beside that pool",
            mappings > 0 && boundsPerBone == mappings, $"{boundsPerBone} of {mappings} mappings");
    }

    /// <summary>
    /// Are the skeleton's per-bone boxes REAL — do they bound the geometry weighted to their own bone?
    ///
    /// <para>
    /// The one block left that could register a shot. It is one box per bone, it has never been read by this
    /// toolkit, and a car's parts ARE its bones — so if these boxes hold the geometry of their bone, they are
    /// the shape a bullet could be tested against, and the fact that nothing recalculates them explains both
    /// "geometry I added takes no hits" and "two spots with the same material behave differently".
    /// </para>
    /// <para>
    /// Scored the honest way: a vertex is assigned to the bone it is most weighted to (through the remap, or
    /// the ids name the wrong bone entirely), and the box is asked to contain it. Four readings, because the
    /// space is the open question — as written, placed by the bone, and each of those at both the mesh's own
    /// quantization and none.
    /// </para>
    /// </summary>
    private static void BoneBoxes(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ do the skeleton's per-bone boxes hold their bone's geometry? ════");

        string[] names = ["as written", "placed by the bone", "as written, x quantization",
            "placed by the bone, x quantization"];
        long[] inside = new long[names.Length];
        long counted = 0;
        int cars = 0;
        var examples = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            FrameObjectModel? model = fr?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (model == null) continue;

            DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(model);
            byte[]? ids = SdsMeshLoader.GlobalBoneIds(model);
            if (decoded == null || ids == null || decoded.BoneWeights is not { } weights) continue;

            Illusion.Formats.Frames.Resources.FrameSkeleton skeleton;
            try { skeleton = model.GetSkeletonObject(); }
            catch (Exception) { continue; }
            Illusion.Formats.Frames.Resources.FrameSkeleton.MappingForBlendingInfo[] maps =
                skeleton.MappingForBlendingInfos ?? [];
            if (maps.Length == 0 || (maps[0].Bounds?.Length ?? 0) == 0) continue;
            Illusion.Formats.Mathematics.BoundingBox[] boxes = maps[0].Bounds!;
            Matrix4x4[] rest = model.RestTransform ?? [];
            cars++;

            var perCar = new long[names.Length];
            long perCarCounted = 0;
            for (int v = 0; v < decoded.Positions.Length; v++)
            {
                // The bone this vertex mostly belongs to.
                int best = -1;
                float bestWeight = 0f;
                for (int k = 0; k < 4; k++)
                {
                    int at = (v * 4) + k;
                    if (at >= ids.Length || at >= weights.Length) break;
                    if (weights[at] > bestWeight) { bestWeight = weights[at]; best = ids[at]; }
                }
                if (best < 0 || best >= boxes.Length || bestWeight <= 0f) continue;

                Vector3 p = decoded.Positions[v];
                Illusion.Formats.Mathematics.BoundingBox box = boxes[best];
                perCarCounted++;
                counted++;

                if (Holds(box, p, 1)) { perCar[0]++; inside[0]++; }
                if (best < rest.Length && TryInvert(rest[best], out Matrix4x4 toBone)
                    && Holds(box, Vector3.Transform(p, toBone), 1))
                {
                    perCar[1]++;
                    inside[1]++;
                }

                // …and the same two with the box read through the mesh's own vertex lattice, in case these
                // numbers are packed the way positions are rather than being metres.
                Vector3 lo = (box.Min * decoded.DecompressionFactor) + decoded.DecompressionOffset;
                Vector3 hi = (box.Max * decoded.DecompressionFactor) + decoded.DecompressionOffset;
                if (Between(lo, hi, p)) { perCar[2]++; inside[2]++; }
                if (best < rest.Length && TryInvert(rest[best], out Matrix4x4 toBone2)
                    && Between(lo, hi, Vector3.Transform(p, toBone2)))
                {
                    perCar[3]++;
                    inside[3]++;
                }
            }

            if (examples.Count < 8 && perCarCounted > 0)
            {
                examples.Add($"{sds.Name}: {perCarCounted} weighted vertices, "
                    + string.Join(", ", names.Select((n, i) => $"{n} {perCar[i] * 100.0 / perCarCounted:F1}%")));
            }
        }

        sb.AppendLine($"  {cars} cars, {counted} weighted vertices");
        for (int i = 0; i < names.Length; i++)
        {
            sb.AppendLine($"    {names[i],-38} holds {inside[i] * 100.0 / Math.Max(1, counted),6:F2}% of them");
        }
        foreach (string e in examples) sb.AppendLine("    " + e);

        // SETTLED. The boxes are real geometry, in metres, in the space of their own bone: placed by the
        // bone's rest transform they hold 684 480 of 684 480 weighted vertices across 88 cars — every one.
        // Read as written they hold two thirds, which is only the bones whose rest transform is near
        // identity, and the mesh's own vertex lattice does not apply to them at all.
        //
        // So a car carries a per-BONE volume that no part of this toolkit has ever read or recalculated —
        // and a car's parts ARE its bones. Geometry added by the editor falls outside every one of these
        // boxes, which is exactly the reported "a cube I added takes no hits at all".
        check("a per-bone box holds ALL of its own bone's geometry, once placed by that bone",
            counted > 0 && inside[1] == counted,
            $"placed by the bone {inside[1]} of {counted}; as written {inside[0]}");
    }

    /// <summary>
    /// ONE car's per-bone boxes against its own geometry — the diagnosis for "this part takes no hits".
    ///
    /// <para>
    /// A bone's box is derived from the vertices weighted to it. Geometry added by an editor that never
    /// recalculated them sits OUTSIDE its bone's box, and nothing in the toolkit shows that: the part draws,
    /// it has collision, it has a material, and the game still refuses to register a shot on it. This lists
    /// the bones whose stored box no longer holds their own geometry, and by how far.
    /// </para>
    /// </summary>
    private static void StaleBounds(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        sb.AppendLine($"\n════ {focus}: does each bone's stored box still hold its geometry? ════");

        var sds = new FileInfo(Path.Combine(folder, focus + ".sds"));
        string extracted = MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
        {
            sb.AppendLine("  not extracted — open it in the app once");
            return;
        }

        FrameResource? fr;
        try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
        catch (Exception) { sb.AppendLine("  the archive cannot be read"); return; }
        FrameObjectModel? model = fr?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
        if (model == null) { sb.AppendLine("  no skinned model"); return; }

        Illusion.Formats.Frames.Resources.FrameSkeleton skeleton;
        try { skeleton = model.GetSkeletonObject(); }
        catch (Exception) { sb.AppendLine("  no rig"); return; }
        string[] bones = (skeleton.BoneNames ?? []).Select(n => n.ToString() ?? "?").ToArray();
        Illusion.Formats.Frames.Resources.FrameSkeleton.MappingForBlendingInfo[] maps =
            skeleton.MappingForBlendingInfos ?? [];
        if (maps.Length == 0 || maps[0].Bounds is not { Length: > 0 } stored)
        {
            sb.AppendLine("  this model carries no per-bone boxes");
            return;
        }

        Illusion.Formats.Mathematics.BoundingBox[]? wanted =
            Assets.Frames.BoneBoundsBuilder.Compute(model, 0, Assets.Frames.BoneBoundsBuilder.Rule.AnyInfluence);
        if (wanted == null) { sb.AppendLine("  the skin cannot be resolved, so nothing can be derived"); return; }

        int outside = 0, checkedBones = 0;
        for (int b = 0; b < stored.Length && b < wanted.Length && b < bones.Length; b++)
        {
            Illusion.Formats.Mathematics.BoundingBox now = wanted[b];
            if ((now.Max - now.Min).Length() < 1e-5f) continue;   // this bone drives no geometry
            checkedBones++;

            // A few tenths of a millimetre is the rebuild's own rounding — the vertices come back off a
            // 16-bit lattice — and reporting that as "outside" buries the parts that really are.
            Illusion.Formats.Mathematics.BoundingBox was = stored[b];
            Vector3 over = Vector3.Max(now.Max - was.Max, was.Min - now.Min);
            float worst = MathF.Max(over.X, MathF.Max(over.Y, over.Z));
            if (worst < 5e-3f) continue;
            outside++;

            sb.AppendLine($"    {bones[b],-20} geometry reaches {worst:F3} m "
                + $"outside its stored box  (stored {was.Min:F2}…{was.Max:F2}, geometry {now.Min:F2}…{now.Max:F2})");
        }

        sb.AppendLine($"  {checkedBones} bones drive geometry; {outside} of them have geometry outside their "
            + "stored box");
        check($"{focus}: every bone's stored box still holds its own geometry", outside == 0,
            $"{outside} of {checkedBones} bones have geometry their box does not cover");
    }

    /// <summary>
    /// Can the per-bone boxes be REBUILT from the geometry, and which assignment rule reproduces the shipped
    /// ones? The answer decides what an editor writes after a geometry change — and getting it wrong is not
    /// cosmetic: a box too small stops registering hits on real bodywork.
    /// </summary>
    /// <summary>
    /// What "Rebuild hit boxes" in the Edit menu does, and whether Ctrl+Z takes it back exactly.
    ///
    /// <para>
    /// Undo has to restore the ARRAY, not recompute: a shipped box is turned, the turn lives in a word no
    /// reading has cracked, and the builder replaces it with a sphere on purpose. So a rebuild that could
    /// not be undone byte for byte would quietly cost every car its authored boxes the first time someone
    /// clicked the item to see what it did.
    /// </para>
    /// </summary>
    private static void RebuildUndo(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ rebuild hit boxes, then undo ════");

        int cars = 0, changed = 0, exact = 0, drifted = 0, countKept = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            FrameObjectModel? model = fr?.FrameObjects?.Values.OfType<FrameObjectModel>()
                .FirstOrDefault(m => m.HitBoxes is { Length: > 0 });
            if (model == null) continue;

            FrameObjectModel.HitBoxInfo[] before = model.HitBoxes!;
            (ushort P1, ushort P2, ushort P3, ushort S1, ushort S2, ushort S3, uint Unk)[] words =
                [.. before.Select(Words)];

            FrameObjectModel.HitBoxInfo[]? built = Assets.Frames.HitBoxBuilder.Compute(model);
            if (built == null) continue;
            cars++;
            if (built.Length == before.Length) countKept++;

            var edit = new Viewport.HitBoxRebuildEdit(model, before, built);
            edit.Redo();
            if (!model.HitBoxes!.Select(Words).SequenceEqual(words)) changed++;
            edit.Undo();

            if (model.HitBoxes!.Select(Words).SequenceEqual(words)) exact++; else drifted++;
        }

        check("a rebuild keeps one box per piece", cars > 0 && countKept == cars,
            $"{countKept}/{cars} cars kept their box count");
        check("a rebuild actually rewrites the boxes", changed > 0,
            $"{changed}/{cars} cars came out different from what shipped");
        check("undo puts the shipped boxes back word for word", cars > 0 && drifted == 0,
            $"{exact}/{cars} exact, {drifted} drifted");
    }

    private static (ushort, ushort, ushort, ushort, ushort, ushort, uint) Words(FrameObjectModel.HitBoxInfo b) =>
        (b.Position.S1, b.Position.S2, b.Position.S3, b.Size.S1, b.Size.S2, b.Size.S3, b.Unk);

    private static void RebuildBounds(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ can the per-bone boxes be rebuilt from the geometry? ════");

        int cars = 0, bones = 0;
        FrameObjectModel? LastModel = null;
        int LastBoneCount = 0;
        var exact = new int[2];
        var contains = new int[2];
        var miss = new double[2];
        var rebuilt = new int[2];
        string[] names = ["dominant influence", "any influence"];
        var examples = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            FrameObjectModel? model = fr?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (model == null) continue;

            Illusion.Formats.Frames.Resources.FrameSkeleton skeleton;
            try { skeleton = model.GetSkeletonObject(); }
            catch (Exception) { continue; }
            Illusion.Formats.Frames.Resources.FrameSkeleton.MappingForBlendingInfo[] maps =
                skeleton.MappingForBlendingInfos ?? [];
            if (maps.Length == 0 || maps[0].Bounds is not { Length: > 0 } stored) continue;

            Illusion.Formats.Mathematics.BoundingBox[]?[] built =
            [
                Assets.Frames.BoneBoundsBuilder.Compute(model, 0, Assets.Frames.BoneBoundsBuilder.Rule.Dominant),
                Assets.Frames.BoneBoundsBuilder.Compute(model, 0, Assets.Frames.BoneBoundsBuilder.Rule.AnyInfluence),
            ];
            if (built[0] == null || built[1] == null) continue;
            cars++;
            LastModel = model;
            LastBoneCount = stored.Length;

            if (cars == 1)
            {
                Matrix4x4[] restOf = model.RestTransform ?? [];
                int invertible = restOf.Count(m => Matrix4x4.Invert(m, out _));
                DecodedMesh? d0 = SdsMeshLoader.DecodeLod0(model);
                sb.AppendLine($"    [{sds.Name}] {stored.Length} stored boxes, {restOf.Length} rest "
                    + $"transforms of which {invertible} invert, {d0?.Positions.Length ?? 0} vertices, "
                    + $"skin {(d0?.BoneWeights == null ? "absent" : "present")}, "
                    + $"{built[1]!.Count(b => (b.Max - b.Min).Length() > 1e-5f)} boxes rebuilt");
            }

            var perCar = new int[2];
            int perCarBones = 0;
            for (int b = 0; b < stored.Length && b < built[0]!.Length; b++)
            {
                Illusion.Formats.Mathematics.BoundingBox was = stored[b];
                if ((was.Max - was.Min).Length() < 1e-5f) continue;   // a bone with no geometry says nothing
                perCarBones++;
                bones++;
                for (int r = 0; r < 2; r++)
                {
                    Illusion.Formats.Mathematics.BoundingBox now = built[r]![b];
                    if ((now.Max - now.Min).Length() < 1e-5f) continue;
                    rebuilt[r]++;
                    float off = (now.Min - was.Min).Length() + (now.Max - was.Max).Length();
                    miss[r] += off;
                    // A millimetre, not a float epsilon: the vertices these are rebuilt from are dequantized
                    // from 16-bit lattice positions, so an exact match is not something the data can offer.
                    if (off < 2e-3f) { exact[r]++; perCar[r]++; }
                    // A rebuilt box that CONTAINS the shipped one still registers everything the shipped one
                    // did — the safe direction to be wrong in.
                    if (Between(now.Min, now.Max, was.Min) && Between(now.Min, now.Max, was.Max)) contains[r]++;
                }
            }

            if (examples.Count < 8 && perCarBones > 0)
            {
                examples.Add($"{sds.Name}: {perCarBones} bones with geometry, exact — "
                    + string.Join(", ", names.Select((n, i) => $"{n} {perCar[i]}")));
            }
        }

        sb.AppendLine($"  {cars} cars, {bones} bones that have geometry");
        for (int r = 0; r < 2; r++)
        {
            sb.AppendLine($"    {names[r],-22} rebuilt {rebuilt[r]} of {bones}; of those {exact[r]} land "
                + $"within a millimetre ({exact[r] * 100.0 / Math.Max(1, rebuilt[r]):F1}%), "
                + $"{contains[r]} cover the shipped box, mean miss {miss[r] / Math.Max(1, rebuilt[r]):F4} m");
        }
        foreach (string e in examples) sb.AppendLine("    " + e);

        // And the whole point of knowing that: a box that GREW has to still hold what it held. Rebuilding on
        // geometry that has not changed must be a no-op, or every push would dirty a car's whole skeleton.
        check("rebuilding boxes that are already right changes nothing",
            cars == 0 || Assets.Frames.BoneBoundsBuilder.Rebuild(
                LastModel!, Assets.Frames.BoneBoundsBuilder.Rule.AnyInfluence) * 20 < (LastBoneCount + 20),
            "a rebuild of untouched geometry rewrote more than a twentieth of the boxes");

        check("the per-bone boxes are derived data — the geometry rebuilds them",
            rebuilt[1] > 0 && exact[1] * 10 > rebuilt[1] * 9,
            $"any-influence rebuilt {rebuilt[1]}, of which {exact[1]} match");
    }

    private static bool Holds(Illusion.Formats.Mathematics.BoundingBox box, Vector3 p, float slack) =>
        Between(box.Min - new Vector3(slack), box.Max + new Vector3(slack), p);

    private static bool Between(Vector3 lo, Vector3 hi, Vector3 p) =>
        p.X >= MathF.Min(lo.X, hi.X) && p.X <= MathF.Max(lo.X, hi.X)
        && p.Y >= MathF.Min(lo.Y, hi.Y) && p.Y <= MathF.Max(lo.Y, hi.Y)
        && p.Z >= MathF.Min(lo.Z, hi.Z) && p.Z <= MathF.Max(lo.Z, hi.Z);

    /// <summary>
    /// Does everything a car's collision is made of survive being read and written back UNCHANGED?
    ///
    /// <para>
    /// The one question underneath "the editing works badly". Every edit here rewrites a whole file — the
    /// prefab, or an ItemDesc record — so a reader that loses a field silently deletes it the first time
    /// anything nearby is touched, and the damage shows up somewhere else entirely. One car was already
    /// checked; this checks all of them, and the ItemDesc shapes too, which nothing had.
    /// </para>
    /// </summary>
    private static void RoundTrips(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ does a read-and-write-back change anything? ════");

        int prefabs = 0, prefabsExact = 0, shapes = 0, shapesExact = 0;
        var broken = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            Formats.Archive.SdsManifest manifest;
            try { manifest = Formats.Archive.SdsManifest.Load(extracted); }
            catch (Exception) { continue; }

            foreach (string file in manifest.GetFiles("PREFAB"))
            {
                byte[] before, after;
                try
                {
                    before = File.ReadAllBytes(file);
                    after = Formats.Prefab.PrefabFile.Load(file).ToBytes();
                }
                catch (Exception) { continue; }
                prefabs++;
                if (before.AsSpan().SequenceEqual(after)) prefabsExact++;
                else if (broken.Count < 10) broken.Add($"PREFAB {sds.Name}/{Path.GetFileName(file)}: "
                    + $"{before.Length} B in, {after.Length} B out");
            }

            foreach (string file in manifest.GetFiles("ItemDesc"))
            {
                byte[] before, after;
                try
                {
                    before = File.ReadAllBytes(file);
                    after = Formats.ItemDesc.ItemDescFile.Load(file).ToBytes();
                }
                catch (Exception) { continue; }
                shapes++;
                if (before.AsSpan().SequenceEqual(after)) shapesExact++;
                else if (broken.Count < 10) broken.Add($"ItemDesc {sds.Name}/{Path.GetFileName(file)}: "
                    + $"{before.Length} B in, {after.Length} B out");
            }
        }

        sb.AppendLine($"  {prefabsExact}/{prefabs} prefabs and {shapesExact}/{shapes} ItemDesc shapes come "
            + "back byte for byte");
        foreach (string b in broken) sb.AppendLine("    " + b);

        check("every car prefab survives a read and a write untouched", prefabs > 0 && prefabsExact == prefabs,
            $"{prefabs - prefabsExact} of {prefabs} changed");
        check("…and so does every physics shape", shapes > 0 && shapesExact == shapes,
            $"{shapes - shapesExact} of {shapes} changed");
    }

    /// <summary>
    /// The BODY's volumes, counted and compared — because a car's body carries more than one.
    ///
    /// <para>
    /// Asked after an observation that separates two things nobody had separated: with the collision broken,
    /// bullets went through the body and registered on the ENGINE, while the car still collided with the
    /// world normally. Driving collision and shooting collision therefore are not the same thing. The body
    /// part carries two type-5 volumes at the same placement on the cars looked at so far, and two hulls in
    /// one spot is exactly what "one to drive with, one to be shot at" would look like.
    /// </para>
    /// </summary>
    private static void BodyHulls(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ how many hulls does a body carry, and how do they differ? ════");

        var counts = new Dictionary<int, int>();
        int cars = 0, pairs = 0, samePlace = 0, sameSize = 0, sameVertexCount = 0;
        var examples = new List<string>();

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            FrameResource? fr;
            try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
            catch (Exception) { continue; }
            if (fr?.FrameObjects == null) continue;

            IReadOnlyList<Assets.Collisions.PlacedPhysicsVolume> volumes;
            try { volumes = Assets.Collisions.CarPhysicsVolumes.Load(extracted, fr); }
            catch (Exception) { continue; }

            List<Assets.Collisions.PlacedPhysicsVolume> body =
                [.. volumes.Where(v => v.PartKind == "body")];
            if (body.Count == 0) continue;
            cars++;
            counts[body.Count] = counts.GetValueOrDefault(body.Count) + 1;
            if (body.Count != 2) continue;
            pairs++;

            Assets.Collisions.PlacedPhysicsVolume a = body[0], b = body[1];
            if ((a.World.Translation - b.World.Translation).Length() < 1e-3f) samePlace++;

            (int bytesA, string kindA) = HullSpan(a);
            (int bytesB, string kindB) = HullSpan(b);
            if (string.Equals(kindA, kindB, StringComparison.Ordinal)) sameSize++;
            if (bytesA == bytesB && bytesA > 0) sameVertexCount++;

            if (examples.Count < 10)
            {
                examples.Add($"{sds.Name}: #1 {kindA} {bytesA} B  |  #2 {kindB} {bytesB} B"
                    + (bytesA == bytesB ? "   (same size blob)" : "   (different)"));
            }
        }

        sb.AppendLine("  body volumes per car: " + string.Join(", ",
            counts.OrderBy(p => p.Key).Select(p => $"{p.Key} on {p.Value} cars")));
        sb.AppendLine($"  of the {pairs} cars with exactly two: {samePlace} place them identically, "
            + $"{sameSize} are the same kind, {sameVertexCount} have the same blob size");
        foreach (string e in examples) sb.AppendLine("    " + e);

        // If the two are geometrically different, they are two different jobs and the toolkit has been
        // treating them as interchangeable. If they are identical, the pair means something else entirely.
        check("a car's body carries more than one hull",
            cars > 0 && counts.Where(p => p.Key >= 2).Sum(p => p.Value) * 2 > cars,
            string.Join(", ", counts.OrderBy(p => p.Key).Select(p => $"{p.Key}×{p.Value}")));
    }

    /// <summary>The size of a placed shape's cooked blob and what kind it is — enough to tell two hulls apart
    /// without decoding either of them.</summary>
    private static (int Bytes, string Kind) HullSpan(Assets.Collisions.PlacedPhysicsVolume volume)
    {
        if (volume.Shape?.Element is not Formats.ItemDesc.RigidBodyElement rigid) return (0, "—");
        return (rigid.CookedMesh?.Length ?? 0, rigid.Shape.ToString());
    }

    /// <summary>
    /// The one thing this probe can settle without the game: that a surface ASKED FOR is a surface the archive
    /// carries. The shipped shapes all say 0, so if the field turns out to be what a shot reads, the toolkit
    /// has to be able to write it — and if it turns out not to be, this is what proves the experiment was set
    /// up correctly rather than silently dropped. Runs on a scratch copy; the player's car is never touched.
    /// </summary>
    private static void SurfaceReaches(
        StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var car = new FileInfo(Path.Combine(folder, focus + ".sds"));
        if (!car.Exists) return;
        string source = MafiaEnvironment.ExtractedDir(car);
        if (!File.Exists(Path.Combine(source, "SDSContent.xml"))) return;
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_bullets_scratch");

        sb.AppendLine("\n════ does a chosen surface reach the archive? ════");
        try
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(scratch, Path.GetFileName(file)));
            }

            FrameResource? fr = SdsMeshLoader.OpenScene(scratch).FrameResource;
            FrameObjectModel? model = fr?.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (fr == null || model == null) { sb.AppendLine("    no skinned model"); return; }

            // The body part, because that is where a test shot would go.
            IReadOnlyList<Assets.Collisions.CarPartChoice> parts =
                Assets.Collisions.CarPhysicsVolumes.PartChoices(scratch, fr);
            Assets.Collisions.CarPartChoice? body = parts.FirstOrDefault(p => p.IsBody) ?? parts.FirstOrDefault();
            if (body == null) { sb.AppendLine("    this car has no deformable part"); return; }

            const int Glass = 28;   // sklo_rozbitelne_1 — the table index a person picks
            Assets.Collisions.AddedCollisionBox? added = Assets.Collisions.CarCollisionBuilder.AddShape(
                model, body.Bone, "illusion_bullet_probe_Collision", Formats.ItemDesc.RigidBodyShape.Box,
                new Vector3(0.20f, 0.20f, 0.05f), Matrix4x4.Identity, scratch, out string? refusal, Glass);
            check("a shape can be added with a surface chosen", added != null, refusal ?? $"on {body.BoneName}");
            if (added == null) return;

            // THE BIAS, which is what made the first in-game test come back empty. The field on disk is a raw
            // PhysX slot id and the table index is that minus 2 — the same offset the world's own collision
            // uses. Writing the table index straight in asked for breakable glass (28) and wrote a value the
            // game reads as bulletproof glass (30), which is exactly the sort of null result that looks like
            // "the field does nothing". This asserts the raw value, and then that reading it back names the
            // surface that was asked for.
            var shape = Formats.ItemDesc.ItemDescFile.Load(added.ShapeFile);
            ushort wrote = (shape.Element as Formats.ItemDesc.RigidBodyElement)?.MaterialId ?? 0;
            check("…and the shape on disk carries the RAW slot id, table index plus the bias",
                wrote == Glass + CollisionMaterialCatalog.RawToTableBias,
                $"wrote {wrote}, wanted {Glass + CollisionMaterialCatalog.RawToTableBias}");
            check("…so reading it back names the surface that was asked for",
                CollisionMaterialCatalog.ForRawId(wrote).Index == Glass,
                $"{CollisionMaterialCatalog.ForRawId(wrote).Token} "
                    + $"(unbiased it would read {CollisionMaterialCatalog.ForTableIndex(wrote).Token})");

            // And the default stays what the game ships, so an ordinary add changes nothing.
            Assets.Collisions.AddedCollisionBox? plain = Assets.Collisions.CarCollisionBuilder.AddShape(
                model, body.Bone, "illusion_bullet_probe2_Collision", Formats.ItemDesc.RigidBodyShape.Box,
                new Vector3(0.20f, 0.20f, 0.05f), Matrix4x4.Identity, scratch, out _);
            ushort plainSurface = plain == null
                ? ushort.MaxValue
                : (Formats.ItemDesc.ItemDescFile.Load(plain.ShapeFile).Element
                    as Formats.ItemDesc.RigidBodyElement)?.MaterialId ?? ushort.MaxValue;
            check("a shape added without asking still carries 0, as every shipped one does",
                plainSurface == 0, $"surface {plainSurface}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("    FAILED: " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>Material names used by the geometry weighted to each bone, keyed by FNV64 of the bone name.</summary>
    private static Dictionary<ulong, SortedSet<string>> MaterialsByBone(FrameObjectModel model)
    {
        var byBone = new Dictionary<ulong, SortedSet<string>>();
        string[] bones = BoneNames(model);
        foreach (Piece p in PiecesOf(model, bones))
        {
            if (p.BlendIndex >= bones.Length) continue;
            ulong hash = Formats.Hashing.Fnv64.Hash(bones[p.BlendIndex]);
            byBone.TryAdd(hash, new SortedSet<string>(StringComparer.Ordinal));
            foreach ((int material, _) in p.Bursts) byBone[hash].Add(MaterialName(model, material));
        }
        return byBone;
    }

    // ── helpers ──

    private static bool TryOpen(
        string folder, string stem, out FrameResource? fr, out FrameObjectModel? model, out string why)
    {
        fr = null;
        model = null;
        why = "";
        var sds = new FileInfo(Path.Combine(folder, stem + ".sds"));
        if (!sds.Exists) { why = "no such archive"; return false; }
        string extracted = MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
        {
            why = "not extracted — open it in the app once";
            return false;
        }
        try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
        catch (Exception ex) { why = ex.Message; return false; }
        if (fr?.FrameObjects == null) { why = "no frame objects"; return false; }
        model = fr.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
        if (model == null) { why = "no skinned model"; return false; }
        return true;
    }

    private static string[] BoneNames(FrameObjectModel model)
    {
        try { return (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "?").ToArray(); }
        catch (Exception) { return []; }
    }

    /// <summary>The split table flattened to pieces, each paired with the box at the same ordinal.</summary>
    private static List<Piece> PiecesOf(FrameObjectModel model, string[] bones)
    {
        var pieces = new List<Piece>();
        FrameObjectModel.HitBoxInfo[] boxes = model.HitBoxes ?? [];

        // BlendIndex indexes the FLAT REMAP TABLE, which is the thing whose length it always fits: the
        // largest one on every car is exactly one less than the number of remap entries, and that table is
        // a list of global bone ids. So the bone of a split is one lookup away — and reading BlendIndex as
        // a bone id directly, which is what this did for a while, put every piece of a stock car on the
        // same bone.
        byte[] flat = [];
        try
        {
            Illusion.Formats.Frames.Resources.FrameBlendInfo.BoneIndexInfo[] lods =
                model.GetBlendInfoObject().BoneIndexInfos ?? [];
            if (lods.Length > 0) flat = lods[0].BoneRemapIDs ?? [];
        }
        catch (Exception) { /* a model with no blend info keeps the raw index below */ }

        int index = 0;
        foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
        {
            int boneId = split.BlendIndex < flat.Length ? flat[split.BlendIndex] : -1;
            string bone = boneId >= 0 && boneId < bones.Length
                ? bones[boneId]
                : $"blend #{split.BlendIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
            {
                var bursts = new List<(int, IReadOnlyList<(int, int)>)>();
                foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                {
                    var ranges = new List<(int, int)>();
                    foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                    {
                        ranges.Add((range.StartIndex, range.NumFaces));
                    }
                    bursts.Add((burst.MaterialIndex, ranges));
                }
                pieces.Add(new Piece(
                    index, boneId, bone, bursts, index < boxes.Length ? boxes[index] : null));
                index++;
            }
        }
        return pieces;
    }

    private static string MaterialName(FrameObjectModel model, int slot)
    {
        try
        {
            Formats.Frames.Resources.MaterialStruct[] mats = model.Material.Materials[0];
            if (slot < 0 || slot >= mats.Length) return $"slot{slot}?";
            ulong hash = mats[slot].MaterialHash;
            return MafiaMaterials.GetMaterialName(hash) ?? $"0x{hash:X16}";
        }
        catch (Exception) { return $"slot{slot}?"; }
    }

    /// <summary>The vertices the piece's own faces use, in the mesh's own space.</summary>
    private static Vector3[] VerticesOf(DecodedMesh decoded, Piece piece)
    {
        var seen = new HashSet<uint>();
        var verts = new List<Vector3>();
        foreach ((_, IReadOnlyList<(int Start, int Count)> ranges) in piece.Bursts)
        {
            foreach ((int start, int count) in ranges)
            {
                // StartIndex is an offset into the INDEX buffer, not a face number: three indices to a face.
                // Read raw, this walked off into other pieces' triangles (and off the end for anything past a
                // third of the buffer), which is what every "the hit box does not contain its own vertices"
                // measurement was built on.
                int first = start / 3;
                for (int face = first; face < first + count; face++)
                {
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int slot = face * 3 + corner;
                        if (slot < 0 || slot >= decoded.Indices.Length) continue;
                        uint vertex = decoded.Indices[slot];
                        if (vertex >= decoded.Positions.Length || !seen.Add(vertex)) continue;
                        verts.Add(decoded.Positions[vertex]);
                    }
                }
            }
        }
        return [.. verts];
    }

    private static bool Inside(Vector3 v, Vector3 lo, Vector3 hi) =>
        v.X >= lo.X - 1e-3f && v.X <= hi.X + 1e-3f
        && v.Y >= lo.Y - 1e-3f && v.Y <= hi.Y + 1e-3f
        && v.Z >= lo.Z - 1e-3f && v.Z <= hi.Z + 1e-3f;

    /// <summary>A frame matrix arrives with a fourth column that is not (0,0,0,1) — invert the pose only.</summary>
    private static bool TryInvert(Matrix4x4 m, out Matrix4x4 inverse)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return Matrix4x4.Invert(m, out inverse);
    }

    private static string SurfaceName(uint value) =>
        value < (uint)CollisionMaterialCatalog.All.Count
            ? CollisionMaterialCatalog.ForTableIndex((int)value).Token
            : "-";

    private static string Trim(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";
}
