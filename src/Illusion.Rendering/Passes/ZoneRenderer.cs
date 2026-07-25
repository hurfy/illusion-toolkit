using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

[StructLayout(LayoutKind.Sequential)]
public struct ZoneConstants
{
    public Matrix4x4 Wvp; // load as-is (reinterpret-as-column transposes)
    public Vector4 Color; // rgb + alpha
}

/// <summary>
/// Debug pass: draws AREA load zones as translucent colored boxes (alpha blending,
/// no depth write and no depth-test — pure overlay). One box per zone, world from AABB.
/// </summary>
public sealed unsafe class ZoneRenderer : IDisposable
{
    private const string Hlsl = @"
cbuffer CB : register(b0) { float4x4 WVP; float4 Color; };
struct VSIn { float3 pos : POSITION; };
struct PSIn { float4 pos : SV_POSITION; };
PSIn VSMain(VSIn i){ PSIn o; o.pos = mul(WVP, float4(i.pos, 1.0)); return o; }
float4 PSMain(PSIn i) : SV_TARGET { return Color; }";

    private ComPtr<ID3D11VertexShader> _vs;
    private ComPtr<ID3D11PixelShader> _ps;
    private ComPtr<ID3D11InputLayout> _layout;
    private ComPtr<ID3D11Buffer> _cb;
    private ComPtr<ID3D11Buffer> _vb;
    private ComPtr<ID3D11Buffer> _ib;
    private ComPtr<ID3D11BlendState> _blend;
    private ComPtr<ID3D11DepthStencilState> _noDepth;
    private ComPtr<ID3D11RasterizerState> _raster;

    // Unit box [0,1]^3.
    private static readonly float[] CubeVerts =
    {
        0,0,0, 1,0,0, 1,1,0, 0,1,0,
        0,0,1, 1,0,1, 1,1,1, 0,1,1,
    };
    private static readonly uint[] CubeIndices =
    {
        0,1,2, 0,2,3,  4,6,5, 4,7,6,
        0,3,7, 0,7,4,  1,5,6, 1,6,2,
        0,4,5, 0,5,1,  3,2,6, 3,6,7,
    };

    public ZoneRenderer(GpuContext gpu)
    {
        using D3DCompiler compiler = D3DCompiler.GetApi();
        ComPtr<ID3D10Blob> vsCode = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "zone");
        ComPtr<ID3D10Blob> psCode = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "zone");
        (_vs, _ps) = ShaderCompiler.CreateShaders(gpu, vsCode, psCode);

        byte* posName = (byte*)SilkMarshal.StringToPtr("POSITION");
        InputElementDesc elem = ShaderCompiler.VertexElement(posName, 0, Format.FormatR32G32B32Float, 0);
        ID3D11InputLayout* layout = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateInputLayout(
            &elem, 1, vsCode.GetBufferPointer(), vsCode.GetBufferSize(), ref layout));
        _layout = layout;
        SilkMarshal.Free((nint)posName);
        vsCode.Dispose();
        psCode.Dispose();

        // Box.
        fixed (float* pv = CubeVerts)
            _vb = GpuBuffers.CreateImmutable(gpu, pv, (uint)(CubeVerts.Length * sizeof(float)), BindFlag.VertexBuffer);
        fixed (uint* pi = CubeIndices)
            _ib = GpuBuffers.CreateImmutable(gpu, pi, (uint)(CubeIndices.Length * sizeof(uint)), BindFlag.IndexBuffer);

        _cb = GpuBuffers.CreateConstant<ZoneConstants>(gpu);

        // Alpha blending (over).
        var bd = new BlendDesc();
        bd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.SrcAlpha,
            DestBlend = Blend.InvSrcAlpha,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.Zero,
            BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ID3D11BlendState* blend = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateBlendState(in bd, ref blend));
        _blend = blend;

        // Overlay: no depth-test and no depth write.
        var dsd = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always };
        ID3D11DepthStencilState* nd = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in dsd, ref nd));
        _noDepth = nd;

        var rsd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
        ID3D11RasterizerState* rs = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rsd, ref rs));
        _raster = rs;
    }

    public void Render(ComPtr<ID3D11DeviceContext> ctx, Matrix4x4 viewProj,
        IReadOnlyList<(Vector3 Min, Vector3 Max, Vector4 Color)> boxes)
    {
        if (boxes == null || boxes.Count == 0) return;

        ctx.OMSetBlendState(_blend, (float*)null, 0xffffffff);
        ctx.OMSetDepthStencilState(_noDepth, 0);
        ctx.RSSetState(_raster);
        ctx.IASetInputLayout(_layout);
        ctx.VSSetShader(_vs, (ID3D11ClassInstance**)null, 0);
        ctx.PSSetShader(_ps, (ID3D11ClassInstance**)null, 0);
        var cb = _cb.Handle;
        ctx.VSSetConstantBuffers(0, 1, &cb);
        ctx.PSSetConstantBuffers(0, 1, &cb);
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        uint stride = 12, offset = 0;
        var vb = _vb.Handle;
        ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset);
        ctx.IASetIndexBuffer(_ib, Format.FormatR32Uint, 0);

        foreach ((Vector3 min, Vector3 max, Vector4 color) in boxes)
        {
            Vector3 size = max - min;
            Matrix4x4 world = Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(min);
            var c = new ZoneConstants { Wvp = world * viewProj, Color = color };
            GpuBuffers.UpdateConstant(ctx, _cb, ref c);
            ctx.DrawIndexed((uint)CubeIndices.Length, 0, 0);
        }

        // Restore opaque blending for the next frame.
        ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, 0xffffffff);
    }

    public void Dispose()
    {
        _raster.Dispose();
        _noDepth.Dispose();
        _blend.Dispose();
        _ib.Dispose();
        _vb.Dispose();
        _cb.Dispose();
        _layout.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
