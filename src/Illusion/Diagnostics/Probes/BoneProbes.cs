using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using Illusion.Rendering.Passes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// A bone as an editable object. On a car the bones ARE the parts, so this walks the thing a modder actually
/// wants to do: pick <c>doorFL</c>, drag it, and have the door's collision hull, lock and handle go with it —
/// then have that survive a save.
/// <para>
/// Edits the extracted working copy of one car and puts the FrameResource back byte for byte at the end.
/// Output: %TEMP%\illusion_bones.txt
/// </para>
/// </summary>
internal static class BoneProbes
{
    /// <summary>
    /// How a skinned mesh's vertices are tied to the rig — the question that has to be answered before the
    /// viewport can make a car's body follow its bones.
    /// <para>
    /// A vertex carries four bone ids and four weights, but the ids are NOT bone indices: they index a per-LOD
    /// remap pool, and which pool a vertex belongs to comes from the face group it is drawn in. This measures
    /// that wiring on the corpus rather than assuming it — including which of the two bytes in the reference's
    /// SkinnedMaterialInfo is the pool and which is the weight count, whose comments contradict their names.
    /// </para>
    /// Output: %TEMP%\illusion_skinning.txt
    /// </summary>
    internal static void RunSkinningProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_skinning.txt");
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

            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            FileInfo[] archives = new DirectoryInfo(folder).GetFiles("*.sds");
            Array.Sort(archives, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // Census over every car: the invariants the renderer will rely on.
            int models = 0, poolsTotal = 0, groupsTotal = 0;
            int remapSumMatches = 0, remapIdsInRange = 0, poolIndexInRange = 0, weightsInRange = 0;
            int transformsMatchBones = 0, weightsSumToOne = 0, verticesChecked = 0;
            var weightCounts = new Dictionary<int, int>();
            int splitSizeChecked = 0, splitSizeMatches = 0;
            var splitSizeOff = new List<string>();
            var poolCounts = new Dictionary<int, int>();
            int largestPool = 0, modelsSplittingTheirRig = 0, modelsBiggerThanAPool = 0;
            string largestPoolAt = "";

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
                    FrameBlendInfo blend;
                    FrameSkeleton skeleton;
                    try { blend = model.GetBlendInfoObject(); skeleton = model.GetSkeletonObject(); }
                    catch (Exception) { continue; }

                    int boneCount = skeleton.BoneNames?.Length ?? 0;
                    if (boneCount == 0 || blend.BoneIndexInfos is not { Length: > 0 } lods) continue;
                    models++;

                    if (blend.BoneTransforms?.Length == boneCount - 1) transformsMatchBones++;

                    FrameBlendInfo.BoneIndexInfo lod0 = lods[0];
                    byte[] pools = lod0.BonesPerRemapPool ?? [];
                    byte[] remap = lod0.BoneRemapIDs ?? [];
                    poolsTotal += pools.Length;

                    // How wide a single pool ever gets, and how often a rig is too big for one. A draw reads
                    // its bones out of ONE pool, so this is the ceiling a rebuild has to respect — putting a
                    // whole 83-bone car rig in one pool is what tore a repacked car apart.
                    int widest = 0;
                    foreach (FrameBlendInfo.BoneIndexInfo lod in lods)
                    {
                        foreach (byte size in lod.BonesPerRemapPool ?? [])
                        {
                            widest = Math.Max(widest, size);
                            if (size > largestPool) { largestPool = size; largestPoolAt = sds.Name; }
                        }
                    }
                    if (pools.Count(p => p > 0) > 1) modelsSplittingTheirRig++;
                    if (boneCount > widest) modelsBiggerThanAPool++;

                    if (pools.Sum(p => (int)p) == remap.Length) remapSumMatches++;
                    if (remap.All(id => id < boneCount)) remapIdsInRange++;

                    var groups = lod0.SkinnedMaterialInfo ?? [];
                    groupsTotal += groups.Length;
                    bool poolOk = true, weightOk = true;
                    foreach (FrameBlendInfo.SkinnedMaterialInfo g in groups)
                    {
                        poolCounts[g.AssignedPoolIndex] = poolCounts.GetValueOrDefault(g.AssignedPoolIndex) + 1;
                        weightCounts[g.NumWeightsPerVertex] = weightCounts.GetValueOrDefault(g.NumWeightsPerVertex) + 1;
                        if (g.AssignedPoolIndex >= pools.Length) poolOk = false;
                        if (g.NumWeightsPerVertex is < 1 or > 4) weightOk = false;
                    }
                    if (poolOk) poolIndexInRange++;
                    if (weightOk) weightsInRange++;

                    // The stored counter against the table it describes. Everything a rebuild writes after
                    // the split block is found by walking past it, so a counter that no longer matches the
                    // bytes makes the whole model unreadable — it simply stops appearing in game. Proving
                    // the formula on every shipped car is what makes it safe to recompute.
                    splitSizeChecked++;
                    if (model.ComputeSplitBlockSize() == model.SplitCounters.Item1) splitSizeMatches++;
                    else if (splitSizeOff.Count < 6)
                    {
                        splitSizeOff.Add($"{sds.Name}: {model.ComputeSplitBlockSize()} vs stored "
                            + $"{model.SplitCounters.Item1}");
                    }

                    // The vertices themselves: weights must sum to 1 for the shader to be a plain blend.
                    foreach (Vertex v in SampleVertices(model, 64))
                    {
                        verticesChecked++;
                        if (Math.Abs(v.BoneWeights.Sum() - 1f) < 0.02f) weightsSumToOne++;
                    }
                }
            }

            sb.AppendLine($"SKINNING CENSUS: {models} skinned models over {archives.Length} car archives\n");
            // Not a defect: the table holds an inverse bind matrix for every bone BUT the root (see below).
            Check("every model's bone-transform table is one shorter than its bone list",
                transformsMatchBones == models, $"{transformsMatchBones} of {models}");
            Check("the remap pools account for exactly the remap table",
                remapSumMatches == models, $"{remapSumMatches} of {models}");
            Check("every remap id is a real bone", remapIdsInRange == models, $"{remapIdsInRange} of {models}");
            Check("every face group names a pool that exists",
                poolIndexInRange == models, $"{poolIndexInRange} of {models}");
            Check("every face group's weight count is 1..4",
                weightsInRange == models, $"{weightsInRange} of {models}");
            Check("the split block size can be recomputed from the table",
                splitSizeChecked > 0 && splitSizeMatches == splitSizeChecked,
                $"{splitSizeMatches} of {splitSizeChecked}"
                    + (splitSizeOff.Count > 0 ? "; " + string.Join("; ", splitSizeOff) : ""));
            Check("vertex weights sum to one",
                verticesChecked > 0 && weightsSumToOne == verticesChecked,
                $"{weightsSumToOne} of {verticesChecked} sampled vertices");

            sb.AppendLine($"\n  {poolsTotal} remap pools, {groupsTotal} face groups over LOD 0");
            sb.AppendLine($"  widest pool the game ships: {largestPool} bones ({largestPoolAt}); "
                + $"{modelsSplittingTheirRig} of {models} models cut their rig across more than one pool, "
                + $"{modelsBiggerThanAPool} have more bones than any one pool holds");
            sb.AppendLine("  weights per vertex, by how many face groups declare it:");
            foreach ((int n, int count) in weightCounts.OrderBy(p => p.Key))
                sb.AppendLine($"    {n,3} weights  {count,6} groups");
            sb.AppendLine("  pool index, by how many face groups use it:");
            foreach ((int n, int count) in poolCounts.OrderBy(p => p.Key))
                sb.AppendLine($"    pool {n,3}  {count,6} groups");

            // What the loader hands the renderer: the ids resolved to the model's own bone list.
            int skinnedMeshes = 0, resolvedMeshes = 0, idsInRange = 0, idsChecked = 0;
            foreach (FileInfo sds in archives.Take(24))
            {
                string extracted = MafiaEnvironment.ExtractedDir(sds);
                if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

                List<SdsFrameNode> roots;
                try { (roots, _, _) = SdsMeshLoader.LoadHierarchy(sds); }
                catch (Exception) { continue; }

                foreach (MeshData mesh in Meshes(roots))
                {
                    if (mesh.Skeleton == null && mesh.BoneIndices == null) continue;
                    skinnedMeshes++;
                    if (!mesh.IsSkinned) continue;
                    resolvedMeshes++;

                    int bones = mesh.Skeleton!.Bones.Count;
                    foreach (byte id in mesh.BoneIndices!)
                    {
                        idsChecked++;
                        if (id < bones) idsInRange++;
                    }
                }
            }

            Check("a skinned mesh reaches the renderer with its skin resolved",
                skinnedMeshes > 0 && resolvedMeshes == skinnedMeshes, $"{resolvedMeshes} of {skinnedMeshes}");
            Check("every resolved bone id is an index into the model's own bone list",
                idsChecked > 0 && idsInRange == idsChecked, $"{idsInRange} of {idsChecked}");

            DumpSkinning(sb, folder, focus, Check);
            sb.Insert(0, $"SKINNING PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "SKINNING PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    /// <summary>
    /// The skin on screen. Renders a car twice — as authored, then with one bone moved — and asserts that the
    /// picture changed. Nothing else in the probe suite can tell whether the palette actually reaches the
    /// vertex shader, and a skinning path that silently does nothing looks exactly like a correct one on a
    /// model at rest.
    /// <para>Output: %TEMP%\illusion_skin_render.txt plus two PNGs.</para>
    /// </summary>
    internal static void RunSkinRenderProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_skin_render.txt");
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

            var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", focus + ".sds"));
            if (!car.Exists) { sb.AppendLine("no such archive"); return; }

            (List<SdsFrameNode> roots, List<MeshData> meshes, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(car);
            MeshData? body = meshes.FirstOrDefault(m => m.IsSkinned);
            Check("the car loads with a skinned body", body != null,
                $"{meshes.Count} meshes, {meshes.Count(m => m.IsSkinned)} skinned");
            if (body == null) return;

            int bones = body.Skeleton!.Bones.Count;
            int index = -1;
            for (int i = 0; i < bones; i++)
                if (string.Equals(body.Skeleton.Bones[i].Name, "doorFL", StringComparison.OrdinalIgnoreCase))
                    index = i;
            Check("its rig has the bone we are going to move", index >= 0, $"doorFL at {index} of {bones}");
            if (index < 0) return;

            // How much of the body is weighted to that bone at all — if this is zero the render can only
            // ever be identical and the comparison below would prove nothing.
            int weighted = 0;
            for (int v = 0; v * 4 < body.BoneIndices!.Length; v++)
            {
                for (int k = 0; k < 4; k++)
                {
                    if (body.BoneIndices[(v * 4) + k] == index && body.BoneWeights![(v * 4) + k] > 0.01f)
                    {
                        weighted++;
                        break;
                    }
                }
            }
            Check("the bone actually owns geometry", weighted > 0,
                $"{weighted} of {body.VertexCount} vertices weighted to doorFL");

            FrameObjectModel? model = null;
            foreach (SdsFrameNode r in roots)
            {
                model ??= FindModel(r);
            }
            RenderPair(sb, Check, meshes, body, index, bones, model, document as Illusion.Assets.Adapters.SceneDocumentAdapter);
            sb.Insert(0, $"SKIN RENDER PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "SKIN RENDER PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static FrameObjectModel? FindModel(SdsFrameNode node)
    {
        if (node.Source is Illusion.Assets.Adapters.FrameNodeAdapter { Frame: FrameObjectModel m }) return m;
        foreach (SdsFrameNode c in node.Children)
        {
            if (FindModel(c) is { } found) return found;
        }
        return null;
    }

    private static void RenderPair(StringBuilder sb, Action<string, bool, string> check,
        List<MeshData> meshes, MeshData body, int bone, int boneCount, FrameObjectModel? model,
        Illusion.Assets.Adapters.SceneDocumentAdapter? document)
    {
        const int W = 900, H = 600;
        using var gpu = new Rendering.Gpu.GpuContext();
        using var renderer = new Rendering.Passes.SceneRenderer(gpu)
        {
            Mode = Rendering.Passes.RenderMode.MaterialPreview,
            ShowSky = false,
        };
        using var target = new Rendering.Gpu.SharedRenderTarget(gpu, W, H);

        var uploaded = new List<Rendering.Gpu.GpuMesh>();
        foreach (MeshData m in meshes) uploaded.Add(renderer.AddMesh(m));

        Rendering.Gpu.GpuMesh? skinned = uploaded.FirstOrDefault(g => g.IsSkinned);
        check("the skinned body reaches the GPU with its skin", skinned != null, "");
        if (skinned == null) return;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (Vector3 p in body.Positions)
        {
            Vector3 w = Vector3.Transform(p, body.World);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }
        Vector3 centre = (min + max) * 0.5f;
        float radius = MathF.Max((max - min).Length() * 0.5f, 0.5f);
        renderer.Camera.Far = (radius * 40f) + 100f;
        // From the LEFT front quarter: doorFL is the bone under test and a camera on the other side would
        // watch it move behind the body, which is how a first attempt at this probe reported no change at all.
        renderer.Camera.LookAt(centre + new Vector3(-radius * 1.7f, radius * 1.7f, radius * 0.8f), centre);

        string restPng = Path.Combine(Path.GetTempPath(), "illusion_skin_rest.png");
        string posedPng = Path.Combine(Path.GetTempPath(), "illusion_skin_posed.png");

        renderer.Render(target);
        byte[] rest = Rendering.Gpu.RenderTargetReadback.Read(gpu, target);
        GpuProbes.SavePng(rest, W, H, restPng);

        // The DOCUMENT's own rest transforms, which is what the renderer reads — a probe posing a private
        // copy would prove nothing about the editor, where every path (gizmo, undo, a push from Blender)
        // writes exactly this array.
        Matrix4x4[] pose = model!.RestTransform;
        var original = (Matrix4x4[])pose.Clone();

        // Move the bone a long way, so the difference is unmistakable rather than a few pixels of shading.
        Matrix4x4 moved = pose[bone];
        moved.Translation += new Vector3(0f, 0f, 1.5f);
        pose[bone] = moved;

        renderer.Render(target);
        byte[] posed = Rendering.Gpu.RenderTargetReadback.Read(gpu, target);
        GpuProbes.SavePng(posed, W, H, posedPng);

        int differing = 0;
        for (int i = 0; i + 3 < rest.Length && i + 3 < posed.Length; i += 4)
        {
            if (rest[i] != posed[i] || rest[i + 1] != posed[i + 1] || rest[i + 2] != posed[i + 2]) differing++;
        }
        double share = 100.0 * differing / (W * H);
        check("moving a bone changes what is drawn", share > 0.5,
            $"{differing} pixels ({share:F2} %) -> {posedPng}");

        // And back: the identity palette must reproduce the original picture exactly, which is the proof that
        // skinning at rest is a no-op rather than a small constant distortion nobody notices.
        Array.Copy(original, pose, pose.Length);
        renderer.Render(target);
        byte[] again = Rendering.Gpu.RenderTargetReadback.Read(gpu, target);
        check("posing back to rest reproduces the original frame", again.AsSpan().SequenceEqual(rest),
            $"{restPng}");

        // A ROTATION, through the editor's own write path — the gizmo's world delta turned into a local
        // matrix exactly as a drag does it. A door swinging on its hinge is the thing a modder will try
        // first, and it is not the same test as sliding one: a rotation exercises the basis of the matrix,
        // where a translation only touches its last row.
        if (model != null && document != null)
        {
            Matrix4x4 oldWorld = model.GetJointWorldTransform(bone);
            Matrix4x4 parentWorld = model.WorldTransform;
            // About Z — the way a door swings on its hinge.
            Matrix4x4 delta = Rendering.Gizmos.TransformOps.RotateDelta(
                oldWorld.Translation, Vector3.UnitZ, MathF.PI / 4f);

            Array.Copy(original, pose, pose.Length);
            pose[bone] = Rendering.Gizmos.TransformOps.WorldDeltaToLocal(oldWorld, parentWorld, delta);
            renderer.Render(target);
            byte[] rotated = Rendering.Gpu.RenderTargetReadback.Read(gpu, target);
            string rotatedPng = Path.Combine(Path.GetTempPath(), "illusion_skin_rotated.png");
            GpuProbes.SavePng(rotated, W, H, rotatedPng);

            int rotDiff = 0;
            for (int i = 0; i + 3 < rest.Length && i + 3 < rotated.Length; i += 4)
            {
                if (rest[i] != rotated[i] || rest[i + 1] != rotated[i + 1] || rest[i + 2] != rotated[i + 2]) rotDiff++;
            }
            check("rotating a bone through the gizmo's own path changes what is drawn",
                rotDiff > 0, $"{rotDiff} pixels ({100.0 * rotDiff / (W * H):F2} %) -> {rotatedPng}");

            // Where the bone's own origin ends up. A hinge rotation must leave it exactly where it was —
            // if it moves, the whole door is being swung about the wrong point.
            Matrix4x4 palette = Affine(pose[bone]);
            Matrix4x4 bind = Affine(body.Skeleton!.Bones[bone].Rest);
            Matrix4x4.Invert(bind, out Matrix4x4 bindInv);
            Vector3 pivotAfter = Vector3.Transform(bind.Translation, bindInv * palette);
            check("the bone's own origin does not move when it is rotated",
                Approx(pivotAfter, bind.Translation, 1e-3f),
                $"{bind.Translation} -> {pivotAfter}");

            sb.AppendLine($"\n    rotation: local before {Fmt(bind)}\n              local after  {Fmt(Affine(pose[bone]))}");

            // The app's frame is not this probe's frame: it draws overlays and a selection silhouette around
            // the mesh pass, and every one of those binds its own shader, input layout and constant buffers.
            // If any of them leaves the pipeline in a state the skinned draw does not restore, the body stops
            // following its bones in the application while a bare render like the one above keeps working —
            // which is exactly the difference being hunted.
            renderer.ShowSkeleton = true;
            renderer.ShowActors = true;
            renderer.ShowNov = true;
            renderer.ShowNavWorld = true;
            renderer.SetSkeletonDistrict(
                "probe", Viewport.DistrictStreamer.BuildRigLines([body.Skeleton!]) ?? default);
            renderer.SetSelectionMeshes([skinned]);

            Matrix4x4 overlayPose = pose[bone];
            Array.Copy(original, pose, pose.Length);
            renderer.Render(target);
            byte[] overlaidRest = Rendering.Gpu.RenderTargetReadback.Read(gpu, target);

            pose[bone] = overlayPose;
            renderer.Render(target);
            byte[] overlaidPosed = Rendering.Gpu.RenderTargetReadback.Read(gpu, target);

            int overlaidDiff = 0;
            for (int i = 0; i + 3 < overlaidRest.Length && i + 3 < overlaidPosed.Length; i += 4)
            {
                if (overlaidRest[i] != overlaidPosed[i] || overlaidRest[i + 1] != overlaidPosed[i + 1]
                    || overlaidRest[i + 2] != overlaidPosed[i + 2])
                {
                    overlaidDiff++;
                }
            }
            string overlaidPng = Path.Combine(Path.GetTempPath(), "illusion_skin_overlaid.png");
            GpuProbes.SavePng(overlaidPosed, W, H, overlaidPng);
            check("the body still follows its bone with the overlays and the outline drawn",
                overlaidDiff > 500, $"{overlaidDiff} pixels -> {overlaidPng}");
            sb.AppendLine($"              palette      {Fmt(skinned.Palette.Get(bone))}");

            // The same rotation again, but driven through the editor's own objects rather than through the
            // maths by hand: the bone adapter supplies the two matrices, exactly as a gizmo drag reads them.
            // Anything the adapter adds on top — an actor placement, a parent that is not what we assumed —
            // shows up here and nowhere else.
            // Back to rest first: the renders above wrote into the document's own array, and an adapter
            // reading an already-rotated bone would be handed the delta a second time.
            Matrix4x4 byHand = pose[bone];
            Array.Copy(original, pose, pose.Length);
            Illusion.Assets.Adapters.BoneNodeAdapter adapter = document.Bone(model, bone);
            sb.AppendLine($"\n    adapter: world  {Fmt(adapter.WorldTransform)}");
            sb.AppendLine($"             parent {Fmt(adapter.ParentWorldTransform)}");
            check("the bone adapter's parent transform is usable",
                Matrix4x4.Invert(adapter.ParentWorldTransform, out _),
                Fmt(adapter.ParentWorldTransform));

            Matrix4x4 viaAdapter = Rendering.Gizmos.TransformOps.WorldDeltaToLocal(
                adapter.WorldTransform, adapter.ParentWorldTransform, delta);
            check("driving the drag through the adapter gives the same matrix as by hand",
                BasisApprox(Affine(viaAdapter), Affine(byHand), 1e-3f)
                && Approx(viaAdapter.Translation, byHand.Translation, 1e-3f),
                Fmt(Affine(viaAdapter)));

            // Every bone, not just the door. A rotation must come out of the write path as a RIGID motion:
            // the palette an orthonormal basis with a positive determinant, and the bone's own origin still
            // where it was. Anything else stretches or shears the geometry weighted to it, which is exactly
            // what "the model breaks when I rotate a bone" looks like.
            var rigid = new List<string>();
            var fixedOrigin = new List<string>();
            for (int b = 0; b < boneCount; b++)
            {
                Matrix4x4 boneWorld = model.GetJointWorldTransform(b);
                Matrix4x4 boneDelta = Rendering.Gizmos.TransformOps.RotateDelta(
                    boneWorld.Translation, Vector3.Normalize(new Vector3(0.3f, 0.5f, 0.81f)), 0.7f);
                Matrix4x4 local = Rendering.Gizmos.TransformOps.WorldDeltaToLocal(
                    boneWorld, adapter.ParentWorldTransform, boneDelta);

                Matrix4x4 restB = Affine(body.Skeleton!.Bones[b].Rest);
                if (!Matrix4x4.Invert(restB, out Matrix4x4 restInv)) continue;
                Matrix4x4 p = restInv * Affine(local);

                string nameB = body.Skeleton.Bones[b].Name;
                if (!IsRigid(p) && rigid.Count < 10) rigid.Add($"{nameB} det {p.GetDeterminant():F3}");
                Vector3 moved2 = Vector3.Transform(restB.Translation, p);
                if (!Approx(moved2, restB.Translation, 5e-3f) && fixedOrigin.Count < 10)
                    fixedOrigin.Add($"{nameB} {(moved2 - restB.Translation).Length():F3} m");
            }
            check("rotating ANY bone stays a rigid motion", rigid.Count == 0, string.Join("; ", rigid));
            check("rotating ANY bone leaves its own origin where it was",
                fixedOrigin.Count == 0, string.Join("; ", fixedOrigin));

            // A bone that carries others has to carry them. doorFL parents its own window and deform panel;
            // swinging the door without them is what tears a car apart on screen.
            byte[] parents = model.GetSkeletonHierarchyObject().ParentIndices ?? [];
            var kids = new List<int>();
            for (int i = 0; i < parents.Length && i < boneCount; i++)
                if (parents[i] == bone && i != bone) kids.Add(i);
            check("the bone under test has children to carry", kids.Count > 0,
                string.Join(", ", kids.Select(k => body.Skeleton!.Bones[k].Name)));

            var beforeKids = kids.ToDictionary(k => k, k => model.RestTransform[k].Translation);
            Matrix4x4 restoreSelf = model.RestTransform[bone];
            adapter.LocalTransform = viaAdapter;

            var stayed = new List<string>();
            foreach (int k in kids)
            {
                Vector3 now = model.RestTransform[k].Translation;
                if (Approx(now, beforeKids[k], 1e-4f)) stayed.Add(body.Skeleton!.Bones[k].Name);
            }
            check("its children moved with it", stayed.Count == 0,
                stayed.Count > 0 ? "left behind: " + string.Join(", ", stayed) : $"{kids.Count} carried");

            // The picture of it: the door open with its own window and deform panel still in it.
            renderer.Render(target);
            string carriedPng = Path.Combine(Path.GetTempPath(), "illusion_skin_carried.png");
            GpuProbes.SavePng(Rendering.Gpu.RenderTargetReadback.Read(gpu, target), W, H, carriedPng);
            sb.AppendLine($"    door swung WITH its children -> {carriedPng}");

            // …and undo puts them back, because the setter applies whatever delta it is given.
            adapter.LocalTransform = restoreSelf;
            var notRestored = kids.Where(k => !Approx(model.RestTransform[k].Translation, beforeKids[k], 1e-4f))
                .Select(k => body.Skeleton!.Bones[k].Name).ToList();
            check("undoing the move puts the children back", notRestored.Count == 0,
                string.Join(", ", notRestored));
        }

        sb.AppendLine($"\nrendered {uploaded.Count} meshes, skinned body has {boneCount} bones");
        foreach (Rendering.Gpu.GpuMesh g in uploaded) g.Dispose();
    }

    /// <summary>Every mesh under these roots, in tree order.</summary>
    private static IEnumerable<MeshData> Meshes(IReadOnlyList<SdsFrameNode> roots)
    {
        foreach (SdsFrameNode root in roots)
        {
            if (root.Mesh is { } mesh) yield return mesh;
            foreach (MeshData child in Meshes(root.Children)) yield return child;
        }
    }

    /// <summary>
    /// What actually makes a car panel crumple. Three candidates ride on a vertex or beside it — the
    /// <c>deform_*</c> bones it is weighted to, the per-vertex <c>DamageGroup</c> channel, and the
    /// <c>BBCoeffs</c> beside it — and a modder adding geometry needs to know which of them they have to fill
    /// in by hand. This measures the wiring on the shipped cars instead of assuming it.
    /// <para>Output: %TEMP%\illusion_damage.txt</para>
    /// </summary>
    internal static void RunDamageProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_damage.txt");
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

            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            FileInfo[] archives = new DirectoryInfo(folder).GetFiles("*.sds");
            Array.Sort(archives, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // ── Census: which of the three channels the shipped cars even carry ──
            int models = 0, withDamageGroup = 0, withBBCoeffs = 0, withDeformBones = 0, deformAndGroup = 0;
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
                    if (model.Geometry?.LOD is not { Length: > 0 } lods) continue;
                    if (!lods[0].VertexDeclaration.HasFlag(VertexFlags.Skin)) continue;
                    models++;
                    bool group = lods[0].VertexDeclaration.HasFlag(VertexFlags.DamageGroup);
                    bool deform = DeformBones(model).Count > 0;
                    if (group) withDamageGroup++;
                    if (lods[0].VertexDeclaration.HasFlag(VertexFlags.BBCoeffs)) withBBCoeffs++;
                    if (deform) withDeformBones++;
                    if (deform && group) deformAndGroup++;
                }
            }

            sb.AppendLine($"DAMAGE CENSUS: {models} skinned car models\n");
            // The two channels come together: a model that can crumple declares the group its vertices belong
            // to. Neither is universal — a car archive also carries skinned models that never deform.
            Check("a model with deform bones always carries the damage-group channel too",
                withDeformBones > 0 && deformAndGroup == withDeformBones,
                $"{deformAndGroup} of {withDeformBones} (damage group on {withDamageGroup} of {models} models)");
            // Not a defect, a fact worth pinning: BBCoeffs is declared by NO car, so it plays no part here.
            Check("no car declares BBCoeffs — it is not part of a car's damage wiring",
                withBBCoeffs == 0, $"{withBBCoeffs} of {models}");

            // ── One car in full: what a damage group corresponds to ──
            var car = new FileInfo(Path.Combine(folder, focus + ".sds"));
            if (car.Exists)
            {
                string extracted = MafiaEnvironment.ExtractedDir(car);
                FrameResource fr = SdsMeshLoader.OpenScene(extracted).FrameResource!;
                FrameObjectModel? body = fr.FrameObjects.Values.OfType<FrameObjectModel>()
                    .FirstOrDefault(m => m.Geometry?.LOD is { Length: > 0 } l
                        && l[0].VertexDeclaration.HasFlag(VertexFlags.Skin));

                // The resolved skin comes off the loader's own path — the ids on the wire are pool-local and
                // the probe has no business re-deriving that here.
                (_, List<MeshData> meshes, _) = SdsMeshLoader.LoadHierarchy(car);
                MeshData? loaded = meshes.FirstOrDefault(m => m.IsSkinned
                    && m.Positions.Length == (body?.Geometry.LOD[0].NumVerts ?? -1));
                if (body != null && loaded?.Skeleton != null && loaded.BoneIndices != null)
                {
                    Vertex[] verts = [.. SampleVertices(body, body.Geometry.LOD[0].NumVerts)];
                    IReadOnlyList<BoneData> bones = loaded.Skeleton.Bones;

                    sb.AppendLine($"\n════ {focus} ════");
                    sb.AppendLine($"declaration: {body.Geometry.LOD[0].VertexDeclaration}");
                    sb.AppendLine($"{verts.Length} vertices decoded, rig {bones.Count} bones");

                    // Damage group -> the bones those vertices ride, heaviest first.
                    var bonesOfGroup = new Dictionary<int, Dictionary<string, int>>();
                    var sizeOfGroup = new Dictionary<int, int>();
                    int zeroCoeffs = 0, onDeform = 0;
                    for (int v = 0; v < verts.Length; v++)
                    {
                        int group = verts[v].DamageGroup;
                        sizeOfGroup[group] = sizeOfGroup.GetValueOrDefault(group) + 1;
                        if (verts[v].BBCoeffs == Vector3.Zero) zeroCoeffs++;
                        if (!bonesOfGroup.TryGetValue(group, out Dictionary<string, int>? named))
                            bonesOfGroup[group] = named = [];
                        bool deformed = false;
                        for (int k = 0; k < 4; k++)
                        {
                            if (verts[v].BoneWeights[k] <= 0f) continue;
                            int id = loaded.BoneIndices[(v * 4) + k];
                            if (id >= bones.Count) continue;
                            named[bones[id].Name] = named.GetValueOrDefault(bones[id].Name) + 1;
                            deformed |= bones[id].Name.StartsWith("deform", StringComparison.OrdinalIgnoreCase);
                        }
                        if (deformed) onDeform++;
                    }

                    Check("geometry is actually weighted to the deform bones", onDeform > 0,
                        $"{onDeform} of {verts.Length} vertices");
                    Check("the damage-group channel is used, not left blank",
                        sizeOfGroup.Keys.Any(g => g != 0), $"{sizeOfGroup.Count} distinct groups");

                    sb.AppendLine($"damage groups: {sizeOfGroup.Count} distinct; "
                        + $"{onDeform} of {verts.Length} vertices ride a deform bone "
                        + $"({zeroCoeffs} carry no BBCoeffs — the channel is undeclared)");

                    // How RIGIDLY a panel holds its geometry. A vertex follows a weighted blend of bones, so
                    // "welded to the hood" means one bone at full weight; anything less and the vertex drifts
                    // between its bones as they move. This says what the shipped panels actually do, which is
                    // the answer to "my new part is not firmly attached".
                    var rigidOf = new Dictionary<string, (int Rigid, int Blended, string Partners)>();
                    for (int v = 0; v < verts.Length; v++)
                    {
                        int heavy = -1;
                        float best = 0f, sum = 0f;
                        for (int k = 0; k < 4; k++)
                        {
                            float weight = verts[v].BoneWeights[k];
                            if (weight <= 0f) continue;
                            sum += weight;
                            if (weight > best) { best = weight; heavy = loaded.BoneIndices[(v * 4) + k]; }
                        }
                        if (heavy < 0 || heavy >= bones.Count || sum <= 0f) continue;
                        string owner = bones[heavy].Name;
                        (int rigid, int blended, string? seen) = rigidOf.GetValueOrDefault(owner);
                        string partners = seen ?? "";
                        if (best / sum > 0.999f) rigid++;
                        else
                        {
                            blended++;
                            for (int k = 0; k < 4 && partners.Length < 40; k++)
                            {
                                if (verts[v].BoneWeights[k] <= 0f) continue;
                                int id = loaded.BoneIndices[(v * 4) + k];
                                if (id >= bones.Count || bones[id].Name == owner) continue;
                                if (!partners.Contains(bones[id].Name, StringComparison.Ordinal))
                                    partners += (partners.Length > 0 ? "+" : "") + bones[id].Name;
                            }
                        }
                        rigidOf[owner] = (rigid, blended, partners);
                    }

                    int rigidTotal = rigidOf.Values.Sum(p => p.Rigid);
                    int blendedTotal = rigidOf.Values.Sum(p => p.Blended);
                    Check("panels hold most of their geometry rigidly — one bone at full weight",
                        rigidTotal > blendedTotal, $"{rigidTotal} rigid vs {blendedTotal} blended");
                    sb.AppendLine($"\nhow firmly each part holds its vertices "
                        + $"({rigidTotal} rigid, {blendedTotal} blended):");
                    foreach ((string owner, (int rigid, int blended, string partners)) in
                        rigidOf.OrderByDescending(p => p.Value.Rigid + p.Value.Blended).Take(14))
                    {
                        sb.AppendLine($"    {owner,-22} {rigid,5} rigid {blended,5} blended"
                            + (partners.Length > 0 ? $"  (shared with {partners})" : ""));
                    }
                    foreach ((int group, int size) in sizeOfGroup.OrderByDescending(p => p.Value).Take(20))
                    {
                        string named = bonesOfGroup.TryGetValue(group, out Dictionary<string, int>? b)
                            ? string.Join(", ", b.OrderByDescending(p => p.Value).Take(5).Select(p => p.Key))
                            : "";
                        sb.AppendLine($"    group {group,4}  {size,5} vertices  → {named}");
                    }
                }
            }

            sb.Insert(0, $"DAMAGE PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "DAMAGE PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    /// <summary>Indices of the model's <c>deform_*</c> bones — the ones a car's damage system moves.</summary>
    private static HashSet<int> DeformBones(FrameObjectModel model)
    {
        var found = new HashSet<int>();
        Formats.Hashing.HashName[] names;
        try { names = model.GetSkeletonObject().BoneNames ?? []; }
        catch (Exception) { return found; }
        for (int i = 0; i < names.Length; i++)
        {
            if (names[i].ToString().StartsWith("deform", StringComparison.OrdinalIgnoreCase)) found.Add(i);
        }
        return found;
    }

    /// <summary>The first <paramref name="count"/> vertices of a skinned model's LOD 0, fully decoded (the
    /// scene loader's fast path skips the skin channels, so this goes through the per-vertex codec).</summary>
    private static IEnumerable<Vertex> SampleVertices(FrameObjectModel model, int count)
    {
        if (model.Geometry?.LOD is not { Length: > 0 } lods) yield break;
        FrameLOD lod = lods[0];
        if (!lod.VertexDeclaration.HasFlag(VertexFlags.Skin)) yield break;
        if (model.GetVertexBuffer(0)?.Data is not { } data) yield break;

        lod.GetVertexOffsets(out int stride);
        if (stride <= 0) yield break;

        int n = Math.Min(count, lod.NumVerts);
        var slice = new byte[stride];
        for (int i = 0; i < n; i++)
        {
            if ((long)(i + 1) * stride > data.Length) yield break;
            Array.Copy(data, i * stride, slice, 0, stride);
            yield return VertexTranslator.DecompressVertex(
                slice, lod.VertexDeclaration, model.Geometry.DecompressionOffset,
                model.Geometry.DecompressionFactor, new Dictionary<VertexFlags, VertexOffset>());
        }
    }

    /// <summary>One car's skin wiring in full, plus the question the renderer actually needs answered: is the
    /// blend info's per-bone matrix the inverse of that bone's rest transform?</summary>
    private static void DumpSkinning(StringBuilder sb, string folder, string focus, Action<string, bool, string> check)
    {
        var sds = new FileInfo(Path.Combine(folder, focus + ".sds"));
        sb.AppendLine($"\n\n════ {focus} ════");
        if (!sds.Exists) { sb.AppendLine("no such archive"); return; }
        string extracted = MafiaEnvironment.ExtractedDir(sds);
        if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) { sb.AppendLine("not extracted"); return; }

        FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
        FrameObjectModel? model = fr?.FrameObjects?.Values.OfType<FrameObjectModel>().FirstOrDefault();
        if (model == null) { sb.AppendLine("no skinned model"); return; }

        FrameBlendInfo blend = model.GetBlendInfoObject();
        FrameSkeleton skeleton = model.GetSkeletonObject();
        var names = (skeleton.BoneNames ?? []).Select(n => n.ToString() ?? "?").ToList();
        Matrix4x4[] rest = model.RestTransform ?? [];

        sb.AppendLine($"bones {names.Count}, bone transforms {blend.BoneTransforms?.Length ?? 0}, " +
                      $"LODs {blend.BoneIndexInfos?.Length ?? 0}");

        // The table that blocks re-topologising a skinned body: per bone, per material, ranges of FACES the
        // game deforms as one piece. Rebuilding it needs the rule it is built by, and the rule is only
        // legible from the shipped data — how many splits there are, what indexes what, and whether the
        // ranges partition the mesh or overlap.
        var splits = model.BlendMeshSplits ?? [];
        sb.AppendLine($"\n— blend mesh splits — {splits.Length} splits, " +
                      $"{model.SplitCounters.Item1} phys split bytes, {model.SplitCounters.Item2} hit-box bytes");
        int totalFaces = 0, bursts = 0, maxFace = 0;
        var facesSeen = new Dictionary<int, int>();       // face -> how many splits claim it
        var blendIndexes = new SortedSet<int>();
        foreach (FrameObjectModel.WeightedByMeshSplit split in splits)
        {
            blendIndexes.Add(split.BlendIndex);
            foreach (FrameObjectModel.BlendMeshSplitInfo info in split.Data ?? [])
            {
                foreach (FrameObjectModel.MiniMaterialBurst burst in info.Data ?? [])
                {
                    foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                    {
                        bursts++;
                        totalFaces += range.NumFaces;
                        maxFace = Math.Max(maxFace, range.StartIndex + range.NumFaces);
                        for (int f = range.StartIndex; f < range.StartIndex + range.NumFaces; f++)
                            facesSeen[f] = facesSeen.GetValueOrDefault(f) + 1;
                    }
                }
            }
        }
        int meshFaces = 0;
        if (model.Geometry?.LOD is { Length: > 0 } geomLods) meshFaces = geomLods[0].NumVerts;
        sb.AppendLine($"    {bursts} face bursts covering {totalFaces} faces; highest face named {maxFace}");
        sb.AppendLine($"    distinct faces covered {facesSeen.Count}; claimed by more than one split " +
                      $"{facesSeen.Count(p => p.Value > 1)}");
        sb.AppendLine($"    blend indexes used: {string.Join(", ", blendIndexes.Take(20))}" +
                      (blendIndexes.Count > 20 ? $", … ({blendIndexes.Count})" : ""));
        sb.AppendLine($"    LOD0 has {meshFaces} vertices; its index buffer holds " +
                      $"{(model.GetIndexBuffer(0)?.GetData().Length ?? 0) / 3} triangles");
        foreach (FrameObjectModel.WeightedByMeshSplit split in splits.Take(6))
        {
            int faces = (split.Data ?? []).SelectMany(i => i.Data ?? [])
                .SelectMany(b => b.Data ?? []).Sum(r => (int)r.NumFaces);
            var mats = (split.Data ?? []).SelectMany(i => i.Data ?? []).Select(b => b.MaterialIndex).Distinct();
            sb.AppendLine($"        blend {split.BlendIndex,3} \"{split.JointName}\" — {faces,5} faces, " +
                          $"materials {string.Join("/", mats)}");
        }

        // Who hangs off whom. A bone with children is a bone whose rotation has to carry them, and the rest
        // transforms are MODEL space — moving one changes nothing about the others unless something makes it.
        byte[] parents = model.GetSkeletonHierarchyObject().ParentIndices ?? [];
        var childrenOf = new Dictionary<int, List<string>>();
        for (int i = 0; i < names.Count && i < parents.Length; i++)
        {
            int p = parents[i];
            if (p == i || p >= names.Count) continue;
            if (!childrenOf.TryGetValue(p, out List<string>? kids)) childrenOf[p] = kids = [];
            kids.Add(names[i]);
        }
        sb.AppendLine($"\n— bones that carry others ({childrenOf.Count} of {names.Count}) —");
        foreach ((int parent, List<string> kids) in childrenOf.OrderByDescending(p => p.Value.Count))
            sb.AppendLine($"    {names[parent],-20} → {string.Join(", ", kids.Take(14))}" +
                          (kids.Count > 14 ? $", … ({kids.Count})" : ""));

        for (int lod = 0; lod < (blend.BoneIndexInfos?.Length ?? 0); lod++)
        {
            FrameBlendInfo.BoneIndexInfo info = blend.BoneIndexInfos![lod];
            byte[] pools = info.BonesPerRemapPool ?? [];
            byte[] remap = info.BoneRemapIDs ?? [];
            sb.AppendLine($"\nLOD {lod}: {pools.Length} pools ({string.Join(",", pools)}), " +
                          $"{remap.Length} remap ids, {info.SkinnedMaterialInfo?.Length ?? 0} face groups");

            int at = 0;
            for (int p = 0; p < pools.Length; p++)
            {
                var slice = remap.Skip(at).Take(pools[p]).ToList();
                at += pools[p];
                sb.AppendLine($"    pool {p}: " + string.Join(", ",
                    slice.Select((id, i) => $"{i}->{id}({(id < names.Count ? names[id] : "?")})").Take(12))
                    + (slice.Count > 12 ? $", … ({slice.Count} bones)" : ""));
            }
            foreach ((FrameBlendInfo.SkinnedMaterialInfo g, int i) in (info.SkinnedMaterialInfo ?? []).Select((g, i) => (g, i)))
                sb.AppendLine($"    face group {i}: pool {g.AssignedPoolIndex}, {g.NumWeightsPerVertex} weights/vertex");
        }

        // The bind pose. The blend info's per-bone matrix table is one SHORTER than the bone list, and that is
        // the whole puzzle: entry j is the inverse bind of bone j+1, so the root has none. Pairing them
        // straight across — the obvious reading, and the one the table's name invites — lands on identity for
        // no bone at all. The four candidates are printed rather than asserted one way so the shift is
        // visible in the report rather than only in a comment.
        var candidates = new (string Name, Func<int, Matrix4x4?> Pair)[]
        {
            ("blend.BoneTransforms[i] * rest[i]",
                i => At(blend.BoneTransforms, i) is { } m ? Affine(m) * Affine(rest[i]) : null),
            ("rest[i] * blend.BoneTransforms[i]",
                i => At(blend.BoneTransforms, i) is { } m ? Affine(rest[i]) * Affine(m) : null),
            ("blend.BoneTransforms[i-1] * rest[i]",
                i => At(blend.BoneTransforms, i - 1) is { } m ? Affine(m) * Affine(rest[i]) : null),
            ("skeleton.JointTransforms[i] * rest[i]",
                i => i < (skeleton.JointTransforms?.Length ?? 0)
                    ? Affine(skeleton.JointTransforms![i]) * Affine(rest[i])
                    : null),
        };

        sb.AppendLine($"\nbind pose — joint transforms {skeleton.JointTransforms?.Length ?? 0}, " +
                      $"world transforms {skeleton.WorldTransforms?.Length ?? 0}, rest {rest.Length}, " +
                      $"rest[0].M44 = {(rest.Length > 0 ? rest[0].M44 : float.NaN)}");
        var score = new Dictionary<string, (int Identity, int Tried)>(StringComparer.Ordinal);
        foreach ((string name, Func<int, Matrix4x4?> pair) in candidates)
        {
            int identity = 0, tried = 0;
            for (int i = 0; i < rest.Length; i++)
            {
                if (pair(i) is not { } product) continue;
                tried++;
                if (IsIdentity(product)) identity++;
            }
            score[name] = (identity, tried);
            sb.AppendLine($"    {name,-42} identity on {identity,3} of {tried,3}");
        }

        (int shiftedIdentity, int shiftedTried) = score["blend.BoneTransforms[i-1] * rest[i]"];
        check("the blend info's bone matrix is the inverse bind pose, shifted by one",
            shiftedTried > 0 && shiftedIdentity * 10 >= shiftedTried * 9,
            $"identity on {shiftedIdentity} of {shiftedTried}");
        check("…and not the straight pairing the table's shape suggests",
            score["blend.BoneTransforms[i] * rest[i]"].Identity == 0,
            $"identity on {score["blend.BoneTransforms[i] * rest[i]"].Identity}");

        // What the renderer will actually do: take the rest transforms AS the bind pose and invert them here.
        // The test that matters is that this is self-consistent — at rest the skin must be a no-op.
        int invertible = 0, restIdentity = 0;
        for (int i = 0; i < rest.Length; i++)
        {
            if (!Matrix4x4.Invert(Affine(rest[i]), out Matrix4x4 inverse)) continue;
            invertible++;
            if (IsIdentity(inverse * Affine(rest[i]))) restIdentity++;
        }
        check("every rest transform inverts", invertible == rest.Length, $"{invertible} of {rest.Length}");
        check("inverse(rest) * rest is identity — skinning at rest is a no-op",
            restIdentity == rest.Length, $"{restIdentity} of {rest.Length}");

        // WHICH table is the bind pose the game reads? Editing the rest transforms moves the model in this
        // viewport, but a car packed back into the game showed no change — so the rig's placement must also
        // (or instead) live in one of the other per-bone tables. Each candidate is scored against the rest
        // transforms; the ones that are a function of them have to be rewritten when a bone moves.
        Matrix4x4[] joint = skeleton.JointTransforms ?? [];
        Matrix4x4[] world = skeleton.WorldTransforms ?? [];
        var relations = new (string Name, Func<int, Matrix4x4?> Pair)[]
        {
            ("skeleton.JointTransforms[i] == rest[i]",
                i => i < joint.Length ? Delta(Affine(joint[i]), Affine(rest[i])) : null),
            ("skeleton.JointTransforms[i] == rest relative to parent",
                i => i < joint.Length && i < parents.Length && parents[i] != i && parents[i] < rest.Length
                    ? Delta(Affine(joint[i]), Relative(rest[i], rest[parents[i]]))
                    : null),
            ("skeleton.WorldTransforms[i] == inverse(rest[i])",
                i => i < world.Length ? Delta(Affine(world[i]), Inverse(rest[i])) : null),
            ("skeleton.WorldTransforms[i] == rest[i]",
                i => i < world.Length ? Delta(Affine(world[i]), Affine(rest[i])) : null),
            ("blend.BoneTransforms[i-1] == inverse(rest[i])",
                i => At(blend.BoneTransforms, i - 1) is { } m ? Delta(Affine(m), Inverse(rest[i])) : null),
        };
        sb.AppendLine("\n    which per-bone table is a function of the rest transforms:");
        foreach ((string relation, Func<int, Matrix4x4?> pair) in relations)
        {
            int same = 0, tried = 0;
            for (int i = 0; i < rest.Length; i++)
            {
                if (pair(i) is not { } d) continue;
                tried++;
                if (IsIdentity(d)) same++;
            }
            sb.AppendLine($"        {relation,-48} holds on {same,3} of {tried,3}");
        }

        // Can the editor's write path even express these matrices? A gizmo drag decomposes the new world
        // transform into rotation + scale and composes it back (TransformOps.WorldDeltaToLocal). That is exact
        // only for a matrix that already IS rotation·scale with a right-handed basis — and a rig is full of
        // mirrored bones, where the decomposition folds the flip into scale.X and the recomposition applies it
        // along the wrong axes.
        int mirrored = 0, survives = 0;
        var broken = new List<string>();
        for (int i = 0; i < rest.Length; i++)
        {
            Matrix4x4 m = Affine(rest[i]);
            if (m.GetDeterminant() < 0) mirrored++;

            TransformMath.TryDecompose(m, out Vector3 scale, out Quaternion rot, out Vector3 pos);
            Matrix4x4 again = TransformMath.Compose(rot, scale, pos);
            if (Approx(again.Translation, m.Translation, 1e-3f) && BasisApprox(again, m, 1e-3f)) survives++;
            else if (broken.Count < 8) broken.Add(i < names.Count ? names[i] : i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.AppendLine($"\n    bones with a mirrored basis: {mirrored} of {rest.Length}");
        check("a rest transform survives the editor's decompose-and-recompose",
            survives == rest.Length,
            $"{survives} of {rest.Length}" + (broken.Count > 0 ? "; breaks on " + string.Join(", ", broken) : ""));
    }

    private static string Fmt(Matrix4x4 m) =>
        $"[{m.M11,7:F3} {m.M12,7:F3} {m.M13,7:F3} | {m.M21,7:F3} {m.M22,7:F3} {m.M23,7:F3} | " +
        $"{m.M31,7:F3} {m.M32,7:F3} {m.M33,7:F3} | T {m.M41,7:F3} {m.M42,7:F3} {m.M43,7:F3}]";

    // A rigid motion: orthonormal basis, determinant +1. Anything else scales or shears whatever is
    // weighted to the bone.
    private static bool IsRigid(Matrix4x4 m)
    {
        var r0 = new Vector3(m.M11, m.M12, m.M13);
        var r1 = new Vector3(m.M21, m.M22, m.M23);
        var r2 = new Vector3(m.M31, m.M32, m.M33);
        return Math.Abs(r0.Length() - 1f) < 5e-3f && Math.Abs(r1.Length() - 1f) < 5e-3f
            && Math.Abs(r2.Length() - 1f) < 5e-3f
            && Math.Abs(Vector3.Dot(r0, r1)) < 5e-3f && Math.Abs(Vector3.Dot(r0, r2)) < 5e-3f
            && Math.Abs(Vector3.Dot(r1, r2)) < 5e-3f
            && m.GetDeterminant() > 0.99f && m.GetDeterminant() < 1.01f;
    }

    private static bool BasisApprox(Matrix4x4 a, Matrix4x4 b, float eps) =>
        Math.Abs(a.M11 - b.M11) < eps && Math.Abs(a.M12 - b.M12) < eps && Math.Abs(a.M13 - b.M13) < eps
        && Math.Abs(a.M21 - b.M21) < eps && Math.Abs(a.M22 - b.M22) < eps && Math.Abs(a.M23 - b.M23) < eps
        && Math.Abs(a.M31 - b.M31) < eps && Math.Abs(a.M32 - b.M32) < eps && Math.Abs(a.M33 - b.M33) < eps;

    // Frame matrices ride as 4x3 (Mat34, 12 floats), so the fourth column comes out (0,0,0,0) and the 4x4 is
    // singular — Matrix4x4.Invert refuses it, and a matrix product silently drops the other operand's
    // translation. Everything that treats one as a transform has to put the 1 back first.
    private static Matrix4x4 Affine(Matrix4x4 m)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return m;
    }

    // "Are these the same matrix?" as something IsIdentity can answer: a * inverse(b).
    private static Matrix4x4? Delta(Matrix4x4 a, Matrix4x4? b) =>
        b is { } other && Matrix4x4.Invert(other, out Matrix4x4 inverse) ? a * inverse : null;

    private static Matrix4x4? Inverse(Matrix4x4 m) =>
        Matrix4x4.Invert(Affine(m), out Matrix4x4 inverse) ? inverse : null;

    // A bone's rest expressed against its parent's, which is the other way a rig can store a pose.
    private static Matrix4x4? Relative(Matrix4x4 child, Matrix4x4 parent) =>
        Matrix4x4.Invert(Affine(parent), out Matrix4x4 inverse) ? Affine(child) * inverse : null;

    private static Matrix4x4? At(FrameBlendInfo.BoneTransform[]? table, int index) =>
        table != null && index >= 0 && index < table.Length ? table[index].Transform : null;

    private static bool IsIdentity(Matrix4x4 m) =>
        Approx(m.Translation, Vector3.Zero, 0.01f)
        && Math.Abs(m.M11 - 1f) < 0.01f && Math.Abs(m.M22 - 1f) < 0.01f && Math.Abs(m.M33 - 1f) < 0.01f
        && Math.Abs(m.M12) < 0.01f && Math.Abs(m.M13) < 0.01f && Math.Abs(m.M21) < 0.01f
        && Math.Abs(m.M23) < 0.01f && Math.Abs(m.M31) < 0.01f && Math.Abs(m.M32) < 0.01f;

    internal static void RunBonesProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bones.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string? frFile = null;
        byte[]? original = null;
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", focus + ".sds"));
            if (!car.Exists) { sb.AppendLine($"no such archive: {car.FullName}"); return; }
            string extracted = MafiaEnvironment.ExtractedDir(car);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml")))
            {
                sb.AppendLine("not extracted — open the car once, or unpack the game");
                return;
            }

            frFile = SdsManifest.Load(extracted).GetFiles("FrameResource")[0];
            original = File.ReadAllBytes(frFile);

            ExtractedSds scene = SdsMeshLoader.OpenScene(extracted);
            FrameResource? fr = scene.FrameResource;
            if (fr == null) { sb.AppendLine("no frame resource"); return; }

            var document = new SceneDocumentAdapter(fr, car);
            FrameObjectModel? model = fr.FrameObjects.Values.OfType<FrameObjectModel>().FirstOrDefault();
            if (model == null) { sb.AppendLine("no skinned model"); return; }

            string[] names = (model.GetSkeletonObject().BoneNames ?? [])
                .Select(n => n.ToString() ?? "?").ToArray();
            int index = Array.FindIndex(names, n => string.Equals(n, "doorFL", StringComparison.OrdinalIgnoreCase));
            Check("the car has a doorFL bone", index >= 0, index >= 0 ? $"bone {index} of {names.Length}" : "absent");
            if (index < 0) return;

            BoneNodeAdapter bone = document.Bone(model, index);
            Check("the same bone comes back as the same object",
                ReferenceEquals(bone, document.Bone(model, index)));
            Check("the bone knows what it is", bone.BoneName == names[index] && bone.TypeName == "Bone",
                $"{bone.BoneName} / {bone.TypeName}");

            // A rest transform is model space, so a drag is re-localized against the MODEL, not a parent bone.
            Check("a bone's parent transform is the model's",
                bone.ParentWorldTransform == model.WorldTransform * document.Placements.For(model));

            // What hangs off doorFL, and where it is before anything moves.
            var attached = new List<(FrameObjectBase Frame, Vector3 Before)>();
            foreach (FrameObjectModel.AttachmentReference a in model.AttachmentReferences ?? [])
                if (a.JointIndex == index && a.Attachment is { } f)
                    attached.Add((f, f.WorldTransform.Translation));
            Check("something hangs off it", attached.Count > 0, $"{attached.Count} frames: " +
                string.Join(", ", attached.Select(x => x.Frame.Name.ToString())));

            // ── The drag ──
            Matrix4x4 beforeLocal = bone.LocalTransform;
            Vector3 beforeWorld = bone.WorldTransform.Translation;
            var delta = new Vector3(0.4f, -0.25f, 0.9f);
            Matrix4x4 moved = beforeLocal;
            moved.Translation = beforeLocal.Translation + delta;
            bone.LocalTransform = moved;

            Check("the bone moved by exactly the delta",
                Approx(bone.WorldTransform.Translation, beforeWorld + delta),
                $"{beforeWorld} -> {bone.WorldTransform.Translation}");

            var strayed = new List<string>();
            foreach ((FrameObjectBase frame, Vector3 before) in attached)
            {
                Vector3 now = frame.WorldTransform.Translation;
                if (!Approx(now, before + delta, 1e-3f)) strayed.Add($"{frame.Name} {before} -> {now}");
            }
            Check("every attached frame moved with it", strayed.Count == 0,
                strayed.Count == 0 ? $"{attached.Count} frames carried" : string.Join("; ", strayed));

            // Nothing else may have moved: a sibling bone and its own attachments stay put.
            int other = Array.FindIndex(names, n => string.Equals(n, "doorFR", StringComparison.OrdinalIgnoreCase));
            if (other >= 0)
            {
                Vector3 sibling = document.Bone(model, other).WorldTransform.Translation;
                bone.LocalTransform = beforeLocal;
                Check("moving one bone leaves its neighbour alone",
                    Approx(document.Bone(model, other).WorldTransform.Translation, sibling));
                bone.LocalTransform = moved;
            }

            // ── Undo, as the history replays it ──
            bone.LocalTransform = beforeLocal;
            Check("undo puts the bone back", Approx(bone.WorldTransform.Translation, beforeWorld));
            Check("…and everything attached to it",
                attached.All(x => Approx(x.Frame.WorldTransform.Translation, x.Before, 1e-3f)));

            // ── Save ──
            bone.LocalTransform = moved;
            SdsWriter.SaveFrameResource(fr, car);

            FrameObjectModel reread = SdsMeshLoader.OpenScene(extracted).FrameResource!
                .FrameObjects.Values.OfType<FrameObjectModel>().First();
            Vector3 saved = reread.RestTransform[index].Translation;
            Check("a moved bone survives save and reload", Approx(saved, moved.Translation, 1e-2f),
                $"want {moved.Translation} got {saved}");

            // And the attachments come back off the SAVED bone, not off where they used to be.
            Vector3 want = attached[0].Before + delta;
            FrameObjectBase? sameFrame = reread.AttachmentReferences
                .FirstOrDefault(a => a.JointIndex == index &&
                                     a.Attachment?.Name.ToString() == attached[0].Frame.Name.ToString())?.Attachment;
            Check("its attachments reload in the new place",
                sameFrame != null && Approx(sameFrame.WorldTransform.Translation, want, 1e-2f),
                sameFrame == null ? "frame not found" : $"want {want} got {sameFrame.WorldTransform.Translation}");

            // ── The overlay ──
            // Loaded the way the viewport loads it, so the rig lines come off the real adapters. The overlay
            // is one immutable buffer per archive: it is rebuilt after a bone moves, and that rebuild has to
            // read the bone's CURRENT place, not the rest matrix captured at load.
            (List<SdsFrameNode> roots, _, _) = SdsMeshLoader.LoadHierarchy(car);
            List<SkeletonData> rigs = Viewport.DistrictStreamer.CollectSkeletons(roots);
            Check("the loaded scene hands over its rigs", rigs.Count > 0, $"{rigs.Count} skeletons");

            BoneData live = rigs[0].Bones[index];
            Check("a bone in the rig is the editable object", live.Source is BoneNodeAdapter,
                live.Source?.GetType().Name ?? "null");

            if (live.Source is IFrameNode node && Viewport.DistrictStreamer.BuildRigLines(rigs) is { } beforeLines)
            {
                Vector3 was = node.WorldTransform.Translation;
                Matrix4x4 shifted = node.LocalTransform;
                shifted.Translation += delta;
                node.LocalTransform = shifted;

                RigLines? afterLines = Viewport.DistrictStreamer.BuildRigLines(rigs);
                Check("the overlay redraws the bone where it now is",
                    afterLines is { } after
                    && after.Joints.Count == beforeLines.Joints.Count
                    && Approx(after.Joints[index * 6] - beforeLines.Joints[index * 6], delta),
                    $"{was} -> {node.WorldTransform.Translation}");
            }

            // ── Restore ──
            File.WriteAllBytes(frFile, original);
            original = null;
            Vector3 back = SdsMeshLoader.OpenScene(extracted).FrameResource!
                .FrameObjects.Values.OfType<FrameObjectModel>().First().RestTransform[index].Translation;
            Check("the working copy goes back to what it was", Approx(back, beforeLocal.Translation, 1e-3f));

            sb.Insert(0, $"BONE EDIT PROBE ({focus}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "BONE EDIT PROBE: FAIL\n\n");
        }
        finally
        {
            // The working copy must never be left edited, whatever went wrong above.
            if (frFile != null && original != null) File.WriteAllBytes(frFile, original);
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
