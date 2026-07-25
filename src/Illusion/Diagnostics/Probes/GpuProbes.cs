using System.IO;
using System.Numerics;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Assets;
using Illusion.Assets.Collisions;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Collisions;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>GPU probes: windowless smoke tests of the renderer, free-threaded loading, render modes and the
/// selection outline (with pixel readback).</summary>
internal static class GpuProbes
{
    // Collision render (windowless GPU): loads a real district's meshes + its collision layer, renders the scene
    // once without collision and once with it, and asserts the collision pass changes a meaningful number of
    // pixels (it actually draws). Also saves the with-collision frame as a PNG so the hull placement/rotation can
    // be eyeballed. Output: %TEMP%\illusion_collision_gpu.txt + illusion_collision_gpu.png
    internal static void RunCollisionGpuProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_gpu.txt");
        string pngFile = Path.Combine(Path.GetTempPath(), "illusion_collision_gpu.png");
        var sb = new StringBuilder();
        GpuContext? gpu = null;
        SceneRenderer? renderer = null;
        SharedRenderTarget? target = null;
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? col = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            if (col == null) { sb.AppendLine("no collision resource in " + district); return; }

            gpu = new GpuContext();
            renderer = new SceneRenderer(gpu) { Mode = RenderMode.Solid, ShowSky = false };
            renderer.Textures.AddFolder(extracted);

            // Load the district's render meshes (context for the collision overlay).
            (_, List<MeshData> meshes, _) = SdsMeshLoader.LoadHierarchy(new FileInfo(sds));
            foreach (MeshData md in meshes) renderer.AddMesh(md);
            sb.AppendLine($"render meshes: {meshes.Count}");

            // Build + upload the collision layer.
            CollisionRenderData data = CollisionSceneBuilder.Build(CollisionFile.Load(col));
            (Vector3 cmin, Vector3 cmax) = CollisionWorldBounds(data);
            sb.AppendLine($"collision: {data.Meshes.Length} meshes, world AABB {cmin:F1} .. {cmax:F1}");

            // Frame the camera over the collision volume (angled overhead — shows occlusion against the buildings).
            Vector3 center = (cmin + cmax) * 0.5f;
            float radius = MathF.Max((cmax - cmin).Length() * 0.5f, 5f);
            renderer.Camera.Far = radius * 8f + 5000f;
            renderer.Camera.LookAt(center + new Vector3(0f, -radius * 1.1f, radius * 0.8f), center);

            const int W = 900, H = 600;
            target = new SharedRenderTarget(gpu, W, H);

            renderer.Render(target);                    // collision OFF
            byte[] off = Readback(gpu, target);
            SavePng(off, W, H, Path.Combine(Path.GetTempPath(), "illusion_collision_gpu_meshes.png"));

            renderer.SetCollisionDistrict("probe", data); // collision ON
            renderer.Render(target);
            byte[] on = Readback(gpu, target);

            int changed = CountDifferingPixels(off, on, W, H);
            SavePng(on, W, H, pngFile);

            int atOrigin = 0;
            foreach (CollisionRenderMesh m in data.Meshes)
                foreach (Matrix4x4 w in m.Instances)
                    if (w.Translation.LengthSquared() < 1f) atOrigin++;
            sb.AppendLine($"instances at world origin (0,0,0): {atOrigin}");

            // Collision-only frame (meshes removed) from the same camera — for a direct footprint/rotation compare.
            renderer.Clear();
            renderer.Render(target);
            SavePng(Readback(gpu, target), W, H, Path.Combine(Path.GetTempPath(), "illusion_collision_gpu_only.png"));

            bool pass = renderer.HasCollisionData && changed > 200;
            sb.Insert(0, $"COLLISION GPU PROBE: {(pass ? "PASS" : "FAIL")} — collision pass changed {changed} px " +
                         $"(rendered {W}x{H}); PNG: {pngFile}\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); sb.Insert(0, "COLLISION GPU PROBE: FAIL\n\n"); }
        finally
        {
            target?.Dispose();
            renderer?.Dispose();
            gpu?.Dispose();
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static (Vector3 Min, Vector3 Max) CollisionWorldBounds(CollisionRenderData data)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (CollisionRenderMesh m in data.Meshes)
            foreach (Matrix4x4 world in m.Instances)
                for (int k = 0; k < 8; k++)
                {
                    var corner = new Vector3(
                        (k & 1) == 0 ? m.LocalMin.X : m.LocalMax.X,
                        (k & 2) == 0 ? m.LocalMin.Y : m.LocalMax.Y,
                        (k & 4) == 0 ? m.LocalMin.Z : m.LocalMax.Z);
                    Vector3 wp = Vector3.Transform(corner, world);
                    min = Vector3.Min(min, wp);
                    max = Vector3.Max(max, wp);
                }
        return (min, max);
    }

    private static int CountDifferingPixels(byte[] a, byte[] b, int w, int h)
    {
        int changed = 0;
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            if (Math.Abs(a[o] - b[o]) + Math.Abs(a[o + 1] - b[o + 1]) + Math.Abs(a[o + 2] - b[o + 2]) > 24) changed++;
        }
        return changed;
    }

