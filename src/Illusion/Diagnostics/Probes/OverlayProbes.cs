using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Scene;
using Illusion.Viewport;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// GPU probes for the shared overlay-line pass — the thing every helper drawing stands on: a line whose width
/// is in PIXELS, feathered edges, and a faint rendering of the part the scene hides. Measured by rendering
/// windowless and reading the pixels back, since none of these properties is observable from the data side.
/// </summary>
internal static unsafe class OverlayProbes
{
    private const int W = 480, H = 320;

    // Overlay lines (windowless GPU): pixel-width, antialiasing, distance independence, the depth split, and
    // the two glyph sizing modes the helper glyphs will be built on. Output: %TEMP%\illusion_overlay.txt
    // plus illusion_overlay_depth.png / illusion_overlay_glyphs.png to eyeball.
    internal static void RunOverlayProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_overlay.txt");
        var sb = new StringBuilder();
        int passed = 0, failed = 0;

        void Check(bool ok, string what)
        {
            if (ok) passed++; else failed++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {what}");
        }

        GpuContext? gpu = null;
        SceneRenderer? renderer = null;
        SharedRenderTarget? target = null;
        OverlayLinePass? pass = null;
        try
        {
            gpu = new GpuContext();
            renderer = new SceneRenderer(gpu) { Mode = RenderMode.Solid, ShowSky = false, ShowNov = true };
            target = new SharedRenderTarget(gpu, W, H);
            renderer.Camera.LookAt(new Vector3(0f, -10f, 0f), Vector3.Zero);

            // ── A wall across the whole view, so both test lines sit on the SAME background: one in front of
            // it (visible), one behind it (hidden). Comparing their contribution measures the depth split
            // without the two being blended over different colours.
            renderer.AddMesh(Wall());
            renderer.Render(target);
            byte[] wallOnly = RenderTargetReadback.Read(gpu, target);

            renderer.SetNavDistrict("probe", new List<Vector3>
            {
                new(-6f, 5f, 1.5f), new(6f, 5f, 1.5f),        // in front of the wall
                new(-6f, 20f, -1.5f), new(6f, 20f, -1.5f),    // behind it
            });
            renderer.Render(target);
            byte[] withLines = RenderTargetReadback.Read(gpu, target);
            GpuProbes.SavePng(withLines, W, H, Path.Combine(Path.GetTempPath(), "illusion_overlay_depth.png"));

            int[] rows = RowPeaks(wallOnly, withLines);
            int visibleRow = PeakRow(rows, 0, H / 2 + 5);
            int hiddenRow = PeakRow(rows, H / 2 + 5, H);
            int visiblePeak = rows[visibleRow], hiddenPeak = rows[hiddenRow];
            sb.AppendLine($"visible line: row {visibleRow}, peak delta {visiblePeak}");
            sb.AppendLine($"hidden  line: row {hiddenRow}, peak delta {hiddenPeak}");

            Check(visiblePeak > 20, "a line in front of the geometry is drawn");
            Check(hiddenPeak > 2, "a line behind the geometry still shows through");
            float ratio = visiblePeak > 0 ? (float)hiddenPeak / visiblePeak : 0f;
            sb.AppendLine($"hidden/visible strength: {ratio:F2} (style asks for 0.22)");
            Check(ratio is > 0.05f and < 0.55f, "the hidden part is clearly fainter than the visible one");

            // ── Thickness in pixels, and the feathered edge. Profile straight down the middle column.
            int[] profile = ColumnProfile(wallOnly, withLines, W / 2, visibleRow - 8, visibleRow + 8);
            int lit = profile.Count(v => v > 2);
            int peak = profile.Max();
            int partial = profile.Count(v => v > peak * 0.15f && v < peak * 0.85f);
            sb.AppendLine($"cross-section: {lit} px wide, peak {peak}, {partial} partially-lit px " +
                          $"[{string.Join(",", profile)}]");
            Check(lit is >= 2 and <= 7, "line width lands in the 1.6 px + feather range");
            Check(partial >= 1, "the edge is antialiased (partially-lit pixels exist)");

            // ── The same width whatever the distance: a near line and one eight times farther away.
            // The baseline has to be a REAL empty frame (same clear colour), or every row differs and the
            // width measurement silently returns the size of its own sampling window.
            renderer.Clear();
            renderer.SetNavDistrict("probe", []);
            renderer.Render(target);
            byte[] empty = RenderTargetReadback.Read(gpu, target);
            renderer.SetNavDistrict("probe", new List<Vector3>
            {
                new(-6f, 15f, 3f), new(6f, 15f, 3f),
                new(-40f, 120f, -8f), new(40f, 120f, -8f),
            });
            renderer.Render(target);
            byte[] two = RenderTargetReadback.Read(gpu, target);
            int[] rows2 = RowPeaks(empty, two);
            int nearRow = PeakRow(rows2, 0, H / 2), farRow = PeakRow(rows2, H / 2, H);
            int nearWidth = ColumnProfile(empty, two, W / 2, nearRow - 8, nearRow + 8).Count(v => v > 2);
            int farWidth = ColumnProfile(empty, two, W / 2, farRow - 8, farRow + 8).Count(v => v > 2);
            sb.AppendLine($"width near (15 m): {nearWidth} px, far (130 m): {farWidth} px");
            Check(nearWidth is >= 2 and <= 7 && farWidth is >= 2 and <= 7 && Math.Abs(nearWidth - farWidth) <= 1,
                "line width does not change with distance");

            // ── Glyph sizing, straight through the pass: the two modes the helper glyphs will use.
            pass = new OverlayLinePass(gpu);
            var cam = new Camera();
            cam.LookAt(new Vector3(0f, -10f, 0f), Vector3.Zero);

            // (1) Screen-sized: offsets are pixels, so a glyph 40 px across stays 40 px across at any depth.
            OverlaySegment[] screenGlyphs =
            [
                Glyph(new Vector3(0f, 20f, 2f), 20f, OverlaySegmentFlags.ScreenSized),
                Glyph(new Vector3(0f, 200f, -20f), 20f, OverlaySegmentFlags.ScreenSized),
            ];
            byte[] shot = DrawDirect(gpu, target, pass, screenGlyphs, OverlayLineStyle.Default(Vector4.One), cam);
            GpuProbes.SavePng(shot, W, H, Path.Combine(Path.GetTempPath(), "illusion_overlay_glyphs.png"));
            int nearLen = WidestRunInBand(shot, 0, H / 2), farLen = WidestRunInBand(shot, H / 2, H);
            sb.AppendLine($"screen-sized glyph: {nearLen} px at 30 m, {farLen} px at 210 m (asked for 40)");
            Check(Math.Abs(nearLen - 40) <= 4 && Math.Abs(farLen - 40) <= 4,
                "a screen-sized glyph keeps its pixel size at any distance");

            // (2) World-sized with a minimum: a 2 cm glyph 70 m away is sub-pixel until the floor lifts it.
            OverlaySegment[] tiny = [Glyph(new Vector3(0f, 60f, 0f), 0.01f, OverlaySegmentFlags.World, extent: 0.01f)];
            byte[] noFloor = DrawDirect(gpu, target, pass, tiny, OverlayLineStyle.Default(Vector4.One), cam);
            byte[] floored = DrawDirect(gpu, target, pass, tiny,
                OverlayLineStyle.Default(Vector4.One) with { MinGlyphPixels = 14f }, cam);
            int without = WidestRunInBand(noFloor, 0, H), with = WidestRunInBand(floored, 0, H);
            sb.AppendLine($"2 cm glyph at 70 m: {without} px without the floor, {with} px with a 14 px floor");
            Check(without <= 3, "a sub-pixel world glyph stays sub-pixel when no floor is asked for");
            Check(with is >= 20 and <= 40, "the minimum-size floor lifts it to roughly 2 x 14 px");

            sb.Insert(0, $"OVERLAY LINE PROBE: {(failed == 0 ? "PASS" : "FAIL")} — {passed} passed, {failed} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "OVERLAY LINE PROBE: FAIL\n\n");
        }
        finally
        {
            pass?.Dispose();
            target?.Dispose();
            renderer?.Dispose();
            gpu?.Dispose();
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // The helper layer on a real car: what each frame kind turns into, which nodes are left out, and a
    // picture of the result over the body. Optional arg = the car. Output: %TEMP%\illusion_helpers.txt
    // plus illusion_helpers.png.
    internal static void RunHelperProbe(string car)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_helpers.txt");
        var sb = new StringBuilder();
        int passed = 0, failed = 0;

        void Check(bool ok, string what, string detail = "")
        {
            if (ok) passed++; else failed++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? " — " + detail : "")}");
        }

