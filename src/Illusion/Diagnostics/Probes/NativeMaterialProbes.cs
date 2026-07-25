using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Formats.Materials;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Twin-free verification of the native material-library codec.</summary>
internal static class NativeMaterialProbes
{
    /// <summary>
    /// Every .mtl of the install: read through the native codec, write back, and the output
    /// must be byte-identical to the file on disk. Output: %TEMP%\illusion_native_mtl.txt
    /// </summary>
    internal static void RunMtlParityProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_native_mtl.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }
            string dir = Path.Combine(MafiaEnvironment.GameRoot, "edit", "materials");
            if (!Directory.Exists(dir))
            {
                sb.AppendLine("no materials folder: " + dir);
                return;
            }

            string[] files = Directory.GetFiles(dir, "*.mtl", SearchOption.TopDirectoryOnly);
            Check("the install offers .mtl libraries", files.Length > 0, $"{files.Length} files");

            int fixpoint = 0, errors = 0;
            long materialsSeen = 0;
            var details = new StringBuilder();
            string scratch = Path.Combine(Path.GetTempPath(), "illusion_mtl_probe");
            Directory.CreateDirectory(scratch);
            foreach (string file in files)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    var library = new MaterialLibrary(MaterialVersion.V_57);
                    library.ReadMatFile(file);
                    materialsSeen += library.Materials.Count;

                    string resaved = Path.Combine(scratch, Path.GetFileName(file));
                    library.WriteMatFile(resaved);
                    byte[] output = File.ReadAllBytes(resaved);
                    File.Delete(resaved);

                    if (output.AsSpan().SequenceEqual(bytes)) fixpoint++;
                    else details.AppendLine(
                        $"FIXPOINT DIFF {Path.GetFileName(file)} at {FirstDiff(bytes, output)}");
                }
                catch (Exception ex)
                {
                    errors++;
                    details.AppendLine($"ERROR {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            Check("no library errored", errors == 0, $"{errors} errors");
            Check("native re-save is byte-identical to disk", fixpoint == files.Length - errors,
                $"{fixpoint}/{files.Length} ({materialsSeen} materials)");

            if (details.Length > 0)
            {
                sb.AppendLine();
                sb.Append(details);
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"NATIVE MTL PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }
}
