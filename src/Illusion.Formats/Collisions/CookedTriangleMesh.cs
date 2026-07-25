using System.Numerics;

namespace Illusion.Formats.Collisions;

/// <summary>
/// The decoded triangle geometry and per-triangle surface materials of a PhysX 2.8 cooked collision mesh
/// (<see cref="CollisionMesh.CookedMesh"/>).
/// </summary>
/// <remarks>
/// The cooked blob is a Novodex "NXS" stream: an <c>"NXS\x01" + "MESH"</c> chunk holding the vertex array, the
/// triangle index array and the optional per-triangle material / face-remap arrays, followed by an OPCODE
/// broadphase tree (<c>"OPC"</c> + <c>"HBM"</c>) used only for runtime queries. This type decodes the renderable
/// geometry; the OPCODE tail is left untouched and can be re-parsed by <see cref="ValidateOpcodeTail"/> as an
/// integrity oracle.
/// <para/>
/// The layout follows the PhysX <c>NxTriangleMesh</c> serial format:
/// <code>
/// +0   "NXS\x01"                        +24  heightFieldVerticalExtent (0)
/// +4   "MESH"                           +28  numVertices
/// +8   version (== 1)                   +32  numTriangles
/// +12  MeshSerialFlags                  +36  vertices  : numVertices * float3
/// +16  convexEdgeThreshold (0.001f)          triangles : numTriangles * 3 * indexWidth
/// +20  heightFieldVerticalAxis (255)         materials : numTriangles * u16   [iff Materials]
///                                            faceRemap : maxIndex dword + numTriangles packed indices
///                                                                                     [iff FaceRemap]
///                                            numConvexParts, numFlatParts dwords, their per-triangle arrays,
///                                            modelSize dword, then the OPCODE model.
/// </code>
/// Vertices are returned exactly as stored (Mafia axes) — any coordinate convention is the renderer's concern.
/// <para/>
/// The per-triangle material array is the <b>only</b> correct source of a triangle's surface material. The
/// <c>.col</c>-level <see cref="CollisionSection"/> ranges describe the <i>authored</i> triangle order, but cooking
/// reorders triangles (that is what the cooked mesh's face-remap flag records), so section ranges and cooked
/// triangles do not line up: measured across <c>city/eastside</c>, 32 104 of 68 937 triangles (46.6 %) resolve to a
/// different material through the sections than through this array, and the mismatch occurs in exactly those
/// meshes that carry the face-remap flag. Material values here are raw PhysX slot ids; the <c>.col</c> section
/// stores the same value biased by −2.
/// </remarks>
public sealed class CookedTriangleMesh
{
    /// <summary>Byte offset of the vertex array within the cooked blob: <c>"NXS\x01" + "MESH"</c> (8 bytes) +
    /// version + flags + epsilon + two height-field constants + vertexCount + triangleCount (7 dwords).</summary>
    internal const int VertexArrayOffset = 8 + 7 * 4;

    /// <summary>Vertex positions in the mesh's local space, as stored (Mafia axes).</summary>
    public Vector3[] Vertices { get; }

    /// <summary>Triangle vertex indices, three per triangle; every value is a valid index into <see cref="Vertices"/>.</summary>
    public int[] Triangles { get; }

    /// <summary>
    /// Raw PhysX surface-material id per triangle, in cooked triangle order — the authoritative material source
    /// (see the type remarks). Empty when the mesh carries no material array, which no stock Mafia II mesh does.
    /// Subtract 2 to get the <c>MaterialsPhysics.tbl</c> index.
    /// </summary>
    public ushort[] TriangleMaterials { get; }

    /// <summary>Number of triangles (<see cref="Triangles"/>.Length / 3).</summary>
    public int TriangleCount => Triangles.Length / 3;

    private CookedTriangleMesh(Vector3[] vertices, int[] triangles, ushort[] triangleMaterials)
    {
        Vertices = vertices;
        Triangles = triangles;
        TriangleMaterials = triangleMaterials;
    }

    /// <summary>Wraps geometry the native core decoded (see <c>NativeCollision.Decode</c>).</summary>
    internal static CookedTriangleMesh FromDecoded(
        Vector3[] vertices, int[] triangles, ushort[] triangleMaterials) =>
        new(vertices, triangles, triangleMaterials);

    /// <summary>Decodes the geometry and per-triangle materials from a cooked collision-mesh blob
    /// (the byte-level work runs in the native core).</summary>
    /// <exception cref="CollisionDecodeException">The blob is not an NXS "MESH" chunk, is truncated, or holds an
    /// out-of-range triangle index.</exception>
    public static CookedTriangleMesh Decode(byte[] cooked)
    {
        ArgumentNullException.ThrowIfNull(cooked);
        return Native.Collisions.NativeCollision.Decode(cooked);
    }

    /// <summary>
    /// Integrity oracle: walks the whole NXS chunk to the byte where the OPCODE model must begin, checks the
    /// <c>"OPC"</c> magic is exactly there, and re-parses the model through the native core. Returns the number of trailing bytes left after the model — expected to be nonzero, since PhysX
    /// stores mesh metadata (local bounds, an epsilon, mass and edge data) after the OPCODE model.
    /// <para/>
    /// Unlike a search for the magic, this proves every optional array's size was interpreted correctly: a wrong
    /// index width, a missed material array or a misread face-remap table all land the cursor somewhere other than
    /// the magic and fail immediately.
    /// </summary>
    /// <exception cref="CollisionDecodeException">The blob is malformed or the OPCODE model is missing.</exception>
    public static int ValidateOpcodeTail(byte[] cooked)
    {
        ArgumentNullException.ThrowIfNull(cooked);
        return Native.Collisions.NativeCollision.ValidateOpcodeTail(cooked);
    }

    /// <summary>
    /// Byte offset of the OPCODE model within the cooked blob (the native core walks every array
    /// the serial flags declare and validates the <c>"OPC"</c> magic sits exactly there).
    /// </summary>
    /// <exception cref="CollisionDecodeException">The blob is truncated or the magic is not where the layout says.</exception>
    /// <param name="cooked">The cooked NXS blob to walk.</param>
    /// <param name="modelSize">The size dword stored immediately before the model — <c>offset + modelSize</c> is
    /// where PhysX's own mesh metadata (bounds, epsilon, mass properties, edge flags) begins.</param>
    internal static int OpcodeModelOffset(byte[] cooked, out uint modelSize)
    {
        Native.Model.ColMeshLayoutW layout = Native.Collisions.NativeCollision.MeshLayout(cooked);
        modelSize = layout.ModelSize;
        return (int)layout.OpcOffset;
    }

    /// <summary>Vertex and triangle counts from the header — the sizes a byte-level patcher needs without
    /// decoding the geometry.</summary>
    internal static (uint VertexCount, uint TriangleCount) ReadCounts(byte[] cooked)
    {
        Native.Model.ColMeshLayoutW layout = Native.Collisions.NativeCollision.MeshLayout(cooked);
        return (layout.VertexCount, layout.TriangleCount);
    }
}