        GpuContext? gpu = null;
        SceneRenderer? renderer = null;
        SharedRenderTarget? target = null;
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", car + ".sds"));
            if (!sds.Exists) { sb.AppendLine("no such car: " + sds.FullName); return; }

            (List<SdsFrameNode> roots, List<MeshData> meshes, _) = SdsMeshLoader.LoadHierarchy(sds);
            HelperGlyphRenderData helpers = HelperGlyphBuilder.BuildFrames(roots);
            HelperGlyphRenderData rig = HelperGlyphBuilder.BuildRig(DistrictStreamer.CollectSkeletons(roots));

            FrameCensus census = CountFrames(roots);
            sb.AppendLine($"{car}: {census.Boxes} volumes, {census.Axes} points/lights/cameras " +
                          $"({census.Placeholders} unnamed at the origin), {census.Holders} frame holders");
            sb.AppendLine($"helper layer: {helpers.GlyphCount} glyphs, {helpers.SegmentCount} segments, " +
                          $"{helpers.HiddenCount} left out");
            sb.AppendLine($"rig layer: {rig.GlyphCount} bones, {rig.SegmentCount} segments");

            Check(helpers.HiddenCount == census.Placeholders,
                "every unnamed placeholder at the origin is left out, and only those",
                $"{helpers.HiddenCount} vs {census.Placeholders}");
            Check(helpers.GlyphCount == census.Boxes + census.Axes - census.Placeholders,
                "everything else gets a glyph — and a frame holder never does", $"{helpers.GlyphCount} glyphs");

