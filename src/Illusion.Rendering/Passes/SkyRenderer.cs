using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Shaders;
using Illusion.Rendering.Textures;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Passes;

[StructLayout(LayoutKind.Sequential)]
public struct SkyConstants
{
    public Matrix4x4 InvViewProj;
    public Vector4 CameraPos;
    public Vector4 Params; // x = has panorama (1/0)
}

/// <summary>
/// Skybox: fullscreen triangle; in the PS it reconstructs the world ray direction
/// from inverse(viewProj). If a Mafia panorama is loaded (equirectangular FreeRide.dds) —
/// samples it by spherical UV; otherwise draws a procedural gradient.
/// </summary>
public sealed unsafe class SkyRenderer : IDisposable
{
    private const string Hlsl = @"
Texture2D    Panorama : register(t0);
SamplerState SkySamp  : register(s0);
cbuffer CB : register(b0)
{
    float4x4 InvViewProj;
    float4   CameraPos;
    float4   Params;      // x = hasPanorama
};
struct SkyOut { float4 pos : SV_POSITION; float2 ndc : TEXCOORD0; };

SkyOut VSMain(uint id : SV_VertexID)
{
    float2 t = float2((id << 1) & 2, id & 2);   // (0,0),(2,0),(0,2)
    SkyOut o;
    o.pos = float4(t.x * 2.0 - 1.0, 1.0 - t.y * 2.0, 1.0, 1.0);
    o.ndc = o.pos.xy;
    return o;
}
float4 PSMain(SkyOut i) : SV_TARGET
{
    float4 world = mul(InvViewProj, float4(i.ndc, 1.0, 1.0));
    world /= world.w;
    float3 dir = normalize(world.xyz - CameraPos.xyz);

    if (Params.x > 0.5)
    {
        // equirectangular: z up
        float u = atan2(dir.y, dir.x) * 0.15915494 + 0.5;      // 1/(2*pi)
        float v = 0.5 - asin(clamp(dir.z, -1.0, 1.0)) * 0.31830989; // 1/pi
        return float4(Panorama.SampleLevel(SkySamp, float2(u, v), 0).rgb, 1.0);
    }

    float h = saturate(dir.z);
    float3 zenith  = float3(0.24, 0.44, 0.72);
    float3 horizon = float3(0.72, 0.78, 0.84);
    return float4(lerp(horizon, zenith, pow(h, 0.55)), 1.0);
}";

    private ComPtr<ID3D11VertexShader> _vs;
    private ComPtr<ID3D11PixelShader> _ps;
    private ComPtr<ID3D11Buffer> _cb;
    private ComPtr<ID3D11DepthStencilState> _noDepth;
    private ComPtr<ID3D11SamplerState> _sampler;
    private ComPtr<ID3D11ShaderResourceView> _panorama;

    public SkyRenderer(GpuContext gpu)
    {
        using D3DCompiler compiler = D3DCompiler.GetApi();

        ComPtr<ID3D10Blob> vsCode = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "sky");
        ComPtr<ID3D10Blob> psCode = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "sky");
        (_vs, _ps) = ShaderCompiler.CreateShaders(gpu, vsCode, psCode);

        vsCode.Dispose();
        psCode.Dispose();

        _cb = GpuBuffers.CreateConstant<SkyConstants>(gpu);

        var dsDesc = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero };
        ID3D11DepthStencilState* dss = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in dsDesc, ref dss));
        _noDepth = dss;

        // Panorama: wrap horizontally, clamp vertically.
        var sampDesc = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunc.Never,
            MaxLOD = float.MaxValue,
        };
        ID3D11SamplerState* samp = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in sampDesc, ref samp));
        _sampler = samp;
    }

    public void SetPanorama(GpuContext gpu, byte[] ddsBytes)
    {
        _panorama.Dispose();
        _panorama = DdsTexture.Load(gpu, ddsBytes);
    }

    public void Render(GpuContext gpu, Camera camera)
    {
        var ctx = gpu.Context11;

        Matrix4x4.Invert(camera.ViewProjection, out Matrix4x4 inv);
        var consts = new SkyConstants
        {
            InvViewProj = inv,
            CameraPos = new Vector4(camera.Position, 1f),
            Params = new Vector4(_panorama.Handle != null ? 1f : 0f, 0, 0, 0),
        };
        GpuBuffers.UpdateConstant(ctx, _cb, ref consts);

        ctx.OMSetDepthStencilState(_noDepth, 0);
        ctx.IASetInputLayout(default(ComPtr<ID3D11InputLayout>));
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_vs, (ID3D11ClassInstance**)null, 0);
        ctx.PSSetShader(_ps, (ID3D11ClassInstance**)null, 0);
        var cb = _cb.Handle;
        ctx.PSSetConstantBuffers(0, 1, &cb);
        var samp = _sampler.Handle;
        ctx.PSSetSamplers(0, 1, &samp);
        var srv = _panorama.Handle;
        ctx.PSSetShaderResources(0, 1, &srv);
        ctx.Draw(3, 0);
    }

    public void Dispose()
    {
        _panorama.Dispose();
        _sampler.Dispose();
        _noDepth.Dispose();
        _cb.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
