using System.Numerics;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Shaders;
using Illusion.Rendering.Textures;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

/// <summary>
/// Draws loaded Mafia meshes into a shared target: the shared Mafia-look shader,
/// depth test, free camera. Culling is disabled (winding in SDS is not guaranteed).
/// </summary>
public sealed unsafe class SceneRenderer : IDisposable
{
    private readonly GpuContext _gpu;
    private readonly MeshShader _shader;
    private readonly InstancedMeshShader _instShader;
    private readonly SkyRenderer _sky;
    private readonly ZoneRenderer _zoneRenderer;
    private readonly CollisionRenderer _collisionRenderer;
    private readonly NavGraphRenderer _navRenderer;
    private readonly NavGraphRenderer _navMeshRenderer;
    private readonly NavGraphRenderer _navWorldRenderer;
    private readonly ActorMarkerRenderer _actorRenderer;
    private readonly ActorMarkerRenderer _actorSelectionRenderer;
    private readonly SelectionOutlineRenderer _selectionOutline;

    /// <summary>Whether to show debug loading-zone boxes (UI toggle). Off by default.</summary>
    public bool ShowZones { get; set; }
    private IReadOnlyList<(Vector3 Min, Vector3 Max, Vector4 Color)>? _zoneBoxes;
    public void SetZoneBoxes(IReadOnlyList<(Vector3 Min, Vector3 Max, Vector4 Color)> boxes) => _zoneBoxes = boxes;

    /// <summary>Uploads/replaces one district's collision geometry (keyed so streaming can remove it alone).</summary>
    public void SetCollisionDistrict(object key, Domain.CollisionRenderData? data) => _collisionRenderer.SetDistrict(key, data);
    /// <summary>Live-updates only a district's collision instance matrices (a placement edit) — cheap in-place
    /// buffer rewrite, no re-decode.</summary>
    public void UpdateCollisionInstances(object key, Domain.CollisionRenderData? data) => _collisionRenderer.UpdateInstances(key, data);
    /// <summary>Removes one district's collision geometry (district unload).</summary>
    public void RemoveCollisionDistrict(object key) => _collisionRenderer.RemoveDistrict(key);
    /// <summary>Sets which collision placements are highlighted as selected (district key + mesh hash + world).</summary>
    public void SetCollisionSelection(IReadOnlyList<(object Key, ulong Hash, Matrix4x4 World)> selection) =>
        _collisionRenderer.SetSelection(selection);
    /// <summary>Removes all collision geometry (scene reset / toggle off).</summary>
    public void ClearCollision() => _collisionRenderer.Clear();
    /// <summary>Whether any collision geometry is currently uploaded.</summary>
    public bool HasCollisionData => _collisionRenderer.HasData;

    /// <summary>Whether to draw the .nov overlay (the AI navigation graph AND its AI-mesh boxes, one toggle).
    /// Off by default; the data is uploaded per district at load, so this only gates drawing.</summary>
    public bool ShowNov { get; set; }
    /// <summary>Uploads/replaces one district's navigation graph as line segments (keyed for streaming).</summary>
    public void SetNavDistrict(object key, IReadOnlyList<Vector3> lineVertices) => _navRenderer.SetDistrict(key, lineVertices);
    /// <summary>Removes one district's navigation graph (district unload).</summary>
    public void RemoveNavDistrict(object key) => _navRenderer.RemoveDistrict(key);
    /// <summary>Uploads/replaces one district's AI-mesh as box wireframe lines (keyed for streaming).</summary>
    public void SetNavMeshDistrict(object key, IReadOnlyList<Vector3> lineVertices) => _navMeshRenderer.SetDistrict(key, lineVertices);
    /// <summary>Removes one district's AI-mesh (district unload).</summary>
    public void RemoveNavMeshDistrict(object key) => _navMeshRenderer.RemoveDistrict(key);
    /// <summary>Removes all .nov overlays — graph and AI-mesh (scene reset).</summary>
    public void ClearNov() { _navRenderer.Clear(); _navMeshRenderer.Clear(); }