            // A Dummy is drawn as ITS OWN box, not a fixed glyph: the biggest and the smallest on the car
            // differ by two orders of magnitude, and both have to survive the trip.
            float[] extents = [.. helpers.Segments.Where(s => !s.ScreenSized && s.Extent > 0f)
                .Select(s => s.Extent).Distinct().OrderBy(v => v)];
            sb.AppendLine($"box radii: {extents.Length} distinct, {extents.FirstOrDefault():F4} m .. {extents.LastOrDefault():F3} m");
            Check(extents.Length > 0 && extents[^1] > 1f, "the climb boxes keep their real size",
                $"largest radius {extents.LastOrDefault():F3} m");
            Check(extents.Length > 0 && extents[0] < 0.02f, "a centimetre-wide dummy is still in the layer",
                $"smallest radius {extents.FirstOrDefault():F4} m");

            Check(helpers.Segments.All(s => IsFinite(s.Anchor) && IsFinite(s.OffsetA) && IsFinite(s.OffsetB)),
                "no glyph carries a NaN (the corpus has NaN matrices)");
            Check(rig.GlyphCount > 0 && rig.Segments.Any(s => s.ScreenSized),
                "leaf bones fall back to a fixed on-screen size", $"{rig.GlyphCount} bones");

            // How long the world-sized bone glyphs come out. A bone that spans the whole car is a bone whose
            // "child" is nowhere near it — the shape then reads as a ray across the body, which is the very
            // thing this layer exists to stop.
            float[] spans = [.. rig.Segments.Where(s => !s.ScreenSized && s.Extent > 0f)
                .Select(s => s.Extent).Distinct().OrderByDescending(v => v)];
            int screenSizedBones = rig.Segments.Count(s => s.ScreenSized) / 5;
            sb.AppendLine($"bone glyphs: {screenSizedBones} at a fixed screen size, {spans.Length} spanning a child" +
                          (spans.Length > 0 ? $" ({spans[0]:F2} m longest, {spans[^1]:F2} m shortest)" : ""));
            Check(spans.Length == 0 || spans[0] < 2f,
                "no bone glyph stretches across the whole car",
                spans.Length > 0 ? $"longest {spans[0]:F2} m" : "none");

            // ── What is drawn is what can be clicked. The pick set is built from the scene tree and the
            // drawing from the frames, so the two agreeing is a real property, not a tautology.
            List<FrameNodeAdapter> drawn = [.. AllAdapters(roots).Where(a => HelperGlyphBuilder.DrawsGlyph(a))];
            sb.AppendLine($"pickable glyph objects: {drawn.Count}");
            Check(drawn.Count == helpers.GlyphCount,
                "every drawn glyph is a click target, and nothing else is",
                $"{drawn.Count} pickable vs {helpers.GlyphCount} drawn");
            Check(AllAdapters(roots).Any(a => a.Frame is FrameObjectFrame) &&
                  !AllAdapters(roots).Where(a => a.Frame is FrameObjectFrame).Any(HelperGlyphBuilder.DrawsGlyph),
                "a frame holder is neither drawn nor clickable");

