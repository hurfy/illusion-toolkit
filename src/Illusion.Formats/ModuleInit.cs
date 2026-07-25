using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Illusion.Formats;

internal static class ModuleInit
{
    // The format layer decodes strings as Windows-1252, which .NET (Core) only provides through the
    // code-pages package provider. Registering here (idempotent) removes the "call this before anything
    // parses" ritual the app previously had to perform.
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Encoding registration is idempotent, has no observable side effects beyond enabling " +
                        "Windows-1252, and every consumer of this library needs it before the first parse.")]
    [ModuleInitializer]
    internal static void RegisterLegacyEncodings() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // Hooks the native core's load so a DLL from a different boundary revision is refused with an
    // explanation instead of surfacing later as a missing export or misread bytes.
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Installing the resolver must happen before the first P/Invoke of this assembly, " +
                        "which is exactly what module initialization guarantees.")]
    [ModuleInitializer]
    internal static void InstallNativeResolver() => Native.NativeCore.Install();
}
