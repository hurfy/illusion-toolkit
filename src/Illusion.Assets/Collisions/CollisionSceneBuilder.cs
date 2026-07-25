using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Collisions;

namespace Illusion.Assets.Collisions;

/// <summary>
/// Builds render-ready <see cref="CollisionRenderData"/> from a streamed <c>Collisions</c> (.col) resource:
/// decodes each referenced collision mesh's cooked blob into triangles, groups placements by mesh hash into
/// hardware-instanced entries, and groups triangles by their surface material.
/// </summary>
public static class CollisionSceneBuilder
{
    /// <summary>Stand-in raw material id for a mesh that carries no per-triangle material array.</summary>
    private const int UnknownMaterialId = -1;

    /// <param name="scaleOf">Session-only display scale per placement (a gizmo resize that has no derived hull
    /// yet). Null means every placement renders unscaled, which is what the .col itself can express.</param>
    public static CollisionRenderData Build(CollisionFile file, Func<CollisionInstance, Vector3>? scaleOf = null)
    {
        // Group placements by the mesh hash they reference; each placement becomes an instance world matrix.
        var byHash = new Dictionary<ulong, List<Matrix4x4>>();
        foreach (CollisionInstance inst in file.Instances)
        {
            Matrix4x4 world = TransformMath.Compose(
                TransformMath.CollisionEulerToQuaternion(inst.Rotation), scaleOf?.Invoke(inst) ?? Vector3.One, inst.Position);
            if (!byHash.TryGetValue(inst.Hash, out List<Matrix4x4>? list))
            {
                list = new List<Matrix4x4>();
                byHash[inst.Hash] = list;
            }
            list.Add(world);
        }

        var meshes = new List<CollisionRenderMesh>();
        foreach (CollisionMesh mesh in file.Meshes)
        {
            if (mesh.CookedMesh is null) continue;
            if (!byHash.TryGetValue(mesh.Hash, out List<Matrix4x4>? instances)) continue; // mesh never placed

            CookedTriangleMesh decoded;
            try { decoded = CookedTriangleMesh.Decode(mesh.CookedMesh); }
            catch (CollisionDecodeException) { continue; } // skip an undecodable mesh, don't fail the whole layer

            (uint[] indices, int[] sourceTriangle, CollisionRenderPart[] parts) = GroupByMaterial(decoded);

            (Vector3 min, Vector3 max) = Bounds(decoded.Vertices);
            meshes.Add(new CollisionRenderMesh
            {
                Hash = mesh.Hash,
                Positions = decoded.Vertices,
                Indices = indices,
                SourceTriangle = sourceTriangle,
                Parts = parts,
                Instances = instances.ToArray(),
                LocalMin = min,
                LocalMax = max,
            });
        }

        return new CollisionRenderData { Meshes = meshes.ToArray() };
    }

    /// <summary>
    /// Whether <paramref name="decoded"/> still covers every hull the current <paramref name="file"/> actually
    /// places. False once the .col gained a mesh that is placed but not in the cache — a hull minted by an edit
    /// (a scaled copy, say). <see cref="RebuildInstances"/> iterates the CACHE, so in that state it silently drops
    /// the new hull's placements, and the caller must re-<see cref="Build"/> instead.
    /// <para>Meshes with no cooked blob are ignored here: <see cref="Build"/> skips them too, so counting them as
    /// "not covered" would ask for a rebuild that can never satisfy the check.</para>
    /// </summary>
    public static bool CoversPlacedMeshes(CollisionRenderData decoded, CollisionFile file)
    {
        var cached = new HashSet<ulong>(decoded.Meshes.Length);
        foreach (CollisionRenderMesh m in decoded.Meshes) cached.Add(m.Hash);

        var placed = new HashSet<ulong>(file.Instances.Count);
        foreach (CollisionInstance inst in file.Instances) placed.Add(inst.Hash);

        foreach (CollisionMesh mesh in file.Meshes)
            if (mesh.CookedMesh is { Length: > 0 } && placed.Contains(mesh.Hash) && !cached.Contains(mesh.Hash))
                return false;
        return true;
    }