            // A ray aimed at a glyph must return SOMETHING the ray passes through — the nearest one, which on
            // a car is often a neighbour, since the helper nodes sit centimetres apart. Aiming at an isolated
            // glyph must return that glyph: that is the part a user can rely on.
            Vector3[] anchors = [.. drawn.Select(a => HelperGlyphBuilder.GlyphAnchor(a))];
            float[] radii = [.. drawn.Select(HelperGlyphBuilder.PickRadius)];
            int missedEverything = 0, offRay = 0, isolated = 0, isolatedHit = 0;
            for (int i = 0; i < anchors.Length; i++)
            {
                Vector3 eye = anchors[i] + new Vector3(0f, -6f, 2.5f);
                Vector3 ray = Vector3.Normalize(anchors[i] - eye);
                // The viewport's own pick, per-glyph radii and all — a copy of the test here would pass while
                // the real one failed.
                int index = ActorPicking.Pick(anchors, radii, eye, ray, out _);
                if (index < 0) { missedEverything++; continue; }

                // Whatever came back has to be a glyph this ray really crosses, not a stray from elsewhere.
                float perpHit = PerpDistance(anchors[index], eye, ray, out float alongHit);
                if (perpHit > MathF.Max(radii[index], alongHit * 0.012f)) offRay++;

                // Isolated = nothing else lies near this RAY (a neighbour far from the target but close to
                // the line of sight is a legitimate winner, and testing distance between anchors misses that).
                bool alone = true;
                for (int k = 0; k < anchors.Length && alone; k++)
                {
                    if (k == i) continue;
                    float perp = PerpDistance(anchors[k], eye, ray, out float along);
                    if (along > 0f && perp < MathF.Max(radii[k], along * 0.012f) + 0.15f) alone = false;
                }
                if (alone) { isolated++; if (index == i) isolatedHit++; }
            }
            sb.AppendLine($"aimed picks: {missedEverything} hit nothing, {offRay} landed off the ray, " +
                          $"{isolatedHit}/{isolated} isolated glyphs picked themselves");
            Check(missedEverything == 0, "a ray aimed at a glyph always hits one");
            Check(offRay == 0, "what a pick returns is a glyph the ray passes through");
            Check(isolated > 0 && isolatedHit == isolated,
                "aiming at a glyph with no neighbours picks that glyph", $"{isolatedHit}/{isolated}");

            // The accent layer: the same shapes again in one colour, drawn whether or not the layer is on.
            var accent = new Vector4(0.91f, 0.53f, 0.24f, 0.95f);
            HelperGlyphRenderData highlight = HelperGlyphBuilder.BuildHighlight(drawn.Take(3), accent);
            Check(highlight.GlyphCount == Math.Min(3, drawn.Count)
                  && highlight.Segments.All(s => s.Color == accent),
                "highlighting re-draws the same glyphs in the accent colour",
                $"{highlight.GlyphCount} glyphs, {highlight.SegmentCount} segments");

            // Moving a node has to move its glyph: the layer is an immutable buffer rebuilt from the frames,
            // so this is the half of that contract the builder owns (the streamer queues the rebuild).
            if (FirstVolume(roots) is { } volume)
            {
                // The glyph is anchored at the box CENTRE, and a dummy attached to a bone has its local
                // transform read through that bone — so the test tracks the anchor nearest the node and the
                // world movement the edit actually produced, not the local nudge that was asked for.
                Vector3 wasAt = volume.WorldTransform.Translation;
                Vector3 anchorBefore = NearestAnchor(helpers, wasAt);
                Matrix4x4 moved = volume.LocalTransform;
                moved.Translation += new Vector3(0f, 0f, 3f);
                volume.LocalTransform = moved;
                Vector3 delta = volume.WorldTransform.Translation - wasAt;

                HelperGlyphRenderData after = HelperGlyphBuilder.BuildFrames(roots);
                bool followed = delta.Length() > 1e-3f
                    && after.SegmentCount == helpers.SegmentCount
                    && after.Segments.Any(s => Vector3.Distance(s.Anchor, anchorBefore + delta) < 1e-3f);

                moved.Translation -= new Vector3(0f, 0f, 3f);
                volume.LocalTransform = moved;
                Check(followed, "a moved node's glyph moves with it", $"anchor {anchorBefore:F2} moved by {delta:F2}");
            }

