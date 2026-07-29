using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Illusion.Updates;

/// <summary>What the staged build is being asked to do: replace the files in one folder, once one process has
/// gone.</summary>
internal sealed record ApplyRequest(string TargetDirectory, int ProcessId);

/// <summary>
/// The swap. Windows will not let a running executable be overwritten, so the toolkit cannot install an update
/// into itself: the downloaded build is started out of its staging folder with
/// <see cref="ApplySwitch"/>, the running one exits, and the staged copy does the replacing and then starts the
/// installed executable again.
/// <para>
/// That makes the argument shape a compatibility contract in the awkward direction — it is WRITTEN by the old
/// build and READ by the new one, so a future version may add optional arguments but must keep reading
/// <c>--apply-update &lt;target folder&gt; &lt;process id&gt;</c>. <c>--probe-update</c> pins it.
/// </para>
/// <para>
/// Files the new release no longer has are left where they are rather than swept: an install folder also holds
/// what the user put there (archive backups, an extracted tree), and deleting by difference cannot tell the two
/// apart. A stale assembly costs disk; a deleted backup costs work.
/// </para>
/// </summary>
internal static class UpdateInstaller
{
    /// <summary>The switch that turns a normal start into the file swap. Part of the frozen contract above.</summary>
    public const string ApplySwitch = "--apply-update";

    /// <summary>How long the staged build waits for the running one to let go of its files.</summary>
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    private const int CopyAttempts = 6;
    private static readonly TimeSpan CopyRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Whether this copy of the toolkit is one an update may replace, and why not when it is not. Checked
    /// before anything is downloaded, so a refusal costs nothing.
    /// </summary>
    public static bool CanInstall(out string reason)
    {
        if (AppVersion.IsDevelopmentBuild)
        {
            reason = "This is a build from source rather than an unpacked release, and installing over it " +
                     "would overwrite the build output. Update the repository instead.";
            return false;
        }

        if (!IsWritable(AppVersion.InstallDirectory))
        {
            reason = $"This account cannot write to {AppVersion.InstallDirectory}. Move the toolkit somewhere " +
                     "writable, or download the release and unpack it over this folder yourself.";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// Starts the staged build as the updater and returns; the caller is expected to shut down immediately
    /// afterwards, because the swap cannot begin until it does.
    /// </summary>
    public static void Start(StagedUpdate staged)
    {
        ArgumentNullException.ThrowIfNull(staged);

        var start = new ProcessStartInfo(staged.ExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = staged.PayloadDirectory,
        };
        foreach (string argument in BuildApplyArguments(AppVersion.InstallDirectory, Environment.ProcessId))
        {
            start.ArgumentList.Add(argument);
        }

        if (Process.Start(start) is null)
        {
            throw new InvalidOperationException("The downloaded toolkit did not start.");
        }
    }

    /// <summary>The command line <see cref="Start"/> hands over — spelled out here so a probe can pin it.</summary>
    internal static string[] BuildApplyArguments(string targetDirectory, int processId) =>
        new[] { ApplySwitch, targetDirectory, processId.ToString(CultureInfo.InvariantCulture) };

    /// <summary>Reads that command line back. False for every other way the toolkit is started.</summary>
    public static bool TryReadApplyRequest(string[] args, out ApplyRequest? request)
    {
        request = null;
        if (args is not { Length: 3 } || !string.Equals(args[0], ApplySwitch, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(args[1]) ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
        {
            return false;
        }

        request = new ApplyRequest(args[1], processId);
        return true;
    }

    /// <summary>
    /// Performs the swap: wait for the old process, copy this staged tree over the target, start it again.
    /// Runs in the staged build, so the tree being copied is the folder this very process runs from.
    /// </summary>
    public static void Apply(ApplyRequest request, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        report?.Invoke("Waiting for the toolkit to close…");
        WaitForExit(request.ProcessId);

        report?.Invoke("Replacing the files…");
        int copied = CopyTree(AppVersion.InstallDirectory, request.TargetDirectory);

        report?.Invoke($"Starting the toolkit again ({copied} files replaced)…");
        string executable = Path.Combine(request.TargetDirectory, AppVersion.ExecutableName);
        Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = request.TargetDirectory,
        });
    }

    /// <summary>
    /// Copies every file of <paramref name="source"/> over <paramref name="target"/>, creating folders as it
    /// goes, and answers how many files it wrote. Nothing is deleted (see the type's remarks).
    /// </summary>
    internal static int CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);

        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyWithRetry(file, destination);
            copied++;
        }
        return copied;
    }

    /// <summary>
    /// Drops the staging folders a finished (or abandoned) update left behind. Best-effort and off the critical
    /// path: an update that just ran is still holding its own folder open, and the next start clears it.
    /// </summary>
    public static void SweepStaging()
    {
        try
        {
            if (!Directory.Exists(UpdateDownloader.StagingRoot)) return;

            foreach (string directory in Directory.EnumerateDirectories(UpdateDownloader.StagingRoot))
            {
                // Never delete the ground this process is standing on — a toolkit started by hand out of a
                // staging folder is still a running toolkit.
                if (AppVersion.InstallDirectory.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void WaitForExit(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;   // already gone, which is the case this is waiting for
        }

        using (process)
        {
            // Process ids are reused. If the number now belongs to something else, waiting on it would hang
            // for the whole timeout and then refuse an update that had nothing in its way.
            string name;
            try { name = process.ProcessName; }
            catch (InvalidOperationException) { return; }

            if (!name.Equals("Illusion", StringComparison.OrdinalIgnoreCase)) return;

            if (!process.WaitForExit((int)ExitTimeout.TotalMilliseconds))
            {
                throw new TimeoutException(
                    "The toolkit is still running, so its files cannot be replaced. Close it and try again.");
            }
        }
    }

    private static void CopyWithRetry(string source, string destination)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < CopyAttempts && ex is IOException or UnauthorizedAccessException)
            {
                // A virus scanner reading the file the old process just released, or a handle Windows has not
                // finished closing. Both clear in well under a second. The other way to be refused is a
                // read-only attribute on the file being replaced — waiting will not lift that one.
                if (ex is UnauthorizedAccessException) TryClearReadOnly(destination);
                Thread.Sleep(CopyRetryDelay);
            }
        }
    }

    private static void TryClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, ".illusion-update-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
