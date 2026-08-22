namespace Illusion.Mcp;

/// <summary>
/// Where the game lives, as the running application understands it.
/// <para>
/// A seam rather than a direct read for the same reason <see cref="IUiThreadMarshal"/> is one: the
/// answer is assembled from the user's settings and from <c>MafiaEnvironment</c>, which live in the
/// executable and the assets layer — neither of which this project may reference. The application
/// registers an implementation through <see cref="McpHostOptions.ConfigureServices"/> and tools take
/// it as a plain parameter.
/// </para>
/// Every member is nullable on purpose: until the user has picked a folder in the launcher there is
/// no game, and a tool that reports "not configured" is more use than one that throws.
/// </summary>
public interface IGameEnvironment
{
    /// <summary>The folder the user configured, verbatim — before any pc/root resolution.</summary>
    string? ConfiguredPath { get; }

    /// <summary>Whether the application resolved that path into a usable install.</summary>
    bool IsInitialized { get; }

    /// <summary>The game's <c>pc</c> folder: the exe and <c>sds\</c> live here.</summary>
    string? PcFolder { get; }

    /// <summary>Root of the install, parent of <c>pc\</c>, where <c>edit\</c> sits.</summary>
    string? GameRoot { get; }

    /// <summary>Where the toolkit unpacks resources to.</summary>
    string? ResourcesFolder { get; }

    /// <summary>The base <c>StreamMapa.bin</c> of this install.</summary>
    string? StreamMapPath { get; }
}
