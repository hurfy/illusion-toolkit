using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Phase 1 of <c>.claude/plans/nimble-grafting-timber.md</c>: everything a NEW bone would have to be written
/// into, measured across every shipped skinned model before a single byte is written.
///
/// <para>
/// A rig is not one array. The skeleton carries names, joint transforms, world transforms, a per-LOD usage
/// table and a per-bone bounding box; the hierarchy carries parents and a last-child index; the blend info
/// carries the remap pools a draw reaches into and a transform per bone; and the model itself carries a rest
/// transform per bone. Getting any one of them out of step is not a cosmetic fault — the car tears into
/// spikes or fails to load, which this session has now seen twice from a much smaller mistake.
/// </para>
/// <para>Reads only; nothing is written. Output: %TEMP%\illusion_bone_add.txt</para>
/// </summary>
internal static class BoneAddProbes
{
    internal static void RunBoneAddProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bone_add.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            var folders = new List<string>
            {
                Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars"),
            };

            int models = 0;
            // Which per-bone arrays really are one entry per bone.
            int jointMatches = 0, worldMatches = 0, restMatches = 0, boneTransformMatches = 0, lodUsageMatches = 0;
            int hierarchyMatches = 0, lastChildMatches = 0;
            // How the four copies of the pose relate.
            int jointEqualsWorld = 0, jointEqualsRest = 0, restEqualsBoneTransform = 0, jointEqualsBoneTransform = 0;
            long posesCompared = 0;
            // Is LastChildIndices derivable?
            int lastChildDerivable = 0, lastChildChecked = 0;
            // The counters nobody has named.
            var numBonesShapes = new Dictionary<string, int>(StringComparer.Ordinal);
            var idTypes = new Dictionary<int, int>();
            int numBlendIdsEqualsBones = 0, unk01Zero = 0, unkDataEmpty = 0;
            // Pool headroom.
            int widestPool = 0, poolsOverSixty = 0;
            var poolCounts = new Dictionary<int, int>();

            foreach (string folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                FileInfo[] archives = new DirectoryInfo(folder).GetFiles("*.sds");
                Array.Sort(archives, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                foreach (FileInfo sds in archives)
                {
                    string extracted = MafiaEnvironment.ExtractedDir(sds);
                    if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

                    FrameResource? fr;
                    try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
                    catch (Exception) { continue; }
                    if (fr?.FrameObjects == null) continue;

                    foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
                    {
                        FrameSkeleton skeleton;
                        FrameSkeletonHierarchy hierarchy;
                        FrameBlendInfo blend;
                        try
                        {
                            skeleton = model.GetSkeletonObject();
                            hierarchy = model.GetSkeletonHierarchyObject();
                            blend = model.GetBlendInfoObject();
                        }
                        catch (Exception) { continue; }

                        HashName[] names = skeleton.BoneNames ?? [];
                        if (names.Length == 0) continue;
                        models++;
                        int bones = names.Length;

                        // ── 1. which arrays are one per bone ──
                        if ((skeleton.JointTransforms?.Length ?? -1) == bones) jointMatches++;
                        if ((skeleton.WorldTransforms?.Length ?? -1) == bones) worldMatches++;
                        if ((model.RestTransform?.Length ?? -1) == bones) restMatches++;
                        if ((blend.BoneTransforms?.Length ?? -1) == bones) boneTransformMatches++;
                        if ((skeleton.BoneLODUsage?.Length ?? -1) == bones) lodUsageMatches++;
                        if ((hierarchy.ParentIndices?.Length ?? -1) == bones) hierarchyMatches++;
                        if ((hierarchy.LastChildIndices?.Length ?? -1) == bones) lastChildMatches++;

                        // ── 2. the pose, in how many places ──
                        Matrix4x4[] joints = skeleton.JointTransforms ?? [];
                        Matrix4x4[] worlds = skeleton.WorldTransforms ?? [];
                        Matrix4x4[] rest = model.RestTransform ?? [];
                        FrameBlendInfo.BoneTransform[] boneTransforms = blend.BoneTransforms ?? [];
                        for (int b = 0; b < bones; b++)
                        {
                            posesCompared++;
                            bool haveJoint = b < joints.Length;
                            if (haveJoint && b < worlds.Length && Same(joints[b], worlds[b])) jointEqualsWorld++;
                            if (haveJoint && b < rest.Length && Same(joints[b], rest[b])) jointEqualsRest++;
                            if (b < rest.Length && b < boneTransforms.Length
                                && Same(rest[b], boneTransforms[b].Transform)) restEqualsBoneTransform++;
                            if (haveJoint && b < boneTransforms.Length
                                && Same(joints[b], boneTransforms[b].Transform)) jointEqualsBoneTransform++;
                        }

                        // ── 3. is LastChildIndices derivable from ParentIndices? ──
                        byte[] parents = hierarchy.ParentIndices ?? [];
                        byte[] lastChild = hierarchy.LastChildIndices ?? [];
                        if (parents.Length == bones && lastChild.Length == bones)
                        {
                            lastChildChecked++;
                            if (DerivedLastChild(parents).SequenceEqual(lastChild)) lastChildDerivable++;
                        }

                        // ── 4/6. the counters ──
                        int[] numBones = skeleton.NumBones ?? [];
                        string shape = string.Join("/", numBones.Select(n => n == bones ? "=bones" : n.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)));
                        numBonesShapes[shape] = numBonesShapes.GetValueOrDefault(shape) + 1;
                        idTypes[skeleton.IDType] = idTypes.GetValueOrDefault(skeleton.IDType) + 1;
                        if (skeleton.NumBlendIDs == bones) numBlendIdsEqualsBones++;
                        if (hierarchy.Unk01 == 0) unk01Zero++;
                        if ((hierarchy.UnkData?.Length ?? 0) == 0) unkDataEmpty++;

                        // ── 5. pool headroom ──
                        foreach (FrameBlendInfo.BoneIndexInfo info in blend.BoneIndexInfos ?? [])
                        {
                            byte[] sizes = info.BonesPerRemapPool ?? [];
                            int used = sizes.Count(s => s > 0);
                            poolCounts[used] = poolCounts.GetValueOrDefault(used) + 1;
                            foreach (byte size in sizes)
                            {
                                widestPool = Math.Max(widestPool, size);
                                if (size > 60) poolsOverSixty++;
                            }
                        }
                    }
                }
            }

