using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Domain;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The district-load cost gate: times the exact hierarchy load the viewport performs and reports
/// the managed allocation and GC pressure it causes. Loading is the toolkit's most user-visible
/// latency, and allocation on this path is what stalls the render thread (a district's worth of
/// per-vertex garbage lands on the Large Object Heap and forces gen2 collections mid-flight), so
/// both numbers are tracked, not just the wall clock.
/// </summary>
internal static class LoadPerfProbes
{
    /// <summary>
    /// Loads one district (default arpatro — the heaviest of the city) twice: the first pass pays
    /// JIT warm-up and extraction, the second is the steady-state number to compare against.
    /// Optional args: district name, then a repeat count. Output: %TEMP%\illusion_loadperf.txt
    /// </summary>
    internal static void RunLoadPerfProbe(string? district, int passes)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_loadperf.txt");
        var sb = new StringBuilder();

        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }

            district ??= "arpatro";
            string path = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(path))
            {
                sb.AppendLine($"no such district: {path}");
                return;
            }

            sb.AppendLine($"LOAD PERF — district={district}");
            sb.AppendLine($"build={(IsDebugBuild() ? "Debug" : "Release")} "
                + $"serverGC={System.Runtime.GCSettings.IsServerGC} "
                + $"latency={System.Runtime.GCSettings.LatencyMode}");
            sb.AppendLine();

            var file = new FileInfo(path);
            for (int pass = 0; pass < passes; pass++)
            {
                // A clean baseline per pass: the numbers below are this load's own cost, not
                // whatever the previous pass left floating.
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);

                long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                var sw = System.Diagnostics.Stopwatch.StartNew();

                (List<SdsFrameNode> roots, List<MeshData> meshes, _) = SdsMeshLoader.LoadHierarchy(file);

                sw.Stop();
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;

                long vertices = 0, triangles = 0;
                foreach (MeshData m in meshes)
                {
                    vertices += m.Positions?.Length ?? 0;
                    triangles += (m.Indices?.Length ?? 0) / 3;
                }

                sb.AppendLine($"pass {pass}{(pass == 0 ? " (cold: JIT + extract)" : " (warm)")}");
                sb.AppendLine($"  time        {sw.Elapsed.TotalMilliseconds,10:F1} ms");
                sb.AppendLine($"  allocated   {allocated / 1048576.0,10:F1} MB"
                    + (vertices > 0 ? $"   ({(double)allocated / vertices,6:F0} B/vertex)" : ""));
                sb.AppendLine($"  GC          gen0 {GC.CollectionCount(0) - g0}, "
                    + $"gen1 {GC.CollectionCount(1) - g1}, gen2 {GC.CollectionCount(2) - g2}");
                sb.AppendLine($"  content     {roots.Count} roots, {meshes.Count} meshes, "
                    + $"{vertices} vertices, {triangles} triangles");
                sb.AppendLine();

                GC.KeepAlive(roots);
                GC.KeepAlive(meshes);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