    /// <summary>Whether to draw the .nav overlay (AI path objects: cover / vault-over / action markers as
    /// boxes). Off by default; uploaded per district at load, so this only gates drawing.</summary>
    public bool ShowNavWorld { get; set; }
    /// <summary>Uploads/replaces one district's .nav path-object boxes (keyed for streaming).</summary>
    public void SetNavWorldDistrict(object key, IReadOnlyList<Vector3> lineVertices) => _navWorldRenderer.SetDistrict(key, lineVertices);
    /// <summary>Removes one district's .nav overlay (district unload).</summary>
    public void RemoveNavWorldDistrict(object key) => _navWorldRenderer.RemoveDistrict(key);
    /// <summary>Removes all .nav overlays (scene reset).</summary>
    public void ClearNavWorld() => _navWorldRenderer.Clear();

    /// <summary>Whether to draw glyphs for the actors nothing else draws (sounds, lights, triggers, script
    /// hooks…). Off by default; uploaded per district at load, so this only gates drawing.</summary>
    public bool ShowActors { get; set; }
    /// <summary>Uploads/replaces one district's actor glyphs (keyed for streaming).</summary>
    public void SetActorDistrict(object key, Domain.ActorMarkerRenderData? markers) => _actorRenderer.SetDistrict(key, markers);
    /// <summary>Removes one district's actor glyphs (district unload).</summary>
    public void RemoveActorDistrict(object key) => _actorRenderer.RemoveDistrict(key);
    /// <summary>Removes every district's actor glyphs (scene reset).</summary>
    public void ClearActors() => _actorRenderer.Clear();
    /// <summary>Actor glyphs currently resident.</summary>
    public int ActorMarkerCount => _actorRenderer.MarkerCount;

    /// <summary>Highlights the selected actors' glyphs (replaces any prior highlight; null clears it). Drawn
    /// whether or not <see cref="ShowActors"/> is on — selecting an actor in the tree has to show where it is
    /// even with the overlay off.</summary>
    public void SetSelectedActorMarkers(Domain.ActorMarkerRenderData? markers) =>
        _actorSelectionRenderer.SetDistrict(SelectionKey, markers);

    private static readonly object SelectionKey = new();

    // Selected mesh(es) to outline. The highlight is a screen-space silhouette contour of the exact geometry
    // (SelectionOutlineRenderer), not a bounding box — so only mesh objects are ever highlighted.
    private readonly List<GpuMesh> _selectionMeshes = new(1);

    // Selected copies of an instanced prototype (crash props): the same geometry outlined at one copy's matrix,
    // since an instanced mesh carries no World of its own to outline.
    private readonly List<(GpuMesh Mesh, System.Numerics.Matrix4x4 World)> _selectionPlacements = new(1);

    /// <summary>Highlights a set of meshes with a Blender-style silhouette outline (replaces any prior selection).</summary>
    public void SetSelectionMeshes(IReadOnlyList<GpuMesh> meshes)
    {
        _selectionMeshes.Clear();
        _selectionMeshes.AddRange(meshes);
    }

    /// <summary>Highlights individual copies of instanced prototypes (crash placements), replacing any prior set.</summary>
    public void SetSelectionPlacements(IReadOnlyList<(GpuMesh Mesh, System.Numerics.Matrix4x4 World)> placements)
    {
        _selectionPlacements.Clear();
        _selectionPlacements.AddRange(placements);
    }

    /// <summary>Clears the selection highlight (nothing selected, or only non-mesh containers are selected).</summary>
    public void ClearSelection()
    {
        _selectionMeshes.Clear();
        _selectionPlacements.Clear();
    }

    /// <summary>Viewport shading mode (Blender-style toolbar toggle). Default = Material Preview.</summary>
    public RenderMode Mode { get; set; } = RenderMode.MaterialPreview;

    /// <summary>Optional scene lighting (caller-settable so a reused viewport can light its own content).
    /// Defaults to the Mafia-look daytime block; the camera eye is filled per frame.</summary>
    public LightingConstants Lighting { get; set; } = LightingConstants.Default;

