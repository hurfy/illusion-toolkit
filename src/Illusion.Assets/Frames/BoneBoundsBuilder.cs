using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Mathematics;

namespace Illusion.Assets.Frames;

/// <summary>
/// The per-BONE bounding boxes a skinned model carries, rebuilt from its geometry.
///
/// <para>
/// A car keeps one box per bone in <see cref="FrameSkeleton.MappingForBlendingInfo.Bounds"/>, in metres, in
/// that bone's own space. Measured on 88 cars (<c>--probe-bullets</c>): placed by the bone's rest transform
/// they hold 684 480 of 684 480 weighted vertices — every one. Nothing in this toolkit read them until now
/// and nothing recalculated them, so geometry added through the bridge or an import fell outside every box
/// and the game stopped registering hits on it.
/// </para>
/// <para>
/// Rebuilt rather than adjusted: the boxes are derived data, and deriving them again from the vertices is
/// the only way to be right after an edit of any shape.
/// </para>
/// </summary>
public static class BoneBoundsBuilder
{
    /// <summary>How a vertex is assigned to a bone when building its box.</summary>
    public enum Rule
    {
        /// <summary>Only the bone the vertex is most weighted to.</summary>
        Dominant,

        /// <summary>Every bone the vertex has any weight for — the bigger, safer box.</summary>
        AnyInfluence,
    }

    /// <summary>
    /// The boxes this model's geometry implies, one per bone, in each bone's own space. Null when the model
    /// has no usable skin — a mesh with no weights has nothing to derive them from.
    /// </summary>
    public static BoundingBox[]? Compute(FrameObjectModel model, int lod, Rule rule)
    {
        ArgumentNullException.ThrowIfNull(model);

        DecodedMesh? decoded = SdsMeshLoader.DecodeLod(model, lod);
        byte[]? ids = SdsMeshLoader.GlobalBoneIds(model, lod);
        if (decoded == null || ids == null || decoded.BoneWeights is not { } weights) return null;

        FrameSkeleton skeleton;
        try { skeleton = model.GetSkeletonObject(); }
        catch (Exception) { return null; }
        int bones = skeleton.BoneNames?.Length ?? 0;
        if (bones == 0) return null;

        Matrix4x4[] rest = model.RestTransform ?? [];
        var lo = new Vector3[bones];
        var hi = new Vector3[bones];
        var touched = new bool[bones];
        for (int i = 0; i < bones; i++)
        {
            lo[i] = new Vector3(float.MaxValue);
            hi[i] = new Vector3(float.MinValue);
        }

        // Each bone's box is in ITS space, so the vertex goes through the inverse of that bone's rest
        // transform before it is measured. Computed once per bone rather than per vertex.
        var toBone = new Matrix4x4[bones];
        var invertible = new bool[bones];
        for (int i = 0; i < bones; i++)
        {
            invertible[i] = i < rest.Length && TryInvert(rest[i], out toBone[i]);
        }

        for (int v = 0; v < decoded.Positions.Length; v++)
        {
            int dominant = -1;
            float best = 0f;
            for (int k = 0; k < 4; k++)
            {
                int at = (v * 4) + k;
                if (at >= ids.Length || at >= weights.Length) break;
                if (weights[at] <= 0f) continue;
                if (weights[at] > best) { best = weights[at]; dominant = ids[at]; }
                if (rule == Rule.AnyInfluence) Take(ids[at], v);
            }
            if (rule == Rule.Dominant && dominant >= 0) Take(dominant, v);
        }

        var built = new BoundingBox[bones];
        for (int i = 0; i < bones; i++)
        {
            built[i] = touched[i] ? new BoundingBox(lo[i], hi[i]) : new BoundingBox(Vector3.Zero, Vector3.Zero);
        }
        return built;

        void Take(int bone, int vertex)
        {
            if (bone < 0 || bone >= bones || !invertible[bone]) return;
            Vector3 p = Vector3.Transform(decoded.Positions[vertex], toBone[bone]);
            lo[bone] = Vector3.Min(lo[bone], p);
            hi[bone] = Vector3.Max(hi[bone], p);
            touched[bone] = true;
        }
    }

    /// <summary>
    /// Rewrites the model's stored per-bone boxes from its geometry, one mapping per LOD.
    ///
    /// <para>
    /// A bone the geometry no longer touches keeps whatever it had: an empty box would be a silent way of
    /// removing a part from everything that reads these, and a part with no vertices is a rig question, not
    /// a bounds question.
    /// </para>
    /// </summary>
    /// <returns>How many boxes changed.</returns>
    public static int Rebuild(FrameObjectModel model, Rule rule = Rule.Dominant)
    {
        ArgumentNullException.ThrowIfNull(model);

        FrameSkeleton skeleton;
        try { skeleton = model.GetSkeletonObject(); }
        catch (Exception) { return 0; }
        FrameSkeleton.MappingForBlendingInfo[] maps = skeleton.MappingForBlendingInfos ?? [];
        if (maps.Length == 0) return 0;

        int changed = 0;
        for (int lod = 0; lod < maps.Length; lod++)
        {
            BoundingBox[]? built = Compute(model, lod, rule);
            BoundingBox[] stored = maps[lod].Bounds ?? [];
            if (built == null || stored.Length == 0) continue;

            int count = Math.Min(built.Length, stored.Length);
            var next = (BoundingBox[])stored.Clone();
            for (int bone = 0; bone < count; bone++)
            {
                if (built[bone].Min == Vector3.Zero && built[bone].Max == Vector3.Zero) continue;
                if (Same(next[bone], built[bone])) continue;
                next[bone] = built[bone];
                changed++;
            }
            maps[lod].Bounds = next;
        }

        skeleton.MappingForBlendingInfos = maps;
        return changed;
    }

    /// <summary>
    /// A rest transform inverted, with its fourth column put back first.
    ///
    /// <para>
    /// The file stores a 3×4 and leaves the projective column as whatever was in memory, so the matrix as
    /// read is singular: on a 55-bone car exactly ONE of the 55 inverts. Normalising the column costs
    /// nothing and turns all of them back into the rigid transforms they are — without it this builder
    /// silently produced no boxes at all, which is the quietest possible way to be wrong.
    /// </para>
    /// </summary>
    private static bool TryInvert(Matrix4x4 m, out Matrix4x4 inverse)
    {
        m.M14 = 0f;
        m.M24 = 0f;
        m.M34 = 0f;
        m.M44 = 1f;
        return Matrix4x4.Invert(m, out inverse);
    }

    private static bool Same(BoundingBox a, BoundingBox b) =>
        (a.Min - b.Min).Length() < 1e-4f && (a.Max - b.Max).Length() < 1e-4f;
}
