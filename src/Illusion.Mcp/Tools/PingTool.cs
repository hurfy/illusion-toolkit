using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// The one tool the foundation ships: a health check that proves a client can reach the toolkit.
/// <para>
/// It is also the template for real tools. A tool is a class marked <c>[McpServerToolType]</c> with
/// <c>[McpServerTool]</c> methods; the <c>[Description]</c> texts are what the model reads to decide
/// when to call them, so they carry real weight. Application state arrives as ordinary parameters
/// resolved from DI (see <see cref="McpHostOptions.ConfigureServices"/>), and anything touching the
/// scene or the UI goes through <see cref="IUiThreadMarshal"/>. Registering a new tool is a single
/// <c>WithTools</c> call in <see cref="McpServerHost"/> — nothing else in the application changes.
/// </para>
/// The class is deliberately not <c>static</c>: <c>WithTools&lt;T&gt;</c> takes it as a type argument,
/// and a static class cannot be one.
/// </summary>
[McpServerToolType]
public sealed class PingTool
{
    [McpServerTool(Name = "ping")]
    [Description("Health check: confirms the Illusion Toolkit MCP server is alive and reports its version.")]
    public static string Ping()
    {
        string version = typeof(PingTool).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        return $"pong - Illusion Toolkit {version}";
    }
}
