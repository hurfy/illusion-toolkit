using System.Numerics;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.ItemDesc;

namespace Illusion.Assets.Collisions;

/// <summary>A collision stub paired with the shape it names and the file that shape lives in.</summary>
/// <param name="Stub">The frame that says WHERE.</param>
/// <param name="Shape">The ItemDesc record that says WHAT.</param>
/// <param name="File">Full path of its .ids inside the extracted folder.</param>
public sealed record ResolvedCollisionShape(FrameObjectCollision Stub, ItemDescFile Shape, string File);

/// <summary>
/// Finds the physics shape a collision stub names, inside its own archive.
///
/// <para>
/// A district keeps that geometry in a <c>.col</c>; a car ships none at all and answers its stubs out of its
/// own ItemDesc entries instead, matched on the record's FILE hash — 1174 of 1174 across the shipped cars
/// (<c>--probe-car-collision</c>). This is the lookup both the overlay and the shape editor need, so it lives
/// in one place rather than being re-derived by each.
/// </para>
/// </summary>
public static class CarCollisionShapes
{
    /// <summary>Every shape the archive carries, by the hash a stub names it with.</summary>
    public static Dictionary<ulong, ResolvedCollisionShape> Load(
        string extractedFolder, IEnumerable<FrameObjectCollision> stubs)
    {
        ArgumentNullException.ThrowIfNull(stubs);
        var found = new Dictionary<ulong, ResolvedCollisionShape>();

        var byHash = new Dictionary<ulong, (ItemDescFile Shape, string File)>();
        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extractedFolder).GetFiles("ItemDesc"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return found; }

        foreach (string file in files)
        {
            try
            {
                ItemDescFile shape = ItemDescFile.Load(file);
                byHash.TryAdd(shape.Hash, (shape, file));
            }
            catch (Exception) { /* a shape this library cannot read simply has none to show */ }
        }

