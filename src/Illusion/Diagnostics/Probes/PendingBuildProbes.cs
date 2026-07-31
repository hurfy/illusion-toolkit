using System.IO;
using System.Text;
using Illusion.Assets.Sds;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The two ways an edit used to go quietly missing, both about book-keeping rather than about bytes:
/// a saved-but-unpacked archive being forgotten the moment its scene closed, and two editor windows
/// working on the one extracted copy an archive has. Needs no game data and no GPU.
/// Output: %TEMP%\illusion_pending.txt
/// </summary>
internal static class PendingBuildProbes
{
    internal static void RunPendingProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_pending.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            // ── Who is holding which archive ──
            //
            // Owners are compared by reference, so any two distinct objects stand in for two editor windows.
            var mapEditor = new object();
            var resourceEditor = new object();
            var district = new FileInfo(Path.Combine(Path.GetTempPath(), "illusion_probe_district.sds"));
            var car = new FileInfo(Path.Combine(Path.GetTempPath(), "illusion_probe_car.sds"));

            OpenArchives.ReleaseAll(mapEditor);
            OpenArchives.ReleaseAll(resourceEditor);

            Check("nothing is held to begin with",
                !OpenArchives.IsHeldByAnyoneElse(district, resourceEditor));

            OpenArchives.Acquire(district, mapEditor);
            Check("an archive one window loaded is reported to the other",
                OpenArchives.IsHeldByAnyoneElse(district, resourceEditor));
            Check("a window is not warned about itself",
                !OpenArchives.IsHeldByAnyoneElse(district, mapEditor));
            Check("an archive nobody loaded stays clear",
                !OpenArchives.IsHeldByAnyoneElse(car, resourceEditor));

            // The same window loading the same district twice (a reload) must not leave a phantom holder
            // behind that outlives the release.
            OpenArchives.Acquire(district, mapEditor);
            OpenArchives.Release(district, mapEditor);
            Check("loading the same archive twice in one window still lets go once",
                !OpenArchives.IsHeldByAnyoneElse(district, resourceEditor));

            OpenArchives.Acquire(district, mapEditor);
            OpenArchives.Acquire(car, mapEditor);
            OpenArchives.ReleaseAll(mapEditor);
            Check("a scene reset lets go of everything that window held",
                !OpenArchives.IsHeldByAnyoneElse(district, resourceEditor)
                && !OpenArchives.IsHeldByAnyoneElse(car, resourceEditor));

            // Two windows may deliberately hold the same archive; letting go of one must not clear the other.
            OpenArchives.Acquire(district, mapEditor);
            OpenArchives.Acquire(district, resourceEditor);
            OpenArchives.Release(district, mapEditor);
            Check("one window letting go does not speak for the other",
                OpenArchives.IsHeldByAnyoneElse(district, mapEditor));
            OpenArchives.ReleaseAll(resourceEditor);
            Check("and then it is clear", !OpenArchives.IsHeldByAnyoneElse(district, mapEditor));

            sb.Insert(0, $"PENDING PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }
}