    /// <summary>
    /// Rebuilds only the per-placement world matrices from the current <paramref name="file"/> instances, reusing
    /// the already-decoded geometry in <paramref name="decoded"/> (same Positions / Indices / Parts arrays — no
    /// PhysX re-decode). Used for a live re-upload after a placement edit: cheap enough to run on every committed
    /// gizmo / property edit. Meshes that no longer have any placement are dropped.
    /// <para>Only hulls already in <paramref name="decoded"/> survive — a mesh added to the .col since the decode
    /// is NOT picked up. Callers that can add meshes must gate on <see cref="CoversPlacedMeshes"/> first.</para>
    /// </summary>
    /// <param name="scaleOf">Session-only display scale per placement; null renders every placement unscaled.</param>
    public static CollisionRenderData RebuildInstances(
        CollisionRenderData decoded, CollisionFile file, Func<CollisionInstance, Vector3>? scaleOf = null)
    {
        var byHash = new Dictionary<ulong, List<Matrix4x4>>();
        foreach (CollisionInstance inst in file.Instances)
        {
            Matrix4x4 world = TransformMath.Compose(
                TransformMath.CollisionEulerToQuaternion(inst.Rotation), scaleOf?.Invoke(inst) ?? Vector3.One, inst.Position);
            if (!byHash.TryGetValue(inst.Hash, out List<Matrix4x4>? list))
            {
                list = new List<Matrix4x4>();
                byHash[inst.Hash] = list;
            }
            list.Add(world);
        }

        var meshes = new List<CollisionRenderMesh>(decoded.Meshes.Length);
        foreach (CollisionRenderMesh m in decoded.Meshes)
        {
            if (!byHash.TryGetValue(m.Hash, out List<Matrix4x4>? instances) || instances.Count == 0) continue;
            meshes.Add(new CollisionRenderMesh
            {
                Hash = m.Hash,
                Positions = m.Positions,
                Indices = m.Indices,
                SourceTriangle = m.SourceTriangle,
                Parts = m.Parts,
                Instances = instances.ToArray(),
                LocalMin = m.LocalMin,
                LocalMax = m.LocalMax,
            });
        }

        return new CollisionRenderData { Meshes = meshes.ToArray() };
    }

    /// <summary>
    /// Reorders a decoded mesh's triangles so that each surface material occupies one contiguous index range, and
    /// emits one <see cref="CollisionRenderPart"/> per material.
    /// </summary>
    /// <remarks>
    /// The material of a triangle comes from the cooked mesh's own per-triangle array, never from the
    /// <c>.col</c>-level <see cref="CollisionSection"/> ranges: cooking reorders triangles, so the sections (which
    /// describe the authored order) disagree with the cooked order for every mesh carrying a face-remap table —
    /// measured at 46.6 % of all triangles on <c>city/eastside</c>.
    /// <para/>
    /// Grouping is a counting sort, which keeps the part count at one per distinct material (789 across eastside).
    /// Emitting runs in raw cooked order instead would produce 14 758 parts — 18.7× the draw calls for identical
    /// pixels. The index buffer is permuted, not grown, so GPU bytes are unchanged.
    /// </remarks>
    private static (uint[] Indices, int[] SourceTriangle, CollisionRenderPart[] Parts) GroupByMaterial(
        CookedTriangleMesh decoded)
    {
        int triangleCount = decoded.TriangleCount;
        ushort[] materials = decoded.TriangleMaterials;
        if (materials.Length != triangleCount)
        {
            // No per-triangle material array (no stock mesh lacks one): keep cooked order as a single part.
            return (Identity(decoded), Sequence(triangleCount), new[]
            {
                new CollisionRenderPart(0, triangleCount * 3, UnknownMaterialId, CollisionMaterialCatalog.UnknownColor),
            });
        }

        // Counting sort: tally each material, turn the tallies into run starts, then scatter triangles into place.
        var triangleCounts = new SortedDictionary<ushort, int>();
        foreach (ushort material in materials)
        {
            triangleCounts.TryGetValue(material, out int n);
            triangleCounts[material] = n + 1;
        }

        var runStart = new Dictionary<ushort, int>(triangleCounts.Count);
        var parts = new CollisionRenderPart[triangleCounts.Count];
        int cursor = 0, part = 0;
        foreach ((ushort material, int count) in triangleCounts)
        {
            runStart[material] = cursor;
            parts[part++] = new CollisionRenderPart(
                cursor * 3, count * 3, material, CollisionMaterialCatalog.ColorForRawId(material));
            cursor += count;
        }

        var indices = new uint[triangleCount * 3];
        var sourceTriangle = new int[triangleCount];
        for (int t = 0; t < triangleCount; t++)
        {
            int target = runStart[materials[t]]++;
            sourceTriangle[target] = t;
            indices[target * 3] = (uint)decoded.Triangles[t * 3];
            indices[target * 3 + 1] = (uint)decoded.Triangles[t * 3 + 1];
            indices[target * 3 + 2] = (uint)decoded.Triangles[t * 3 + 2];
        }

        return (indices, sourceTriangle, parts);
    }

    private static uint[] Identity(CookedTriangleMesh decoded)
    {
        var indices = new uint[decoded.Triangles.Length];
        for (int i = 0; i < indices.Length; i++) indices[i] = (uint)decoded.Triangles[i];
        return indices;
    }

    private static int[] Sequence(int count)
    {
        var values = new int[count];
        for (int i = 0; i < count; i++) values[i] = i;
        return values;
    }

    private static (Vector3 Min, Vector3 Max) Bounds(Vector3[] vertices)
    {
        if (vertices.Length == 0) return (Vector3.Zero, Vector3.Zero);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (Vector3 v in vertices)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        return (min, max);
    }
}
