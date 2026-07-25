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
public struct NavGraphConstants
{
    public Matrix4x4 Wvp; // load as-is (reinterpret-as-column transposes)
    public Vector4 Color; // rgb + alpha
}

/// <summary>
/// Debug pass: draws navigation graphs (.nov road graphs) as colored line lists — one immutable
/// vertex buffer per resident district, keyed so streaming can drop a district's graph alone. Pure
/// overlay: alpha blend, no depth test/write, so lines stay visible over the scene. The vertex list
/// is edge endpoint pairs (A,B,A,B,...) in the same world space the meshes use.
/// </summary>
public sealed unsafe class NavGraphRenderer : IDisposable
{
    private const string Hlsl = @"
cbuffer CB : register(b0) { float4x4 WVP; float4 Color; };
struct VSIn { float3 pos : POSITION; };
struct PSIn { float4 pos : SV_POSITION; };
PSIn VSMain(VSIn i){ PSIn o; o.pos = mul(WVP, float4(i.pos, 1.0)); return o; }
float4 PSMain(PSIn i) : SV_TARGET { return Color; }";

    private readonly GpuContext _gpu;
    private ComPtr<ID3D11VertexShader> _vs;
    private ComPtr<ID3D11PixelShader> _ps;
    private ComPtr<ID3D11InputLayout> _layout;
    private ComPtr<ID3D11Buffer> _cb;
    private ComPtr<ID3D11BlendState> _blend;
    private ComPtr<ID3D11DepthStencilState> _noDepth;
    private ComPtr<ID3D11RasterizerState> _raster;

    // One line-list vertex buffer per district (key = the SDS scene node, as collision uses).
    private readonly Dictionary<object, District> _districts = new();

    private struct District
    {
        public ComPtr<ID3D11Buffer> Vb;
        public uint VertexCount;
    }

    /// <summary>True while any district graph is uploaded.</summary>
    public bool HasData => _districts.Count > 0;

    public NavGraphRenderer(GpuContext gpu)
    {
        _gpu = gpu;

        using D3DCompiler compiler = D3DCompiler.GetApi();
        ComPtr<ID3D10Blob> vsCode = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "navgraph");
        ComPtr<ID3D10Blob> psCode = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "navgraph");
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

        _cb = GpuBuffers.CreateConstant<NavGraphConstants>(gpu);

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

        // Overlay: no depth-test and no depth write, so graph lines never z-fight the scene or pop.
        var dsd = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always };
        ID3D11DepthStencilState* nd = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in dsd, ref nd));
        _noDepth = nd;

        var rsd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
        ID3D11RasterizerState* rs = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rsd, ref rs));
        _raster = rs;
    }

    /// <summary>Uploads/replaces one district's graph (line endpoint pairs). Empty removes it.</summary>
    public void SetDistrict(object key, IReadOnlyList<Vector3> lineVertices)
    {
        RemoveDistrict(key);
        if (lineVertices == null || lineVertices.Count == 0) return;

        var verts = lineVertices as Vector3[] ?? [.. lineVertices];
        ComPtr<ID3D11Buffer> vb;
        fixed (Vector3* p = verts)
            vb = GpuBuffers.CreateImmutable(_gpu, p, (uint)(verts.Length * sizeof(Vector3)), BindFlag.VertexBuffer);
        _districts[key] = new District { Vb = vb, VertexCount = (uint)verts.Length };
    }

    /// <summary>Drops one district's graph (district unload).</summary>
    public void RemoveDistrict(object key)
    {
        if (_districts.Remove(key, out District d)) d.Vb.Dispose();
    }

    /// <summary>Drops every district's graph (scene reset).</summary>
    public void Clear()
    {
        foreach (District d in _districts.Values) d.Vb.Dispose();
        _districts.Clear();
    }

    public void Render(ComPtr<ID3D11DeviceContext> ctx, Matrix4x4 viewProj, Vector4 color)
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
        ctx.PSSetConstantBuffers(0, 1, &cb);
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist);

        var consts = new NavGraphConstants { Wvp = viewProj, Color = color };
        GpuBuffers.UpdateConstant(ctx, _cb, ref consts);

        uint stride = 12, offset = 0;
        foreach (District d in _districts.Values)
        {
            var vb = d.Vb.Handle;
            ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset);
            ctx.Draw(d.VertexCount, 0);
        }

        // Restore opaque blending for the next frame.
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
