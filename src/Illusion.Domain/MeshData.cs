using System.Numerics;

namespace Illusion.Domain;

/// <summary>
/// Render-neutral geometry of a single mesh: decoded MafiaToolkit vertices,
/// ready to upload into Silk.NET buffers, split into parts by material.
/// </summary>
public sealed class MeshData
{
    public string Name { get; init; } = null!;
    public Matrix4x4 World { get; init; }
    public Vector3[] Positions { get; init; } = null!;
    public Vector3[] Normals { get; init; } = null!;
    public Vector2[]? UVs { get; init; }
    /// <summary>Per-vertex tangent/binormal (world of the local frame) for normal mapping; null when the
    /// source mesh has no tangent channel — then the shader falls back to the vertex normal.</summary>
    public Vector3[]? Tangents { get; init; }
    public Vector3[]? Binormals { get; init; }
    public uint[] Indices { get; init; } = null!;
    public MeshPart[] Parts { get; init; } = null!;

    /// <summary>
    /// World matrices of copies for hardware instancing (city_crash / Translokator). null for a regular
    /// mesh — then the single <see cref="World"/> is used. Vertex positions here are in the prototype's
    /// LOCAL space: each matrix is already = refTransform·instanceTRS.
    /// </summary>
    public Matrix4x4[]? Instances { get; init; }

    public int VertexCount => Positions.Length;
    public int TriangleCount => Indices.Length / 3;
}
