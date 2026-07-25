namespace Illusion.Rendering.Passes;

/// <summary>Viewport shading mode (Blender-style), toggled from the toolbar. Switching is seamless —
/// only the shading changes per frame (BaseColor selector + raster state); geometry, textures and GPU
/// buffers are never rebuilt.</summary>
public enum RenderMode
{
    /// <summary>Full Mafia-look: diffuse + normal + specular maps, Phong specular, hemisphere ambient (default).</summary>
    Render,
    /// <summary>Diffuse texture only, simple lighting (geometric normal, lambert + ambient) — no normal/specular maps.</summary>
    MaterialPreview,
    /// <summary>Flat neutral color, still lit but untextured — geometry only.</summary>
    Solid,
    /// <summary>Wireframe fill over the flat color — the mesh grid.</summary>
    Wireframe,
}
