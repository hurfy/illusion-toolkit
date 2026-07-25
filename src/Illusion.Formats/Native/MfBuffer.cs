using System.Runtime.InteropServices;

namespace Illusion.Formats.Native;

/// <summary>A native-owned <c>{ptr,len}</c> pair exactly as it crosses the boundary
/// (mirrors <c>MfBuffer</c> in <c>mf_abi.h</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MfRawBuffer
{
    public nint Data;
    public ulong Length;
}

/// <summary>
/// Managed owner of a buffer allocated by the native core. <see cref="ToArray"/> copies the
/// bytes out; <see cref="Dispose"/> returns the memory via <c>mf_free</c> at most once. The
/// native side additionally refuses pointers it does not own, so even a stale handle cannot
/// corrupt the heap.
/// </summary>
internal sealed class MfBuffer : IDisposable
{
    private MfRawBuffer _raw;
    private bool _freed;

    internal MfBuffer(MfRawBuffer raw) => _raw = raw;

    internal ulong Length => _raw.Length;

    internal byte[] ToArray()
    {
        ObjectDisposedException.ThrowIf(_freed, this);
        if (_raw.Data == 0 || _raw.Length == 0)
        {
            return [];
        }
        var bytes = new byte[checked((int)_raw.Length)];
        Marshal.Copy(_raw.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        if (_freed)
        {
            return;
        }
        _freed = true;
        // The status is deliberately ignored here: the native side already guards against
        // a bad free, and Dispose must not throw. Probes assert free semantics directly.
        _ = NativeMethods.Free(ref _raw);
    }
}
