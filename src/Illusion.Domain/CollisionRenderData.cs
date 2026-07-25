using System.Numerics;

namespace Illusion.Domain;

/// <summary>
/// Render-neutral collision geometry for a district: the unique collision meshes it references, each decoded to
/// triangles and carrying the world matrices of every placement (instance) that uses it. Built in the asset
/// layer from the streamed <c>Collisions</c> resource and handed to the viewport's collision pass to upload.
/// </summary>
public sealed class CollisionRenderData
{
    public CollisionRenderMesh[] Meshes { get; init; } = null!;
}

/// <summary>
/// One unique collision mesh (shared by many placements): local-space vertices, a flat triangle index buffer,
/// per-surface-material colored parts (contiguous index ranges), and the world matrices it is instanced at.
/// </summary>
public sealed class CollisionRenderMesh
{
    /// <summary>FNV64 of the collision mesh (so an instance-only rebuild can re-group placements by hash without
    /// re-decoding the cooked geometry — see <c>CollisionSceneBuilder.RebuildInstances</c>).</summary>
    public ulong Hash { get; init; }

    public Vector3[] Positions { get; init; } = null!;

    /// <summary>Triangle indices, grouped so that every <see cref="CollisionRenderPart"/> is one contiguous run of
    /// a single surface material (see <c>CollisionSceneBuilder</c>) — a permutation of the cooked triangle order,
    /// not the cooked order itself.</summary>
    public uint[] Indices { get; init; } = null!;

    /// <summary>Cooked-mesh triangle index for each render triangle, i.e. the inverse of the grouping permutation
    /// applied to <see cref="Indices"/>. Lets an edit made on a rendered triangle be written back to the right
    /// entry of the cooked mesh's material array.</summary>
    public int[] SourceTriangle { get; init; } = null!;

    /// <summary>Contiguous, non-overlapping index ranges covering <see cref="Indices"/> exactly, one per material.</summary>
    public CollisionRenderPart[] Parts { get; init; } = null!;

    /// <summary>World matrix per placement — vertices are in local space, so each is the instance's transform.</summary>
    public Matrix4x4[] Instances { get; init; } = null!;

    /// <summary>Local-space bounds of <see cref="Positions"/> (for conservative instanced cell culling).</summary>
    public Vector3 LocalMin { get; init; }
    public Vector3 LocalMax { get; init; }

    public int TriangleCount => Indices.Length / 3;
}

/// <summary>A contiguous index range of one surface material within a collision mesh, plus its display color
/// (RGB; the pass applies its own fill/wireframe alpha).</summary>
public readonly struct CollisionRenderPart
{
    public CollisionRenderPart(int startIndex, int indexCount, int rawMaterialId, Vector3 color)
    {
        StartIndex = startIndex;
        IndexCount = indexCount;
        RawMaterialId = rawMaterialId;
        Color = color;
    }

    public int StartIndex { get; }
    public int IndexCount { get; }

    /// <summary>Raw PhysX slot id as stored per triangle in the cooked mesh; resolve names and colours through
    /// <see cref="CollisionMaterialCatalog.ForRawId"/>.</summary>
    public int RawMaterialId { get; }

    public Vector3 Color { get; }
}
