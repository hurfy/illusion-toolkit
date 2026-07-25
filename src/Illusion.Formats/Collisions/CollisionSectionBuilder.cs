namespace Illusion.Formats.Collisions;

/// <summary>Triangles grouped by surface, ready for the cooker, plus the <c>.col</c> sections describing them.</summary>
/// <param name="TriangleIndices">Vertex indices, three per triangle, reordered so one surface runs contiguously.</param>
/// <param name="SurfaceIds">Raw PhysX surface id per triangle, in the same order.</param>
/// <param name="Sections">Section records for <see cref="CollisionMesh.Sections"/>, one per surface.</param>
public readonly record struct CollisionSectionPlan(
    int[] TriangleIndices, ushort[] SurfaceIds, IReadOnlyList<CollisionSection> Sections);

/// <summary>
/// Groups a hull's triangles by surface and writes the <c>.col</c> section records for them.
/// <para>
/// This reproduces the old toolkit's recipe deliberately, because it is the only one the game is known to
/// accept: sort triangles by surface id, then emit one section per surface as a range over that <b>pre-cook</b>
/// order. The cooker reorders triangles afterwards and records the permutation in its own face-remap table, so
/// the sections end up describing an order the cooked mesh no longer has — which sounds wrong until you notice
/// that shipped data is already like that, that the toolkit reads surfaces from the cooked per-triangle array
/// rather than from sections, and that years of mods were built exactly this way.
/// </para>
/// </summary>
public static class CollisionSectionBuilder
{
    /// <summary>
    /// The <c>.col</c> section record stores a surface id biased by −2. Ids below that are not surfaces, and
    /// subtracting would wrap the unsigned field to an enormous number.
    /// </summary>
    public const ushort SectionMaterialBias = 2;

    /// <summary>
    /// Plans one hull, or returns null with a reason. Triangle count and surface count must agree — one surface
    /// per triangle is what the cooker's input format means.
    /// </summary>
    public static CollisionSectionPlan? TryBuild(int[] triangleIndices, ushort[] surfaceIds, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(triangleIndices);
        ArgumentNullException.ThrowIfNull(surfaceIds);
        return Native.Collisions.NativeCollision.TryBuildSections(triangleIndices, surfaceIds, out refusal);
    }
}
