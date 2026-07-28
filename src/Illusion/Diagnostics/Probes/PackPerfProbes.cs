using System.Diagnostics;
using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Archive;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Times what a Build actually spends on one archive: reading the extracted folder and compressing it
/// (<see cref="SdsArchive.Pack"/>), writing the container out, and versioning the previous archive into
/// <c>backups\</c>. Packing goes to a throwaway file — the game's archives are never touched. Output:
/// %TEMP%\illusion_packperf.txt
/// </summary>
internal static class PackPerfProbes
{
    internal static void RunPackPerfProbe(string? archiveName)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_packperf.txt");
        var sb = new StringBuilder();

        try
        {
            if (!ProbeAssert.InitEnv(out string? envError))
            {
                File.WriteAllText(outFile, "ENV ERROR: " + envError);
                return;
            }

            string name = archiveName ?? "city_crash";
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "city_crash", name + ".sds"));
            if (!sds.Exists)
            {
                File.WriteAllText(outFile, $"NOT FOUND: {sds.FullName}");
                return;
            }

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            string[] files = Directory.GetFiles(extracted, "*", SearchOption.AllDirectories);
            long bytes = 0;
            foreach (string f in files) bytes += new FileInfo(f).Length;

            sb.AppendLine($"PACK PERF — {sds.Name}");
            sb.AppendLine($"  archive on disk   {sds.Length / 1024.0 / 1024.0:F1} MB");
            sb.AppendLine($"  extracted         {files.Length} files, {bytes / 1024.0 / 1024.0:F1} MB");
            sb.AppendLine();

            for (int pass = 0; pass < 2; pass++)
            {
                var swPack = Stopwatch.StartNew();
                SdsArchive archive = SdsArchive.Pack(extracted, GameProfile.MafiaII);
                swPack.Stop();

                string tmp = Path.Combine(Path.GetTempPath(), "illusion_packperf.sds");
                var swSave = Stopwatch.StartNew();
                using (FileStream output = File.Create(tmp))
                {
                    archive.Save(output, new SdsWriteOptions());
                }
                swSave.Stop();

                long produced = new FileInfo(tmp).Length;

                // The backup step of a real build: a straight copy of the previous archive.
                string backupTarget = Path.Combine(Path.GetTempPath(), "illusion_packperf_backup.sds");
                var swBackup = Stopwatch.StartNew();
                File.Copy(sds.FullName, backupTarget, overwrite: true);
                swBackup.Stop();

                sb.AppendLine($"pass {pass}{(pass == 0 ? " (cold: JIT + file cache)" : " (warm)")}");
                sb.AppendLine($"  Pack (read + compress)  {swPack.Elapsed.TotalMilliseconds,8:F0} ms");
                sb.AppendLine($"  Save (write container)  {swSave.Elapsed.TotalMilliseconds,8:F0} ms");
                sb.AppendLine($"  Backup (copy previous)  {swBackup.Elapsed.TotalMilliseconds,8:F0} ms");
                sb.AppendLine($"  TOTAL                   "
                            + $"{(swPack.Elapsed + swSave.Elapsed + swBackup.Elapsed).TotalMilliseconds,8:F0} ms"
                            + $"   → {produced / 1024.0 / 1024.0:F1} MB");
                sb.AppendLine();

                try { File.Delete(tmp); } catch { /* scratch */ }
                try { File.Delete(backupTarget); } catch { /* scratch */ }
            }

            sb.AppendLine("A seasonal crash edit packs TWO archives (summer + winter), so a build costs about "
                        + "twice one pass.");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }

        File.WriteAllText(outFile, sb.ToString());
    }
}
