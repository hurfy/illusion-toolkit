using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

[StructLayout(LayoutKind.Sequential)]
internal struct CollisionConstants
{
    public Matrix4x4 ViewProj;  // row-major (System.Numerics); HLSL side = row_major
    public Vector4 CameraPos;   // xyz = eye (for the toward-camera depth offset)
    public Vector4 Color;       // rgb + alpha (per material section)
}

[StructLayout(LayoutKind.Sequential)]
internal struct CollisionOutlineConstants
{
    public Vector4 Texel; // x,y = 1/width,1/height; z = outline radius (px); w = border alpha
}

/// <summary>
/// Overlay pass drawing collision hulls as a hardware-instanced, translucent layer colored per surface material,
/// with a colored screen-space silhouette border. It is depth-tested against the opaque scene (so a hull behind a
/// building is correctly occluded — no "x-ray" that makes far collision look mis-placed over near geometry), but
/// never writes depth. Coincident-surface z-fighting (flicker) is removed by nudging each vertex a small fraction
/// of its camera distance toward the eye in the vertex shader — a world-space, view-angle-independent offset, so
/// it neither flickers nor pops in and out with the camera the way a slope-scaled depth bias did. The border
/// reuses the selection-outline mask+dilation technique, with the mask storing the fill color so the border comes
/// out in each hull's own color.
/// </summary>
public sealed unsafe class CollisionRenderer : IDisposable
{
    private const float FillAlpha = 0.22f;
    private const float OutlineRadiusPx = 3.5f;   // border half-width ("рамка", a bit thick)
    private const float OutlineAlpha = 0.95f;

    // Instanced fill/mask: pos (slot 0) + per-instance world (slot 1); PSMain = flat fill, PSMask = color coverage.
    // The vertex is pulled 0.4% of its camera distance toward the eye so a collision surface coincident with a
    // visual surface consistently wins the depth test (no flicker) while still being occluded by nearer geometry.
    private const string Hlsl = @"
cbuffer CB : register(b0) { row_major float4x4 ViewProj; float4 CameraPos; float4 Color; };
struct VSIn { float3 pos : POSITION; float4 w0 : WORLD0; float4 w1 : WORLD1; float4 w2 : WORLD2; float4 w3 : WORLD3; };
struct PSIn { float4 pos : SV_POSITION; };
PSIn VSMain(VSIn i)
{
    PSIn o;
    float4x4 world = float4x4(i.w0, i.w1, i.w2, i.w3);   // rows = System.Numerics row-major
    float3 wp = mul(float4(i.pos, 1.0), world).xyz;      // row-vector: pos * World
    wp += (CameraPos.xyz - wp) * CameraPos.w;            // depth-fight guard: nudge toward the camera (w = factor)
    o.pos = mul(float4(wp, 1.0), ViewProj);
    return o;
}
float4 PSMain(PSIn i) : SV_TARGET { return Color; }
float4 PSMask(PSIn i) : SV_TARGET { return float4(Color.rgb, 1.0); }"; // rgb = material color, a = coverage

    // Fullscreen dilation contour: a background pixel within the radius of the silhouette takes the color of the
    // nearest covered mask texel — a colored border around each hull.
    private const string OutlineHlsl = @"
Texture2D    Mask : register(t0);
SamplerState Samp : register(s0);
cbuffer CB : register(b0) { float4 Texel; }; // xy = 1/size, z = radius (px), w = alpha
struct VOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VOut VSMain(uint id : SV_VertexID)
{
    float2 t = float2((id << 1) & 2, id & 2);
    VOut o;
    o.pos = float4(t.x * 2.0 - 1.0, 1.0 - t.y * 2.0, 0.0, 1.0);
    o.uv  = t;
    return o;
}
float4 PSMain(VOut i) : SV_TARGET
{
    float2 px = Texel.xy;
    float  r  = Texel.z;
    float  centerA = Mask.SampleLevel(Samp, i.uv, 0).a;
    float  bestA = 0.0;
    float3 bestC = 0.0;
    [unroll] for (int k = 0; k < 16; k++)
    {
        float ang = 6.28318530718 * (k / 16.0);
        float2 dir = float2(cos(ang), sin(ang));
        float4 s = Mask.SampleLevel(Samp, i.uv + dir * px * r, 0);
        if (s.a > bestA) { bestA = s.a; bestC = s.rgb; }
    }
    float a = saturate(bestA) * (1.0 - centerA);   // outside pixels adjacent to a silhouette; interior = 0
    if (a < 0.02) discard;
    return float4(saturate(bestC * 1.35 + 0.08), a * Texel.w); // brightened so the border reads over the faint fill
}";

