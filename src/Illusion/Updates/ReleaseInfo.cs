using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Illusion.Updates;

/// <summary>
/// One published release, reduced to what an update needs: which version it is, where its page is, and the one
/// archive to download (plus the checksum file beside it, when that release published one).
/// </summary>
internal sealed record ReleaseInfo(
    UpdateVersion Version,
    string Tag,
    string Title,
    string PageUrl,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string? ChecksumName,
    string? ChecksumUrl)
{
    /// <summary>The archive's size the way a download prompt should say it.</summary>
    public string AssetSizeText =>
        AssetSize <= 0
            ? ""
            : (AssetSize / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// Reads one release out of the GitHub API's JSON. Everything is validated rather than assumed: the reply
    /// is a third party's, and a release with no usable archive (someone published the tag before the workflow
    /// finished uploading) has to read as "nothing to install", not as a download of nothing.
    /// </summary>
    public static bool TryParse(string json, out ReleaseInfo? release, out string error)
    {
        release = null;
        error = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "GitHub replied with something that is not a release.";
                return false;
            }

            // /releases/latest never returns either of these; a fixture or a hand-picked release id can.
            if (Flag(root, "draft") || Flag(root, "prerelease"))
            {
                error = "The newest release is not published yet.";
                return false;
            }

            string tag = Text(root, "tag_name") ?? "";
            if (!UpdateVersion.TryParse(tag, out UpdateVersion version))
            {
                error = tag.Length == 0
                    ? "The release carries no tag."
                    : $"'{tag}' is not a version this build knows how to compare.";
                return false;
            }

            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            {
                error = $"Release {tag} has no files attached.";
                return false;
            }

            JsonElement? archive = null;
            var checksums = new List<JsonElement>();
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = Text(asset, "name") ?? "";
                if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    checksums.Add(asset);
                }
                // The Windows build is the only one there is, but naming it explicitly keeps a future second
                // archive from being downloaded onto the wrong machine.
                else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                         name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                {
                    archive ??= asset;
                }
            }

            if (archive is not { } zip)
            {
                error = $"Release {tag} has no win-x64 archive attached.";
                return false;
            }

            string assetName = Text(zip, "name") ?? "";
            string? assetUrl = Text(zip, "browser_download_url");
            if (string.IsNullOrEmpty(assetUrl))
            {
                error = $"'{assetName}' has no download address.";
                return false;
            }

            // The archive's name becomes a file name under the staging folder, so it has to BE a file name.
            // GitHub does not allow a separator in one, which is exactly why this is worth asserting rather
            // than assuming: the reply is still someone else's, and a name that walks out of the staging
            // folder would put a download anywhere it liked.
            if (assetName.Length == 0 || Path.GetFileName(assetName) != assetName)
            {
                error = $"'{assetName}' is not a file name.";
                return false;
            }

            // Prefer "<archive>.sha256" over a shared sums file, and take whatever single one is there when no
            // name matches — a release carrying one checksum file meant it for the one archive.
            JsonElement? checksum = null;
            foreach (JsonElement candidate in checksums)
            {
                if (string.Equals(Text(candidate, "name"), assetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksum = candidate;
                    break;
                }
            }
            checksum ??= checksums.Count == 1 ? checksums[0] : null;

            release = new ReleaseInfo(
                version,
                tag,
                Text(root, "name") ?? tag,
                Text(root, "html_url") ?? "",
                assetName,
                assetUrl,
                Number(zip, "size"),
                checksum is { } sums ? Text(sums, "name") : null,
                checksum is { } sums2 ? Text(sums2, "browser_download_url") : null);
            return true;
        }
        catch (JsonException ex)
        {
            error = "GitHub's reply could not be read: " + ex.Message;
            return false;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static long Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long number)
            ? number
            : 0;
}
