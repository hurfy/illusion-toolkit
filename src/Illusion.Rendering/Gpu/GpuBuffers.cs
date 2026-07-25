using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Gpu;

/// <summary>
/// Small D3D11 buffer helpers shared by the mesh/shader classes — the immutable vertex/index/instance
/// buffer idiom and the Default-usage constant-buffer create/update idiom, factored out of every renderer.
/// </summary>
internal static unsafe class GpuBuffers
{
    /// <summary>Creates an immutable GPU buffer initialized from <paramref name="data"/> (pinned by the caller).</summary>
    public static ComPtr<ID3D11Buffer> CreateImmutable(GpuContext gpu, void* data, uint byteWidth, BindFlag bind)
    {
        var desc = new BufferDesc { ByteWidth = byteWidth, Usage = Usage.Immutable, BindFlags = (uint)bind };
        var srd = new SubresourceData { PSysMem = data };
        ID3D11Buffer* buffer = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateBuffer(in desc, in srd, ref buffer));
        return buffer;
    }

    /// <summary>Creates a Default-usage GPU buffer initialized from <paramref name="data"/> (pinned by the caller).
    /// Unlike an immutable buffer it can be rewritten wholesale via <see cref="UpdateBuffer"/> (UpdateSubresource) —
    /// used for instance buffers that a live edit repaints without a Map/Discard cycle.</summary>
    public static ComPtr<ID3D11Buffer> CreateDefault(GpuContext gpu, void* data, uint byteWidth, BindFlag bind)
    {
        var desc = new BufferDesc { ByteWidth = byteWidth, Usage = Usage.Default, BindFlags = (uint)bind };
        var srd = new SubresourceData { PSysMem = data };
        ID3D11Buffer* buffer = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateBuffer(in desc, in srd, ref buffer));
        return buffer;
    }

    /// <summary>Rewrites a Default-usage buffer's whole contents from <paramref name="data"/> (must match its byte
    /// width — the buffer size is fixed at creation). For resizing, recreate via <see cref="CreateDefault"/>.</summary>
    public static void UpdateBuffer(ComPtr<ID3D11DeviceContext> ctx, ComPtr<ID3D11Buffer> buffer, void* data)
    {
        ctx.UpdateSubresource((ID3D11Resource*)buffer.Handle, 0, (Box*)null, data, 0, 0);
    }

    /// <summary>Creates a Dynamic constant buffer sized for <typeparamref name="T"/>, written each
    /// frame via <see cref="UpdateConstant"/>'s Map(WRITE_DISCARD) — the fast per-draw upload idiom
    /// (vs UpdateSubresource on a Default buffer). Every constant buffer in the renderer is only ever
    /// written through UpdateConstant, so Dynamic is safe across the board.</summary>
    public static ComPtr<ID3D11Buffer> CreateConstant<T>(GpuContext gpu) where T : unmanaged
    {
        var desc = new BufferDesc
        {
            ByteWidth = (uint)sizeof(T),
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ID3D11Buffer* cb = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateBuffer(in desc, (SubresourceData*)null, ref cb));
        return cb;
    }

    /// <summary>Uploads <paramref name="value"/> into a Dynamic constant buffer via Map(WRITE_DISCARD).</summary>
    public static void UpdateConstant<T>(ComPtr<ID3D11DeviceContext> ctx, ComPtr<ID3D11Buffer> cb, ref T value) where T : unmanaged
    {
        var mapped = new MappedSubresource();
        SilkMarshal.ThrowHResult(ctx.Map((ID3D11Resource*)cb.Handle, 0, Map.WriteDiscard, 0, ref mapped));
        fixed (T* p = &value)
        {
            System.Buffer.MemoryCopy(p, mapped.PData, sizeof(T), sizeof(T));
        }
        ctx.Unmap((ID3D11Resource*)cb.Handle, 0);
    }
}
