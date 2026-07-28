using System.Numerics;
using System.Runtime.InteropServices;
using Illusion.Domain;
using Illusion.Rendering.Textures;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Gpu;

[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 UV;
    public Vector3 Tangent;   // local frame; transformed to world in the VS for normal mapping
    public Vector3 Binormal;  // decoder-supplied, handedness-signed
}

/// <summary>Range of indices with its material's texture maps (SRVs belong to TextureLibrary).</summary>
public struct GpuPart
{
    public uint StartIndex;
    public uint IndexCount;
    public ulong MaterialHash;                          // source game material (0 = none) — for live re-resolve
    public ComPtr<ID3D11ShaderResourceView> Srv;        // diffuse t0
    public ComPtr<ID3D11ShaderResourceView> NormalSrv;  // normal t1 (flat-normal when absent)
    public ComPtr<ID3D11ShaderResourceView> SpecSrv;    // specular-level t2 (white when absent → full level)
}

/// <summary>GPU buffers of a single mesh + its parts by material + world matrix.</summary>
public sealed unsafe class GpuMesh : IDisposable
{
    public ComPtr<ID3D11Buffer> VertexBuffer;
    public ComPtr<ID3D11Buffer> IndexBuffer;
    public Matrix4x4 World;
    public bool Visible = true;

    /// <summary>Bridge edit mode: true renders the mesh desaturated/translucent in a separate pass
    /// (it is NOT part of the set currently open in Blender). Managed by SceneRenderer.SetGhostFocus.</summary>
    public bool Ghost;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    public int TriangleCount;
    public readonly List<GpuPart> Parts = new();

    // Hardware instancing (city_crash): geometry is shared, copies live in a separate matrix buffer.
    // The buffer is cell-major sorted; InstanceCells lets the renderer frustum-cull whole cells and
    // draw only visible ranges (see InstanceChunks). Null only for a mesh built before chunking.
    public ComPtr<ID3D11Buffer> InstanceBuffer;
    public int InstanceCount = 1;
    public bool Instanced;
    public InstanceCell[]? InstanceCells;

    /// <summary>The scene-tree leaf that owns this mesh (set on upload) — lets a viewport pick resolve to a node.</summary>
    public object? Owner;

    // CPU geometry (local space) retained for ray-picking and world-AABB recompute after a transform edit.
    // These reference the source MeshData arrays (no extra copy). An instanced cloud keeps them too — not for the
    // ordinary picker (which skips instanced meshes, since one silhouette cannot stand for thousands of copies)
    // but so the crash-placement picker can test a ray against this prototype at one copy's matrix.
    public Vector3[]? PickPositions;
    public uint[]? PickIndices;

    /// <summary>Local-space AABB of the prototype geometry — the crash-placement picker's broad phase transforms
    /// its corners by a single copy's matrix.</summary>
    public Vector3 LocalMin => _localMin;

    /// <inheritdoc cref="LocalMin"/>
    public Vector3 LocalMax => _localMax;

    // Local-space AABB, cached once so RecomputeBounds transforms 8 corners (O(1)) instead of every vertex.
    private Vector3 _localMin;
    private Vector3 _localMax;

    // Texture acquisitions recorded at creation; returned to the library on dispose so an SRV whose last
    // user is gone can actually be released (otherwise VRAM grows for the whole session while streaming).
    private TextureLibrary? _textures;
    private readonly List<TextureLease> _textureLeases = new();
    private bool _disposed;

