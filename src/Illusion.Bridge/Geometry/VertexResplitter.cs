using System.Numerics;
using Illusion.Bridge.Payload;

namespace Illusion.Bridge.Geometry;

/// <summary>Per-source-split-vertex attributes recovered from a Blender push. Vertices no loop
/// referenced (degenerate-only or unreferenced in Blender) have <see cref="Seen"/> false — they
/// keep their original packed bytes downstream.</summary>
public sealed class ResplitResult
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] Uvs { get; init; }
    public required bool[] Seen { get; init; }
}

/// <summary>
/// Maps a pushed (welded, per-loop) mesh payload back onto the game's split-vertex layout via the
/// <c>_orig_index</c> identity — the count-preserving path. Any sign the mesh's topology changed in
/// Blender (new vertices, re-welded corners, per-corner UV splits) is a clean failure with a reason;
/// the full-rebuild path of a later phase takes over from there.
/// </summary>
public static class VertexResplitter
{
    // Half-precision quantum around |uv| ≈ 1 — corners sharing a source vertex must agree closer
    // than the packed format could even express a difference.
    private const float UvTolerance = 5e-4f;

    public static ResplitResult? TryResplitCountPreserving(
        MeshObjectPayload payload, int splitVertexCount, out string? failReason)
    {
        failReason = null;
        int loops = payload.LoopOrigIndex.Length;
        if (payload.LoopVertexIndices.Length != loops || payload.LoopNormals.Length != loops
            || payload.LoopUvs.Length != loops)
        {
            failReason = "malformed payload (loop array lengths disagree)";
            return null;
        }

        var positions = new Vector3[splitVertexCount];
        var normalSums = new Vector3[splitVertexCount];
        var normalCounts = new int[splitVertexCount];
        var uvs = new Vector2[splitVertexCount];
        var seen = new bool[splitVertexCount];

        for (int i = 0; i < loops; i++)
        {
            int orig = payload.LoopOrigIndex[i];
            if (orig < 0)
            {
                failReason = "topology changed (vertices created in Blender)";
                return null;
            }
            if (orig >= splitVertexCount)
            {
                failReason = "malformed payload (source vertex index out of range)";
                return null;
            }
            uint welded = payload.LoopVertexIndices[i];
            if (welded >= payload.Positions.Length)
            {
                failReason = "malformed payload (welded vertex index out of range)";
                return null;
            }

            Vector3 pos = payload.Positions[welded];
            Vector2 uv = new(payload.LoopUvs[i].X, 1f - payload.LoopUvs[i].Y); // back to the game's V

            if (!seen[orig])
            {
                seen[orig] = true;
                positions[orig] = pos;
                uvs[orig] = uv;
            }
            else
            {
                if (positions[orig] != pos)
                {
                    failReason = "topology changed (a source vertex maps to diverging positions)";
                    return null;
                }
                if (MathF.Abs(uvs[orig].X - uv.X) > UvTolerance || MathF.Abs(uvs[orig].Y - uv.Y) > UvTolerance)
                {
                    failReason = "topology changed (per-corner UVs of one source vertex diverged)";
                    return null;
                }
            }
            normalSums[orig] += payload.LoopNormals[i];
            normalCounts[orig]++;
        }

        // Plain average by count — NOT unit-normalized: the packed format's decoded normals are not
        // unit vectors ((byte−127)/127 per component), and forcing unit length here would shift
        // bytes on re-encode. Direction comparison against the original is the applier's job.
        var normals = new Vector3[splitVertexCount];
        for (int v = 0; v < splitVertexCount; v++)
        {
            if (!seen[v]) continue;
            normals[v] = normalSums[v] / normalCounts[v];
        }

        return new ResplitResult { Positions = positions, Normals = normals, Uvs = uvs, Seen = seen };
    }
}
