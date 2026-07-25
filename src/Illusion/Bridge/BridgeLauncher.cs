using System.Diagnostics;
using System.IO;

namespace Illusion.Bridge;

/// <summary>
/// Spawns a bridge-dedicated Blender: the addon shipped in the toolkit's output folder is injected
/// via <c>BLENDER_USER_SCRIPTS</c> (zero-install — nothing lands in the user's Blender config) and
/// enabled by the bootstrap script. UseShellExecute is off because the environment must be set.
/// </summary>
internal static class BridgeLauncher
{
    /// <summary>The addon payload shipped beside the exe (Content items in the csproj).</summary>
    public static string AddonRoot => Path.Combine(AppContext.BaseDirectory, "BlenderAddon");

    /// <param name="blenderExe">Resolved blender.exe path.</param>
    /// <param name="redirectOutput">Capture stdout/stderr (diagnostic probes; the caller must then
    /// consume the streams via <see cref="Process.OutputDataReceived"/> or reads).</param>
    public static Process Launch(string blenderExe, bool redirectOutput = false)
    {
        string bootstrap = Path.Combine(AddonRoot, "bootstrap.py");
        if (!File.Exists(bootstrap))
            throw new FileNotFoundException("Bridge addon bootstrap is missing from the install.", bootstrap);

        var psi = new ProcessStartInfo(blenderExe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(blenderExe)!,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            // blender.exe is a console-subsystem binary — without this a console window opens next
            // to Blender and just sits there. Blender's own GUI window is unaffected.
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--python");
        psi.ArgumentList.Add(bootstrap);
        psi.Environment["BLENDER_USER_SCRIPTS"] = Path.Combine(AddonRoot, "scripts");
        if (redirectOutput)
        {
            // Python block-buffers stdout on a pipe — tracebacks would sit unflushed until exit
            // (and be lost on a kill). Unbuffered output makes the diagnostics live.
            psi.Environment["PYTHONUNBUFFERED"] = "1";
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("Blender failed to start.");
    }
}
