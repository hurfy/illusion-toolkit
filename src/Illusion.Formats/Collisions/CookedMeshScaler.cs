using System.Numerics;

namespace Illusion.Formats.Collisions;

/// <summary>
/// Rescales a PhysX-cooked collision mesh by rewriting numbers in place, leaving the cooked structure untouched.
/// </summary>
/// <remarks>
/// <para>
/// The OPCODE broadphase in a Mafia II <c>.col</c> is an <c>AABBQuantizedNoLeafTree</c>: each node stores its box
/// as quantized integers, and the real box is recovered as <c>center = centerShort * centerCoeff</c> /
/// <c>extent = extentsShort * extentsCoeff</c>, with the two coefficient triples stored once at the end of the
/// tree. Multiplying every vertex and both coefficient triples by the same <c>s</c> therefore moves the geometry
/// and every node box together, by an exact algebraic identity: <b>every quantized integer stays bit-identical</b>.
/// No requantization, no re-partitioning, no change in topology, triangle order, material array or face remap.
/// </para>
/// <para>
/// That is the whole reason scaling does not need a re-cook. The tree that ships in the game — one PhysX itself
/// produced and the engine already accepts — survives the edit; only the scale it is expressed in changes. A
/// rebuilt tree would be a structure the game has never seen, verifiable only by playing it.
/// </para>
/// <para>
/// Per-axis scaling works the same way and is just as exact, because the coefficients are per-axis triples: scaling
/// x alone multiplies <c>centerCoeff.x</c> and <c>extentsCoeff.x</c> and leaves every quantized integer alone. Only
/// two things in the tail stop being a multiplication — the bounding sphere is no longer a sphere and has to be
/// refitted, and the inertia tensor needs a real transform rather than a scalar. Both are handled below.
/// </para>
/// <para>
/// A NEGATIVE factor is still refused: it mirrors the hull, which flips triangle winding and means rewriting index
/// bytes. That is a separate piece of work, and getting it subtly wrong is a hull whose faces face inwards, with
/// nothing but play-testing to catch it.
/// </para>
/// </remarks>
public static class CookedMeshScaler
{
    /// <summary>
    /// Returns a copy of <paramref name="cooked"/> scaled uniformly by <paramref name="scale"/> about the mesh's
    /// local origin. The output has exactly the same length and differs only in the vertex floats, the tree's six
    /// quantization coefficients and the tail's mass/bounds floats.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="scale"/> is not a positive, finite number.</exception>
    /// <exception cref="NotSupportedException">The blob's tail does not match the layout this patcher understands,
    /// so its bounds and mass properties cannot be rescaled — better refused than left describing the old size.</exception>
    /// <exception cref="CollisionDecodeException">The blob is malformed.</exception>
    public static byte[] Scale(byte[] cooked, float scale) => Scale(cooked, new Vector3(scale));

    /// <summary>
    /// Returns a copy of <paramref name="cooked"/> scaled per axis about the mesh's local origin. The output has
    /// exactly the same length and differs only in the vertex floats, the tree's six quantization coefficients and
    /// the tail's bounds and mass properties.
    /// </summary>
    /// <exception cref="ArgumentException">A component of <paramref name="scale"/> is not positive and finite. A
    /// negative factor would mirror the hull, which flips triangle winding — not supported here.</exception>
    /// <exception cref="NotSupportedException">The blob's tail does not match the layout this patcher understands,
    /// so its bounds and mass properties cannot be rescaled — better refused than left describing the old size.</exception>
    /// <exception cref="CollisionDecodeException">The blob is malformed.</exception>
    public static byte[] Scale(byte[] cooked, Vector3 scale)
    {
        ArgumentNullException.ThrowIfNull(cooked);
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(scale.Z)
            || scale.X <= 0f || scale.Y <= 0f || scale.Z <= 0f)
        {
            throw new ArgumentException($"every scale axis must be positive and finite, got {scale}", nameof(scale));
        }
        // The byte-level patch runs in the native core.
        return Native.Collisions.NativeCollision.Scale(cooked, scale);
    }
}
