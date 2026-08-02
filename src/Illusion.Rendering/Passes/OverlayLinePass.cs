using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>Everything about this frame's view that an overlay glyph needs to size itself.</summary>
/// <param name="ViewProj">Camera view-projection, as the meshes use it.</param>
/// <param name="HalfViewport">Half the target size in pixels — the NDC-to-pixel factor.</param>
/// <param name="PixelScale">Metres per pixel per unit of clip-space W: <c>mpp = clipW * PixelScale</c>.
/// Derived from the projection, so it already accounts for the field of view and the target height.</param>
internal readonly record struct OverlayFrame(Matrix4x4 ViewProj, Vector2 HalfViewport, float PixelScale);

/// <summary>How an overlay reads the scene's depth buffer.</summary>
internal enum OverlayDepth
{
    /// <summary>Ignore depth entirely — draws over the scene (what every overlay used to do).</summary>
    Always,

    /// <summary>Only where the overlay is in front of the scene.</summary>
    Visible,

    /// <summary>Only where the scene hides the overlay — drawn faint, so a glyph inside a car body reads as
    /// being inside it rather than sitting on top of it.</summary>
    Hidden,
}

/// <summary>Look of one overlay layer.</summary>
internal struct OverlayLineStyle
{
    /// <summary>Line width in pixels — constant at any distance and any window size.</summary>
    public float Thickness;

    /// <summary>Soft edge width in pixels. Must stay above zero: it IS the antialiasing.</summary>
    public float Feather;

    /// <summary>Multiplies every segment's own colour (a layer whose segments are white is coloured here).</summary>
    public Vector4 Tint;

    /// <summary>Alpha for the part the scene hides. 1 disables the split and draws over everything in one pass.</summary>
    public float HiddenAlpha;

    /// <summary>Smallest a world-space glyph may project to, in pixels (0 = no floor). Segments opt in by
    /// carrying their glyph radius in <see cref="OverlaySegment.Extent"/>.</summary>
    public float MinGlyphPixels;

    /// <summary>Depth nudge toward the camera, in clip units — keeps a line that lies exactly on a surface
    /// from z-fighting it.</summary>
    public float DepthBias;