    private readonly GpuContext _gpu;
    private ComPtr<ID3D11VertexShader> _vs;
    private ComPtr<ID3D11PixelShader> _psFill;
    private ComPtr<ID3D11PixelShader> _psMask;
    private ComPtr<ID3D11InputLayout> _layout;
    private ComPtr<ID3D11Buffer> _cb;

    private ComPtr<ID3D11VertexShader> _outlineVs;
    private ComPtr<ID3D11PixelShader> _outlinePs;
    private ComPtr<ID3D11Buffer> _outlineCb;
    private ComPtr<ID3D11SamplerState> _sampler;

    private ComPtr<ID3D11BlendState> _blend;
    private ComPtr<ID3D11DepthStencilState> _depth;    // test (LessEqual), no write — fill + mask
    private ComPtr<ID3D11DepthStencilState> _noDepth;  // dilation (fullscreen)
    private ComPtr<ID3D11RasterizerState> _raster;

    // Colored coverage mask (RGBA8), tracks the viewport size.
    private ComPtr<ID3D11Texture2D> _maskTex;
    private ComPtr<ID3D11RenderTargetView> _maskRtv;
    private ComPtr<ID3D11ShaderResourceView> _maskSrv;
    private int _maskW, _maskH;

    private readonly Dictionary<object, List<Mesh>> _byKey = new();
    private readonly List<(uint Start, uint Count)> _visibleRanges = new();

    // Selected placements to highlight (district key + mesh hash + current world), and a reusable 1-element
    // Default-usage instance buffer the highlight pass rewrites per selected hull.
    private readonly List<(object Key, ulong Hash, Matrix4x4 World)> _selection = new();
    private ComPtr<ID3D11Buffer> _highlightInstance;
    private static readonly Vector4 HighlightColor = new(1f, 0.82f, 0.20f, 0.85f); // bright amber, fairly opaque

