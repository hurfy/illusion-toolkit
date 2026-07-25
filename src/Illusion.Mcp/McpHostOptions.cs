using Microsoft.Extensions.DependencyInjection;

namespace Illusion.Mcp;

/// <summary>
/// Startup options for <see cref="McpServerHost"/>. Named "host" rather than "server" options so it
/// cannot be confused with the SDK's own <c>ModelContextProtocol.Server.McpServerOptions</c>.
/// </summary>
public sealed class McpHostOptions
{
    /// <summary>Default loopback port — Mafia II's release year, easy to recall and far away from the
    /// usual development ports.</summary>
    public const int DefaultPort = 2010;

    /// <summary>The MCP endpoint path. Clients connect to <c>http://127.0.0.1:{Port}{Path}</c>.</summary>
    public const string Path = "/mcp";

    /// <summary>TCP port on the loopback interface. <c>0</c> asks the OS for a free one — probes use that
    /// so they can never collide with a running instance of the application.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// The seam through which the application publishes live state to future tools. Whatever is
    /// registered here can be taken as a plain parameter by any <c>[McpServerTool]</c> method: the SDK
    /// binds parameters whose type resolves from DI and hides them from the schema the model sees.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }
}