    public static GpuMesh Create(GpuContext gpu, MeshData mesh, TextureLibrary textures)
    {
        int n = mesh.VertexCount;
        var verts = new MeshVertex[n];
        Vector3[]? tangents = mesh.Tangents;
        Vector3[]? binormals = mesh.Binormals;
        for (int i = 0; i < n; i++)
        {
            verts[i].Position = mesh.Positions[i];
            verts[i].Normal = mesh.Normals[i];
            verts[i].UV = mesh.UVs != null ? mesh.UVs[i] : default;
            if (tangents != null && binormals != null)
            {
                verts[i].Tangent = tangents[i];
                verts[i].Binormal = binormals[i];
            }
            // No tangent channel: leave Tangent/Binormal zero (from new MeshVertex[n]). The shader's
            // zero-tangent guard then reconstructs the geometric normal and skips the normal-map sample.
        }

        bool instanced = mesh.Instances != null && mesh.Instances.Length > 0;

        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        var lmin = new Vector3(float.MaxValue);
        var lmax = new Vector3(float.MinValue);
        for (int i = 0; i < n; i++)
        {
            lmin = Vector3.Min(lmin, mesh.Positions[i]);
            lmax = Vector3.Max(lmax, mesh.Positions[i]);
        }

        Matrix4x4[]? sortedInstances = null;
        InstanceCell[]? cells = null;
        if (instanced)
        {
            // Partition the cloud into XY cells (cell-major sorted copy + per-cell AABB) so the renderer
            // can cull whole cells; the mesh AABB is the union of cell AABBs, which covers the prototype
            // geometry extents — not just the copy translations.
            (sortedInstances, cells) = InstanceChunks.Build(mesh.Instances!, lmin, lmax, mesh.InstanceDrawDistances);
            foreach (InstanceCell cell in cells)
            {
                bmin = Vector3.Min(bmin, cell.Min);
                bmax = Vector3.Max(bmax, cell.Max);
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                Vector3 wp = Vector3.Transform(mesh.Positions[i], mesh.World);
                bmin = Vector3.Min(bmin, wp);
                bmax = Vector3.Max(bmax, wp);
            }
        }

        var result = new GpuMesh
        {
            World = mesh.World,
            BoundsMin = bmin,
            BoundsMax = bmax,
            _localMin = lmin,
            _localMax = lmax,
            TriangleCount = mesh.Indices.Length / 3,
            Instanced = instanced,
            InstanceCount = instanced ? mesh.Instances!.Length : 1,
            InstanceCells = cells,
            PickPositions = mesh.Positions,
            PickIndices = mesh.Indices,
        };

        fixed (MeshVertex* pVerts = verts)
            result.VertexBuffer = GpuBuffers.CreateImmutable(gpu, pVerts, (uint)(n * sizeof(MeshVertex)), BindFlag.VertexBuffer);

        fixed (uint* pIdx = mesh.Indices)
            result.IndexBuffer = GpuBuffers.CreateImmutable(gpu, pIdx, (uint)(mesh.Indices.Length * sizeof(uint)), BindFlag.IndexBuffer);

        result._textures = textures;
        foreach (MeshPart part in mesh.Parts)
        {
            result.Parts.Add(new GpuPart
            {
                StartIndex = (uint)part.StartIndex,
                IndexCount = (uint)part.IndexCount,
                MaterialHash = part.MaterialHash,
                Srv = textures.Acquire(part.DiffuseTexture, result._textureLeases),
                NormalSrv = textures.AcquireNormalOrFlat(part.NormalTexture, result._textureLeases), // flat-normal when absent
                SpecSrv = textures.Acquire(part.SpecularTexture, result._textureLeases),             // white when absent → spec level ×1
            });
        }

        if (instanced)
        {
            // Upload the CELL-MAJOR sorted copy — InstanceCells' Start/Count index into this order.
            fixed (Matrix4x4* pInst = sortedInstances)
                result.InstanceBuffer = GpuBuffers.CreateImmutable(gpu, pInst, (uint)(sortedInstances!.Length * sizeof(Matrix4x4)), BindFlag.VertexBuffer);
        }

        return result;
    }

    /// <summary>
    /// Re-resolves the textures of every part bound to <paramref name="materialHash"/> (a material edit changed
    /// its slots). New SRVs come from the same library the mesh acquired from; the superseded leases stay recorded
    /// until the mesh is disposed — a few stale leases per edit is bounded, and releasing them early would need
    /// per-part lease bookkeeping. Returns the number of parts rebound.
    /// </summary>
    public int RebindPartTextures(ulong materialHash, string? diffuse, string? normal, string? specular)
    {
        if (_disposed || _textures == null || materialHash == 0) return 0;
        int rebound = 0;
        for (int i = 0; i < Parts.Count; i++)
        {
            if (Parts[i].MaterialHash != materialHash) continue;
            GpuPart p = Parts[i];
            p.Srv = _textures.Acquire(diffuse, _textureLeases);
            p.NormalSrv = _textures.AcquireNormalOrFlat(normal, _textureLeases);
            p.SpecSrv = _textures.Acquire(specular, _textureLeases);
            Parts[i] = p;
            rebound++;
        }
        return rebound;
    }

