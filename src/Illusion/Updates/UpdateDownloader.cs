using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Illusion.Updates;

/// <summary>How far a download has got. <see cref="Total"/> is 0 when the server did not say.</summary>
internal readonly record struct DownloadProgress(long Received, long Total);

/// <summary>A downloaded release, unpacked and ready for <see cref="UpdateInstaller"/> to copy into place.</summary>
/// <param name="Version">The version that was staged.</param>
/// <param name="PayloadDirectory">The folder holding exactly what an install folder should contain.</param>
/// <param name="ExecutablePath">The staged executable, which is what performs the swap.</param>
internal sealed record StagedUpdate(UpdateVersion Version, string PayloadDirectory, string ExecutablePath);

/// <summary>
/// Fetches a release archive and unpacks it under <c>%LOCALAPPDATA%\Illusion\updates</c>. Staging outside the
/// install folder is the point: nothing in the folder being replaced is touched until the swap runs, so a
/// download that fails, is cancelled, or arrives corrupt leaves a working toolkit behind.
/// </summary>
internal static class UpdateDownloader
{
    /// <summary>Where downloads are unpacked — beside settings.json, never inside the install folder.</summary>
    public static string StagingRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Illusion", "updates");

    private static StagedUpdate? _staged;

    /// <summary>
    /// The build already downloaded this session, when it is the one being asked about and its files are still
    /// there. Declining the restart and then asking again should not fetch the same archive twice.
    /// </summary>
    public static StagedUpdate? ReadyFor(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return _staged is { } staged && staged.Version == release.Version && File.Exists(staged.ExecutablePath)
            ? staged
            : null;
    }

    /// <summary>
    /// Downloads the release's archive, checks it against the published SHA256 when there is one, and unpacks
    /// it. Throws on every failure — the caller shows the message, and the install folder is untouched either
    /// way.
    /// </summary>
    public static async Task<StagedUpdate> DownloadAsync(
        ReleaseInfo release,
        IProgress<DownloadProgress>? progress,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        string workDirectory = Path.Combine(StagingRoot, release.Version.ToString());
        Reset(workDirectory);

        string archivePath = Path.Combine(workDirectory, release.AssetName);
        using (HttpClient http = CreateClient())
        {
            await FetchAsync(http, release, archivePath, progress, token).ConfigureAwait(false);

            if (release.ChecksumUrl is { Length: > 0 } checksumUrl)
            {
                string published = await http.GetStringAsync(checksumUrl, token).ConfigureAwait(false);
                string? expected = ParseChecksum(published, release.AssetName);
                if (expected is null)
                {
                    throw new InvalidDataException(
                        $"{release.ChecksumName} carries no checksum for {release.AssetName}.");
                }
                if (!Verify(archivePath, expected))
                {
                    throw new InvalidDataException(
                        "The downloaded archive does not match the checksum published with the release. " +
                        "Nothing was installed.");
                }
            }
        }

        StagedUpdate staged = Stage(archivePath, Path.Combine(workDirectory, "staged"), release.Version);

        // The archive has served its purpose and is the larger half of what is on disk.
        try { File.Delete(archivePath); }
        catch (IOException) { /* a scanner still holding it costs disk, not correctness */ }
        catch (UnauthorizedAccessException) { }

        _staged = staged;
        return staged;
    }

    /// <summary>
    /// Unpacks a release archive and finds the payload in it. The archive the release workflow builds wraps
    /// everything in one folder (<c>Illusion-Toolkit-&lt;version&gt;-win-x64</c>), so the payload is that folder
    /// rather than the extraction root; an archive packed flat works too.
    /// </summary>
    public static StagedUpdate Stage(string archivePath, string stageDirectory, UpdateVersion version)
    {
        Reset(stageDirectory);
        ZipFile.ExtractToDirectory(archivePath, stageDirectory);

        string payload = ResolvePayloadRoot(stageDirectory);
        string executable = Path.Combine(payload, AppVersion.ExecutableName);
        if (!File.Exists(executable))
        {
            throw new InvalidDataException(
                $"The archive holds no {AppVersion.ExecutableName} — it is not a toolkit release.");
        }
        return new StagedUpdate(version, payload, executable);
    }

    /// <summary>Whether a file hashes to the expected SHA256 (hex, case does not matter).</summary>
    public static bool Verify(string filePath, string expectedSha256)
    {
        using FileStream stream = File.OpenRead(filePath);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pulls the hash for one file out of a checksum file. Both shapes are read: the <c>sha256sum</c> form
    /// (<c>&lt;hash&gt;  &lt;name&gt;</c>, one file per line) and a file holding nothing but the hash. A line
    /// naming a different file is ignored, so a shared sums file cannot hand over the wrong hash.
    /// </summary>
    internal static string? ParseChecksum(string text, string assetName)
    {
        string? loneHash = null;
        int hashCount = 0;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || !IsSha256(fields[0])) continue;

            hashCount++;
            loneHash ??= fields[0];

            if (fields.Length < 2) continue;
            // sha256sum marks a binary-mode file with a leading '*'; the name may also be a path.
            string named = Path.GetFileName(fields[1].TrimStart('*'));
            if (string.Equals(named, assetName, StringComparison.OrdinalIgnoreCase)) return fields[0];
        }

        // A file with exactly one hash in it and no matching name still means that one archive.
        return hashCount == 1 ? loneHash : null;
    }

    private static bool IsSha256(string token)
    {
        if (token.Length != 64) return false;
        foreach (char c in token)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    private static async Task FetchAsync(
        HttpClient http,
        ReleaseInfo release,
        string archivePath,
        IProgress<DownloadProgress>? progress,
        CancellationToken token)
    {
        using HttpResponseMessage response = await http
            .GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? release.AssetSize;
        progress?.Report(new DownloadProgress(0, total));

        await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var destination = new FileStream(
            archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        byte[] buffer = new byte[64 * 1024];
        long received = 0;
        long lastReported = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            received += read;

            // One report per 64 KB — around a hundred over a release archive. Enough for a bar that moves,
            // few enough that the UI thread is not woken thousands of times for it.
            if (received - lastReported >= 64 * 1024)
            {
                lastReported = received;
                progress?.Report(new DownloadProgress(received, total));
            }
        }
        progress?.Report(new DownloadProgress(received, total == 0 ? received : total));
    }

    private static HttpClient CreateClient()
    {
        // No overall timeout: the whole archive travels through this client, and a slow connection is not a
        // failure. The cancellation token is how a download is given up on.
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Illusion-Toolkit", AppVersion.Current.ToString()));
        return http;
    }

    /// <summary>An empty directory at that path, whatever was there before.</summary>
    private static void Reset(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
    }

    /// <summary>Descends through wrapper folders — a level holding one folder and no files is packaging.</summary>
    private static string ResolvePayloadRoot(string extracted)
    {
        string current = extracted;
        for (int depth = 0; depth < 4; depth++)
        {
            string[] directories = Directory.GetDirectories(current);
            if (directories.Length != 1 || Directory.GetFiles(current).Length != 0) break;
            current = directories[0];
        }
        return current;
    }
}
