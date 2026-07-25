using System.Diagnostics;

namespace Illusion.Assets.Sds;

/// <summary>
/// Bulk unpack of ALL game <c>.sds</c> into the mirror <c>&lt;root&gt;\resources</c> (preserving
/// the game structure). The map editor reads strictly from /resources, so unpacking is a mandatory
/// first step. Writes a marker file on completion so it does not unpack again.
/// </summary>
public static class ResourceUnpacker
{
    private static string MarkerPath() => Path.Combine(MafiaEnvironment.ResourcesFolder!, ".unpacked");

    /// <summary>Unpacking already done (marker present)?</summary>
    public static bool IsUnpacked() =>
        MafiaEnvironment.ResourcesFolder != null && File.Exists(MarkerPath());

    /// <summary>All game SDS (recursively from the root), except already unpacked ones/backups.</summary>
    public static List<FileInfo> EnumerateGameSds()
    {
        string root = MafiaEnvironment.GameRoot;
        if (root == null || !Directory.Exists(root)) return new List<FileInfo>();

        return Directory.EnumerateFiles(root, "*.sds", SearchOption.AllDirectories)
            .Where(IsUnpackableGameSds)
            .Select(p => new FileInfo(p))
            .ToList();
    }

    /// <summary>
    /// A game <c>.sds</c> the bulk unpacker should extract into <c>/resources</c>: not already under the mirror
    /// (<c>\resources\</c> / <c>\extracted\</c>), not a MafiaToolkit archive backup (<c>\BackupSDS\</c>), and not one
    /// of our own versioned build backups (<see cref="SdsWriter.BackupFolderName"/> — those are full <c>.sds</c>
    /// copies that live beside the game archives and would otherwise be re-extracted every unpack).
    /// </summary>
    public static bool IsUnpackableGameSds(string path) =>
        path.IndexOf(@"\resources\", StringComparison.OrdinalIgnoreCase) < 0
        && path.IndexOf(@"\extracted\", StringComparison.OrdinalIgnoreCase) < 0
        && path.IndexOf(@"\BackupSDS\", StringComparison.OrdinalIgnoreCase) < 0
        && path.IndexOf($@"\{SdsWriter.BackupFolderName}\", StringComparison.OrdinalIgnoreCase) < 0;

    /// <summary>
    /// Unpack all SDS into /resources. Call on a background thread. <paramref name="progress"/> —
    /// (done, total, name of current). Throws <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    public static void UnpackAll(IProgress<(int done, int total, string name)> progress, CancellationToken ct)
    {
        List<FileInfo> sds = EnumerateGameSds();
        int total = sds.Count;
        int done = 0;

        foreach (FileInfo f in sds)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, total, f.Name));
            try
            {
                SdsMeshLoader.EnsureExtracted(f);
            }
            catch (Exception ex)
            {
                // Skip a corrupt/non-standard SDS — don't crash the rest of the game.
                Debug.WriteLine("Unpack failed " + f.Name + ": " + ex.Message);
            }
            done++;
        }

        Directory.CreateDirectory(MafiaEnvironment.ResourcesFolder!);
        File.WriteAllText(MarkerPath(), $"{total} sds unpacked");
        progress?.Report((total, total, "done"));
    }
}