    /// <summary>Repoints one part at a different material (a mesh-slot reassignment): swaps the recorded hash
    /// and re-resolves the three texture SRVs. Same lease policy as <see cref="RebindPartTextures"/>.</summary>
    public bool SetPartMaterial(int index, ulong materialHash, string? diffuse, string? normal, string? specular)
    {
        if (_disposed || _textures == null || index < 0 || index >= Parts.Count) return false;
        GpuPart p = Parts[index];
        p.MaterialHash = materialHash;
        p.Srv = _textures.Acquire(diffuse, _textureLeases);
        p.NormalSrv = _textures.AcquireNormalOrFlat(normal, _textureLeases);
        p.SpecSrv = _textures.Acquire(specular, _textureLeases);
        Parts[index] = p;
        return true;
    }

    /// <summary>Sets a new world matrix and refreshes the world-space AABB (used by frustum culling + picking).</summary>
    public void SetWorld(Matrix4x4 world)
    {
        World = world;
        RecomputeBounds();
    }

    /// <summary>
    /// Recomputes the world-space AABB by transforming the 8 cached local-AABB corners (O(1), conservative under
    /// rotation — always a superset, which is safe for culling and picking broad-phase). No-op for instanced meshes.
    /// </summary>
    public void RecomputeBounds()
    {
        // An instanced cloud's bounds are the union of its cell AABBs, not this mesh's World — see SetInstances.
        if (Instanced || PickPositions == null || _localMin.X > _localMax.X) return;
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? _localMin.X : _localMax.X,
                (i & 2) == 0 ? _localMin.Y : _localMax.Y,
                (i & 4) == 0 ? _localMin.Z : _localMax.Z);
            Vector3 wp = Vector3.Transform(corner, World);
            bmin = Vector3.Min(bmin, wp);
            bmax = Vector3.Max(bmax, wp);
        }
        BoundsMin = bmin;
        BoundsMax = bmax;
    }

    /// <summary>
    /// Replaces the copies of an instanced cloud (a crash placement was moved, added or removed). Only the matrix
    /// buffer is rebuilt — the geometry, its parts and every texture lease stay exactly as they are, which is what
    /// makes this cheap enough to run on every frame of a gizmo drag. The buffer is immutable by design (it is
    /// written once and read every frame), so "update" means release and re-upload; the cell partition and the
    /// world AABB are rebuilt with it. An empty cloud leaves the mesh with nothing to draw.
    /// </summary>
    public void SetInstances(GpuContext gpu, Matrix4x4[] instances, float[]? drawDistances = null)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (_disposed) return;

        if (Instanced) InstanceBuffer.Dispose();
        Instanced = instances.Length > 0;
        InstanceCount = instances.Length;

        if (!Instanced)
        {
            InstanceBuffer = default;
            InstanceCells = null;
            BoundsMin = new Vector3(float.MaxValue);
            BoundsMax = new Vector3(float.MinValue);
            return;
        }

        (Matrix4x4[] sorted, InstanceCell[] cells) =
            InstanceChunks.Build(instances, _localMin, _localMax, drawDistances);
        InstanceCells = cells;

        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        foreach (InstanceCell cell in cells)
        {
            bmin = Vector3.Min(bmin, cell.Min);
            bmax = Vector3.Max(bmax, cell.Max);
        }
        BoundsMin = bmin;
        BoundsMax = bmax;

        fixed (Matrix4x4* pInst = sorted)
            InstanceBuffer = GpuBuffers.CreateImmutable(
                gpu, pInst, (uint)(sorted.Length * sizeof(Matrix4x4)), BindFlag.VertexBuffer);
    }

    public void Dispose()
    {
        if (_disposed) return; // several owners may dispose (render list, delete edit, stale load) — once only
        _disposed = true;
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
        if (Instanced) InstanceBuffer.Dispose();
        // SRVs belong to TextureLibrary — returning the leases lets it evict entries with no users left.
        _textures?.Release(_textureLeases);
    }
}
