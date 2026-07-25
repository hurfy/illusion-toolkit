using System.Numerics;

namespace Illusion.Bridge.Geometry;

/// <summary>A mesh re-expressed in Blender's native model: welded (position-unique) vertices plus
/// per-loop (face-corner) attributes. <see cref="LoopOrigIndex"/> remembers which source
/// split-vertex each corner came from — the identity the push-back byte-diff keys on.</summary>
public sealed class WeldedMesh
{
    public Vector3[] Positions { get; init; } = Array.Empty<Vector3>();

    /// <summary>Per source split-vertex: its welded vertex index.</summary>
    public int[] SplitToWelded { get; init; } = Array.Empty<int>();

    public uint[] LoopVertexIndices { get; init; } = Array.Empty<uint>();
    public Vector3[] LoopNormals { get; init; } = Array.Empty<Vector3>();
    public Vector2[] LoopUvs { get; init; } = Array.Empty<Vector2>();
    public int[] LoopOrigIndex { get; init; } = Array.Empty<int>();

    /// <summary>Original triangle index of each kept (non-degenerate) triangle, in order — the map
    /// from payload faces back to the source index buffer.</summary>
    public int[] KeptTriangles { get; init; } = Array.Empty<int>();

    /// <summary>Source triangles dropped because welding collapsed two of their corners into one
    /// vertex — Blender rejects such polygons (its validate() would strip them AND desync every
    /// per-corner array). They are zero-area in the game data; the count-preserving push path never
    /// misses them because it keeps the original index buffer.</summary>
    public int DroppedDegenerateTriangles { get; init; }

    /// <summary>Source triangles dropped because another kept triangle already covers the same
    /// welded vertex set (typically the back face of double-sided game geometry) — Blender's
    /// validate() strips duplicate polygons. Vertex edits still reach both sides on push, since both
    /// faces index the same welded vertices.</summary>
    public int DroppedDuplicateTriangles { get; init; }
}

/// <summary>
/// Builds the weld map from split-vertex game buffers. The weld key is supplied by the caller (the
/// asset layer derives it from the RAW quantized position bytes), so welding is bit-exact and
/// deterministic — no float epsilons, and vertices that merely look coincident after decoding never
/// merge unless their source data agrees. Welded order is first-appearance order.
/// </summary>
public static class WeldMapBuilder
{
    public static WeldedMesh Build(
        ulong[] weldKeys, Vector3[] positions, Vector3[] normals, Vector2[]? uvs, uint[] indices)
    {
        if (weldKeys.Length != positions.Length)
            throw new ArgumentException("One weld key per split vertex is required.", nameof(weldKeys));

        var keyToWelded = new Dictionary<ulong, int>(positions.Length);
        var splitToWelded = new int[positions.Length];
        var weldedPositions = new List<Vector3>(positions.Length);

        for (int i = 0; i < positions.Length; i++)
        {
            if (!keyToWelded.TryGetValue(weldKeys[i], out int welded))
            {
                welded = weldedPositions.Count;
                keyToWelded[weldKeys[i]] = welded;
                weldedPositions.Add(positions[i]);
            }
            splitToWelded[i] = welded;
        }

        int triangles = indices.Length / 3;
        var loopVertex = new List<uint>(indices.Length);
        var loopNormals = new List<Vector3>(indices.Length);
        var loopUvs = new List<Vector2>(indices.Length);
        var loopOrig = new List<int>(indices.Length);
        var kept = new List<int>(triangles);
        var seenFaces = new HashSet<(int, int, int)>(triangles);
        int degenerate = 0, duplicate = 0;

        for (int t = 0; t < triangles; t++)
        {
            uint s0 = indices[t * 3 + 0], s1 = indices[t * 3 + 1], s2 = indices[t * 3 + 2];
            int w0 = splitToWelded[s0], w1 = splitToWelded[s1], w2 = splitToWelded[s2];
            if (w0 == w1 || w1 == w2 || w0 == w2) { degenerate++; continue; } // see docs above

            // Winding-insensitive face identity (sorted triple) — matches Blender's duplicate rule.
            (int a, int b, int c) = Sort3(w0, w1, w2);
            if (!seenFaces.Add((a, b, c))) { duplicate++; continue; }

            kept.Add(t);
            AddLoop(s0);
            AddLoop(s1);
            AddLoop(s2);

            void AddLoop(uint split)
            {
                loopVertex.Add((uint)splitToWelded[split]);
                loopNormals.Add(normals[split]);
                loopUvs.Add(uvs != null ? uvs[split] : default);
                loopOrig.Add((int)split);
            }
        }

        return new WeldedMesh
        {
            Positions = weldedPositions.ToArray(),
            SplitToWelded = splitToWelded,
            LoopVertexIndices = loopVertex.ToArray(),
            LoopNormals = loopNormals.ToArray(),
            LoopUvs = loopUvs.ToArray(),
            LoopOrigIndex = loopOrig.ToArray(),
            KeptTriangles = kept.ToArray(),
            DroppedDegenerateTriangles = degenerate,
            DroppedDuplicateTriangles = duplicate,
        };
    }

    private static (int, int, int) Sort3(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }
}