    private static void SavePng(byte[] bgra, int w, int h, string path)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    // GPU smoke test of the instancing path (windowless): context → renderer (both shaders) → instanced draw.
    internal static void RunGpuProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_gpu.txt");
        var sb = new StringBuilder();
        GpuContext? gpu = null;
        SceneRenderer? renderer = null;
        SharedRenderTarget? target = null;
        try
        {
            gpu = new GpuContext();
            sb.AppendLine("GpuContext OK (D3D11 + D3D9Ex)");

            renderer = new SceneRenderer(gpu);
            sb.AppendLine("SceneRenderer OK (MeshShader + InstancedMeshShader compiled)");

            var md = new MeshData
            {
                Name = "probe",
                World = Matrix4x4.Identity,
                Positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) },
                Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
                UVs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
                Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
                Parts = new[] { new MeshPart(0, 6, null) },
                Instances = new[]
                {
                    Matrix4x4.CreateTranslation(0, 0, 0),
                    Matrix4x4.CreateTranslation(2, 0, 0),
                    Matrix4x4.CreateTranslation(4, 0, 0),
                },
            };
            GpuMesh gm = renderer.AddMesh(md);
            sb.AppendLine($"AddMesh OK: Instanced={gm.Instanced} InstanceCount={gm.InstanceCount} " +
                          $"cells={gm.InstanceCells?.Length} totalTris={renderer.TotalTriangles}");

            // Aim at the instance row so per-cell frustum culling keeps them (instanced draws are
            // culled now — an unaimed camera would legitimately draw nothing and test nothing).
            renderer.Camera.LookAt(new Vector3(2f, -10f, 0.5f), new Vector3(2f, 0.5f, 0.5f));

            target = new SharedRenderTarget(gpu, 64, 64);
            renderer.Render(target);
            sb.AppendLine($"Render OK: drawn={renderer.DrawnMeshes} drawCalls={renderer.DrawCalls} " +
                          $"drawnInstances={renderer.DrawnInstances}");
            if (renderer.DrawnMeshes != 1 || renderer.DrawnInstances != 3)
                sb.AppendLine($"FAIL: expected drawn=1 drawnInstances=3 (per-cell culling lost the aimed-at instances)");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally
        {
            target?.Dispose();
            renderer?.Dispose();
            gpu?.Dispose();
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Concurrency smoke test of the streaming pipeline: 4 loader threads create meshes through
    // SceneRenderer.CreateMeshGpu (racing on the same texture names via the shared TextureLibrary)
    // while the main thread renders; then everything attaches on the main thread and renders again.
    // Proves free-threaded device-object creation on the user's actual driver — the one assumption of
    // the background scene build that a code review cannot verify.
    internal static void RunAsyncProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_async.txt");
        var sb = new StringBuilder();
        GpuContext? gpu = null;
        SceneRenderer? renderer = null;
        SharedRenderTarget? target = null;
        try
        {
            gpu = new GpuContext();
            renderer = new SceneRenderer(gpu);
            target = new SharedRenderTarget(gpu, 64, 64);
            sb.AppendLine("GpuContext + SceneRenderer OK");

            // Shared texture folder with generated DDS files (uncompressed BGRA32) — loader threads race
            // on the same names through the TextureLibrary cache.
            string texDir = Path.Combine(Path.GetTempPath(), "illusion_async_tex");
            Directory.CreateDirectory(texDir);
            for (int i = 0; i < 8; i++) File.WriteAllBytes(Path.Combine(texDir, $"async{i}.dds"), MakeDds(4, 4));
            renderer.Textures.AddFolder(texDir);

            const int tasksN = 4, meshesPerTask = 64;
            SceneRenderer r = renderer;
            Task<List<GpuMesh>>[] tasks = Enumerable.Range(0, tasksN).Select(t => Task.Run(() =>
            {
                var list = new List<GpuMesh>(meshesPerTask);
                for (int i = 0; i < meshesPerTask; i++) list.Add(r.CreateMeshGpu(AsyncQuad(t, i)));
                return list;
            })).ToArray();

            // Render on the main thread while the loaders run — the contention pattern of streaming.
            int frames = 0;
            Task all = Task.WhenAll(tasks);
            while (!all.IsCompleted && frames < 50_000) { renderer.Render(target); frames++; }
            all.Wait(); // propagate loader exceptions

            int created = 0;
            foreach (Task<List<GpuMesh>> t in tasks)
                foreach (GpuMesh gm in t.Result) { renderer.AttachMesh(gm); created++; }

            renderer.Camera.LookAt(new Vector3(4f, -15f, 3f), new Vector3(4f, 2f, 1f));
            renderer.Render(target);

            bool ok = created == tasksN * meshesPerTask && renderer.DrawnMeshes > 0;
            sb.AppendLine($"created={created} (expected {tasksN * meshesPerTask}), framesWhileLoading={frames}, " +
                          $"drawn={renderer.DrawnMeshes} drawCalls={renderer.DrawCalls}");
            sb.Insert(0, ok ? "ASYNC PROBE: PASS\n\n" : "ASYNC PROBE: FAIL\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "ASYNC PROBE: FAIL\n\n");
        }
        finally
        {
            target?.Dispose();
            renderer?.Dispose();
            gpu?.Dispose();
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // A unit quad with a per-index texture so concurrent loaders collide on the cache (8 shared names).
    private static MeshData AsyncQuad(int task, int i) => new()
    {
        Name = $"async_{task}_{i}",
        World = Matrix4x4.CreateTranslation(i % 8, task, i / 8),
        Positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) },
        Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
        UVs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
        Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
        Parts = new[] { new MeshPart(0, 6, $"async{i % 8}.dds") },
    };

    // Minimal uncompressed BGRA32 DDS (the format DdsTexture's no-FourCC branch expects).
    private static byte[] MakeDds(int width, int height)
    {
        var dds = new byte[128 + width * height * 4];
        dds[0] = (byte)'D'; dds[1] = (byte)'D'; dds[2] = (byte)'S'; dds[3] = (byte)' ';
        BitConverter.GetBytes(124).CopyTo(dds, 4);     // header size
        BitConverter.GetBytes(0x1007).CopyTo(dds, 8);  // CAPS | HEIGHT | WIDTH | PIXELFORMAT
        BitConverter.GetBytes(height).CopyTo(dds, 12);
        BitConverter.GetBytes(width).CopyTo(dds, 16);
        BitConverter.GetBytes(32).CopyTo(dds, 76);     // pixel-format size
        BitConverter.GetBytes(0x41).CopyTo(dds, 80);   // RGB | ALPHAPIXELS (no FOURCC → BGRA32 branch)
        for (int i = 128; i < dds.Length; i += 4)
        {
            dds[i] = 0x40; dds[i + 1] = 0x80; dds[i + 2] = 0xC0; dds[i + 3] = 0xFF;
        }
        return dds;
    }

    // Render-mode smoke test (windowless): renders one mesh into a 64x64 target once per RenderMode.
    // Verifies the shared MafiaLitPs compiles with its textured/simple branches and that all four shading
    // modes execute without error — a headless guard for the seamless mode switch and the shader edits.
    internal static void RunModesProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_modes.txt");
        var sb = new StringBuilder();
        GpuContext? gpu = null;
        SceneRenderer? renderer = null;
        SharedRenderTarget? target = null;
        try
        {
            gpu = new GpuContext();
            renderer = new SceneRenderer(gpu);
            sb.AppendLine("GpuContext + SceneRenderer OK (shaders compiled)");

            var md = new MeshData
            {
                Name = "probe",
                World = Matrix4x4.Identity,
                Positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) },
                Normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
                UVs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
                Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
                Parts = new[] { new MeshPart(0, 6, null) },
            };
            renderer.AddMesh(md);
            renderer.Camera.LookAt(new Vector3(0.5f, 0.5f, 3f), new Vector3(0.5f, 0.5f, 0f)); // frame the quad

            target = new SharedRenderTarget(gpu, 64, 64);
            var modes = Enum.GetValues<RenderMode>();
            foreach (RenderMode mode in modes)
            {
                renderer.Mode = mode;
                renderer.Render(target);   // includes the GPU-completion fence (GpuContext.WaitForGpu)
                sb.AppendLine($"[OK] {mode}: rendered, drawn={renderer.DrawnMeshes}");
            }
            sb.Insert(0, $"MODES PROBE: {modes.Length} modes rendered without error\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally
        {
            target?.Dispose();
            renderer?.Dispose();
            gpu?.Dispose();
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Selection outline (windowless GPU render + pixel readback): renders a centred quad with the silhouette
    // contour off, on, then cleared, scanning the centre row each time. Proves the contour appears on the
    // silhouette when selected, the interior centre pixel is never tinted, and clearing removes it. No game data.
    internal static void RunOutlineProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_outline.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        GpuContext? gpu = null;
        SceneRenderer? renderer = null;
        SharedRenderTarget? target = null;
        try
        {
            gpu = new GpuContext();
            // Solid shading + no sky → a known flat-grey interior over a plain dark clear colour, so an orange
            // contour pixel is unambiguous against both.
            renderer = new SceneRenderer(gpu) { Mode = RenderMode.Solid, ShowSky = false };

            const int S = 128;
            target = new SharedRenderTarget(gpu, S, S);

            // A 2×2 quad in the XZ plane (Mafia is Z-up), facing the camera on -Y, framed to the central third
            // of the view so background surrounds it on the centre row for the contour to land on.
            var md = new MeshData
            {
                Name = "outline",
                World = Matrix4x4.Identity,
                Positions = new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(1, 0, 1), new Vector3(-1, 0, 1) },
                Normals = new[] { -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY },
                UVs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) },
                Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
                Parts = new[] { new MeshPart(0, 6, null) },
            };
            GpuMesh gm = renderer.AddMesh(md);
            renderer.Camera.LookAt(new Vector3(0f, -8f, 0f), Vector3.Zero);

            // 1) Baseline: nothing selected → no contour on the centre row.
            renderer.ClearSelection();
            renderer.Render(target);
            Check("Baseline (no selection) — centre row has no orange", !ScanlineHasOrange(Readback(gpu, target), S, S / 2));

            // 2) Selected → contour appears on the centre row; interior centre stays untouched.
            renderer.SetSelectionMeshes(new[] { gm });
            renderer.Render(target);
            byte[] sel = Readback(gpu, target);
            Check("Selected mesh draws an orange contour on the centre row", ScanlineHasOrange(sel, S, S / 2));
            (float cr, float cg, float cb) = Pixel(sel, S, S / 2, S / 2);
            Check("Interior centre is untouched (not orange)", !IsOrange(cr, cg, cb), $"rgb=({cr:F2},{cg:F2},{cb:F2})");

            // 3) Hiding the selected mesh removes its contour — the mesh pass skips hidden meshes, so the
            // outline must too (no contour floating around empty space after an eye-toggle).
            gm.Visible = false;
            renderer.Render(target);
            Check("Hidden selected mesh draws no contour", !ScanlineHasOrange(Readback(gpu, target), S, S / 2));
            gm.Visible = true;

            // 4) Clearing removes it again.
            renderer.ClearSelection();
            renderer.Render(target);
            Check("Cleared selection removes the contour", !ScanlineHasOrange(Readback(gpu, target), S, S / 2));

            sb.Insert(0, $"OUTLINE PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "OUTLINE PROBE: FAIL\n\n");
        }
        finally
        {
            target?.Dispose();
            renderer?.Dispose();
            gpu?.Dispose();
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // True if any pixel on row y of a BGRA buffer reads as the orange selection contour.
    private static bool ScanlineHasOrange(byte[] bgra, int width, int y)
    {
        for (int x = 0; x < width; x++)
        {
            (float r, float g, float b) = Pixel(bgra, width, x, y);
            if (IsOrange(r, g, b)) return true;
        }
        return false;
    }

    private static (float r, float g, float b) Pixel(byte[] bgra, int width, int x, int y)
    {
        int idx = (y * width + x) * 4;
        return (bgra[idx + 2] / 255f, bgra[idx + 1] / 255f, bgra[idx + 0] / 255f);
    }

    // The contour colour is (1.0, 0.60, 0.15): strongly red-dominant, little blue. Grey interior and the dark
    // clear colour both have r-b ≈ 0, so this cleanly separates the contour from everything else in the frame.
    private static bool IsOrange(float r, float g, float b) =>
        r > 0.5f && b < 0.4f && g > 0.25f && g < 0.8f && (r - b) > 0.3f;

    // Copies the shared render target to a CPU-readable staging texture and returns its BGRA bytes (tight rows).
    private static byte[] Readback(GpuContext gpu, SharedRenderTarget target) =>
        RenderTargetReadback.Read(gpu, target);
}
