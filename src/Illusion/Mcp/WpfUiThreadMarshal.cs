using System.Windows.Threading;

namespace Illusion.Mcp;

/// <summary>
/// The application's implementation of <see cref="IUiThreadMarshal"/>: hops from a Kestrel
/// thread-pool thread onto the WPF dispatcher, so tools can touch the scene and the UI.
/// </summary>
internal sealed class WpfUiThreadMarshal : IUiThreadMarshal
{
    private readonly Dispatcher _dispatcher;

    public WpfUiThreadMarshal(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public Task<T> RunAsync<T>(Func<T> work) => _dispatcher.InvokeAsync(work).Task;

    public Task RunAsync(Action work) => _dispatcher.InvokeAsync(work).Task;
}
