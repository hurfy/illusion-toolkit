using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

[StructLayout(LayoutKind.Sequential)]
public struct ActorMarkerConstants
{
    public Matrix4x4 Wvp; // load as-is (reinterpret-as-column transposes)
}

[StructLayout(LayoutKind.Sequential)]
internal struct ActorMarkerVertex
{
    public Vector3 Position;
    public Vector4 Color;
}

/// <summary>
/// Overlay pass for actors with nothing to draw (sounds, lights, triggers, script hooks…): a coloured line
/// list of glyphs, one immutable vertex buffer per resident district so streaming can drop a district's
/// markers alone. Colour rides per vertex — unlike the navigation pass, one draw covers every actor category.
/// No depth test or write, so a marker inside a wall stays reachable.
/// </summary>
public sealed unsafe class ActorMarkerRenderer : IDisposable
{
    private const string Hlsl = @"
cbuffer CB : register(b0) { float4x4 WVP; };
struct VSIn { float3 pos : POSITION; float4 col : COLOR; };
struct PSIn { float4 pos : SV_POSITION; float4 col : COLOR; };
PSIn VSMain(VSIn i){ PSIn o; o.pos = mul(WVP, float4(i.pos, 1.0)); o.col = i.col; return o; }
float4 PSMain(PSIn i) : SV_TARGET { return i.col; }";

    private readonly GpuContext _gpu;
    private ComPtr<ID3D11VertexShader> _vs;
    private ComPtr<ID3D11PixelShader> _ps;
    private ComPtr<ID3D11InputLayout> _layout;
    private ComPtr<ID3D11Buffer> _cb;
    private ComPtr<ID3D11BlendState> _blend;
    private ComPtr<ID3D11DepthStencilState> _noDepth;
    private ComPtr<ID3D11RasterizerState> _raster;

    private readonly Dictionary<object, District> _districts = new();

    private struct District
    {
        public ComPtr<ID3D11Buffer> Vb;
        public uint VertexCount;
        public int MarkerCount;
    }

    /// <summary>True while any district's markers are uploaded.</summary>
    public bool HasData => _districts.Count > 0;

    /// <summary>Markers currently resident (for the status line and the probes).</summary>
    public int MarkerCount
    {
        get
        {
            int n = 0;
            foreach (District d in _districts.Values) n += d.MarkerCount;
            return n;
        }
    }

    public ActorMarkerRenderer(GpuContext gpu)
    {
        _gpu = gpu;

        using D3DCompiler compiler = D3DCompiler.GetApi();
        ComPtr<ID3D10Blob> vsCode = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "actormarker");
        ComPtr<ID3D10Blob> psCode = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "actormarker");
        (_vs, _ps) = ShaderCompiler.CreateShaders(gpu, vsCode, psCode);

        byte* posName = (byte*)SilkMarshal.StringToPtr("POSITION");
        byte* colName = (byte*)SilkMarshal.StringToPtr("COLOR");
        Span<InputElementDesc> elems =
        [
            ShaderCompiler.VertexElement(posName, 0, Format.FormatR32G32B32Float, 0),
            ShaderCompiler.VertexElement(colName, 0, Format.FormatR32G32B32A32Float, 12),
        ];
        ID3D11InputLayout* layout = null;
        fixed (InputElementDesc* e = elems)
        {
            SilkMarshal.ThrowHResult(gpu.Device11.CreateInputLayout(
                e, 2, vsCode.GetBufferPointer(), vsCode.GetBufferSize(), ref layout));
        }
        _layout = layout;
        SilkMarshal.Free((nint)posName);
        SilkMarshal.Free((nint)colName);
        vsCode.Dispose();
        psCode.Dispose();

        _cb = GpuBuffers.CreateConstant<ActorMarkerConstants>(gpu);

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

        var dsd = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always };
        ID3D11DepthStencilState* nd = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in dsd, ref nd));
        _noDepth = nd;

        var rsd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
        ID3D11RasterizerState* rs = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rsd, ref rs));
        _raster = rs;
    }

    /// <summary>Uploads/replaces one district's markers. Null or empty removes them.</summary>
    public void SetDistrict(object key, ActorMarkerRenderData? data)
    {
        RemoveDistrict(key);
        if (data == null || data.VertexCount == 0) return;

        var verts = new ActorMarkerVertex[data.VertexCount];
        for (int i = 0; i < verts.Length; i++)
        {
            verts[i] = new ActorMarkerVertex { Position = data.Positions[i], Color = data.Colors[i] };
        }

        ComPtr<ID3D11Buffer> vb;
        fixed (ActorMarkerVertex* p = verts)
            vb = GpuBuffers.CreateImmutable(_gpu, p, (uint)(verts.Length * sizeof(ActorMarkerVertex)), BindFlag.VertexBuffer);
        _districts[key] = new District { Vb = vb, VertexCount = (uint)verts.Length, MarkerCount = data.MarkerCount };
    }

    /// <summary>Drops one district's markers (district unload).</summary>
    public void RemoveDistrict(object key)
    {
        if (_districts.Remove(key, out District d)) d.Vb.Dispose();
    }

    /// <summary>Drops every district's markers (scene reset).</summary>
    public void Clear()
    {
        foreach (District d in _districts.Values) d.Vb.Dispose();
        _districts.Clear();
    }

    public void Render(ComPtr<ID3D11DeviceContext> ctx, Matrix4x4 viewProj)
    {
        if (_districts.Count == 0) return;

        ctx.OMSetBlendState(_blend, (float*)null, 0xffffffff);
        ctx.OMSetDepthStencilState(_noDepth, 0);
        ctx.RSSetState(_raster);
        ctx.IASetInputLayout(_layout);
        ctx.VSSetShader(_vs, (ID3D11ClassInstance**)null, 0);
        ctx.PSSetShader(_ps, (ID3D11ClassInstance**)null, 0);
        var cb = _cb.Handle;
        ctx.VSSetConstantBuffers(0, 1, &cb);
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist);

        var consts = new ActorMarkerConstants { Wvp = viewProj };
        GpuBuffers.UpdateConstant(ctx, _cb, ref consts);

        uint stride = (uint)sizeof(ActorMarkerVertex), offset = 0;
        foreach (District d in _districts.Values)
        {
            var vb = d.Vb.Handle;
            ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset);
            ctx.Draw(d.VertexCount, 0);
        }

        ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, 0xffffffff);
    }

    public void Dispose()
    {
        Clear();
        _raster.Dispose();
        _noDepth.Dispose();
        _blend.Dispose();
        _cb.Dispose();
        _layout.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
