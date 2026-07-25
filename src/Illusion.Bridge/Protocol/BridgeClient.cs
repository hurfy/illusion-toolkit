using System.Net;
using System.Net.Sockets;

namespace Illusion.Bridge.Protocol;

/// <summary>
/// The toolkit's side of the control channel: connects to the addon's loopback server, performs the
/// hello handshake, then runs a background read loop. Incoming messages surface on
/// <see cref="MessageReceived"/> (read-loop thread — the subscriber marshals to its own thread);
/// requests that expect a direct reply go through <see cref="Request"/>.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NdjsonConnection _connection;
    private readonly object _pendingLock = new();
    private Predicate<BridgeMessage>? _pendingMatch;
    private BridgeMessage? _pendingReply;
    private ManualResetEventSlim? _pendingEvent;
    private Thread? _readThread;
    private volatile bool _disposed;
    private volatile bool _dead; // read loop ended or disposed — no reply can ever arrive

    /// <summary>Raised for every message not claimed by a pending <see cref="Request"/>.</summary>
    public event Action<BridgeMessage>? MessageReceived;

    /// <summary>Raised once when the read loop ends (peer closed, network error, or Dispose).</summary>
    public event Action<Exception?>? Disconnected;

    private BridgeClient(TcpClient tcp)
    {
        _tcp = tcp;
        _connection = new NdjsonConnection(tcp.GetStream());
    }

    /// <summary>Connects to the addon server on loopback. Throws on refusal/timeout.</summary>
    public static BridgeClient Connect(int port, TimeSpan timeout)
    {
        var tcp = new TcpClient();
        try
        {
            if (!tcp.ConnectAsync(IPAddress.Loopback, port).Wait(timeout))
                throw new TimeoutException($"Bridge server on port {port} did not accept within {timeout.TotalSeconds:0}s.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        return new BridgeClient(tcp);
    }

    public void Send(BridgeMessage message) => _connection.Send(message);

    /// <summary>Sends a message and blocks for the first reply matching <paramref name="expect"/>.
    /// Must be called before <see cref="StartReadLoop"/> or from a non-read-loop thread after it.</summary>
    public BridgeMessage Request(BridgeMessage message, Predicate<BridgeMessage> expect, TimeSpan timeout)
    {
        if (_readThread == null)
        {
            // Pre-loop (handshake): read inline. The socket receive timeout bounds each blocking
            // read so a silent peer cannot hang the handshake past the deadline.
            _tcp.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            try
            {
                _connection.Send(message);
                DateTime deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    BridgeMessage? reply;
                    try { reply = _connection.ReadMessage(); }
                    // A peer that closed the connection is a refusal, not a timeout — EndOfStreamException
                    // derives from IOException and must not fall into the receive-timeout branch below
                    // (the caller maps a timeout to "Blender is busy" and skips spawning a fresh instance).
                    catch (EndOfStreamException) { throw new IOException("Bridge peer closed the connection during the handshake."); }
                    catch (IOException) { break; } // receive timeout
                    if (reply != null && expect(reply)) return reply;
                }
                throw new TimeoutException("Bridge peer did not reply in time.");
            }
            finally
            {
                _tcp.ReceiveTimeout = 0; // back to infinite for the read loop
            }
        }

        var evt = new ManualResetEventSlim(false);
        lock (_pendingLock)
        {
            if (_pendingEvent != null) throw new InvalidOperationException("A bridge request is already pending.");
            if (_dead) throw new IOException("Bridge connection is closed.");
            _pendingMatch = expect;
            _pendingReply = null;
            _pendingEvent = evt;
        }
        try
        {
            _connection.Send(message);
            if (!evt.Wait(timeout)) throw new TimeoutException("Bridge peer did not reply in time.");
            lock (_pendingLock)
            {
                // A set event with no reply means the read loop died while we waited — fail fast
                // instead of sitting out the timeout (and misreporting a disconnect as "no reply").
                return _pendingReply ?? throw new IOException("Bridge connection closed while waiting for a reply.");
            }
        }
        finally
        {
            lock (_pendingLock)
            {
                _pendingMatch = null;
                _pendingEvent = null;
            }
            evt.Dispose();
        }
    }

    /// <summary>Starts the background read loop; call once, after the handshake.</summary>
    public void StartReadLoop()
    {
        if (_readThread != null) return;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "BridgeClient.Read" };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        Exception? cause = null;
        try
        {
            while (!_disposed)
            {
                BridgeMessage? message = _connection.ReadMessage();
                if (message == null) continue;

                lock (_pendingLock)
                {
                    if (_pendingMatch != null && _pendingMatch(message))
                    {
                        _pendingReply = message;
                        _pendingMatch = null;
                        _pendingEvent!.Set();
                        continue;
                    }
                }
                MessageReceived?.Invoke(message);
            }
        }
        catch (Exception ex)
        {
            if (!_disposed) cause = ex;
        }
        AbortPending();
        Disconnected?.Invoke(cause);
    }

    // Wakes a blocked Request with "no reply" the moment the connection is gone.
    private void AbortPending()
    {
        lock (_pendingLock)
        {
            _dead = true;
            if (_pendingEvent != null)
            {
                _pendingReply = null;
                _pendingMatch = null;
                _pendingEvent.Set();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AbortPending();
        try { _connection.Dispose(); } catch (IOException) { }
        _tcp.Dispose();
    }
}
