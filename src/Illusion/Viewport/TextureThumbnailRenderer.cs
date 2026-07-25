using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Shaders;

namespace Illusion.Viewport;

/// <summary>
/// Flat texture thumbnails for the material editor's slot rows: the .dds drawn on a camera-facing quad
/// in <see cref="RenderMode.MaterialPreview"/> (diffuse only) under flat lighting — effectively the raw
/// image, resolved through the same scope the preview sphere samples (folders + whole-mirror index).
/// Owns its own lazy headless GPU stack (the <see cref="MaterialThumbnailRenderer"/> pattern); results
/// are cached by texture name + folder count. UI thread only.
/// </summary>
internal sealed class TextureThumbnailRenderer : IDisposable
{
    public const int Size = 64;

    private GpuContext? _gpu;
    private SceneRenderer? _renderer;
    private SharedRenderTarget? _target;
    private bool _failed; // a device that failed to create once will fail again — don't retry per row
    private readonly Dictionary<string, ImageSource> _cache = new();

    /// <summary>The thumbnail for one texture name (cached), or null when the name is empty or the GPU
    /// stack is unavailable. An unresolvable .dds renders as the library's placeholder.</summary>
    public ImageSource? Render(string? texture, IReadOnlyList<string> folders)
    {
        if (string.IsNullOrEmpty(texture)) return null;
        string key = texture + "|" + folders.Count; // a new folder may resolve a previously missing .dds
        if (_cache.TryGetValue(key, out ImageSource? hit)) return hit;

        if (!EnsureContext()) return null;
        foreach (string folder in folders) _renderer!.Textures.AddFolder(folder);

        _renderer!.Clear();
        _renderer.AddMesh(Quad(texture));
        _renderer.Render(_target!);

        byte[] bgra = RenderTargetReadback.Read(_gpu!, _target!);
        BitmapSource bmp = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, bgra, Size * 4);
        bmp.Freeze();
        _cache[key] = bmp;
        return bmp;
    }

    // A camera-facing quad in the XZ plane (the camera sits on -Y, Z is up): V grows down the screen,
    // matching the texture's own orientation. Slightly larger than the frame so no background shows.
    private static MeshData Quad(string texture) => new()
    {
        Name = "texture-thumb",
        World = Matrix4x4.Identity,
        Positions = new[]
        {
            new Vector3(-1f, 0f, 1f), new Vector3(1f, 0f, 1f),
            new Vector3(1f, 0f, -1f), new Vector3(-1f, 0f, -1f),
        },
        Normals = new[]
        {
            new Vector3(0f, -1f, 0f), new Vector3(0f, -1f, 0f),
            new Vector3(0f, -1f, 0f), new Vector3(0f, -1f, 0f),
        },
        UVs = new[]
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f),
            new Vector2(1f, 1f), new Vector2(0f, 1f),
        },
        Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
        Parts = new[] { new MeshPart(0, 6, texture) },
    };

    private bool EnsureContext()
    {
        if (_renderer != null) return true;
        if (_failed) return false;
        try
        {
            _gpu = new GpuContext();
            _renderer = new SceneRenderer(_gpu) { Mode = RenderMode.MaterialPreview, ShowSky = false };
            // Whole-mirror fallback: a slot's texture resolves even when its district is not loaded.
            _renderer.Textures.SetFallbackResolver(Assets.Textures.TextureSearchIndex.FindPath);
            _target = new SharedRenderTarget(_gpu, Size, Size);
            // No sun, unit hemisphere, no specular: MaterialPreview shading degenerates to the plain texel.
            _renderer.Lighting = new LightingConstants
            {
                AmbientUp = new Vector4(1f, 1f, 1f, 0f),
                AmbientDown = new Vector4(1f, 1f, 1f, 0f),
                SpecParams = new Vector4(16f, 0f, 0f, 5f),
                Gamma = new Vector4(1f, 1f, 1f, 1f),
            };
            // Half-height 1 fills the square frame at d = 1/tan(fov/2) = √3; slightly closer overfills.
            _renderer.Camera.LookAt(new Vector3(0f, -1.7f, 0f), Vector3.Zero);
            return true;
        }
        catch
        {
            Dispose();
            _failed = true;
            return false;
        }
    }

    public void Dispose()
    {
        _renderer?.Dispose();
        _target?.Dispose();
        _gpu?.Dispose();
        _renderer = null;
        _target = null;
        _gpu = null;
        _cache.Clear();
    }
}
