namespace Illusion.Formats.Compression;

/// <summary>
/// Binds the native core's oodle shim to the game's own <c>oo2core_8_win64.dll</c> (Oodle is
/// proprietary and not redistributable, so it is loaded from the install rather than shipped).
/// Only Mafia II DE archives use oodle blocks; a classic install never triggers a load, so a
/// missing DLL is not fatal here — it only surfaces if an oodle block is actually decompressed.
/// </summary>
public static class OodleNative
{
    private const string DllName = "oo2core_8_win64.dll";

    private static bool _resolved;

    /// <summary>Binds the native oodle shim to the DLL in <paramref name="folder"/>. Idempotent;
    /// returns false when the DLL is not present there.</summary>
    public static bool TryResolveFrom(string folder)
    {
        if (_resolved)
        {
            return true;
        }

        string candidate = Path.Combine(folder, DllName);
        if (!File.Exists(candidate))
        {
            return false;
        }

        _resolved = Native.NativeFormats.OodleBind(candidate) == Native.NativeMethods.Ok;
        return _resolved;
    }
}