            // ── A picture over the body, which is the only thing that says the glyphs land on the parts.
            gpu = new GpuContext();
            renderer = new SceneRenderer(gpu)
            {
                Mode = RenderMode.Solid,
                ShowSky = false,
                ShowHelpers = true,
                ShowSkeleton = true,
            };
            foreach (MeshData md in meshes) renderer.AddMesh(md);
            renderer.SetHelperDistrict("probe", helpers);
            renderer.SetSkeletonDistrict("probe", rig);

            (Vector3 min, Vector3 max) = GlyphBounds(helpers);
            Vector3 centre = (min + max) * 0.5f;
            float radius = MathF.Max((max - min).Length() * 0.5f, 2f);
            renderer.Camera.LookAt(centre + new Vector3(radius * 0.9f, -radius * 1.1f, radius * 0.55f), centre);

            const int w = 900, h = 600;
            target = new SharedRenderTarget(gpu, w, h);

            // One frame per layer as well as both together: a layer that reads fine alone and turns to soup
            // in company is a thing only separate pictures show.
            byte[] bare = Shot(showHelpers: false, showRig: false, "illusion_helpers_bare.png");
            Shot(showHelpers: true, showRig: false, "illusion_helpers_only.png");
            Shot(showHelpers: false, showRig: true, "illusion_helpers_rig.png");
            string png = Path.Combine(Path.GetTempPath(), "illusion_helpers.png");
            byte[] withGlyphs = Shot(showHelpers: true, showRig: true, "illusion_helpers.png");

            byte[] Shot(bool showHelpers, bool showRig, string name)
            {
                renderer.ShowHelpers = showHelpers;
                renderer.ShowSkeleton = showRig;
                renderer.Render(target);
                byte[] pixels = RenderTargetReadback.Read(gpu, target);
                GpuProbes.SavePng(pixels, w, h, Path.Combine(Path.GetTempPath(), name));
                return pixels;
            }

            int changed = CountChanged(bare, withGlyphs);
            sb.AppendLine($"glyph pass changed {changed} px -> {png}");
            Check(changed > 500, "the layers actually draw over the car", $"{changed} px");

            // The accent over the ordinary layer, and again with both layers OFF — selecting a helper node in
            // the tree has to show where it is even when its layer is hidden.
            renderer.SetHelperHighlight(HelperGlyphBuilder.BuildHighlight(drawn.Take(4), accent));
            byte[] accented = Shot(showHelpers: true, showRig: true, "illusion_helpers_accent.png");
            byte[] accentOnly = Shot(showHelpers: false, showRig: false, "illusion_helpers_accent_only.png");
            int accentDelta = CountChanged(withGlyphs, accented);
            int aloneDelta = CountChanged(bare, accentOnly);
            sb.AppendLine($"accent changed {accentDelta} px over the layers, {aloneDelta} px with them off");
            Check(accentDelta > 100, "the accent reads over the ordinary glyphs", $"{accentDelta} px");
            Check(aloneDelta > 100, "a highlighted glyph shows even with its layer switched off", $"{aloneDelta} px");