        foreach (FrameObjectCollision stub in stubs)
        {
            if (byHash.TryGetValue(stub.Hash, out (ItemDescFile Shape, string File) hit))
            {
                found[stub.Hash] = new ResolvedCollisionShape(stub, hit.Shape, hit.File);
            }
        }
        return found;
    }

    /// <summary>
    /// The wireframe of one shape, in world space, as line-segment pairs. Only the primitives are drawn as
    /// what they are; a cooked hull has no vertices this library can read, so it is shown as the box that
    /// bounds it — an honest "something is here, about this big" rather than nothing at all.
    /// </summary>
    public static void AppendWireframe(
        List<Vector3> lines, ResolvedCollisionShape resolved, Matrix4x4 world)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(resolved);
        if (resolved.Shape.Element is not RigidBodyElement rigid) return;

        // A cooked hull is the common case on a car (599 of the 1174 shipped shapes), so drawing it as a
        // token marker would put a 30 cm cube where a whole door is — which is exactly what it looked like.
        if (rigid.Shape is RigidBodyShape.ConvexPolyhedron or RigidBodyShape.TriangleMesh)
        {
            if (TryReadCookedBounds(rigid.CookedMesh, out Vector3 min, out Vector3 max))
            {
                AppendBox(lines, (max - min) * 0.5f,
                    Matrix4x4.CreateTranslation((min + max) * 0.5f) * world);
            }
            return;
        }

        Vector3 half = rigid.Shape switch
        {
            RigidBodyShape.Box => rigid.BoxDimensions,
            RigidBodyShape.Sphere => new Vector3(rigid.Radius),
            RigidBodyShape.Capsule or RigidBodyShape.Cylinder =>
                new Vector3(rigid.Radius, (rigid.Height * 0.5f) + rigid.Radius, rigid.Radius),
            _ => new Vector3(0.15f),
        };
        AppendBox(lines, half, world);
    }

    /// <summary>
    /// The local bounding box a PhysX-cooked hull carries in its own tail: six floats, min then max, sitting
    /// 76 bytes from the end of the blob. Nothing here decodes the hull — only the box it lives in, which is
    /// all an overlay needs to say "this door is protected THIS far".
    /// <para>
    /// Cross-checked rather than trusted (<c>--probe-car-collision</c>): the blob must open with the cooker's
    /// own <c>"NXS\x01CVXM"</c> signature, and the box that comes out of the tail must contain the hull's
    /// actual vertices, which start 64 bytes in. Both hold on every cooked shape the cars ship.
    /// </para>
    /// </summary>
    public static bool TryReadCookedBounds(byte[]? cooked, out Vector3 min, out Vector3 max)
    {
        min = default;
        max = default;
        if (cooked == null || !HasConvexSignature(cooked)) return false;

        int vertices = FindCookedVertices(cooked);
        if (vertices < 0) return false;
        uint count = ReadVertexCount(cooked, vertices);
        if (count == 0 || vertices + ((long)count * 12) > cooked.Length) return false;

        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (uint i = 0; i < count; i++)
        {
            Vector3 v = ReadVector(cooked, vertices + ((int)i * 12));
            if (!float.IsFinite(v.X + v.Y + v.Z)) return false;
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        return true;
    }

    /// <summary>
    /// How many vertices the hull has: the SECOND of the seven counters after the chunk tag. Which one it is
    /// was measured, not guessed — taking slot 1 and walking that many vertices reproduces the bounding box
    /// the blob keeps in its own tail on 599 of 599 shipped hulls, while no other slot comes close
    /// (<c>--probe-car-collision</c>).
    /// </summary>
    public static uint ReadVertexCount(byte[] cooked, int verticesAt)
    {
        int at = verticesAt - (7 * sizeof(uint)) + sizeof(uint);
        return at >= 0 && at + 4 <= cooked.Length ? BitConverter.ToUInt32(cooked, at) : 0;
    }

    /// <summary>The bounding box the blob carries in its own tail — the independent reading the vertex walk
    /// is checked against.</summary>
    public static bool TryReadTailBounds(byte[]? cooked, out Vector3 min, out Vector3 max)
    {
        min = default;
        max = default;
        if (cooked is not { Length: >= BoundsFromEnd } || !HasConvexSignature(cooked)) return false;
        int at = cooked.Length - BoundsFromEnd;
        min = ReadVector(cooked, at);
        max = ReadVector(cooked, at + 12);
        return min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;
    }

    /// <summary>
    /// Where the hull's own vertex array starts, found by the chunk tag rather than by a fixed offset — the
    /// blob's preamble is not the same length in every shape. The tag is <c>"ICE\x01CVHL"</c>, followed by
    /// seven counters and then the vertices. -1 when the tag is not there.
    /// </summary>
    public static int FindCookedVertices(byte[]? cooked)
    {
        if (cooked == null) return -1;
        ReadOnlySpan<byte> tag = [(byte)'I', (byte)'C', (byte)'E', 1, (byte)'C', (byte)'V', (byte)'H', (byte)'L'];
        for (int at = 0; at + tag.Length <= cooked.Length; at++)
        {
            if (cooked.AsSpan(at, tag.Length).SequenceEqual(tag))
            {
                int start = at + tag.Length + (7 * sizeof(uint));
                return start <= cooked.Length ? start : -1;
            }
        }
        return -1;
    }

    /// <summary>Distance from the END of a cooked blob to its local bounding box (min, then max).</summary>
    private const int BoundsFromEnd = 76;

    /// <summary>Whether a blob opens with the cooker's convex-mesh signature, <c>"NXS\x01" + "CVXM"</c>.</summary>
    public static bool HasConvexSignature(byte[]? cooked) =>
        cooked is { Length: >= 8 }
        && cooked[0] == (byte)'N' && cooked[1] == (byte)'X' && cooked[2] == (byte)'S' && cooked[3] == 1
        && cooked[4] == (byte)'C' && cooked[5] == (byte)'V' && cooked[6] == (byte)'X' && cooked[7] == (byte)'M';

    /// <summary>Reads one vector out of a blob.</summary>
    public static Vector3 ReadVector(byte[] bytes, int at) => new(
        BitConverter.ToSingle(bytes, at),
        BitConverter.ToSingle(bytes, at + 4),
        BitConverter.ToSingle(bytes, at + 8));

    /// <summary>The twelve edges of a box, transformed into world space.</summary>
    public static void AppendBox(List<Vector3> lines, Vector3 half, Matrix4x4 world)
    {
        ArgumentNullException.ThrowIfNull(lines);
        Span<Vector3> corner = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var local = new Vector3(
                (i & 1) == 0 ? -half.X : half.X,
                (i & 2) == 0 ? -half.Y : half.Y,
                (i & 4) == 0 ? -half.Z : half.Z);
            corner[i] = Vector3.Transform(local, world);
        }

        // 0..7 index the corners as an (x, y, z) bit pattern, so an edge is a pair differing in one bit.
        ReadOnlySpan<int> edges =
        [
            0, 1, 2, 3, 4, 5, 6, 7,   // along X
            0, 2, 1, 3, 4, 6, 5, 7,   // along Y
            0, 4, 1, 5, 2, 6, 3, 7,   // along Z
        ];
        for (int i = 0; i < edges.Length; i += 2)
        {
            lines.Add(corner[edges[i]]);
            lines.Add(corner[edges[i + 1]]);
        }
    }
}