    /// <summary>The default look: a thin antialiased line, faint where the scene hides it.</summary>
    public static OverlayLineStyle Default(Vector4 tint) => new()
    {
        Thickness = 1.6f,
        Feather = 0.75f,
        Tint = tint,
        HiddenAlpha = 0.22f,
        MinGlyphPixels = 0f,
        DepthBias = 1e-4f,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct OverlayLineConstants
{
    public Matrix4x4 Wvp;   // load as-is (reinterpret-as-column transposes)
    public Vector4 Viewport; // xy = half viewport (px), z = pixel scale, w = depth bias
    public Vector4 Style;    // x = thickness (px), y = feather (px), z = alpha multiplier, w = min glyph (px)
    public Vector4 Tint;     // multiplies the per-segment colour
}

/// <summary>
/// The shared way this viewport draws overlay lines: rig bones, navigation graphs, path boxes, helper glyphs.
/// <para>
/// Each segment is ONE INSTANCE of a four-vertex quad that the vertex shader expands in screen space, so the
/// width is in pixels (constant at any distance, any DPI) and the pixel shader can feather the edge — a
/// <c>LINELIST</c> can do neither, and its one-pixel aliased result is the single biggest reason an overlay
/// looks dated. The same expansion gives glyphs a constant on-screen SIZE for free: a segment states an anchor
/// plus offsets, and the shader converts pixel offsets to metres at the anchor's depth.
/// </para>
/// <para>
/// Owned once by <see cref="SceneRenderer"/> and shared by every overlay layer — the shader, the states and
/// the constant buffer exist once rather than once per layer.
/// </para>
/// </summary>
internal sealed unsafe class OverlayLinePass : IDisposable
{
    // Segment quad: SV_VertexID 0,1 = the A end (both sides), 2,3 = the B end, drawn as a triangle strip.
    private const string Hlsl = @"
cbuffer CB : register(b0)
{
    float4x4 WVP;
    float4   Viewport;   // xy = half viewport (px), z = metres-per-pixel per clip W, w = depth bias
    float4   Style;      // x = thickness px, y = feather px, z = alpha multiplier, w = min glyph px
    float4   Tint;
};

struct VSIn
{
    float3 anchor  : ANCHOR;
    float3 offsetA : OFFSET0;
    float3 offsetB : OFFSET1;
    float  extent  : EXTENT;
    float4 color   : COLOR;
    uint   flags   : FLAGS;
    uint   vid     : SV_VertexID;
};

struct PSIn
{
    float4 pos    : SV_POSITION;
    float4 color  : COLOR;
    float  offset : TEXCOORD0;   // signed distance from the line centre, in pixels
};

PSIn VSMain(VSIn i)
{
    PSIn o;

    // The anchor's depth sets the pixel scale for the whole glyph.
    float4 ca0 = mul(WVP, float4(i.anchor, 1.0));
    float  mpp = max(ca0.w, 1e-3) * Viewport.z;      // world metres per screen pixel at this depth

    float3 oa = i.offsetA;
    float3 ob = i.offsetB;
    if ((i.flags & 1u) != 0u)
    {
        oa *= mpp;                                    // offsets are PIXELS: same size on screen at any distance
        ob *= mpp;
    }
    else if (Style.w > 0.0 && i.extent > 1e-6)
    {
        float k = max(1.0, (mpp * Style.w) / i.extent);  // floor: never smaller than N pixels across
        oa *= k;
        ob *= k;
    }

    float4 ca = mul(WVP, float4(i.anchor + oa, 1.0));
    float4 cb = mul(WVP, float4(i.anchor + ob, 1.0));

    // Screen-space expansion needs both ends in front of the camera; clip the segment to the near plane
    // first, and throw away one that is entirely behind it (its projection is meaningless).
    const float EPS = 1e-4;
    if (ca.w < EPS && cb.w < EPS)
    {
        o.pos = float4(0.0, 0.0, -1.0, 1.0);          // outside [0,w] depth → clipped away
        o.color = 0.0;
        o.offset = 0.0;
        return o;
    }
    if (ca.w < EPS) ca = lerp(ca, cb, (EPS - ca.w) / (cb.w - ca.w));
    if (cb.w < EPS) cb = lerp(cb, ca, (EPS - cb.w) / (ca.w - cb.w));

    float2 sa = ca.xy / ca.w * Viewport.xy;
    float2 sb = cb.xy / cb.w * Viewport.xy;
    float2 d  = sb - sa;
    float  len = length(d);
    float2 dir = (len > 1e-5) ? d / len : float2(1.0, 0.0);
    float2 nor = float2(-dir.y, dir.x);

    float halfW = Style.x * 0.5 + Style.y;            // half thickness + the feather margin
    float side  = (i.vid & 1u) ? 1.0 : -1.0;
    bool  atB   = (i.vid >= 2u);

    float4 clip = atB ? cb : ca;
    float2 sp   = atB ? sb : sa;
    sp += nor * side * halfW;
    sp += dir * (atB ? Style.y : -Style.y);           // pad the ends by the feather only — no visible caps

    o.pos = float4(sp / Viewport.xy * clip.w, clip.z - Viewport.w * clip.w, clip.w);
    o.color = i.color * Tint;
    o.color.a *= Style.z;
    o.offset = side * halfW;
    return o;
}

float4 PSMain(PSIn i) : SV_TARGET
{
    float hw = Style.x * 0.5;
    float a = 1.0 - smoothstep(hw - Style.y, hw + Style.y, abs(i.offset));
    float4 c = i.color;
    c.a *= a;
    clip(c.a - 0.004);
    return c;
}";

    private readonly GpuContext _gpu;
    private ComPtr<ID3D11VertexShader> _vs;
    private ComPtr<ID3D11PixelShader> _ps;
    private ComPtr<ID3D11InputLayout> _layout;
    private ComPtr<ID3D11Buffer> _cb;
    private ComPtr<ID3D11BlendState> _blend;
    private ComPtr<ID3D11DepthStencilState> _depthAlways;
    private ComPtr<ID3D11DepthStencilState> _depthVisible;
    private ComPtr<ID3D11DepthStencilState> _depthHidden;
    private ComPtr<ID3D11RasterizerState> _raster;

    public OverlayLinePass(GpuContext gpu)
    {
        _gpu = gpu;

        using D3DCompiler compiler = D3DCompiler.GetApi();
        ComPtr<ID3D10Blob> vsCode = ShaderCompiler.Compile(compiler, Hlsl, "VSMain", "vs_5_0", "overlayline");
        ComPtr<ID3D10Blob> psCode = ShaderCompiler.Compile(compiler, Hlsl, "PSMain", "ps_5_0", "overlayline");
        (_vs, _ps) = ShaderCompiler.CreateShaders(gpu, vsCode, psCode);

        byte* anchorName = (byte*)SilkMarshal.StringToPtr("ANCHOR");
        byte* offsetName = (byte*)SilkMarshal.StringToPtr("OFFSET");
        byte* extentName = (byte*)SilkMarshal.StringToPtr("EXTENT");
        byte* colorName = (byte*)SilkMarshal.StringToPtr("COLOR");
        byte* flagsName = (byte*)SilkMarshal.StringToPtr("FLAGS");
        Span<InputElementDesc> elems =
        [
            InstanceElement(anchorName, 0, Format.FormatR32G32B32Float, 0),
            InstanceElement(offsetName, 0, Format.FormatR32G32B32Float, 12),
            InstanceElement(offsetName, 1, Format.FormatR32G32B32Float, 24),
            InstanceElement(extentName, 0, Format.FormatR32Float, 36),
            InstanceElement(colorName, 0, Format.FormatR8G8B8A8Unorm, 40),
            InstanceElement(flagsName, 0, Format.FormatR32Uint, 44),
        ];
        ID3D11InputLayout* layout = null;
        fixed (InputElementDesc* e = elems)
        {
            SilkMarshal.ThrowHResult(gpu.Device11.CreateInputLayout(
                e, (uint)elems.Length, vsCode.GetBufferPointer(), vsCode.GetBufferSize(), ref layout));
        }
        _layout = layout;
        SilkMarshal.Free((nint)anchorName);
        SilkMarshal.Free((nint)offsetName);
        SilkMarshal.Free((nint)extentName);
        SilkMarshal.Free((nint)colorName);
        SilkMarshal.Free((nint)flagsName);
        vsCode.Dispose();
        psCode.Dispose();

        _cb = GpuBuffers.CreateConstant<OverlayLineConstants>(gpu);

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

        // Three readings of the scene depth; none of them WRITES depth — an overlay must not occlude anything.
        _depthAlways = CreateDepth(gpu, enabled: false, ComparisonFunc.Always);
        _depthVisible = CreateDepth(gpu, enabled: true, ComparisonFunc.LessEqual);
        _depthHidden = CreateDepth(gpu, enabled: true, ComparisonFunc.Greater);

        var rsd = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1 };
        ID3D11RasterizerState* rs = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rsd, ref rs));
        _raster = rs;
    }

    private static ComPtr<ID3D11DepthStencilState> CreateDepth(GpuContext gpu, bool enabled, ComparisonFunc func)
    {
        var dsd = new DepthStencilDesc
        {
            DepthEnable = enabled,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = func,
        };
        ID3D11DepthStencilState* state = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in dsd, ref state));
        return state;
    }

    private static InputElementDesc InstanceElement(byte* name, uint index, Format format, uint offset) => new()
    {
        SemanticName = name,
        SemanticIndex = index,
        Format = format,
        InputSlot = 0,
        AlignedByteOffset = offset,
        InputSlotClass = InputClassification.PerInstanceData,
        InstanceDataStepRate = 1,
    };

    /// <summary>Uploads a segment list as an immutable instance buffer. Empty input returns a null buffer.</summary>
    public ComPtr<ID3D11Buffer> CreateBuffer(ReadOnlySpan<OverlaySegment> segments)
    {
        if (segments.Length == 0) return default;
        fixed (OverlaySegment* p = segments)
        {
            return GpuBuffers.CreateImmutable(
                _gpu, p, (uint)(segments.Length * sizeof(OverlaySegment)), BindFlag.VertexBuffer);
        }
    }

    /// <summary>
    /// Binds the pipeline for one depth reading of one layer. Call once, then <see cref="DrawSegments"/> for
    /// each buffer, then <see cref="End"/> — a layer split across depth readings binds twice.
    /// </summary>
    public void Begin(ComPtr<ID3D11DeviceContext> ctx, in OverlayFrame frame, in OverlayLineStyle style,
        OverlayDepth depth, float alphaMul)
    {
        ctx.OMSetBlendState(_blend, (float*)null, 0xffffffff);
        ctx.OMSetDepthStencilState(depth switch
        {
            OverlayDepth.Visible => _depthVisible,
            OverlayDepth.Hidden => _depthHidden,
            _ => _depthAlways,
        }, 0);
        ctx.RSSetState(_raster);
        ctx.IASetInputLayout(_layout);
        ctx.VSSetShader(_vs, (ID3D11ClassInstance**)null, 0);
        ctx.PSSetShader(_ps, (ID3D11ClassInstance**)null, 0);
        var cb = _cb.Handle;
        ctx.VSSetConstantBuffers(0, 1, &cb);
        ctx.PSSetConstantBuffers(0, 1, &cb);
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglestrip);

        var consts = new OverlayLineConstants
        {
            Wvp = frame.ViewProj,
            Viewport = new Vector4(frame.HalfViewport.X, frame.HalfViewport.Y, frame.PixelScale, style.DepthBias),
            Style = new Vector4(style.Thickness, MathF.Max(style.Feather, 0.25f), alphaMul, style.MinGlyphPixels),
            Tint = style.Tint,
        };
        GpuBuffers.UpdateConstant(ctx, _cb, ref consts);
    }

    /// <summary>Draws one uploaded segment buffer with the state <see cref="Begin"/> left bound.</summary>
    public void DrawSegments(ComPtr<ID3D11DeviceContext> ctx, ComPtr<ID3D11Buffer> buffer, uint segmentCount)
    {
        if (buffer.Handle == null || segmentCount == 0) return;
        uint stride = (uint)sizeof(OverlaySegment), offset = 0;
        var vb = buffer.Handle;
        ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset);
        ctx.DrawInstanced(4, segmentCount, 0, 0);
    }

    /// <summary>Restores opaque blending for whatever draws next.</summary>
    public void End(ComPtr<ID3D11DeviceContext> ctx)
    {
        ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, 0xffffffff);
    }

    public void Dispose()
    {
        _raster.Dispose();
        _depthHidden.Dispose();
        _depthVisible.Dispose();
        _depthAlways.Dispose();
        _blend.Dispose();
        _cb.Dispose();
        _layout.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