    public CollisionRenderer(GpuContext gpu)
    {
        _gpu = gpu;
        using D3DCompiler compiler = D3DCompiler.GetApi();

        ComPtr<ID3D10Blob> vsCode = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "collision");
        ComPtr<ID3D10Blob> fillCode = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "collision-fill");
        ComPtr<ID3D10Blob> maskCode = ShaderCompiler.Compile(compiler, Hlsl, "PSMask", "ps_5_0", "collision-mask");
        (_vs, _psFill) = ShaderCompiler.CreateShaders(gpu, vsCode, fillCode);
        (ComPtr<ID3D11VertexShader> vsDup, _psMask) = ShaderCompiler.CreateShaders(gpu, vsCode, maskCode);
        vsDup.Dispose();

        byte* posName = (byte*)SilkMarshal.StringToPtr("POSITION");
        byte* wName = (byte*)SilkMarshal.StringToPtr("WORLD");
        var elems = stackalloc InputElementDesc[5];
        elems[0] = ShaderCompiler.VertexElement(posName, 0, Format.FormatR32G32B32Float, 0);
        for (uint r = 0; r < 4; r++)
        {
            elems[1 + r] = new InputElementDesc
            {
                SemanticName = wName,
                SemanticIndex = r,
                Format = Format.FormatR32G32B32A32Float,
                InputSlot = 1,
                AlignedByteOffset = r * 16,
                InputSlotClass = InputClassification.PerInstanceData,
                InstanceDataStepRate = 1,
            };
        }
        ID3D11InputLayout* layout = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateInputLayout(
            elems, 5, vsCode.GetBufferPointer(), vsCode.GetBufferSize(), ref layout));
        _layout = layout;
        SilkMarshal.Free((nint)posName);
        SilkMarshal.Free((nint)wName);
        vsCode.Dispose();
        fillCode.Dispose();
        maskCode.Dispose();

        ComPtr<ID3D10Blob> outVs = ShaderCompiler.Compile(compiler, OutlineHlsl, "VSMain", "vs_5_0", "collision-outline");
        ComPtr<ID3D10Blob> outPs = ShaderCompiler.Compile(compiler, OutlineHlsl, "PSMain", "ps_5_0", "collision-outline");
        (_outlineVs, _outlinePs) = ShaderCompiler.CreateShaders(gpu, outVs, outPs);
        outVs.Dispose();
        outPs.Dispose();

        _cb = GpuBuffers.CreateConstant<CollisionConstants>(gpu);
        _outlineCb = GpuBuffers.CreateConstant<CollisionOutlineConstants>(gpu);

        var sampDesc = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunc.Never,
            MaxLOD = float.MaxValue,
        };
        ID3D11SamplerState* samp = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in sampDesc, ref samp));
        _sampler = samp;

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

        // Depth-tested (LessEqual) but never writing — occluded by nearer geometry, no z-fight thanks to the VS nudge.
        var dsd = new DepthStencilDesc { DepthEnable = 1, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.LessEqual };
        ID3D11DepthStencilState* depth = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in dsd, ref depth));
        _depth = depth;

        var noDsd = new DepthStencilDesc { DepthEnable = 0, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always };
        ID3D11DepthStencilState* noDepth = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in noDsd, ref noDepth));
        _noDepth = noDepth;

        var rsd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
        ID3D11RasterizerState* rs = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rsd, ref rs));
        _raster = rs;

        Matrix4x4 identity = Matrix4x4.Identity;
        _highlightInstance = GpuBuffers.CreateDefault(gpu, &identity, (uint)sizeof(Matrix4x4), BindFlag.VertexBuffer);
    }

    /// <summary>Sets the selected placements to highlight (district key + mesh hash + current world matrix). The
    /// highlight pass draws each as a bright, opaque overlay so the selected hull stands out. Refreshed on
    /// selection change and each gizmo-drag frame, so it tracks the drag.</summary>
    public void SetSelection(IReadOnlyList<(object Key, ulong Hash, Matrix4x4 World)> selection)
    {
        _selection.Clear();
        _selection.AddRange(selection);
    }

    /// <summary>Uploads (or replaces) one source's collision meshes, keyed so it can be removed independently as
    /// districts stream in and out. Each unique mesh is uploaded once and instanced per placement; a null
    /// <paramref name="data"/> just removes the key.</summary>
    public void SetDistrict(object key, CollisionRenderData? data)
    {
        RemoveDistrict(key);
        if (data == null) return;
        var list = new List<Mesh>(data.Meshes.Length);
        foreach (CollisionRenderMesh src in data.Meshes)
        {
            if (src.Indices.Length == 0 || src.Instances.Length == 0) continue;
            list.Add(Mesh.Create(_gpu, src));
        }
        if (list.Count > 0) _byKey[key] = list;
    }

    /// <summary>Updates only the per-placement instance matrices of an already-uploaded source (a live transform
    /// edit) — rewrites each mesh's Default-usage instance buffer + re-cells it, keeping the decoded vertex/index
    /// buffers. Falls back to a full <see cref="SetDistrict"/> when the mesh set changed (add/delete of a hull).</summary>
    public void UpdateInstances(object key, CollisionRenderData? data)
    {
        if (data == null || !_byKey.TryGetValue(key, out List<Mesh>? list) || list.Count != data.Meshes.Length)
        {
            SetDistrict(key, data);
            return;
        }
        var byHash = new Dictionary<ulong, CollisionRenderMesh>(data.Meshes.Length);
        foreach (CollisionRenderMesh m in data.Meshes) byHash[m.Hash] = m;

        var ctx = _gpu.Context11;
        foreach (Mesh mesh in list)
        {
            if (!byHash.TryGetValue(mesh.Hash, out CollisionRenderMesh? src)) { SetDistrict(key, data); return; }
            mesh.UpdateInstances(_gpu, ctx, src);
        }
    }

    /// <summary>Removes one source's collision meshes (district unload).</summary>
    public void RemoveDistrict(object key)
    {
        if (_byKey.Remove(key, out List<Mesh>? list))
            foreach (Mesh m in list) m.Dispose();
    }

    /// <summary>Removes every source's collision meshes (scene reset / toggle off).</summary>
    public void Clear()
    {
        foreach (List<Mesh> list in _byKey.Values)
            foreach (Mesh m in list) m.Dispose();
        _byKey.Clear();
        _selection.Clear();
    }

    public bool HasData => _byKey.Count > 0;

    public void Render(SharedRenderTarget target, Matrix4x4 viewProj, Vector3 cameraPos, in Frustum frustum)
    {
        if (_byKey.Count == 0) return;
        var ctx = _gpu.Context11;
        EnsureMask(target.Width, target.Height);
        var camera = new Vector4(cameraPos, 0.004f); // xyz = eye, w = toward-camera nudge factor (anti-flicker)

        // ── Pass 1: translucent fill, depth-tested against the scene (occluded, no z-fight via the VS nudge). ──
        ctx.OMSetBlendState(_blend, (float*)null, 0xFFFFFFFF);
        ctx.OMSetDepthStencilState(_depth, 0);
        ctx.RSSetState(_raster);
        ctx.IASetInputLayout(_layout);
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_vs, (ID3D11ClassInstance**)null, 0);
        ctx.PSSetShader(_psFill, (ID3D11ClassInstance**)null, 0);
        var cb = _cb.Handle;
        ctx.VSSetConstantBuffers(0, 1, &cb);
        ctx.PSSetConstantBuffers(0, 1, &cb);
        foreach (List<Mesh> list in _byKey.Values)
            foreach (Mesh mesh in list) DrawFill(ctx, mesh, viewProj, camera, frustum);

        // ── Pass 2: colored coverage mask, same depth test against the scene DSV so the border matches the
        // visible fill (a hull occluded by a building contributes no border). ──
        var maskRtv = _maskRtv.Handle;
        ctx.OMSetRenderTargets(1, &maskRtv, target.Dsv);
        var clear = stackalloc float[4] { 0f, 0f, 0f, 0f };
        ctx.ClearRenderTargetView((ID3D11RenderTargetView*)_maskRtv.Handle, clear);
        ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, 0xFFFFFFFF);
        ctx.PSSetShader(_psMask, (ID3D11ClassInstance**)null, 0);
        foreach (List<Mesh> list in _byKey.Values)
            foreach (Mesh mesh in list) DrawMask(ctx, mesh, viewProj, camera, frustum);

        // ── Pass 3: colored dilation border back over the scene target. ──
        var sceneRtv = target.Rtv.Handle;
        ctx.OMSetRenderTargets(1, &sceneRtv, target.Dsv);
        var oc = new CollisionOutlineConstants
        {
            Texel = new Vector4(1f / target.Width, 1f / target.Height, OutlineRadiusPx, OutlineAlpha),
        };
        GpuBuffers.UpdateConstant(ctx, _outlineCb, ref oc);
        ctx.OMSetBlendState(_blend, (float*)null, 0xFFFFFFFF);
        ctx.OMSetDepthStencilState(_noDepth, 0);
        ctx.IASetInputLayout(default(ComPtr<ID3D11InputLayout>));
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_outlineVs, (ID3D11ClassInstance**)null, 0);
        ctx.PSSetShader(_outlinePs, (ID3D11ClassInstance**)null, 0);
        var outCb = _outlineCb.Handle;
        ctx.PSSetConstantBuffers(0, 1, &outCb);
        var samp = _sampler.Handle;
        ctx.PSSetSamplers(0, 1, &samp);
        var srv = _maskSrv.Handle;
        ctx.PSSetShaderResources(0, 1, &srv);
        ctx.Draw(3, 0);

        // Unbind the mask SRV (so next frame can render into it).
        ID3D11ShaderResourceView* nullSrv = null;
        ctx.PSSetShaderResources(0, 1, &nullSrv);

        // ── Pass 4: bright highlight of the selected placement(s), over the scene target. ──
        DrawHighlight(ctx, target, viewProj, cameraPos);

        // Restore opaque blending.
        ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, 0xFFFFFFFF);
    }

    // Draws each selected placement's hull as a solid amber overlay: depth-tested (occluded by nearer walls) but
    // nudged a touch more toward the camera than the normal fill so it wins their coincident surface. Reuses the
    // fill VS/PS with a 1-element instance buffer rewritten per selected hull.
    private void DrawHighlight(ComPtr<ID3D11DeviceContext> ctx, SharedRenderTarget target, Matrix4x4 viewProj, Vector3 cameraPos)
    {
        if (_selection.Count == 0) return;

        var sceneRtv = target.Rtv.Handle;
        ctx.OMSetRenderTargets(1, &sceneRtv, target.Dsv);
        ctx.OMSetBlendState(_blend, (float*)null, 0xFFFFFFFF);
        ctx.OMSetDepthStencilState(_depth, 0);
        ctx.RSSetState(_raster);
        ctx.IASetInputLayout(_layout);
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        ctx.VSSetShader(_vs, (ID3D11ClassInstance**)null, 0);
        ctx.PSSetShader(_psFill, (ID3D11ClassInstance**)null, 0);
        var cb = _cb.Handle;
        ctx.VSSetConstantBuffers(0, 1, &cb);
        ctx.PSSetConstantBuffers(0, 1, &cb);

        var cameraHi = new Vector4(cameraPos, 0.008f); // stronger nudge → the highlight beats the normal fill
        uint stride = 12, instStride = (uint)sizeof(Matrix4x4), offset = 0;
        foreach ((object key, ulong hash, Matrix4x4 world) in _selection)
        {
            if (!_byKey.TryGetValue(key, out List<Mesh>? list)) continue;
            Mesh? mesh = null;
            foreach (Mesh m in list) if (m.Hash == hash) { mesh = m; break; }
            if (mesh == null) continue;

            Matrix4x4 w = world;
            GpuBuffers.UpdateBuffer(ctx, _highlightInstance, &w);

            var vb = mesh.VertexBuffer.Handle;
            ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset);
            var ib = _highlightInstance.Handle;
            ctx.IASetVertexBuffers(1, 1, &ib, &instStride, &offset);
            ctx.IASetIndexBuffer(mesh.IndexBuffer, Format.FormatR32Uint, 0);

            var consts = new CollisionConstants { ViewProj = viewProj, CameraPos = cameraHi, Color = HighlightColor };
            GpuBuffers.UpdateConstant(ctx, _cb, ref consts);
            foreach (Part part in mesh.Parts)
                ctx.DrawIndexedInstanced(part.IndexCount, 1, part.StartIndex, 0, 0);
        }
    }

    private void DrawFill(ComPtr<ID3D11DeviceContext> ctx, Mesh mesh, Matrix4x4 viewProj, Vector4 cameraPos, in Frustum frustum)
    {
        if (!ComputeVisibleRanges(mesh, frustum)) return;
        BindMesh(ctx, mesh);
        foreach (Part part in mesh.Parts)
        {
            var consts = new CollisionConstants { ViewProj = viewProj, CameraPos = cameraPos, Color = new Vector4(part.Color, FillAlpha) };
            GpuBuffers.UpdateConstant(ctx, _cb, ref consts);
            foreach ((uint start, uint count) in _visibleRanges)
                ctx.DrawIndexedInstanced(part.IndexCount, count, part.StartIndex, 0, start);
        }
    }

    private void DrawMask(ComPtr<ID3D11DeviceContext> ctx, Mesh mesh, Matrix4x4 viewProj, Vector4 cameraPos, in Frustum frustum)
    {
        if (!ComputeVisibleRanges(mesh, frustum)) return;
        BindMesh(ctx, mesh);
        foreach (Part part in mesh.Parts)
        {
            var consts = new CollisionConstants { ViewProj = viewProj, CameraPos = cameraPos, Color = new Vector4(part.Color, 1f) };
            GpuBuffers.UpdateConstant(ctx, _cb, ref consts);
            foreach ((uint start, uint count) in _visibleRanges)
                ctx.DrawIndexedInstanced(part.IndexCount, count, part.StartIndex, 0, start);
        }
    }

    private void BindMesh(ComPtr<ID3D11DeviceContext> ctx, Mesh mesh)
    {
        uint stride = 12, instStride = (uint)sizeof(Matrix4x4), offset = 0;
        var vb = mesh.VertexBuffer.Handle;
        ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset);
        var ib = mesh.InstanceBuffer.Handle;
        ctx.IASetVertexBuffers(1, 1, &ib, &instStride, &offset);
        ctx.IASetIndexBuffer(mesh.IndexBuffer, Format.FormatR32Uint, 0);
    }

    // Fills _visibleRanges with the frustum-visible instance cell ranges (contiguous cells merged). False = none.
    private bool ComputeVisibleRanges(Mesh mesh, in Frustum frustum)
    {
        _visibleRanges.Clear();
        if (!frustum.Intersects(mesh.BoundsMin, mesh.BoundsMax)) return false;
        foreach (InstanceCell cell in mesh.Cells)
        {
            if (!frustum.Intersects(cell.Min, cell.Max)) continue;
            if (_visibleRanges.Count > 0 && _visibleRanges[^1].Start + _visibleRanges[^1].Count == cell.Start)
            {
                (uint start, uint count) = _visibleRanges[^1];
                _visibleRanges[^1] = (start, count + cell.Count);
            }
            else
            {
                _visibleRanges.Add((cell.Start, cell.Count));
            }
        }
        return _visibleRanges.Count > 0;
    }

    private void EnsureMask(int width, int height)
    {
        if (_maskTex.Handle != null && _maskW == width && _maskH == height) return;
        DisposeMask();

        var desc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)(BindFlag.RenderTarget | BindFlag.ShaderResource),
        };
        ID3D11Texture2D* tex = null;
        SilkMarshal.ThrowHResult(_gpu.Device11.CreateTexture2D(in desc, (SubresourceData*)null, ref tex));
        _maskTex = tex;

        ID3D11RenderTargetView* rtv = null;
        SilkMarshal.ThrowHResult(_gpu.Device11.CreateRenderTargetView((ID3D11Resource*)_maskTex.Handle, (RenderTargetViewDesc*)null, &rtv));
        _maskRtv = rtv;

        ID3D11ShaderResourceView* srv = null;
        SilkMarshal.ThrowHResult(_gpu.Device11.CreateShaderResourceView((ID3D11Resource*)_maskTex.Handle, (ShaderResourceViewDesc*)null, &srv));
        _maskSrv = srv;

        _maskW = width;
        _maskH = height;
    }

    private void DisposeMask()
    {
        _maskSrv.Dispose();
        _maskRtv.Dispose();
        _maskTex.Dispose();
        _maskSrv = default;
        _maskRtv = default;
        _maskTex = default;
        _maskW = _maskH = 0;
    }

    public void Dispose()
    {
        Clear();
        DisposeMask();
        _highlightInstance.Dispose();
        _raster.Dispose();
        _noDepth.Dispose();
        _depth.Dispose();
        _blend.Dispose();
        _sampler.Dispose();
        _outlineCb.Dispose();
        _outlinePs.Dispose();
        _outlineVs.Dispose();
        _cb.Dispose();
        _layout.Dispose();
        _psMask.Dispose();
        _psFill.Dispose();
        _vs.Dispose();
    }

    private readonly record struct Part(uint StartIndex, uint IndexCount, Vector3 Color);

    // One unique collision mesh uploaded to the GPU + its instance cloud (cell-major sorted) and material parts.
    private sealed class Mesh : IDisposable
    {
        public ulong Hash;
        public ComPtr<ID3D11Buffer> VertexBuffer;
        public ComPtr<ID3D11Buffer> IndexBuffer;
        public ComPtr<ID3D11Buffer> InstanceBuffer;
        public int InstanceCount;
        public InstanceCell[] Cells = Array.Empty<InstanceCell>();
        public Part[] Parts = Array.Empty<Part>();
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;

        public static Mesh Create(GpuContext gpu, CollisionRenderMesh src)
        {
            (Matrix4x4[] sorted, InstanceCell[] cells) = InstanceChunks.Build(src.Instances, src.LocalMin, src.LocalMax);

            var mesh = new Mesh { Hash = src.Hash, Cells = cells, InstanceCount = sorted.Length };
            fixed (Vector3* pv = src.Positions)
                mesh.VertexBuffer = GpuBuffers.CreateImmutable(gpu, pv, (uint)(src.Positions.Length * sizeof(Vector3)), BindFlag.VertexBuffer);
            fixed (uint* pi = src.Indices)
                mesh.IndexBuffer = GpuBuffers.CreateImmutable(gpu, pi, (uint)(src.Indices.Length * sizeof(uint)), BindFlag.IndexBuffer);
            // Default-usage (not immutable) so a live placement edit can rewrite the matrices in place.
            fixed (Matrix4x4* pm = sorted)
                mesh.InstanceBuffer = GpuBuffers.CreateDefault(gpu, pm, (uint)(sorted.Length * sizeof(Matrix4x4)), BindFlag.VertexBuffer);

            var parts = new Part[src.Parts.Length];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = new Part((uint)src.Parts[i].StartIndex, (uint)src.Parts[i].IndexCount, src.Parts[i].Color);
            mesh.Parts = parts;
            mesh.RecomputeBounds();
            return mesh;
        }

        // Rewrites the instance matrices from an edited placement set: same count → in-place UpdateSubresource;
        // changed count → recreate the (small) instance buffer. Vertex/index buffers are untouched.
        public void UpdateInstances(GpuContext gpu, ComPtr<ID3D11DeviceContext> ctx, CollisionRenderMesh src)
        {
            (Matrix4x4[] sorted, InstanceCell[] cells) = InstanceChunks.Build(src.Instances, src.LocalMin, src.LocalMax);
            if (sorted.Length == InstanceCount && InstanceCount > 0)
            {
                fixed (Matrix4x4* pm = sorted) GpuBuffers.UpdateBuffer(ctx, InstanceBuffer, pm);
            }
            else
            {
                InstanceBuffer.Dispose();
                if (sorted.Length > 0)
                    fixed (Matrix4x4* pm = sorted)
                        InstanceBuffer = GpuBuffers.CreateDefault(gpu, pm, (uint)(sorted.Length * sizeof(Matrix4x4)), BindFlag.VertexBuffer);
                else
                    InstanceBuffer = default;
                InstanceCount = sorted.Length;
            }
            Cells = cells;
            RecomputeBounds();
        }

        private void RecomputeBounds()
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (InstanceCell c in Cells)
            {
                min = Vector3.Min(min, c.Min);
                max = Vector3.Max(max, c.Max);
            }
            BoundsMin = min;
            BoundsMax = max;
        }

        public void Dispose()
        {
            InstanceBuffer.Dispose();
            IndexBuffer.Dispose();
            VertexBuffer.Dispose();
        }
    }
}
