using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Shaders;

namespace Illusion.Viewport;

/// <summary>
/// One texture, drawn at its own size and handed back as a picture — what the resource editor puts on the
/// stage when the resource you opened is a texture rather than a scene.
/// <para>
/// The game ships DDS, which WPF cannot decode and nothing in the toolkit decodes on the CPU either: the
/// only reader is <c>DdsTexture</c>, and it produces a GPU view. So the picture is made the same way the
/// material editor's thumbnails are — the texture on a camera-facing quad under flat lighting, read back
/// off the render target — only sized to the texture instead of to a 64px row.
/// </para>
/// <para>
/// Owns its own lazy headless GPU stack, so opening a texture costs nothing until one is opened and does
/// not start the scene viewport (which the resource editor deliberately leaves down until there is a scene
/// to draw). UI thread only.
/// </para>
/// </summary>
internal sealed class TexturePreviewRenderer : IDisposable
{
    /// <summary>Longest side the preview is rendered at. A 4096² readback is 64 MB through a staging copy
    /// for a picture that lands in a pane a few hundred pixels wide; past this the texture is drawn smaller
    /// and WPF scales it the rest of the way.</summary>
    private const int MaxSide = 1024;

    private GpuContext? _gpu;
    private SceneRenderer? _renderer;
    private SharedRenderTarget? _target;
    private bool _failed;   // a device that failed to create once will fail again — do not retry per click

    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The picture for one extracted <c>.dds</c>, cached by path, or null when the file cannot be sized or
    /// the GPU stack is unavailable. Never throws: a texture that will not decode is a thing to report in
    /// the pane, not a reason to take the window down.
    /// </summary>
    public ImageSource? Render(FileInfo dds)
    {
        if (!dds.Exists) return null;
        if (_cache.TryGetValue(dds.FullName, out ImageSource? hit)) return hit;
        if (ReadSize(dds) is not var (width, height)) return null;
        if (!EnsureContext()) return null;

        try
        {
            // The quad fills the frame exactly, so a target shaped like the texture gives back the texture:
            // no letterboxing to undo and no aspect to correct on the way out.
            Fit(width, height, out int w, out int h);
            if (_target == null || _target.Width != w || _target.Height != h)
            {
                _target?.Dispose();
                _target = new SharedRenderTarget(_gpu!, w, h);
            }

            _renderer!.Textures.AddFolder(dds.DirectoryName!);
            _renderer.Clear();
            _renderer.AddMesh(Quad(dds.Name));
            _renderer.Render(_target);

            byte[] bgra = RenderTargetReadback.Read(_gpu!, _target);
            BitmapSource bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            bmp.Freeze();
            _cache[dds.FullName] = bmp;
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The texture's own pixel size, straight out of the DDS header, or null if the file is not
    /// one. Read rather than guessed because the preview is rendered AT that size.</summary>
    public static (int Width, int Height)? ReadSize(FileInfo dds)
    {
        try
        {
            using FileStream stream = dds.OpenRead();
            Span<byte> head = stackalloc byte[20];
            if (stream.Read(head) < head.Length) return null;
            // "DDS " then a 124-byte header whose first three dwords are size, flags, height, width.
            if (head[0] != 'D' || head[1] != 'D' || head[2] != 'S' || head[3] != ' ') return null;
            int height = BitConverter.ToInt32(head[12..16]);
            int width = BitConverter.ToInt32(head[16..20]);
            return width > 0 && height > 0 ? (width, height) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Longest side capped, aspect kept, never smaller than a pixel.
    private static void Fit(int width, int height, out int w, out int h)
    {
        double scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        w = Math.Max(1, (int)Math.Round(width * scale));
        h = Math.Max(1, (int)Math.Round(height * scale));
    }

    // A camera-facing quad in the XZ plane (the camera sits on -Y, Z is up): V grows down the screen,
    // matching the texture's own orientation.
    private static MeshData Quad(string texture) => new()
    {
        Name = "texture-preview",
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
            // No sun, unit hemisphere, no specular: MaterialPreview shading degenerates to the plain texel,
            // which is the point — this is meant to be the image, not a lit surface showing it.
            _renderer.Lighting = new LightingConstants
            {
                AmbientUp = new Vector4(1f, 1f, 1f, 0f),
                AmbientDown = new Vector4(1f, 1f, 1f, 0f),
                SpecParams = new Vector4(16f, 0f, 0f, 5f),
                Gamma = new Vector4(1f, 1f, 1f, 1f),
            };
            // Half-height 1 fills the frame at d = 1/tan(fov/2) = √3; slightly closer overfills, so no
            // background can show along an edge.
            _renderer.Camera.LookAt(new Vector3(0f, -1.7f, 0f), Vector3.Zero);
            return true;
        }
        catch (Exception)
        {
            Dispose();
            _failed = true;
            return false;
        }
    }

    public void Dispose()
    {
        _target?.Dispose();
        _target = null;
        _renderer?.Dispose();
        _renderer = null;
        _gpu?.Dispose();
        _gpu = null;
        _cache.Clear();
    }
}