            sb.Insert(0, $"HELPER GLYPH PROBE ({car}): {(failed == 0 ? "PASS" : "FAIL")} — " +
                         $"{passed} passed, {failed} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "HELPER GLYPH PROBE: FAIL\n\n");
        }
        finally
        {
            target?.Dispose();
            renderer?.Dispose();
            gpu?.Dispose();
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Pixels that differ noticeably between two frames.
    private static int CountChanged(byte[] a, byte[] b)
    {
        int changed = 0;
        for (int i = 0; i < a.Length && i < b.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) > 6 || Math.Abs(a[i + 1] - b[i + 1]) > 6) changed++;
        }
        return changed;
    }

    // Distance from a point to a ray, plus how far along the ray its projection falls.
    private static float PerpDistance(Vector3 point, Vector3 origin, Vector3 dir, out float along)
    {
        Vector3 to = point - origin;
        along = Vector3.Dot(to, dir);
        return MathF.Sqrt(MathF.Max(0f, to.LengthSquared() - along * along));
    }

    // Every frame node of the loaded tree, in tree order.
    private static IEnumerable<FrameNodeAdapter> AllAdapters(IReadOnlyList<SdsFrameNode> roots)
    {
        foreach (SdsFrameNode root in roots)
        {
            foreach (FrameNodeAdapter a in Walk(root)) yield return a;
        }

        static IEnumerable<FrameNodeAdapter> Walk(SdsFrameNode node)
        {
            if (node.Source is FrameNodeAdapter adapter) yield return adapter;
            foreach (SdsFrameNode c in node.Children)
            {
                foreach (FrameNodeAdapter a in Walk(c)) yield return a;
            }
        }
    }

    // The first Dummy in the tree — the node kind whose glyph is its own bounds, so a move shows up in the
    // anchor rather than only in the offsets.
    private static FrameNodeAdapter? FirstVolume(IReadOnlyList<SdsFrameNode> roots)
    {
        foreach (SdsFrameNode root in roots)
        {
            if (Find(root) is { } found) return found;
        }
        return null;

        static FrameNodeAdapter? Find(SdsFrameNode node)
        {
            if (node.Source is FrameNodeAdapter { Frame: FrameObjectDummy } adapter) return adapter;
            foreach (SdsFrameNode c in node.Children)
            {
                if (Find(c) is { } found) return found;
            }
            return null;
        }
    }

    private static Vector3 NearestAnchor(HelperGlyphRenderData data, Vector3 to)
    {
        Vector3 best = to;
        float bestDistance = float.MaxValue;
        foreach (GlyphSegment s in data.Segments)
        {
            float d = Vector3.DistanceSquared(s.Anchor, to);
            if (d < bestDistance) { bestDistance = d; best = s.Anchor; }
        }
        return best;
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static (Vector3 Min, Vector3 Max) GlyphBounds(HelperGlyphRenderData data)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (GlyphSegment s in data.Segments)
        {
            if (!IsFinite(s.Anchor)) continue;
            min = Vector3.Min(min, s.Anchor);
            max = Vector3.Max(max, s.Anchor);
        }
        return min.X > max.X ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }

    /// <param name="Boxes">Nodes that carry bounds — drawn as their own box.</param>
    /// <param name="Axes">Nodes that carry only a matrix — drawn as axes.</param>
    /// <param name="Placeholders">Of those, the unnamed ones parked at the origin, which are left out.</param>
    /// <param name="Holders">Frame holders, which are deliberately never drawn.</param>
    private readonly record struct FrameCensus(int Boxes, int Axes, int Placeholders, int Holders);

    // The frame kinds the helper layer speaks for, counted straight off the loaded tree — so the glyph counts
    // are measured against the archive rather than against numbers written down in the probe.
    private static FrameCensus CountFrames(IReadOnlyList<SdsFrameNode> roots)
    {
        int boxes = 0, axes = 0, placeholders = 0, holders = 0;
        foreach (SdsFrameNode root in roots) Walk(root);
        return new FrameCensus(boxes, axes, placeholders, holders);

        void Walk(SdsFrameNode node)
        {
            if (node.Source is FrameNodeAdapter adapter)
            {
                bool placeholder = IsPlaceholder(adapter);
                switch (adapter.Frame)
                {
                    case FrameObjectDummy:
                    case FrameObjectArea:
                    case FrameObjectSector:
                        boxes++;
                        if (placeholder) placeholders++;
                        break;
                    case FrameObjectPoint:
                    case FrameObjectTarget:
                    case FrameObjectDeflector:
                    case FrameObjectLight:
                    case FrameObjectCamera:
                        axes++;
                        if (placeholder) placeholders++;
                        break;
                    case FrameObjectFrame:
                        holders++;
                        break;
                }
            }
            foreach (SdsFrameNode c in node.Children) Walk(c);
        }

        static bool IsPlaceholder(FrameNodeAdapter adapter)
        {
            string name = adapter.Frame.Name?.ToString() ?? "";
            return (name.Length == 0 || name == "0")
                && adapter.Frame.LocalTransform.Translation.LengthSquared() < 1e-8f;
        }
    }

    // A wall filling the view at y = 10, so everything is drawn over one flat colour.
    private static MeshData Wall() => new()
    {
        Name = "probe-wall",
        World = Matrix4x4.Identity,
        Positions =
        [
            new Vector3(-40f, 10f, -30f), new Vector3(40f, 10f, -30f),
            new Vector3(40f, 10f, 30f), new Vector3(-40f, 10f, 30f),
        ],
        Normals = [-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY],
        UVs = [new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1)],
        Indices = [0, 1, 2, 0, 2, 3],
        Parts = [new MeshPart(0, 6, null)],
    };

    // One horizontal segment centred on the anchor: the smallest thing that measures a glyph's size.
    private static OverlaySegment Glyph(Vector3 anchor, float halfSize, OverlaySegmentFlags flags, float extent = 0f) => new()
    {
        Anchor = anchor,
        OffsetA = new Vector3(-halfSize, 0f, 0f),
        OffsetB = new Vector3(halfSize, 0f, 0f),
        Extent = extent,
        Rgba = OverlaySegments.White,
        Flags = (uint)flags,
    };

    // Draws segments through the pass alone (no scene, no depth content) and reads the frame back.
    private static byte[] DrawDirect(GpuContext gpu, SharedRenderTarget target, OverlayLinePass pass,
        OverlaySegment[] segments, in OverlayLineStyle style, Camera cam)
    {
        var ctx = gpu.Context11;
        var vp = new Silk.NET.Direct3D11.Viewport(0f, 0f, target.Width, target.Height, 0f, 1f);
        ctx.RSSetViewports(1, &vp);
        var rtv = target.Rtv.Handle;
        ctx.OMSetRenderTargets(1, &rtv, target.Dsv);
        var clear = stackalloc float[4] { 0f, 0f, 0f, 1f };
        ctx.ClearRenderTargetView((ID3D11RenderTargetView*)target.Rtv.Handle, clear);
        ctx.ClearDepthStencilView((ID3D11DepthStencilView*)target.Dsv.Handle,
            (uint)(ClearFlag.Depth | ClearFlag.Stencil), 1f, 0);

        cam.AspectRatio = (float)target.Width / target.Height;
        var frame = new OverlayFrame(
            cam.ViewProjection,
            new Vector2(target.Width * 0.5f, target.Height * 0.5f),
            2f / (cam.Projection.M22 * target.Height));

        ComPtr<ID3D11Buffer> vb = pass.CreateBuffer(segments);
        pass.Begin(ctx, frame, style, OverlayDepth.Always, 1f);
        pass.DrawSegments(ctx, vb, (uint)segments.Length);
        pass.End(ctx);
        gpu.WaitForGpu();
        vb.Dispose();
        return RenderTargetReadback.Read(gpu, target);
    }

    // Per-row maximum channel difference between two frames — where a line landed, and how strongly.
    private static int[] RowPeaks(byte[] a, byte[] b)
    {
        var rows = new int[H];
        for (int y = 0; y < H; y++)
        {
            int max = 0;
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                int d = Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
                if (d > max) max = d;
            }
            rows[y] = max;
        }
        return rows;
    }

    private static int PeakRow(int[] rows, int from, int to)
    {
        int best = Math.Clamp(from, 0, H - 1), bestValue = -1;
        for (int y = Math.Max(from, 0); y < Math.Min(to, H); y++)
        {
            if (rows[y] > bestValue) { bestValue = rows[y]; best = y; }
        }
        return best;
    }

    // The vertical slice through one column: how a line's intensity falls off across its width.
    private static int[] ColumnProfile(byte[] a, byte[] b, int x, int fromRow, int toRow)
    {
        var profile = new List<int>();
        for (int y = Math.Max(fromRow, 0); y < Math.Min(toRow, H); y++)
        {
            int i = (y * W + x) * 4;
            profile.Add(Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2]))));
        }
        return [.. profile];
    }

    // Longest horizontal run of lit pixels within a band of rows — a glyph's on-screen length.
    private static int WidestRunInBand(byte[] frame, int fromRow, int toRow)
    {
        int best = 0;
        for (int y = Math.Max(fromRow, 0); y < Math.Min(toRow, H); y++)
        {
            int run = 0;
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                bool lit = frame[i] > 8 || frame[i + 1] > 8 || frame[i + 2] > 8;
                if (lit) { run++; if (run > best) best = run; }
                else run = 0;
            }
        }
        return best;
    }
}
