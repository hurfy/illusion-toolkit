using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Illusion.Rendering.Controls;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What a splitter drag costs the GPU.
///
/// <para>
/// Dragging a panel edge makes WPF raise a size change per mouse move — hundreds a second — and the viewport
/// answered each one by throwing its shared surface away and building a new one. This probe replays that
/// sequence on the real device and measures it: how long one rebuild takes, and how much video memory the
/// discarded surfaces hold on to. Both are the difference between a viewport that survives a drag and one
/// that stalls to zero frames and then dies on an allocation failure.
/// </para>
/// </summary>
internal static class ResizeProbes
{
    // A drag is not a resize — it is hundreds of them, one per mouse report. Two seconds of it at 60 frames
    // a second, which is the length a panel edge actually gets dragged.
    private const int DragFrames = 120;

    // The video-memory comparison deliberately reproduces the leak, so it runs a SHORT drag: at ~8.6 MB a
    // surface pair, 150 steps pile up over a gigabyte, which is enough to see and little enough that the
    // probe cannot itself run a small card out of memory.
    private const int VramDragSteps = 150;
    private const int BaseW = 1200, BaseH = 900;

    // Size changes fed to the live window — a couple of seconds of a hand on a splitter.
    private const int LiveDragReports = 600;

    /// <summary>A frame at 60 Hz, for the synthetic drags — they run on a clock the probe advances itself.</summary>
    private const double FrameMs = 1000.0 / 60;

    internal static void RunResizeProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_resize.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        GpuContext? gpu = null;
        SceneRenderer? renderer = null;
        try
        {
            gpu = new GpuContext();
            renderer = new SceneRenderer(gpu) { ShowSky = false };
            for (int i = 0; i < 64; i++) renderer.AddMesh(Quad(i));
            renderer.Camera.LookAt(new Vector3(4f, -20f, 6f), new Vector3(4f, 0f, 2f));

            long vramStart = gpu.VideoMemoryUsed();
            sb.AppendLine($"video memory in use at start: {Mb(vramStart)}");
            sb.AppendLine();

            // ---- 1. What one rebuild costs -------------------------------------------------------------
            // Steady frames first, so the per-frame cost of drawing is known and the rebuild can be read as
            // the difference rather than as a lump.
            using (var steady = new SharedRenderTarget(gpu, BaseW, BaseH))
            {
                for (int i = 0; i < 10; i++) renderer.Render(steady);   // warm the driver
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 60; i++) renderer.Render(steady);
                double frameMs = sw.Elapsed.TotalMilliseconds / 60;
                sb.AppendLine($"steady frame: {frameMs:F2} ms ({1000 / frameMs:F0} fps)");
            }

            double rebuildMs = TimeRebuilds(gpu, renderer, 40, out double worstMs);
            sb.AppendLine($"one surface rebuild: {rebuildMs:F2} ms average, {worstMs:F2} ms worst");
            sb.AppendLine();

            // A rebuild costing more than a frame is what turns a drag into a slideshow: at mouse-report rate
            // the UI thread does nothing but allocate.
            Check("a rebuild is affordable once per frame", rebuildMs < 8.0, $"{rebuildMs:F2} ms");
            double perReport = DragFrames * FramePerRequests * rebuildMs;
            sb.AppendLine($"a surface per mouse report over a {DragFrames / 60.0:F1} s drag: {perReport:F0} ms of "
                          + $"allocation -> {(perReport > DragFrames * 16.0 ? "the UI thread cannot keep up" : "survivable")}");
            sb.AppendLine();

            // ---- 2. Does the video memory come back? ---------------------------------------------------
            // Measured as a high-water mark DURING the drag, because releasing a surface does not free it
            // where it is released — the driver retires the allocation on its own schedule, and a plain
            // before/after delta reads whatever is still draining from the previous run rather than what this
            // one cost. The high-water mark is the honest number: it is where the allocation that kills the
            // viewport fails.
            //
            // Whether the outgoing surface is unbound from the device first makes no measurable difference —
            // that was the first suspicion and it was wrong. What decides the pile is purely HOW MANY
            // surfaces get built, which is why the fix is about rate rather than about release.
            DragMemory perRequest = DragVram(gpu, renderer, throughSurface: false);
            DragMemory perFrame = DragVram(gpu, renderer, throughSurface: true);
            sb.AppendLine($"video memory over the same {VramDragSteps}-step drag "
                          + $"({Mb(SurfacePairBytes)} a surface at this size):");
            sb.AppendLine($"  answering every size request directly: "
                          + $"{perRequest.Allocations} built, peak {Mb(perRequest.Peak)} "
                          + $"= {perRequest.Peak / (double)SurfacePairBytes:F0} surfaces resident at once");
            sb.AppendLine($"  through ViewportSurface: "
                          + $"{perFrame.Allocations} built, peak {Mb(perFrame.Peak)} "
                          + $"= {perFrame.Peak / (double)SurfacePairBytes:F0} surfaces resident at once");
            sb.AppendLine();

