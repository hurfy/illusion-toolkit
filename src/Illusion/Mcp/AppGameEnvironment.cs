using Illusion.Assets;
using Illusion.Settings;

namespace Illusion.Mcp;

/// <summary>
/// The application's answer to <see cref="IGameEnvironment"/>: the configured path out of the user's
/// settings, and the resolved folders out of <see cref="MafiaEnvironment"/> once the launcher has
/// initialized it.
/// <para>
/// Read live on every call rather than captured at construction — the user can pick a different
/// folder in the launcher at any point in the session, and a tool answering with the path from
/// startup would quietly send a model looking in the wrong install.
/// </para>
/// Nothing here touches WPF, so no dispatcher hop is needed; both sources are plain static state.
/// </summary>
internal sealed class AppGameEnvironment : IGameEnvironment
{
    public string? ConfiguredPath => UserSettings.Current.GamePath;

    public bool IsInitialized => MafiaEnvironment.IsInitialized;

    public string? PcFolder => MafiaEnvironment.IsInitialized ? MafiaEnvironment.PcFolder : null;

    public string? GameRoot => MafiaEnvironment.IsInitialized ? MafiaEnvironment.GameRoot : null;

    public string? ResourcesFolder => MafiaEnvironment.IsInitialized ? MafiaEnvironment.ResourcesFolder : null;

    public string? StreamMapPath => MafiaEnvironment.IsInitialized ? MafiaEnvironment.StreamMapPath : null;
}
