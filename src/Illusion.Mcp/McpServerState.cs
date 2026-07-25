namespace Illusion.Mcp;

/// <summary>Lifecycle of the embedded server, as surfaced in the launcher's status bar.</summary>
public enum McpServerStatus
{
    /// <summary>Not started yet, or stopped after a clean shutdown.</summary>
    Stopped,

    /// <summary>Kestrel is binding the port.</summary>
    Starting,

    /// <summary>Listening and serving MCP requests.</summary>
    Running,

    /// <summary>Startup failed — see <see cref="McpServerState.Error"/>. The application keeps working.</summary>
    Failed,
}

/// <summary>
/// An immutable snapshot of the server's state. Exposed as a single reference so a reader on the UI
/// thread can never observe a torn combination (a <see cref="McpServerStatus.Running"/> status with
/// the address of the previous run, say) while a background thread swaps it.
/// </summary>
/// <param name="Status">Where the server is in its lifecycle.</param>
/// <param name="Address">The full client-facing URL once running, otherwise <c>null</c>.</param>
/// <param name="Error">A short human-readable reason when <see cref="McpServerStatus.Failed"/>.</param>
public sealed record McpServerState(McpServerStatus Status, string? Address, string? Error)
{
    internal static readonly McpServerState Stopped = new(McpServerStatus.Stopped, null, null);
}
