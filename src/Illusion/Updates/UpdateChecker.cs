using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Illusion.Updates;

/// <summary>What one look at the releases page came back with.</summary>
internal enum UpdateStatus
{
    /// <summary>The newest release is this one (or older).</summary>
    UpToDate,

    /// <summary>There is a newer release, and it has an archive attached.</summary>
    UpdateAvailable,

    /// <summary>Nothing could be established — no network, a rate limit, an unreadable reply.</summary>
    Failed,
}

/// <summary>The outcome of a check: a status, the release it read (when it read one) and why it failed.</summary>
internal sealed record UpdateCheckResult(UpdateStatus Status, ReleaseInfo? Release, string Error)
{
    public bool HasUpdate => Status == UpdateStatus.UpdateAvailable && Release is not null;
}

/// <summary>
/// Asks GitHub whether a newer release exists. One unauthenticated GET against the releases API, which is what
/// keeps this free of any token or account: <c>/releases/latest</c> skips drafts and pre-releases by itself, so
/// a draft sitting on the releases page while its notes are being written is invisible here.
/// <para>
/// A failure is never an error the user has to deal with — a machine with no network is a supported way to run
/// the toolkit. The startup check keeps the reason to itself and simply shows no button; the settings window is
/// where anyone who wants to know can press the check and read what happened.
/// </para>
/// </summary>
internal static class UpdateChecker
{
    /// <summary>The repository releases are published from.</summary>
    public const string Repository = "hurfy/illusion-toolkit";

    /// <summary>The page a user is sent to when the toolkit cannot install an update itself.</summary>
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // The launcher is built anew every time the editor hands control back, so without this the same check would
    // run again on every return. "Check now" in the settings passes force and refreshes it.
    private static UpdateCheckResult? _cached;

    /// <summary>The last result this session produced, for a window that wants to show it without asking again.</summary>
    public static UpdateCheckResult? Cached => _cached;

    /// <summary>
    /// Looks for a newer release. Never throws: everything that can go wrong comes back as
    /// <see cref="UpdateStatus.Failed"/> with a sentence saying what.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(bool force = false, CancellationToken token = default)
    {
        if (!force && _cached is { } cached) return cached;

        UpdateCheckResult result = await FetchAsync(token).ConfigureAwait(false);
        _cached = result;
        return result;
    }

    private static async Task<UpdateCheckResult> FetchAsync(CancellationToken token)
    {
        try
        {
            using HttpClient http = CreateClient();
            using HttpResponseMessage response =
                await http.GetAsync(LatestReleaseApi, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Failed(Explain(response));
            }

            string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!ReleaseInfo.TryParse(json, out ReleaseInfo? release, out string error))
            {
                return Failed(error);
            }

            return release!.Version.IsNewerThan(AppVersion.Current)
                ? new UpdateCheckResult(UpdateStatus.UpdateAvailable, release, "")
                : new UpdateCheckResult(UpdateStatus.UpToDate, release, "");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // HttpClient reports its own timeout this way rather than as a timeout exception.
            return Failed("GitHub did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return Failed("Could not reach GitHub: " + ex.Message);
        }
        catch (System.IO.IOException ex)
        {
            // A connection dropped mid-reply surfaces from the stream rather than from the request.
            return Failed("The connection to GitHub broke: " + ex.Message);
        }
    }

    /// <summary>Builds the client GitHub's API expects — it refuses a request that names no client at all.</summary>
    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Illusion-Toolkit", AppVersion.Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    /// <summary>Turns an HTTP failure into something worth putting in front of a user.</summary>
    private static string Explain(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "No release has been published yet.";
        }

        // Unauthenticated callers get 60 requests an hour per address. Hitting that from a desktop app means
        // something else on the connection is using the API, so it is worth naming rather than calling it a
        // refusal.
        bool exhausted =
            response.Headers.TryGetValues("x-ratelimit-remaining", out IEnumerable<string>? remaining) &&
            remaining.FirstOrDefault() == "0";
        if (exhausted)
        {
            return "GitHub's hourly request limit for this connection is used up — try again later.";
        }

        return $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    private static UpdateCheckResult Failed(string error) =>
        new(UpdateStatus.Failed, null, error);
}
