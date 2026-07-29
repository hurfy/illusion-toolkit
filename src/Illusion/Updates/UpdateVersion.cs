using System.Globalization;

namespace Illusion.Updates;

/// <summary>
/// A release version: <c>major.minor.patch</c> with an optional pre-release suffix. That is what this project's
/// tags carry (<c>v0.3.1</c>) and what its own assembly reads as (<c>0.3.1+&lt;sha&gt;</c>) — the build metadata
/// after <c>+</c> says nothing about order and is dropped on the way in.
/// <para>
/// Only as much of SemVer as "is theirs newer than mine" needs: the numeric triple decides, and a pre-release
/// loses to the same triple without one, so a <c>v0.4.0-rc1</c> tag can never talk an installed 0.4.0 into
/// replacing itself. Two pre-releases of the same triple are ordered as plain text, which is NOT what SemVer
/// says (it compares dot-separated identifiers); this project publishes none, and the check only ever reads the
/// latest release, which GitHub never reports as a pre-release.
/// </para>
/// </summary>
internal readonly record struct UpdateVersion(int Major, int Minor, int Patch, string? PreRelease)
{
    /// <summary>The pre-release suffix, never null — <c>""</c> means a normal release.</summary>
    public string Suffix => PreRelease ?? "";

    /// <summary>True for the zero version, which is what a failed parse leaves behind.</summary>
    public bool IsEmpty => Major == 0 && Minor == 0 && Patch == 0 && Suffix.Length == 0;

    /// <summary>
    /// Reads a tag (<c>v0.3.1</c>), an informational version (<c>0.3.1+sha</c>) or a plain assembly version
    /// (<c>0.3.1.0</c>). One to four numeric components are accepted and the fourth is ignored: the release
    /// workflow only ever produces three, but the local fallback comes from an <c>AssemblyVersion</c>, which
    /// always has four.
    /// </summary>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V')) span = span[1..];

        int plus = span.IndexOf('+');
        if (plus >= 0) span = span[..plus];

        string suffix = "";
        int dash = span.IndexOf('-');
        if (dash >= 0)
        {
            suffix = span[(dash + 1)..].ToString();
            span = span[..dash];
            // "1.2.3-" is malformed rather than "1.2.3 with an empty suffix" — refuse it, so a mangled tag
            // cannot read as a normal release. The charset is SemVer's, which the release workflow already
            // enforces on the tag, and it is load-bearing rather than pedantic: a version becomes a folder
            // name under the staging root, so a suffix carrying a separator — or being nothing but dots —
            // would put a download somewhere other than where the caller believes.
            if (!IsUsableSuffix(suffix)) return false;
        }

        if (span.Length == 0) return false;

        Span<int> parts = stackalloc int[4];
        int count = 0;
        foreach (Range range in span.Split('.'))
        {
            if (count == 4) return false;
            ReadOnlySpan<char> part = span[range];
            if (part.Length == 0 ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                return false;
            }
            parts[count++] = value;
        }

        version = new UpdateVersion(
            parts[0],
            count > 1 ? parts[1] : 0,
            count > 2 ? parts[2] : 0,
            suffix.Length == 0 ? null : suffix);
        return true;
    }

    /// <summary>SemVer's pre-release charset, and it must open on a letter or a digit — which is what stops
    /// <c>".."</c> from ever being one.</summary>
    private static bool IsUsableSuffix(string suffix)
    {
        if (suffix.Length == 0 || !char.IsAsciiLetterOrDigit(suffix[0])) return false;
        foreach (char c in suffix)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-') return false;
        }
        return true;
    }

    /// <summary>Whether this version supersedes <paramref name="other"/> — the question the update check asks.</summary>
    public bool IsNewerThan(UpdateVersion other)
    {
        if (Major != other.Major) return Major > other.Major;
        if (Minor != other.Minor) return Minor > other.Minor;
        if (Patch != other.Patch) return Patch > other.Patch;

        string mine = Suffix, theirs = other.Suffix;
        if (mine.Length == 0) return theirs.Length != 0;   // a release beats its own pre-releases
        if (theirs.Length == 0) return false;
        return string.CompareOrdinal(mine, theirs) > 0;
    }

    public override string ToString() =>
        Suffix.Length == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Suffix}");
}
