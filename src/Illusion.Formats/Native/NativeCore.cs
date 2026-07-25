using System.Reflection;
using System.Runtime.InteropServices;

namespace Illusion.Formats.Native;

/// <summary>
/// The load-time handshake with the native core. The managed facade and Mafia.Formats.dll are two
/// halves of one boundary: a facade compiled against revision N cannot talk to a DLL built at
/// revision M. A source build catches that in the project file before anything compiles; when the
/// DLL ships separately (a toolkit consuming a released core) this is the only guard.
/// <para/>
/// It hooks the library load itself rather than a static constructor: a throwing type initializer
/// would bury the explanation under "The type initializer for X threw an exception", and this
/// failure has to read plainly — it is the one a contributor with a stale DLL will meet.
/// </summary>
internal static class NativeCore
{
    private const string LibraryName = "Mafia.Formats.dll";

    private static nint _handle;

    /// <summary>Installs the resolver. Called from the module initializer, so it is in place before
    /// the first P/Invoke this assembly makes.</summary>
    internal static void Install() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeCore).Assembly, Resolve);

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase))
        {
            return nint.Zero; // not ours — let the default resolution run
        }
        if (_handle != nint.Zero)
        {
            return _handle;
        }

        if (!NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint handle))
        {
            throw new NativeCoreException(
                $"the native core ({LibraryName}) could not be loaded — it ships next to the " +
                "application; build it from source or fetch the released DLL");
        }

        VerifyRevision(handle);
        _handle = handle;
        return handle;
    }

    private static unsafe void VerifyRevision(nint handle)
    {
        if (!NativeLibrary.TryGetExport(handle, "mf_abi_rev", out nint entry))
        {
            throw new NativeCoreException(
                $"the {LibraryName} beside the application exports no mf_abi_rev — it is not this " +
                "native core");
        }

        uint rev = ((delegate* unmanaged<uint>)entry)();
        if (rev != NativeFormats.ExpectedAbiRev)
        {
            throw new NativeCoreException(
                $"the native core speaks boundary revision {rev}, this build expects " +
                $"{NativeFormats.ExpectedAbiRev} — the two halves come from different revisions. " +
                $"Update {LibraryName} (or rebuild it from matching sources).");
        }
    }
}
