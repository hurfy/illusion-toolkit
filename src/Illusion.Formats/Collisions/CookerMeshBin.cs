using System.Numerics;

namespace Illusion.Formats.Collisions;

/// <summary>
/// Serializes triangles into the raw model format the PhysX cooker reads.
/// <para>
/// The layout is the old toolkit's, which is what the shipped cooker expects:
/// <code>
/// u32 numVertices        float3 * numVertices
/// u32 numIndices         u32   * numIndices     (numIndices == triangles * 3)
/// u32 numMaterialIds     u16   * numMaterialIds (one raw PhysX surface id PER TRIANGLE)
/// </code>
/// </para>
/// <para>
/// Everything is validated up front. The cooker's way of rejecting bad geometry is to exit successfully and
/// leave a zero-byte file behind, so anything caught here is the difference between telling a modder which
/// material is wrong and telling them the cook failed for no stated reason.
/// </para>
/// </summary>
public static class CookerMeshBin
{
    /// <summary>
    /// A collision surface id below this is not a real surface. The <c>.col</c> section record stores the id
    /// biased by −2, so 0 or 1 would underflow that field to an enormous number.
    /// </summary>
    public const ushort MinimumSurfaceId = 2;

    /// <summary>
    /// Builds the cooker's input for one mesh, or returns null with a human-readable reason.
    /// </summary>
    /// <param name="positions">Vertex positions in hull-local space.</param>
    /// <param name="triangleIndices">Three vertex indices per triangle.</param>
    /// <param name="surfaceIds">One raw PhysX surface id per triangle.</param>
    /// <param name="refusal">Why the input was rejected, when the result is null.</param>
    public static byte[]? TryWrite(
        Vector3[] positions, int[] triangleIndices, ushort[] surfaceIds, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(triangleIndices);
        ArgumentNullException.ThrowIfNull(surfaceIds);
        return Native.Collisions.NativeCollision.TryWriteCookerBin(positions, triangleIndices, surfaceIds, out refusal);
    }
}