    /// <summary>World-space direction the sun points (from sky toward the ground). Caller-settable.</summary>
    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(0.4f, 0.5f, -0.8f));

    /// <summary>Draw the sky background (panorama or gradient). Off → the flat clear color shows through.</summary>
    public bool ShowSky { get; set; } = true;

    /// <summary>The flat background when the sky is off (and behind it when on). Caller-settable —
    /// thumbnails pick a neutral gray so tiles don't read blue.</summary>
    public Vector4 ClearColor { get; set; } = new(0.10f, 0.12f, 0.16f, 1f);

    private ComPtr<ID3D11RasterizerState> _raster;      // solid fill (Material / Solid)
    private ComPtr<ID3D11RasterizerState> _rasterWire;  // wireframe fill (Wireframe mode)
    private ComPtr<ID3D11DepthStencilState> _depthState;
    private ComPtr<ID3D11DepthStencilState> _depthReadOnly; // ghost pass: test but never write
    private ComPtr<ID3D11BlendState> _blendGhost;           // ghost pass: standard alpha blend
    private ComPtr<ID3D11SamplerState> _sampler;

    private readonly List<GpuMesh> _meshes = new();

    // Bridge edit mode: while non-null, every mesh OUTSIDE this set renders ghosted. Newly attached
    // meshes (streaming) join the ghost side automatically.
    private HashSet<GpuMesh>? _ghostFocus;

    /// <summary>Enters/leaves bridge edit mode: every mesh except <paramref name="edited"/> renders
    /// desaturated and translucent; null restores normal rendering for all.</summary>
    public void SetGhostFocus(IReadOnlyCollection<GpuMesh>? edited)
    {
        _ghostFocus = edited == null ? null : new HashSet<GpuMesh>(edited);
        foreach (GpuMesh m in _meshes) m.Ghost = _ghostFocus != null && !_ghostFocus.Contains(m);
    }

    /// <summary>All loaded meshes — read-only, for viewport ray-picking.</summary>
    public IReadOnlyList<GpuMesh> Meshes => _meshes;

    public Camera Camera { get; } = new();
    public TextureLibrary Textures { get; }
    public int DrawnMeshes { get; private set; }
    public long TotalTriangles { get; private set; }

    /// <summary>Draw* calls issued this frame (both passes) — the CPU-side cost signal for the stats bar.</summary>
    public int DrawCalls { get; private set; }

    /// <summary>Instances that survived per-cell culling this frame (instanced pass only).</summary>
    public long DrawnInstances { get; private set; }

    /// <summary>Max distance (m) at which instanced cells still draw; 0 = disabled (frustum culling only).
    /// Off by default — enabling it makes distant clutter pop in/out, a visible behavior change.</summary>
    public float InstanceDrawDistance { get; set; }

    // Scratch for the instanced pass: visible instance ranges of one mesh (contiguous cells merged).
    private readonly List<(uint Start, uint Count)> _visibleRanges = new();

    public SceneRenderer(GpuContext gpu)
    {
        _gpu = gpu;
        _shader = new MeshShader(gpu);
        _instShader = new InstancedMeshShader(gpu);
        _sky = new SkyRenderer(gpu);
        _zoneRenderer = new ZoneRenderer(gpu);
        _collisionRenderer = new CollisionRenderer(gpu);
        _navRenderer = new NavGraphRenderer(gpu);
        _navMeshRenderer = new NavGraphRenderer(gpu);
        _navWorldRenderer = new NavGraphRenderer(gpu);
        _actorRenderer = new ActorMarkerRenderer(gpu);
        _actorSelectionRenderer = new ActorMarkerRenderer(gpu);
        _selectionOutline = new SelectionOutlineRenderer(gpu);
        Textures = new TextureLibrary(gpu);

        var rsDesc = new RasterizerDesc
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = 1,
        };
        ID3D11RasterizerState* rs = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rsDesc, ref rs));
        _raster = rs;

        // Wireframe variant (same culling): edges only, for the "mesh grid" mode.
        var rsWireDesc = new RasterizerDesc
        {
            FillMode = FillMode.Wireframe,
            CullMode = CullMode.None,
            DepthClipEnable = 1,
        };
        ID3D11RasterizerState* rsWire = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateRasterizerState(in rsWireDesc, ref rsWire));
        _rasterWire = rsWire;

        var dsDesc = new DepthStencilDesc
        {
            DepthEnable = 1,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunc.Less,
        };
        ID3D11DepthStencilState* dss = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in dsDesc, ref dss));
        _depthState = dss;

        // Ghost pass: depth-tested against the opaque scene but never writing — translucent meshes
        // neither occlude the edited set nor fight each other for depth.
        var dsReadDesc = dsDesc with { DepthWriteMask = DepthWriteMask.Zero };
        ID3D11DepthStencilState* dssRead = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilState(in dsReadDesc, ref dssRead));
        _depthReadOnly = dssRead;

        var blendDesc = new BlendDesc();
        blendDesc.RenderTarget[0] = new RenderTargetBlendDesc
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
        SilkMarshal.ThrowHResult(gpu.Device11.CreateBlendState(in blendDesc, ref blend));
        _blendGhost = blend;

        var sampDesc = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunc.Never,
            MaxLOD = float.MaxValue,
        };
        ID3D11SamplerState* samp = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in sampDesc, ref samp));
        _sampler = samp;
    }

    public void LoadSky(string ddsPath)
    {
        _sky.SetPanorama(_gpu, System.IO.File.ReadAllBytes(ddsPath));
    }

    /// <summary>Unloads all scene meshes (on area change). Folders stay registered; texture entries whose
    /// last user is among the disposed meshes are evicted by the library's lease accounting.</summary>
    public void Clear()
    {
        _selectionMeshes.Clear(); // the selected mesh is among those about to be disposed
        foreach (GpuMesh m in _meshes) m.Dispose();
        _meshes.Clear();
        DrawnMeshes = 0;
        TotalTriangles = 0;
    }

    /// <summary>Unloads specific meshes (for streaming district unload). Each disposed mesh returns its texture
    /// leases, so SRVs no other mesh uses are released with it. Returns the count actually removed from the render
    /// list (may be fewer than requested — e.g. a mesh already detached by a delete is no longer present), so the
    /// caller keeps its own counters accurate.</summary>
    public int RemoveMeshes(IEnumerable<GpuMesh> meshes)
    {
        // One RemoveAll sweep instead of List.Remove per mesh — district unload is O(n), not O(n·m).
        var set = new HashSet<GpuMesh>(meshes);
        if (set.Count == 0) return 0;
        _selectionMeshes.RemoveAll(set.Contains); // drop any selected mesh that's being disposed
        return _meshes.RemoveAll(m =>
        {
            if (!set.Remove(m)) return false;
            TotalTriangles -= (long)m.TriangleCount * m.InstanceCount;
            m.Dispose();
            return true;
        });
    }

    /// <summary>Removes meshes from the render list WITHOUT disposing them (for an undoable delete — the edit
    /// keeps them alive so undo can re-attach). The caller owns the returned lifetime until it re-attaches or
    /// disposes them.</summary>
    public void DetachMeshes(IEnumerable<GpuMesh> meshes)
    {
        var set = new HashSet<GpuMesh>(meshes);
        if (set.Count == 0) return;
        _selectionMeshes.RemoveAll(set.Contains);
        _meshes.RemoveAll(m =>
        {
            if (!set.Contains(m)) return false;
            TotalTriangles -= (long)m.TriangleCount * m.InstanceCount;
            return true;
        });
    }

    /// <summary>Creates GPU resources for one mesh without registering it. Device-only work
    /// (buffers, textures) — the D3D11 device is free-threaded, so this is safe on a loader thread.</summary>
    public GpuMesh CreateMeshGpu(Domain.MeshData md) => GpuMesh.Create(_gpu, md, Textures);

    /// <summary>Replaces the copies of an instanced mesh in place — a crash placement was moved, added or
    /// removed. Only the matrix buffer is rebuilt; the mesh keeps its geometry, parts and texture leases, and
    /// stays in the render list.</summary>
    public void UpdateInstances(GpuMesh mesh, System.Numerics.Matrix4x4[] instances, float[]? drawDistances = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        mesh.SetInstances(_gpu, instances, drawDistances);
    }

    /// <summary>Registers an already-created mesh with the render list. UI thread only —
    /// the render loop iterates the list without a lock.</summary>
    public void AttachMesh(GpuMesh gm)
    {
        // Meshes arriving during bridge edit mode (streaming, undo re-attach) join the ghost side
        // unless they belong to the edited set.
        gm.Ghost = _ghostFocus != null && !_ghostFocus.Contains(gm);
        _meshes.Add(gm);
        TotalTriangles += (long)gm.TriangleCount * gm.InstanceCount;
    }

    /// <summary>Creates and adds a single mesh in one step (probes / single-threaded callers).</summary>
    public GpuMesh AddMesh(Domain.MeshData md)
    {
        GpuMesh gm = CreateMeshGpu(md);
        AttachMesh(gm);
        return gm;
    }

    public void Render(SharedRenderTarget target)
    {
        var ctx = _gpu.Context11;

        var vp = new Viewport(0, 0, target.Width, target.Height, 0, 1);
        ctx.RSSetViewports(1, &vp);

        var rtv = target.Rtv.Handle;
        ctx.OMSetRenderTargets(1, &rtv, target.Dsv);

        var clear = stackalloc float[4] { ClearColor.X, ClearColor.Y, ClearColor.Z, ClearColor.W };
        ctx.ClearRenderTargetView((ID3D11RenderTargetView*)target.Rtv.Handle, clear);
        ctx.ClearDepthStencilView((ID3D11DepthStencilView*)target.Dsv.Handle,
            (uint)(ClearFlag.Depth | ClearFlag.Stencil), 1f, 0);

        // Sky gradient as background (depth disabled inside). Optional — off leaves the flat clear color.
        Camera.AspectRatio = target.Height > 0 ? (float)target.Width / target.Height : 1f;
        if (ShowSky) _sky.Render(_gpu, Camera);

        // Shading mode: wireframe fill for the grid; BaseColor.a is the shading selector consumed by
        // MafiaLitPs (2 = textured + normal/spec maps, 1 = textured + simple lighting, 0 = flat + full
        // lighting). Switching is a per-frame constant change only — geometry/textures are never rebuilt.
        bool wire = Mode == RenderMode.Wireframe;
        Vector4 baseColor = Mode switch
        {
            RenderMode.Render => new Vector4(1f, 1f, 1f, 2f),           // .a=2 → DiffuseTex + normal/spec maps
            RenderMode.MaterialPreview => new Vector4(1f, 1f, 1f, 1f),  // .a=1 → DiffuseTex only, simple lighting
            RenderMode.Solid => new Vector4(0.80f, 0.81f, 0.83f, 0f),   // flat neutral, still lit
            _ => new Vector4(0.85f, 0.87f, 0.90f, 0f),                  // Wireframe: brighter flat lines
        };

        ctx.RSSetState(wire ? _rasterWire : _raster);
        ctx.OMSetDepthStencilState(_depthState, 0);
        _shader.Bind(ctx);
        var samp = _sampler.Handle;
        ctx.PSSetSamplers(0, 1, &samp);

        Matrix4x4 viewProj = Camera.ViewProjection;
        Frustum frustum = Frustum.FromMatrix(viewProj);
        Vector3 lightDir = LightDirection;
        // Lighting is identical for every mesh this frame — build it once, fill only the camera eye.
        var lighting = Lighting with { CameraPos = new Vector4(Camera.Position, 0f) };

        int drawn = 0;
        DrawCalls = 0;
        DrawnInstances = 0;

        // Opaque pass: everything not ghosted, then its instanced counterpart.
        drawn += DrawMeshPass(ctx, viewProj, frustum, lightDir, baseColor, lighting, ghostPass: false);
        drawn += RenderInstanced(ctx, viewProj, frustum, lightDir, baseColor, lighting, ghostPass: false);

        // Ghost pass (bridge edit mode): meshes NOT open in Blender — alpha-blended, depth-read-only,
        // shading selector offset by +4 (the shader's ghost marker).
        bool anyGhost = false;
        foreach (GpuMesh m in _meshes)
            if (m.Ghost && m.Visible) { anyGhost = true; break; }
        if (anyGhost)
        {
            Vector4 ghostColor = baseColor with { W = baseColor.W + 4f };
            ctx.OMSetBlendState(_blendGhost, (float*)null, 0xFFFFFFFF);
            ctx.OMSetDepthStencilState(_depthReadOnly, 0);
            _shader.Bind(ctx);
            drawn += DrawMeshPass(ctx, viewProj, frustum, lightDir, ghostColor, lighting, ghostPass: true);
            drawn += RenderInstanced(ctx, viewProj, frustum, lightDir, ghostColor, lighting, ghostPass: true);
            ctx.OMSetBlendState((ID3D11BlendState*)null, (float*)null, 0xFFFFFFFF);
            ctx.OMSetDepthStencilState(_depthState, 0);
        }

        DrawnMeshes = drawn;

        // Collision overlay (translucent fill + colored silhouette border): a depth-less overlay, so it never
        // z-fights the coincident visual geometry (no flicker) and is always visible (no camera-angle popping).
        // Renders only when a collision layer is loaded (the toggle loads/clears the data).
        _collisionRenderer.Render(target, viewProj, Camera.Position, frustum);

        // Debug overlay of loading zones (on top of meshes, semi-transparent boxes).
        if (ShowZones && _zoneBoxes != null) _zoneRenderer.Render(ctx, viewProj, _zoneBoxes);

        // .nov overlay, one toggle: the road graph (green lines) plus its AI-mesh boxes (amber wireframe).
        if (ShowNov)
        {
            _navRenderer.Render(ctx, viewProj, new Vector4(0.25f, 1f, 0.45f, 0.9f));
            _navMeshRenderer.Render(ctx, viewProj, new Vector4(1f, 0.6f, 0.1f, 0.85f));
        }

        // .nav overlay: AI path objects (cover / vault-over / action markers) as cyan boxes.
        if (ShowNavWorld) _navWorldRenderer.Render(ctx, viewProj, new Vector4(0.2f, 0.7f, 1f, 0.9f));

        // Actor glyphs: everything the .act pack places that has no geometry of its own, coloured per category.
        if (ShowActors) _actorRenderer.Render(ctx, viewProj);
        // The selected actor's glyph is drawn even with the overlay off, so a tree selection always shows up.
        _actorSelectionRenderer.Render(ctx, viewProj);

        // Selection silhouette outline (screen-space, on top of everything): an offscreen mask of the selected
        // mesh's exact geometry, then a dilation pass paints a constant-width contour — never a bounding box.
        if (_selectionMeshes.Count > 0 || _selectionPlacements.Count > 0)
            _selectionOutline.Render(target, viewProj, _selectionMeshes, _selectionPlacements);

        // Leave a solid raster bound for the next frame: the sky (drawn first, before we re-pick the
        // mode raster) inherits whatever was last set — in wireframe mode its fullscreen triangle would
        // otherwise degenerate into edges. Restoring here keeps the sky pass itself untouched.
        ctx.RSSetState(_raster);

        // Block until the GPU has finished this frame before the caller presents the shared surface —
        // otherwise WPF (D3D9Ex/D3DImage) can composite a half-written surface and the viewport flickers.
        _gpu.WaitForGpu();
    }

    // One pass over the regular (non-instanced) meshes matching the ghost filter.
    private int DrawMeshPass(ComPtr<ID3D11DeviceContext> ctx, Matrix4x4 viewProj, in Frustum frustum,
        Vector3 lightDir, Vector4 baseColor, in LightingConstants lighting, bool ghostPass)
    {
        uint stride = (uint)sizeof(MeshVertex);
        uint offset = 0;
        int drawn = 0;
        var srvs = stackalloc ID3D11ShaderResourceView*[3];            // reused per part: t0 diffuse, t1 normal, t2 spec
        ID3D11ShaderResourceView* last0 = null, last1 = null, last2 = null; // skip redundant SRV binds

        foreach (GpuMesh mesh in _meshes)
        {
            if (!mesh.Visible || mesh.Instanced) continue;             // instanced ones — separate pass
            if (mesh.Ghost != ghostPass) continue;
            if (!frustum.Intersects(mesh.BoundsMin, mesh.BoundsMax)) continue; // frustum culling
            drawn++;

            var consts = new FrameConstants
            {
                Wvp = mesh.World * viewProj,
                World = mesh.World,
                LightDir = new Vector4(lightDir, 0f),
                BaseColor = baseColor,
                Lighting = lighting,
            };
            _shader.UpdateConstants(ctx, ref consts);

            var vb = mesh.VertexBuffer.Handle;
            ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset);
            ctx.IASetIndexBuffer(mesh.IndexBuffer, Format.FormatR32Uint, 0);

            foreach (GpuPart part in mesh.Parts)
            {
                srvs[0] = part.Srv.Handle;         // t0 diffuse
                srvs[1] = part.NormalSrv.Handle;   // t1 normal
                srvs[2] = part.SpecSrv.Handle;     // t2 specular level
                if (srvs[0] != last0 || srvs[1] != last1 || srvs[2] != last2)
                {
                    ctx.PSSetShaderResources(0, 3, srvs);
                    last0 = srvs[0]; last1 = srvs[1]; last2 = srvs[2];
                }
                ctx.DrawIndexed(part.IndexCount, part.StartIndex, 0);
                DrawCalls++;
            }
        }
        return drawn;
    }

    // Second pass: instanced meshes. The cloud spans the whole map, so culling works per CELL: the
    // instance buffer is cell-major sorted (InstanceChunks) and only frustum-visible cell ranges are
    // drawn via StartInstanceLocation, with contiguous ranges merged into one DrawIndexedInstanced.
    private int RenderInstanced(ComPtr<ID3D11DeviceContext> ctx, Matrix4x4 viewProj, in Frustum frustum,
        Vector3 lightDir, Vector4 baseColor, in LightingConstants lighting, bool ghostPass)
    {
        bool any = false;
        foreach (GpuMesh m in _meshes)
            if (m.Instanced && m.Visible && m.Ghost == ghostPass) { any = true; break; }
        if (!any) return 0;

        // Raster state carries over from the mesh pass (solid/wireframe by mode) — instances match it.
        _instShader.Bind(ctx);
        var samp = _sampler.Handle;
        ctx.PSSetSamplers(0, 1, &samp);
        var consts = new InstancedConstants
        {
            ViewProj = viewProj,
            LightDir = new Vector4(lightDir, 0f),
            BaseColor = baseColor,
            Lighting = lighting,
        };
        _instShader.UpdateConstants(ctx, ref consts);

        uint stride = (uint)sizeof(MeshVertex);
        uint instStride = (uint)sizeof(Matrix4x4);
        uint offset = 0;
        int drawn = 0;
        float maxDist = InstanceDrawDistance;
        float maxDistSq = maxDist * maxDist;
        Vector3 eye = Camera.Position;
        var srvs = stackalloc ID3D11ShaderResourceView*[3];            // reused per part: t0 diffuse, t1 normal, t2 spec
        ID3D11ShaderResourceView* last0 = null, last1 = null, last2 = null; // skip redundant SRV binds

        foreach (GpuMesh mesh in _meshes)
        {
            if (!mesh.Instanced || !mesh.Visible || mesh.Ghost != ghostPass) continue;
            if (!frustum.Intersects(mesh.BoundsMin, mesh.BoundsMax)) continue; // whole-cloud early-out

            // Cull cells once per mesh (shared by all parts), merging contiguous survivors so a mostly
            // visible cloud still collapses into a handful of DrawIndexedInstanced calls.
            _visibleRanges.Clear();
            InstanceCell[]? cells = mesh.InstanceCells;
            if (cells == null)
            {
                _visibleRanges.Add((0u, (uint)mesh.InstanceCount)); // pre-chunking mesh — draw everything
            }
            else
            {
                foreach (InstanceCell cell in cells)
                {
                    if (!frustum.Intersects(cell.Min, cell.Max)) continue;
                    // The range the source data itself gives these copies — the crash table draws a bin at 20 m
                    // and a billboard at 300 m, and the viewport shows what the game would. A cell holds copies of
                    // ONE distance (InstanceChunks bins by it), so this stays a single per-cell test.
                    if (cell.DrawDistance > 0f
                        && DistanceSqToAabb(eye, cell.Min, cell.Max) > cell.DrawDistance * cell.DrawDistance)
                    {
                        continue;
                    }
                    if (maxDist > 0f && DistanceSqToAabb(eye, cell.Min, cell.Max) > maxDistSq) continue;
                    if (_visibleRanges.Count > 0
                        && _visibleRanges[^1].Start + _visibleRanges[^1].Count == cell.Start)
                    {
                        (uint start, uint count) = _visibleRanges[^1];
                        _visibleRanges[^1] = (start, count + cell.Count);
                    }
                    else
                    {
                        _visibleRanges.Add((cell.Start, cell.Count));
                    }
                }
            }
            if (_visibleRanges.Count == 0) continue;
            drawn++;
            foreach ((uint _, uint count) in _visibleRanges) DrawnInstances += count;

            var vb = mesh.VertexBuffer.Handle;
            ctx.IASetVertexBuffers(0, 1, &vb, &stride, &offset);
            var ib = mesh.InstanceBuffer.Handle;
            ctx.IASetVertexBuffers(1, 1, &ib, &instStride, &offset);
            ctx.IASetIndexBuffer(mesh.IndexBuffer, Format.FormatR32Uint, 0);

            foreach (GpuPart part in mesh.Parts)
            {
                srvs[0] = part.Srv.Handle;         // t0 diffuse
                srvs[1] = part.NormalSrv.Handle;   // t1 normal
                srvs[2] = part.SpecSrv.Handle;     // t2 specular level
                if (srvs[0] != last0 || srvs[1] != last1 || srvs[2] != last2)
                {
                    ctx.PSSetShaderResources(0, 3, srvs);
                    last0 = srvs[0]; last1 = srvs[1]; last2 = srvs[2];
                }
                foreach ((uint start, uint count) in _visibleRanges)
                {
                    ctx.DrawIndexedInstanced(part.IndexCount, count, part.StartIndex, 0, start);
                    DrawCalls++;
                }
            }
        }
        return drawn;
    }

    // Squared distance from a point to the closest point of an AABB (0 inside).
    private static float DistanceSqToAabb(Vector3 p, Vector3 min, Vector3 max)
    {
        Vector3 c = Vector3.Clamp(p, min, max);
        return (p - c).LengthSquared();
    }

    public void Dispose()
    {
        foreach (GpuMesh m in _meshes) m.Dispose();
        _meshes.Clear();
        Textures.Dispose();
        _sampler.Dispose();
        _blendGhost.Dispose();
        _depthReadOnly.Dispose();
        _depthState.Dispose();
        _rasterWire.Dispose();
        _raster.Dispose();
        _selectionOutline.Dispose();
        _actorSelectionRenderer.Dispose();
        _actorRenderer.Dispose();
        _navWorldRenderer.Dispose();
        _navMeshRenderer.Dispose();
        _navRenderer.Dispose();
        _collisionRenderer.Dispose();
        _zoneRenderer.Dispose();
        _sky.Dispose();
        _instShader.Dispose();
        _shader.Dispose();
    }
}