            // The finding, stated as a check: during a drag NOTHING is freed. Every surface built is still
            // resident at the end of it, so the pile is exactly as big as the number of rebuilds — which is
            // how a second of dragging asks a 4 GB card for more than it has.
            Check("a drag holds on to every surface it builds",
                perRequest.Peak > SurfacePairBytes * VramDragSteps * 0.7,
                $"{Mb(perRequest.Peak)} for {perRequest.Allocations} surfaces");
            Check("committing per frame instead of per request shrinks the pile in proportion",
                perFrame.Peak * 2 < perRequest.Peak,
                $"{Mb(perFrame.Peak)} vs {Mb(perRequest.Peak)}");

            // ---- 3. The surface a viewport actually owns ----------------------------------------------
            // ViewportSurface is what the control uses: layout asks for a size, the frame loop decides when
            // the GPU hears about it. Replay the same drag through it and count what reaches the driver.
            // Driven off a clock the probe owns, so what is measured is the RULE rather than how fast this
            // machine happens to be.
            double clock = 0;
            using (var surface = new ViewportSurface(gpu, () => clock))
            {
                var published = new List<nint>();
                int quarterWay = 0;
                for (int frame = 0; frame < DragFrames; frame++)
                {
                    clock += FrameMs;                                  // one frame passes…
                    surface.Request(BaseW + frame * FramePerRequests, BaseH);   // …carrying a few mouse reports
                    surface.Commit(p => published.Add(p));
                    if (surface.Target is { } t) renderer.Render(t);
                    if (frame == DragFrames / 4) quarterWay = surface.Allocations;
                }
                // The drag ends: the mouse stops, the size stops changing, and the surface catches up.
                clock += ViewportSurface.QuietMs;
                surface.Commit(p => published.Add(p));
                if (surface.Target is { } last) renderer.Render(last);

                double dragSeconds = DragFrames * FrameMs / 1000.0;
                sb.AppendLine($"a {dragSeconds:F1} s drag through ViewportSurface: {surface.Allocations} "
                              + $"surfaces built over {DragFrames} frames and "
                              + $"{DragFrames * FramePerRequests} size requests, final surface "
                              + $"{surface.Target?.Width}x{surface.Target?.Height}");

                // The rule is a capped RATE, not a capped total: a drag twice as long does cost twice as
                // much, but at one rebuild every fifth of a second instead of one per size change.
                Check("a drag rebuilds at a fixed low rate",
                    surface.Allocations <= dragSeconds * 1000 / ViewportSurface.CatchUpMs + 2,
                    $"{surface.Allocations} over {dragSeconds:F1} s");
                Check("the rate holds for the whole drag rather than climbing",
                    Math.Abs(surface.Allocations - quarterWay * 4) <= 3,
                    $"{quarterWay} at a quarter of the drag, {surface.Allocations} at the end");
                Check("the surface ends at the size that was last asked for",
                    surface.Target?.Width == BaseW + (DragFrames - 1) * FramePerRequests
                    && surface.Target?.Height == BaseH,
                    $"{surface.Target?.Width}x{surface.Target?.Height}");
                Check("the back buffer is cleared before its surface dies",
                    published.Count >= surface.Allocations * 2 && published[0] == 0,
                    $"{published.Count} publishes, first = {published.FirstOrDefault()}");
                Check("no failure while resizing", surface.LastFailure == null, surface.LastFailure ?? "");

                // A size that has stopped changing is honoured as soon as it goes quiet — the wait is for the
                // drag, not for the size — and not one frame sooner.
                int before = surface.Allocations;
                surface.Request(BaseW + 3, BaseH + 3);
                clock += FrameMs;
                surface.Commit(null);
                bool waited = surface.Allocations == before;
                clock += ViewportSurface.QuietMs;
                surface.Commit(null);
                Check("a size that stops changing is built as soon as it goes quiet",
                    waited && surface.Allocations == before + 1 && surface.Target?.Width == BaseW + 3,
                    $"{surface.Target?.Width}x{surface.Target?.Height}");

                // A size that cannot be allocated must not take the viewport down with it: the old surface
                // stays, the frame loop keeps running, and the next size is tried again.
                surface.Request(1_000_000, 1_000_000);
                bool threw = false;
                try { CommitWhenQuiet(); } catch { threw = true; }
                Check("an impossible size is refused rather than thrown", !threw, surface.LastFailure ?? "no failure");
                Check("the refusal says what was refused", surface.LastFailure?.StartsWith("1000000x") == true,
                    surface.LastFailure ?? "no failure");
                Check("a refused size leaves a usable surface behind", surface.Target?.Width == BaseW + 3);

                // …and it is not retried on every frame either, or a viewport that cannot allocate spends the
                // rest of the session failing at full speed.
                int failedAt = surface.Allocations;
                clock += FrameMs;
                surface.Commit(null);
                Check("a refused size is not retried every frame",
                    surface.Allocations == failedAt && surface.LastFailure != null);

                // A local helper rather than one Commit: the surface answers a size only once it has stopped
                // moving, so a request needs one frame to register and a quiet spell before it is built.
                void CommitWhenQuiet()
                {
                    clock += FrameMs;
                    surface.Commit(null);
                    clock += ViewportSurface.QuietMs;
                    surface.Commit(null);
                }

                surface.Request(BaseW, BaseH);
                clock += ViewportSurface.CatchUpMs;   // past the back-off the refusal put in the way
                CommitWhenQuiet();
                Check("the viewport recovers on the next size",
                    surface.Target?.Width == BaseW && surface.LastFailure == null,
                    $"{surface.Target?.Width}x{surface.Target?.Height}, failure: {surface.LastFailure ?? "none"}");
            }

