namespace Illusion.Mcp;

/// <summary>
/// Hops onto the application's UI thread.
/// <para>
/// Tool calls arrive on Kestrel's thread-pool threads, while the scene document, the viewport and
/// every WPF control are single-threaded. Any future tool that reads or mutates application state
/// must therefore route through this marshal — the same discipline the Blender bridge follows with
/// its <c>Dispatcher.Invoke</c> calls off the socket read thread.
/// </para>
/// The interface lives here (platform-neutral) and is implemented over the WPF dispatcher by the
/// executable, which registers it through <see cref="McpHostOptions.ConfigureServices"/>.
/// </summary>
public interface IUiThreadMarshal
{
    /// <summary>Runs <paramref name="work"/> on the UI thread and returns its result.</summary>
    Task<T> RunAsync<T>(Func<T> work);

    /// <summary>Runs <paramref name="work"/> on the UI thread.</summary>
    Task RunAsync(Action work);
}
