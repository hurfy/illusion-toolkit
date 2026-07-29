using System.IO;
using System.Reflection;

namespace Illusion.Updates;

/// <summary>
/// What this build is and where it lives — the two facts the update check is decided by.
/// <para>
/// The version comes from <c>AssemblyInformationalVersion</c>, which the release workflow stamps from the tag
/// (<c>0.3.1+&lt;sha&gt;</c>); the SDK appends the commit itself, so the same attribute also carries which
/// commit this is. A build made outside that workflow gets the <c>&lt;Version&gt;</c> fallback from
/// Directory.Build.props, which is exactly why <see cref="IsDevelopmentBuild"/> exists: that number is whatever
/// the repository last agreed on, so it must never be allowed to overwrite a working tree with a download.
/// </para>
/// </summary>
internal static class AppVersion
{
    static AppVersion()
    {
        Assembly assembly = typeof(AppVersion).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        int plus = informational?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        string? commit = plus >= 0 ? informational![(plus + 1)..] : null;
        Commit = string.IsNullOrEmpty(commit) ? null : commit;

        if (!UpdateVersion.TryParse(informational, out UpdateVersion parsed))
        {
            UpdateVersion.TryParse(assembly.GetName().Version?.ToString(), out parsed);
        }
        Current = parsed;

        // AppContext.BaseDirectory ends in a separator; the directory is what everything else here compares
        // against and joins onto, so it is trimmed once.
        InstallDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        IsDevelopmentBuild = LooksLikeBuildOutput(InstallDirectory);
    }

    /// <summary>The running version, or the zero version when neither attribute could be read.</summary>
    public static UpdateVersion Current { get; }

    /// <summary>The commit this was built from, when the build recorded one.</summary>
    public static string? Commit { get; }

    /// <summary>The commit, cut to the length a release note would print it at.</summary>
    public static string? ShortCommit => Commit is { Length: > 7 } full ? full[..7] : Commit;

    /// <summary>The folder the executable runs from — the folder an update replaces.</summary>
    public static string InstallDirectory { get; }

    /// <summary>The executable an update relaunches once it has replaced the files.</summary>
    public static string ExecutableName => "Illusion.exe";

    /// <summary>
    /// True when this is running out of a build folder rather than an unpacked release. Nothing about such a
    /// tree is safe to overwrite — the files belong to the compiler, not to a download — so the installer
    /// refuses, and says so instead of failing halfway.
    /// </summary>
    public static bool IsDevelopmentBuild { get; }

    /// <summary>The path shape <see cref="IsDevelopmentBuild"/> recognises, separated out so a probe can put
    /// paths through it that this machine does not have.</summary>
    internal static bool LooksLikeBuildOutput(string directory)
    {
        string normalized = Path.TrimEndingDirectorySeparator(directory)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string separator = Path.DirectorySeparatorChar.ToString();
        return normalized.Contains(separator + "bin" + separator + "Debug" + separator, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(separator + "bin" + separator + "Release" + separator, StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(separator + "bin" + separator + "Debug", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(separator + "bin" + separator + "Release", StringComparison.OrdinalIgnoreCase);
    }
}
