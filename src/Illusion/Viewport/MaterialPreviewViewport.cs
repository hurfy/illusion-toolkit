using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Shaders;
// System.Windows.Interop also defines a RenderMode enum — ours must win unqualified.
using RenderMode = Illusion.Rendering.Passes.RenderMode;

namespace Illusion.Viewport;

/// <summary>
/// The material editor's live preview: a <see cref="ViewportControl"/> whose whole scene is one shaded
/// sphere carrying the selected material's textures, lit with the material's own specular/fresnel
/// parameters (D013/D027 — the scene viewport uses one global block instead). Left-drag orbits around
/// the sphere, the wheel zooms; the base's fly-camera stays available but is slowed to sphere scale.
/// This is the app's second live GPU stack beside the map viewport — each instance owns its devices.
/// </summary>
public sealed class MaterialPreviewViewport : ViewportControl
{
    private MeshPart _part = new(0, 0, null);
    private LightingConstants _materialLighting = LightingConstants.Default;
    private IReadOnlyList<string> _folders = Array.Empty<string>();
    private bool _orbiting;
    private Point _lastOrbit;

    public MaterialPreviewViewport()
    {
        ShowSky = true; // gradient sky, or the game panorama once the owner calls LoadSky
        RenderMode = RenderMode.Render;

        // Left-drag orbit (the base's left click is a pick hook, middle-drag is free-look — neither
        // suits inspecting a single sphere).
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _orbiting = true;
            _lastOrbit = e.GetPosition(this);
            CaptureMouse();
        };
        MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _orbiting = false;
            ReleaseMouseCapture();
        };
        MouseMove += (_, e) =>
        {
            if (!_orbiting) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _orbiting = false; return; }
            Point p = e.GetPosition(this);
            const float sens = 0.008f;
            OrbitCamera(-(float)(p.X - _lastOrbit.X) * sens, -(float)(p.Y - _lastOrbit.Y) * sens);
            _lastOrbit = p;
        };
    }

    /// <summary>Texture folders the sphere's maps resolve against (mirror of the map viewport's scope).
    /// Safe before load; applied on the next rebuild.</summary>
    public void SetFolders(IReadOnlyList<string> folders)
    {
        _folders = folders;
        Rebuild();
    }

    /// <summary>Shows a material: its three maps on the sphere + its own specular/fresnel response.</summary>
    public void SetMaterial(ulong hash, string? diffuse, string? normal, string? specular, LightingConstants lighting)
    {
        _part = new MeshPart(0, 0, diffuse, normal, specular, hash);
        _materialLighting = lighting;
        Lighting = lighting;
        Rebuild();
    }

    protected override void OnSceneInitialized()
    {
        Renderer!.Camera.LookAt(new Vector3(0f, -2.6f, 1.0f), Vector3.Zero);
        Renderer.Camera.MoveSpeed = 2.5f; // the base fly-camera at map speed would leave the sphere instantly
        _orbitDistance = new Vector3(0f, -2.6f, 1.0f).Length();
        Lighting = _materialLighting;
        Rebuild();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Renderer == null) return;
        Camera cam = Renderer.Camera;
        float dist = cam.Position.Length(); // the orbit pivot is the sphere at the origin
        float target = Math.Clamp(dist - e.Delta / 120f * 0.3f, 1.3f, 8f);
        cam.Position *= target / MathF.Max(dist, 0.001f);
        _orbitDistance = target;
        e.Handled = true;
    }

    private void Rebuild()
    {
        if (Renderer == null) return; // pre-load: OnSceneInitialized rebuilds with the cached state
        foreach (string folder in _folders) Renderer.Textures.AddFolder(folder);
        // Whole-mirror fallback: the preview shows real textures even for unloaded districts.
        Renderer.Textures.SetFallbackResolver(Assets.Textures.TextureSearchIndex.FindPath);
        Renderer.Clear();
        Renderer.AddMesh(SphereMesh.Create(_part));
    }

    /// <summary>
    /// The preview lighting block for a material: the scene defaults with the specular response replaced by
    /// the material's own D013 (SpecularPowerAndLevel) and D027 (FresnelBiasAndPower → the shader's rim
    /// term, its closest analogue). Clamped — game data carries outliers.
    /// </summary>
    public static LightingConstants LightingFor(MaterialInfo? info)
    {
        LightingConstants lighting = LightingConstants.Default;
        if (info == null) return lighting;
        Vector4 spec = lighting.SpecParams;
        foreach (MaterialParamInfo p in info.Parameters)
        {
            if (p.ParamId == "D013" && p.Values.Count >= 2)
            {
                spec.X = Math.Clamp(p.Values[0], 1f, 256f);   // Phong exponent
                spec.Y = Math.Clamp(p.Values[1], 0f, 2f);     // specular level
            }
            else if (p.ParamId == "D027" && p.Values.Count >= 2)
            {
                spec.Z = Math.Clamp(p.Values[0] * 0.5f, 0f, 0.4f); // fresnel bias → rim strength
                spec.W = Math.Clamp(p.Values[1], 1f, 16f);         // fresnel power → rim power
            }
        }
        return lighting with { SpecParams = spec };
    }
}