            // ---- 4. A real window, really dragged -----------------------------------------------------
            // Everything above drives the surface by hand. This drives a live viewport in a live window the
            // way a hand on a splitter does — WPF's own layout pass, WPF's own frame loop — because that is
            // the path that failed, and a bench that never touches it cannot say it is fixed.
            LiveDrag(sb, Check);

            long vramEnd = gpu.VideoMemoryUsed();
            sb.AppendLine();
            sb.AppendLine($"video memory in use at end: {Mb(vramEnd)} (started at {Mb(vramStart)})");
            sb.Insert(0, $"RESIZE PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "RESIZE PROBE: FAIL\n\n");
        }
        finally
        {
            renderer?.Dispose();
            gpu?.Dispose();
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    /// <summary>Times the old path — throw the surface away, build another, draw a frame into it.</summary>
    private static double TimeRebuilds(GpuContext gpu, SceneRenderer renderer, int steps, out double worstMs)
    {
        SharedRenderTarget? target = null;
        double total = 0;
        worstMs = 0;
        try
        {
            for (int i = 0; i < steps; i++)
            {
                var sw = Stopwatch.StartNew();
                target?.Dispose();
                target = new SharedRenderTarget(gpu, BaseW + i, BaseH);
                double ms = sw.Elapsed.TotalMilliseconds;
                total += ms;
                worstMs = Math.Max(worstMs, ms);
                renderer.Render(target);
            }
        }
        finally { target?.Dispose(); }
        return total / steps;
    }

    /// <summary>
    /// A live viewport in a live window, resized the way a splitter resizes it: a size change per mouse
    /// report, with WPF's own frame loop deciding when anything is drawn. Off-screen, so nothing flashes up.
    /// </summary>
    private static void LiveDrag(StringBuilder sb, Action<string, bool, string> check)
    {
        var viewport = new ViewportControl();
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Left = -20_000,             // off-screen: this is a bench, not a window anyone should see
            Top = -20_000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = viewport,
        };
        try
        {
            window.Show();
            Pump(600);                                  // let the device come up and the frame loop start
            int afterOpen = viewport.SurfaceRebuilds;
            double fpsBefore = viewport.Fps;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < LiveDragReports; i++)
            {
                window.Width = 900 + i % 200;           // a hand on the splitter, one report at a time
                window.UpdateLayout();                  // WPF's layout pass, which is where the surface used
                if (i % 8 == 0) Pump(1);                //   to be rebuilt; a frame every eight reports
            }
            double dragMs = sw.Elapsed.TotalMilliseconds;
            Pump(400);                                  // the drag ends and the viewport settles

            int built = viewport.SurfaceRebuilds - afterOpen;
            sb.AppendLine();
            sb.AppendLine($"a live window dragged {LiveDragReports} times in {dragMs:F0} ms: "
                          + $"{built} surfaces built, {viewport.Fps:F0} fps after (was {fpsBefore:F0})");

            check("a live drag rebuilds at the rate the clock allows",
                built <= dragMs / ViewportSurface.CatchUpMs + 3,
                $"{built} over {dragMs:F0} ms of dragging");
            check("the viewport is still drawing after the drag", viewport.Fps > 0,
                $"{viewport.Fps:F1} fps");
        }
        finally
        {
            window.Close();
            Pump(100);
        }
    }

    /// <summary>Runs the dispatcher for a while, so WPF lays out and the viewport's frame loop runs.</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>Video memory a drag took above where it started, at its worst, and what it built to get there.</summary>
    private readonly record struct DragMemory(long Peak, int Allocations);

    /// <summary>
    /// Replays the same drag through <see cref="ViewportSurface"/> at two different commit rates and watches
    /// video memory through it. Starts from a settled baseline, so the previous run's pile cannot be read as
    /// this one's.
    /// </summary>
    /// <param name="throughSurface">
    /// False replays what the viewport used to do — a surface built inside the size-change handler, so one
    /// per mouse report. True routes the same requests through <see cref="ViewportSurface"/>, committed at
    /// frame rate.
    /// </param>
    private static DragMemory DragVram(GpuContext gpu, SceneRenderer renderer, bool throughSurface)
    {
        long before = Settle(gpu);
        long peak = before;
        double clock = 0;
        using var surface = new ViewportSurface(gpu, () => clock);
        SharedRenderTarget? raw = null;
        try
        {
            for (int i = 0; i < VramDragSteps; i++)
            {
                if (throughSurface)
                {
                    surface.Request(BaseW + i, BaseH);
                    if (i % FramePerRequests == 0)
                    {
                        clock += FrameMs;
                        surface.Commit(null);
                        if (surface.Target is { } target) renderer.Render(target);
                    }
                }
                else
                {
                    raw?.Dispose();
                    raw = new SharedRenderTarget(gpu, BaseW + i, BaseH);
                    renderer.Render(raw);
                }
                if (i % 5 == 0) peak = Math.Max(peak, gpu.VideoMemoryUsed());
            }
        }
        finally
        {
            peak = Math.Max(peak, gpu.VideoMemoryUsed());
            gpu.UnbindRenderTargets();
            raw?.Dispose();
        }
        return new DragMemory(Math.Max(0, peak - before), throughSurface ? surface.Allocations : VramDragSteps);
    }

    /// <summary>
    /// Waits for the driver to finish returning what was released, and answers with where it came to rest.
    /// Releasing a surface only queues the work; reading the counter straight afterwards reads the queue.
    /// </summary>
    private static long Settle(GpuContext gpu)
    {
        var sw = Stopwatch.StartNew();
        long last = gpu.VideoMemoryUsed();
        while (sw.Elapsed.TotalSeconds < 8)
        {
            Thread.Sleep(150);
            long now = gpu.VideoMemoryUsed();
            if (now >= last - 1024 * 1024) return now;   // stopped falling by more than a megabyte
            last = now;
        }
        return last;
    }

    /// <summary>What one surface costs: a BGRA colour surface plus its D24S8 depth buffer, 4 bytes a pixel each.</summary>
    private const long SurfacePairBytes = (long)BaseW * BaseH * 8;

    /// <summary>
    /// Size requests a 60 Hz frame loop lets past between commits. A mouse reports at ~500 Hz and WPF raises a
    /// size change per report, so a frame swallows roughly eight of them.
    /// </summary>
    private const int FramePerRequests = 8;

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";

    private static MeshData Quad(int i) => new()
    {
        Name = $"resize_{i}",
        World = Matrix4x4.CreateTranslation(i % 8, i / 8, 0),
        Positions = [new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)],
        Normals = [-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY],
        UVs = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
        Indices = [0, 1, 2, 0, 2, 3],
        Parts = [new MeshPart(0, 6, null)],
    };
}
