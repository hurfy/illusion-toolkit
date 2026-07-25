using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Shaders;

/// <summary>
/// The Mafia-look lighting block shared by every mesh cbuffer — embedded as one field in both
/// <see cref="FrameConstants"/> and <see cref="InstancedConstants"/> so the C# layout is defined once and
/// stays in sync with the HLSL side (<see cref="ShaderCompiler.LightingCbufferTail"/>). Sequential layout
/// keeps it a flat 96-byte tail, matching the flat float4 cbuffer fields.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct LightingConstants
{
    public Vector4 CameraPos;    // .xyz world eye, for the specular view vector
    public Vector4 SunColor;     // warm directional-sun radiance (gamma space)
    public Vector4 AmbientUp;    // hemisphere sky fill (up-facing)
    public Vector4 AmbientDown;  // hemisphere ground bounce (down-facing)
    public Vector4 SpecParams;   // x=Phong exponent, y=level, z=rim strength, w=rim power
    public Vector4 Gamma;        // per-channel output gamma exponent (1 = passthrough)

    /// <summary>Mafia II-style daytime defaults (gamma-space; traced from the game's material/light fragments).
    /// <see cref="CameraPos"/> stays zero — the renderer fills it per frame.</summary>
    public static LightingConstants Default => new()
    {
        SunColor = new(0.90f, 0.83f, 0.70f, 0f), // warm daytime key light
        AmbientUp = new(0.45f, 0.52f, 0.62f, 0f), // cool sky fill (up-facing)
        AmbientDown = new(0.30f, 0.28f, 0.25f, 0f), // warm ground bounce (down-facing)
        SpecParams = new(16f, 0.35f, 0.06f, 5.0f), // x=Phong exponent, y=level, z=rim strength, w=rim power
        Gamma = new(1f, 1f, 1f, 1f),          // passthrough
    };
}

/// <summary>
/// Shared plumbing for the mesh shaders (regular + instanced): holds the VS/PS, input layout and constant
/// buffer, plus the identical <see cref="Bind"/> and <see cref="Dispose"/>. Subclasses supply only the HLSL,
/// their input-layout construction, and a typed UpdateConstants that pins its constant-buffer struct.
/// </summary>
public abstract unsafe class MeshShaderBase : IDisposable
{
    protected ComPtr<ID3D11VertexShader> _vs;
    protected ComPtr<ID3D11PixelShader> _ps;
    protected ComPtr<ID3D11InputLayout> _layout;
    protected ComPtr<ID3D11Buffer> _cb;

    public void Bind(ComPtr<ID3D11DeviceContext> ctx)
    {
        ctx.IASetInputLayout(_layout);
        ctx.VSSetShader(_vs, (ID3D11ClassInstance**)null, 0);
        ctx.PSSetShader(_ps, (ID3D11ClassInstance**)null, 0);
        var cb = _cb.Handle;
        ctx.VSSetConstantBuffers(0, 1, &cb);
        ctx.PSSetConstantBuffers(0, 1, &cb);
        ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
    }

    public void Dispose()
    {
        _cb.Dispose();
        _layout.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
