namespace Illusion.Formats.Collisions;

/// <summary>
/// Rewrites a freshly cooked mesh's triangle indices to 32 bits.
/// <para>
/// The cooker picks the narrowest index width the vertex count allows — one byte up to 255 vertices, two up to
/// 65535 — but <b>every</b> triangle mesh Mafia II ships is 32-bit, so a hull cooked here would be the first
/// narrow-index collision the game has ever been handed. The old toolkit had the same problem and solved it the
/// same way, by clearing the width flags before writing the mesh back out.
/// </para>
/// <para>
/// Only two things change: the two width bits in the serial-flags dword, and the triangle index array itself.
/// The header counts, the material array, the face remap, the part arrays, the OPCODE model and PhysX's own
/// metadata tail are all copied through byte for byte — none of them encodes the index width.
/// </para>
/// </summary>
public static class CookedIndexWidener
{
    /// <summary>
    /// Returns <paramref name="cooked"/> with 32-bit triangle indices. A blob that already has them is returned
    /// as an unchanged copy, so this is safe to apply unconditionally.
    /// </summary>
    /// <exception cref="CollisionDecodeException">The blob is not an NXS mesh or is truncated.</exception>
    public static byte[] Widen(byte[] cooked)
    {
        ArgumentNullException.ThrowIfNull(cooked);
        return Native.Collisions.NativeCollision.Widen(cooked);
    }
}

/// <summary>
/// The metadata block PhysX writes after the OPCODE model — bounds, mass properties, and one edge-convexity
/// byte per triangle.
/// </summary>
public static class CookedMeshTail
{
    /// <summary>Fixed part: epsilon, bounding sphere, local AABB, mass, inertia, centre of mass, triangle count.</summary>
    public const int FixedBytes = 100;

    /// <summary>
    /// Whether a cooked mesh's tail is the layout this toolkit understands, and if not, why.
    /// <para>
    /// PhysX writes a mass of −1 and OMITS the inertia tensor and centre of mass when it cannot compute mass
    /// properties, which shortens the tail by 48 bytes. No shipped Mafia II mesh does that — the full layout
    /// holds on all 14 957 of them — so rather than support a shape nothing has ever exercised, a mesh that
    /// comes back this way is refused. It only happens for geometry enclosing no volume, which is a hull worth
    /// rejecting on its own merits.
    /// </para>
    /// </summary>
    public static bool IsSupported(byte[] cooked, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(cooked);
        return Native.Collisions.NativeCollision.TailSupported(cooked, out refusal);
    }
}
