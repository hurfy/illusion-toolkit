using Microsoft.Win32;

namespace Illusion.Assets.Collisions;

/// <summary>Whether hull shape editing can run here, and if not, what to tell the user to install.</summary>
/// <param name="Available">True when a cook can actually be attempted.</param>
/// <param name="Detail">Why it cannot, or where the pieces were found.</param>
public readonly record struct CookAvailability(bool Available, string Detail);

/// <summary>
/// Finds the two things a cook needs: the vendored <c>M2PhysX.exe</c>, and NVIDIA's PhysX engine.
/// <para>
/// The exe imports only <c>PhysXLoader.dll</c>; the loader then locates the engine through the registry, not
/// through <c>PATH</c> and not from DLLs sitting next to the exe — copies placed there are provably ignored. So
/// cooking depends on the user having installed NVIDIA's freely distributed PhysX System Software, and
/// specifically on it carrying the legacy <b>v2.8.0</b> engine, which is the version the exe asks for by name.
/// </para>
/// <para>
/// Nothing here throws or prompts. A missing runtime disables one feature and says so; the rest of the toolkit
/// does not care.
/// </para>
/// </summary>
public static class PhysXRuntimeLocator
{
    private const string AgeiaKey = @"SOFTWARE\WOW6432Node\AGEIA Technologies";
    private const string CorePathValue = "PhysXCore Path";
    private const string RequiredEngine = "v2.8.0";

    private static CookAvailability? _cached;

    /// <summary>Path of the vendored cooker beside the app, whether or not it exists.</summary>
    public static string CookerPath =>
        Path.Combine(AppContext.BaseDirectory, "tools", "M2PhysX", "M2PhysX.exe");

    /// <summary>
    /// Whether a cook can be attempted, cached for the session — the answer cannot change without an install,
    /// and this is called every time a push arrives.
    /// </summary>
    public static CookAvailability Check() => _cached ??= Probe();

    /// <summary>Forgets the cached answer, so a user who installs the runtime need not restart.</summary>
    public static void Forget() => _cached = null;

    private static CookAvailability Probe()
    {
        string cooker = CookerPath;
        if (!File.Exists(cooker))
        {
            return new CookAvailability(false,
                "the PhysX cooker is missing from this build (expected at " + cooker + ")");
        }

        string? enginePath = ReadEnginePath();
        if (enginePath == null)
        {
            return new CookAvailability(false,
                "NVIDIA PhysX System Software is not installed — hull shape editing needs it "
                + "(the legacy v2.8.0 engine specifically). Everything else works without it.");
        }

        string core = Path.Combine(enginePath, RequiredEngine, "PhysXCore.dll");
        if (!File.Exists(core))
        {
            return new CookAvailability(false,
                $"the installed PhysX System Software has no {RequiredEngine} engine (looked in {enginePath}). "
                + "Hull shape editing needs that legacy version.");
        }

        return new CookAvailability(true, "PhysX " + RequiredEngine + " at " + core);
    }

    // HKLM only: the engine path is written by the system-software installer, not per user.
    private static string? ReadEnginePath()
    {
        // The cooker is a 32-bit Windows executable; off Windows there is nothing to find and no registry to
        // look in. Assets targets a platform-neutral framework, so the guard is required, not defensive.
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(AgeiaKey);
            return key?.GetValue(CorePathValue) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
            or IOException)
        {
            return null; // an unreadable registry is indistinguishable from an absent install, and just as fatal
        }
    }
}
