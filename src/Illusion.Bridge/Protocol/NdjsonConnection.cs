using System.Text;
using System.Text.Json;

namespace Illusion.Bridge.Protocol;

/// <summary>
/// NDJSON framing over a stream: one UTF-8 JSON object per '\n'-terminated line. Sending is
/// serialized by a lock (any thread may send); reading is single-consumer (the owner's read loop).
/// A malformed incoming line yields null rather than throwing, so one bad line cannot kill the
/// session.
/// </summary>
public sealed class NdjsonConnection : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly object _sendLock = new();
    private long _seq;

    public NdjsonConnection(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), false, 64 * 1024, leaveOpen: true);
    }

    /// <summary>Serializes and sends one message, stamping the next send sequence number.</summary>
    public void Send(BridgeMessage message)
    {
        lock (_sendLock)
        {
            message.Seq = ++_seq;
            byte[] line = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            _stream.Write(line);
            _stream.WriteByte((byte)'\n');
            _stream.Flush();
        }
    }

    /// <summary>Blocks until the next line. Returns the parsed message, null for a malformed line,
    /// and throws <see cref="EndOfStreamException"/> when the peer closed the connection.</summary>
    public BridgeMessage? ReadMessage()
    {
        string? line = _reader.ReadLine() ?? throw new EndOfStreamException("Bridge connection closed by peer.");
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            return JsonSerializer.Deserialize<BridgeMessage>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null; // one bad line is the peer's bug, not a reason to drop the session
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