            sb.AppendLine($"BONE-ADD SURVEY: {models} skinned models\n");

            sb.AppendLine("── which arrays carry exactly one entry per bone ──");
            Report(sb, "skeleton.JointTransforms", jointMatches, models);
            Report(sb, "skeleton.WorldTransforms", worldMatches, models);
            Report(sb, "model.RestTransform", restMatches, models);
            Report(sb, "blendInfo.BoneTransforms", boneTransformMatches, models);
            Report(sb, "skeleton.BoneLODUsage", lodUsageMatches, models);
            Report(sb, "hierarchy.ParentIndices", hierarchyMatches, models);
            Report(sb, "hierarchy.LastChildIndices", lastChildMatches, models);
            Check("every per-bone array really is one entry per bone",
                models > 0 && jointMatches == models && restMatches == models && hierarchyMatches == models,
                "a new bone appends one entry to each of these");

            sb.AppendLine($"\n── the pose, over {posesCompared} bones ──");
            Report(sb, "JointTransforms == WorldTransforms", jointEqualsWorld, posesCompared);
            Report(sb, "JointTransforms == model.RestTransform", jointEqualsRest, posesCompared);
            Report(sb, "model.RestTransform == BoneTransforms", restEqualsBoneTransform, posesCompared);
            Report(sb, "JointTransforms == BoneTransforms", jointEqualsBoneTransform, posesCompared);

            sb.AppendLine($"\n── hierarchy ──");
            sb.AppendLine($"    LastChildIndices reproduced from ParentIndices on {lastChildDerivable} "
                + $"of {lastChildChecked} models");
            // Measured and NOT settled: "the highest index of any bone whose parent is this one" reproduces
            // the shipped array on none of the 92 models. So the field means something else — a depth-first
            // last DESCENDANT, or an end-exclusive bound — and until that is known a new bone cannot compute
            // its entry. Phase 2 does not start on this array.
            sb.AppendLine("    the naive rule (highest direct child) is NOT what the field holds — still open");

            sb.AppendLine($"\n── the counters ──");
            sb.AppendLine("    NumBones reads as: " + string.Join(", ",
                numBonesShapes.OrderByDescending(p => p.Value).Select(p => $"[{p.Key}] ×{p.Value}")));
            sb.AppendLine("    IDType: " + string.Join(", ",
                idTypes.OrderByDescending(p => p.Value).Select(p => $"{p.Key}×{p.Value}")));
            sb.AppendLine($"    NumBlendIDs equals the bone count on {numBlendIdsEqualsBones} of {models}");
            sb.AppendLine($"    hierarchy.Unk01 is zero on {unk01Zero} of {models}; "
                + $"UnkData is empty on {unkDataEmpty} of {models}");

            sb.AppendLine($"\n── remap pools, the ceiling a new bone runs into ──");
            sb.AppendLine($"    widest pool shipped: {widestPool} bones; pools over 60: {poolsOverSixty}");
            sb.AppendLine("    pools in use per model: " + string.Join(", ",
                poolCounts.OrderBy(p => p.Key).Select(p => $"{p.Key}×{p.Value}")));
            Check("no shipped pool passes 64, so that is the ceiling a new bone must respect",
                widestPool is > 0 and <= 64, $"widest is {widestPool}");

            sb.Insert(0, $"BONE-ADD PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "BONE-ADD PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static void Report(StringBuilder sb, string what, long hits, long of) =>
        sb.AppendLine($"    {what,-42} {hits,7} of {of}");

    /// <summary>
    /// The last-child index each bone would have, derived from the parent list alone: the highest index of
    /// any bone whose parent is this one, or the bone itself when it has no children. If this reproduces the
    /// shipped array, a new bone's entry is computable; if it does not, the array has to be understood first.
    /// </summary>
    private static byte[] DerivedLastChild(byte[] parents)
    {
        var last = new byte[parents.Length];
        for (int b = 0; b < parents.Length; b++) last[b] = (byte)b;
        for (int b = 0; b < parents.Length; b++)
        {
            int parent = parents[b];
            if (parent == b || parent >= parents.Length) continue;
            if (b > last[parent]) last[parent] = (byte)b;
        }
        return last;
    }

    private static bool Same(Matrix4x4 a, Matrix4x4 b)
    {
        const float Eps = 1e-4f;
        return MathF.Abs(a.M11 - b.M11) < Eps && MathF.Abs(a.M12 - b.M12) < Eps
            && MathF.Abs(a.M13 - b.M13) < Eps && MathF.Abs(a.M21 - b.M21) < Eps
            && MathF.Abs(a.M22 - b.M22) < Eps && MathF.Abs(a.M23 - b.M23) < Eps
            && MathF.Abs(a.M31 - b.M31) < Eps && MathF.Abs(a.M32 - b.M32) < Eps
            && MathF.Abs(a.M33 - b.M33) < Eps && MathF.Abs(a.M41 - b.M41) < Eps
            && MathF.Abs(a.M42 - b.M42) < Eps && MathF.Abs(a.M43 - b.M43) < Eps;
    }
}
