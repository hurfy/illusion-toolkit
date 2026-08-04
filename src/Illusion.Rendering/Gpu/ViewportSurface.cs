using System.Diagnostics;

namespace Illusion.Rendering.Gpu;

/// <summary>
/// The shared surface a viewport draws into, and — the point of the class — the decision of WHEN it is
/// rebuilt.
///
/// <para>
/// Layout and the GPU disagree about how often a viewport changes size. Dragging a panel edge makes WPF raise
/// a size change per mouse report, hundreds a second; a surface rebuild is a video-memory allocation through
/// two drivers, and measurement says none of it comes back until well after the drag is over — so the pile is
/// exactly as tall as the number of rebuilds. Answering every size change directly is what took the viewport
/// to zero frames a second and then killed it on an allocation that could no longer be served
/// (<c>--probe-resize</c>: 150 rebuilds held 1.7 GB at once).
/// </para>
/// <para>
/// So layout only <see cref="Request">asks</see>, and the frame loop <see cref="Commit">decides</see> — and
/// it decides on a CLOCK rather than per frame, because frame rate is not a fixed thing to measure against:
/// a 240 Hz display outruns a 125 Hz mouse, every frame sees a settled size, and a per-frame rule quietly
/// degrades into a rebuild per size change. On a clock, a drag costs the same handful of surfaces whatever
/// the display and the mouse are doing.
/// </para>
/// <para>
/// The swap order matters as much as the rate. The new surface is built BEFORE the old one is let go, so a
/// size that cannot be allocated leaves the viewport exactly as it was instead of blank; the D3DImage is
/// handed a null back buffer and the device's output-merger stage is unbound before the old surface dies, so
/// nothing is still pointing at freed memory when WPF next composites.
/// </para>
/// </summary>
public sealed class ViewportSurface : IDisposable
{
    /// <summary>
    /// How long the requested size must hold still before it is built. Long enough that a drag never goes
    /// quiet mid-flight (a mouse reports every few milliseconds), short enough that letting go of a splitter
    /// — or maximising a window — snaps to the true resolution before the eye settles on it.
    /// </summary>
    public const double QuietMs = 50;

    /// <summary>
    /// …and how long a viewport may be left drawing at the wrong resolution while the size keeps moving. The
    /// surface is stretched onto the control in the meantime, so a drag stays smooth and merely goes slightly
    /// soft; this is what keeps it from being soft for the WHOLE drag.
    /// </summary>
    public const double CatchUpMs = 200;

    private readonly GpuContext _gpu;
    private readonly Func<double> _nowMs;
    private SharedRenderTarget? _target;
    private int _wantWidth = 1, _wantHeight = 1;
    private int _lastRequestWidth, _lastRequestHeight;
    private double _changedAt, _builtAt, _retryAfter;
    private bool _forced;

    /// <param name="clock">Milliseconds from any fixed origin. Supplied by tests; otherwise a stopwatch.</param>
    public ViewportSurface(GpuContext gpu, Func<double>? clock = null)
    {
        _gpu = gpu;
        _nowMs = clock ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>The surface to draw into, or null when none could be built yet.</summary>
    public SharedRenderTarget? Target => _target;

    /// <summary>How many surfaces have actually been built — the number a drag is judged by.</summary>
    public int Allocations { get; private set; }

    /// <summary>Why the last rebuild was refused, or null when the surface is current.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>The size the viewport wants, in device pixels. Cheap: nothing reaches the GPU here.</summary>
    public void Request(int width, int height)
    {
        _wantWidth = Math.Max(1, width);
        _wantHeight = Math.Max(1, height);
    }

    /// <summary>Rebuilds on the next <see cref="Commit"/> even at an unchanged size — for a front buffer that
    /// went away and came back, where the D3DImage has to be handed its surface again.</summary>
    public void Invalidate() => _forced = true;

    /// <summary>
    /// Builds the requested surface if it is not the one already in hand and the size has earned it. Call
    /// once per frame.
    /// </summary>
    /// <param name="publish">
    /// Hands the D3DImage its back buffer: called with 0 to make it let go of the outgoing surface, then with
    /// the new surface's pointer. Both calls happen only when a surface was actually built.
    /// </param>
    /// <returns>True when the surface was replaced.</returns>
    public bool Commit(Action<nint>? publish)
    {
        double now = _nowMs();
        if (_wantWidth != _lastRequestWidth || _wantHeight != _lastRequestHeight)
        {
            _lastRequestWidth = _wantWidth;
            _lastRequestHeight = _wantHeight;
            _changedAt = now;
        }

        if (!_forced && _target != null && _target.Width == _wantWidth && _target.Height == _wantHeight)
        {
            return false;
        }

        // Mid-drag the size is different every few milliseconds and nothing built during a drag comes back
        // until it is over. Wait for it to hold still — and, if it will not, settle for catching up now and
        // then rather than every time it moves.
        if (!_forced && _target != null && now - _changedAt < QuietMs && now - _builtAt < CatchUpMs)
        {
            return false;
        }

        // A size that just failed has gone quiet by definition — the request is not moving, it is unbuildable
        // — so the rule above would wave it through on every single frame from here on. Back off instead.
        if (LastFailure != null && now < _retryAfter) return false;

        SharedRenderTarget built;
        try
        {
            built = new SharedRenderTarget(_gpu, _wantWidth, _wantHeight);
        }
        catch (Exception ex)
        {
            // Out of video memory, a device reset, a size the driver will not serve — none of it is worth
            // taking the application down for. The viewport keeps the surface it has and tries again later,
            // by which point the drag has usually asked for something smaller anyway.
            LastFailure = $"{_wantWidth}x{_wantHeight}: {ex.Message}";
            _retryAfter = now + CatchUpMs;
            return false;
        }

        _forced = false;
        _builtAt = now;
        LastFailure = null;
        Allocations++;

        publish?.Invoke(0);             // WPF lets go first…
        _gpu.UnbindRenderTargets();     // …and so does the device, before the memory it points at is freed
        _target?.Dispose();
        _target = built;
        publish?.Invoke(built.SurfacePointer);
        return true;
    }

    public void Dispose()
    {
        _target?.Dispose();
        _target = null;
    }
}
