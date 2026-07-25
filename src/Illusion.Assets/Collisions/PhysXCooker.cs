using System.Diagnostics;
using System.Numerics;
using System.Text;
using Illusion.Formats.Collisions;

namespace Illusion.Assets.Collisions;

/// <summary>Outcome of a cook: the finished cooked blob, or why there is not one.</summary>
/// <param name="Cooked">A validated, 32-bit-index cooked mesh ready to mint, or null.</param>
/// <param name="Refusal">Human-readable reason, or null on success.</param>
public readonly record struct CookResult(byte[]? Cooked, string? Refusal);

/// <summary>
/// Cooks triangles into a PhysX collision mesh by shelling out to the vendored <c>M2PhysX.exe</c>.
/// <para>
/// Everything about the subprocess lives here, so there is one place that knows the cooker exists and one place
/// to get its failure modes right — and they are unusual. <b>The exit code means nothing:</b> a cook that failed
/// still exits 0, having written a zero-byte file. Success is read from the output instead, and only after it
/// survives the full chain — non-empty, decodes, its OPCODE model re-parses, its metadata tail is the shape this
/// toolkit understands, and it decodes again after the indices are widened.
/// </para>
/// <para>
/// The old toolkit called the same binary with fixed temp filenames in the working directory, which two
/// instances would clobber; each cook here gets its own directory instead.
/// </para>
/// </summary>
public static class PhysXCooker
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Where per-cook scratch directories live, so they can be swept as a group.</summary>
    private static string ScratchRoot => Path.Combine(Path.GetTempPath(), "Illusion", "cook");

    /// <summary>
    /// Cooks one hull. Never throws for bad geometry, a missing runtime or a failed cook — those come back as
    /// <see cref="CookResult.Refusal"/>, because a push that refuses one object must still apply the rest.
    /// </summary>
    public static CookResult Cook(Vector3[] positions, int[] triangleIndices, ushort[] surfaceIds)
    {
        CookAvailability availability = PhysXRuntimeLocator.Check();
        if (!availability.Available) return new CookResult(null, availability.Detail);

        byte[]? input = CookerMeshBin.TryWrite(positions, triangleIndices, surfaceIds, out string? refusal);
        if (input == null) return new CookResult(null, refusal);

        string dir = Path.Combine(ScratchRoot, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string inPath = Path.Combine(dir, "mesh.bin");
            string outPath = Path.Combine(dir, "cooked.bin");
            File.WriteAllBytes(inPath, input);

            string? runFailure = Run(PhysXRuntimeLocator.CookerPath, inPath, outPath, out string output);
            if (runFailure != null) return new CookResult(null, runFailure);

            if (!File.Exists(outPath)) return new CookResult(null, Explain("the cooker produced no file", output));
            byte[] cooked = File.ReadAllBytes(outPath);
            // The signature failure: exit 0, empty file. PhysX rejected the geometry.
            if (cooked.Length == 0)
                return new CookResult(null, Explain("PhysX rejected this geometry", output));

            return Validate(cooked);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CookResult(null, "the cook could not use its scratch folder: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Deletes cook scratch directories left behind by a crash or a kill. Cheap, best-effort, and called at
    /// startup — a cook that never got to clean up should not accumulate in %TEMP% forever.
    /// </summary>
    public static void SweepStaleScratch()
    {
        try
        {
            if (!Directory.Exists(ScratchRoot)) return;
            DateTime cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (string dir in Directory.GetDirectories(ScratchRoot))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(dir) < cutoff) Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string? Run(string exe, string inPath, string outPath, out string output)
    {
        var captured = new StringBuilder();
        output = string.Empty;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(exe)
        {
            // Never the current working directory: the cooker resolves nothing relative to it, and pinning the
            // process somewhere stable keeps a directory the app happens to be sitting in out of the picture.
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.StartInfo.ArgumentList.Add("-CookTriangleMesh");
        process.StartInfo.ArgumentList.Add(inPath);
        process.StartInfo.ArgumentList.Add(outPath);

        // Read both pipes asynchronously. Waiting for exit while a full pipe blocks the child is the classic
        // way to deadlock a redirected subprocess, and this one is chatty on success.
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (captured) captured.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (captured) captured.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "the PhysX cooker could not be started: " + ex.Message;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
            return $"the PhysX cooker did not finish within {Timeout.TotalSeconds:F0} s and was stopped";
        }
        process.WaitForExit(); // flushes the async readers

        lock (captured) output = captured.ToString().Trim();
        return null;
    }

    // Nothing the cooker writes is trusted until it survives the same reader the game's own hulls go through.
    private static CookResult Validate(byte[] cooked)
    {
        byte[] widened;
        try
        {
            CookedTriangleMesh.Decode(cooked);
            CookedTriangleMesh.ValidateOpcodeTail(cooked);
            if (!CookedMeshTail.IsSupported(cooked, out string? tailProblem))
                return new CookResult(null, tailProblem);

            // Shipped Mafia II collision is 32-bit throughout; the cooker picks the narrowest width that fits.
            widened = CookedIndexWidener.Widen(cooked);
            CookedTriangleMesh.Decode(widened);
            CookedTriangleMesh.ValidateOpcodeTail(widened);
        }
        catch (CollisionDecodeException ex)
        {
            return new CookResult(null, "the cooked mesh did not survive validation: " + ex.Message);
        }
        return new CookResult(widened, null);
    }

    private static string Explain(string what, string cookerOutput) =>
        string.IsNullOrWhiteSpace(cookerOutput) ? what : what + " (" + cookerOutput.Replace('\n', ' ').Trim() + ")";
}
