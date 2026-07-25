using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Gpu;

/// <summary>
/// CPU readback of a rendered <see cref="SharedRenderTarget"/>: copy into a staging texture, map, and return
/// tight-row BGRA bytes. This is how an offscreen render becomes a WPF-displayable bitmap (material thumbnails,
/// probe screenshots) without any D3DImage machinery. Must run on the thread that owns the immediate context.
/// </summary>
public static unsafe class RenderTargetReadback
{
    public static byte[] Read(GpuContext gpu, SharedRenderTarget target)
    {
        var ctx = gpu.Context11;
        int w = target.Width, h = target.Height;
        var desc = new Texture2DDesc
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Silk.NET.DXGI.Format.FormatB8G8R8A8Unorm,
            SampleDesc = new Silk.NET.DXGI.SampleDesc(1, 0),
            Usage = Usage.Staging,
            CPUAccessFlags = (uint)CpuAccessFlag.Read,
        };
        ID3D11Texture2D* stagingPtr = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, ref stagingPtr));
        ComPtr<ID3D11Texture2D> staging = stagingPtr;
        try
        {
            ctx.CopyResource((ID3D11Resource*)staging.Handle, (ID3D11Resource*)target.Texture11.Handle);
            var mapped = new MappedSubresource();
            SilkMarshal.ThrowHResult(ctx.Map((ID3D11Resource*)staging.Handle, 0, Map.Read, 0, ref mapped));
            var buf = new byte[w * h * 4];
            byte* src = (byte*)mapped.PData;
            for (int row = 0; row < h; row++)
                System.Runtime.InteropServices.Marshal.Copy((nint)(src + row * mapped.RowPitch), buf, row * w * 4, w * 4);
            ctx.Unmap((ID3D11Resource*)staging.Handle, 0);
            return buf;
        }
        finally { staging.Dispose(); }
    }
}
