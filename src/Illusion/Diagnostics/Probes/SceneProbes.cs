using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Collisions;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Collisions;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Translokator;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Scene;
using Illusion.Scene;
using Illusion.Viewport;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the scene/frame layer: district scene trees, name-table flags and the crash chain.</summary>
internal static class SceneProbes
{
    // Collision render pipeline (no GPU): for a district, decode+build the collision layer (CollisionSceneBuilder)
    // and report instances/meshes/triangles plus the collision world AABB vs the district's render-mesh world AABB.
    // A grossly wrong position convention (e.g. an axis swap) shows as the collision centre landing far outside the
    // render bounds; the rotation convention still needs a visual check. Output: %TEMP%\illusion_collision_render.txt
    internal static void RunCollisionRenderProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_render.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? col = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"COLLISION RENDER PROBE — district={district}");
            sb.AppendLine($".col: {(col != null ? Path.GetFileName(col) : "NOT FOUND")}");
            if (col == null) { sb.AppendLine("RESULT: NO FILES"); return; }

            CollisionRenderData data = CollisionSceneBuilder.Build(CollisionFile.Load(col));

            long instances = 0, tris = 0;
            var cmin = new Vector3(float.MaxValue);
            var cmax = new Vector3(float.MinValue);
            bool finite = true;
            foreach (CollisionRenderMesh m in data.Meshes)
            {
                instances += m.Instances.Length;
                tris += (long)m.TriangleCount * m.Instances.Length;
                foreach (Matrix4x4 world in m.Instances)
                {
                    for (int k = 0; k < 8; k++)
                    {
                        var corner = new Vector3(
                            (k & 1) == 0 ? m.LocalMin.X : m.LocalMax.X,
                            (k & 2) == 0 ? m.LocalMin.Y : m.LocalMax.Y,
                            (k & 4) == 0 ? m.LocalMin.Z : m.LocalMax.Z);
                        Vector3 wp = Vector3.Transform(corner, world);
                        if (!float.IsFinite(wp.X) || !float.IsFinite(wp.Y) || !float.IsFinite(wp.Z)) finite = false;
                        cmin = Vector3.Min(cmin, wp);
                        cmax = Vector3.Max(cmax, wp);
                    }
                }
            }

            // District render-mesh world bounds (mesh world translations — a cheap footprint for the axis check).
            (_, List<MeshData> meshes, _) = SdsMeshLoader.LoadHierarchy(new FileInfo(sds));
            var rmin = new Vector3(float.MaxValue);
            var rmax = new Vector3(float.MinValue);
            foreach (MeshData md in meshes)
            {
                Vector3 t = md.World.Translation;
                rmin = Vector3.Min(rmin, t);
                rmax = Vector3.Max(rmax, t);
            }

            Vector3 cCenter = (cmin + cmax) * 0.5f;
            Vector3 rCenter = (rmin + rmax) * 0.5f;
            float centerDist = (cCenter - rCenter).Length();
            bool centerInside = meshes.Count > 0
                && cCenter.X >= rmin.X && cCenter.X <= rmax.X
                && cCenter.Y >= rmin.Y && cCenter.Y <= rmax.Y
                && cCenter.Z >= rmin.Z && cCenter.Z <= rmax.Z;

            bool pass = finite && instances > 0 && data.Meshes.Length > 0;
            sb.AppendLine(pass ? "RESULT: PASS" : "RESULT: FAIL");
            sb.AppendLine();
            sb.AppendLine($"collision: {data.Meshes.Length} unique meshes, {instances} instances, {tris} triangles (parts×instances)");
            sb.AppendLine($"collision world AABB: {cmin:F1} .. {cmax:F1}  center={cCenter:F1} size={(cmax - cmin):F1}");
            sb.AppendLine($"render    world AABB: {rmin:F1} .. {rmax:F1}  center={rCenter:F1} size={(rmax - rmin):F1}  ({meshes.Count} meshes)");
            sb.AppendLine($"alignment: centerDist={centerDist:F1} m, collisionCentreInsideRenderAABB={centerInside}  " +
                          $"(→ position convention {(centerInside ? "PLAUSIBLE" : "SUSPECT — check axis mapping")}; rotation needs a visual check)");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Candidate Euler compositions to fit: every axis order × every per-axis sign pattern. A product written
    // left→right applies its RIGHTMOST factor first (System.Numerics quaternion multiply). "ZXY ---" is the
    // current CollisionSceneBuilder.CollisionEulerToQuaternion convention (z*x*y with every angle negated =
    // apply −Y, then −X, then −Z); it should come out rank 1 at ~0° against V2.
    private static readonly string[] CollOrders = { "XYZ", "XZY", "YXZ", "YZX", "ZXY", "ZYX" };
    private static readonly int[][] CollSigns =
    {
        new[]{ 1, 1, 1 }, new[]{ 1, 1, -1 }, new[]{ 1, -1, 1 }, new[]{ -1, 1, 1 },
        new[]{ 1, -1, -1 }, new[]{ -1, 1, -1 }, new[]{ -1, -1, 1 }, new[]{ -1, -1, -1 },
    };

    private static Quaternion CollBuild(string order, int[] s, Vector3 r)
    {
        Quaternion C(char c) => c switch
        {
            'X' => Quaternion.CreateFromAxisAngle(Vector3.UnitX, s[0] * r.X),
            'Y' => Quaternion.CreateFromAxisAngle(Vector3.UnitY, s[1] * r.Y),
            _ => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, s[2] * r.Z),
        };
        return C(order[0]) * C(order[1]) * C(order[2]);
    }

    private static string CollSignStr(int[] s) =>
        $"{(s[0] < 0 ? '-' : '+')}{(s[1] < 0 ? '-' : '+')}{(s[2] < 0 ? '-' : '+')}";

    private static float QAngleDeg(Quaternion a, Quaternion b)
    {
        a = Quaternion.Normalize(a); b = Quaternion.Normalize(b);
        float d = MathF.Min(1f, MathF.Abs(Quaternion.Dot(a, b)));
        return 2f * MathF.Acos(d) * 180f / MathF.PI;
    }

    // DirectXMath XMQuaternionRotationRollPitchYaw(pitch, yaw, roll) — verbatim from the Windows SDK
    // (DirectXMathMisc.inl, no-intrinsics path). This is exactly what MafiaToolkitV2 calls to place a
    // collision instance in its Y-up engine.
    private static Quaternion XmRollPitchYaw(float pitch, float yaw, float roll)
    {
        float cp = MathF.Cos(pitch * 0.5f), sp = MathF.Sin(pitch * 0.5f);
        float cy = MathF.Cos(yaw * 0.5f), sy = MathF.Sin(yaw * 0.5f);
        float cr = MathF.Cos(roll * 0.5f), sr = MathF.Sin(roll * 0.5f);
        return new Quaternion(
            cr * sp * cy + sr * cp * sy,
            cr * cp * sy - sr * sp * cy,
            sr * cp * cy - cr * sp * sy,
            cr * cp * cy + sr * sp * sy);
    }

    // Emulates V2's exact collision pipeline and reports which of our Euler builders reproduces it, with NO
    // game data. V2 swaps vertex/translation Y↔Z (Wicked is Y-up) and rotates with XMQuaternionRotationRollPitchYaw
    // (Rotation.X, Rotation.Z, Rotation.Y). Mapping that world back to our Z-up space is a conjugation by the Y↔Z
    // swap S, so the correct our-space rotation R must satisfy, for every vertex v: R·v == S( Qw · (S·v) ).
    private static void RunConventionSelfCheck(StringBuilder sb)
    {
        static Vector3 Swap(Vector3 v) => new(v.X, v.Z, v.Y);

        Vector3[] testRot =
        {
            new(0.30f, 0.50f, 0.70f), new(0.90f, -0.40f, 1.20f), new(-0.60f, 1.10f, -0.30f),
            new(1.00f, 0.00f, 0.00f), new(0.00f, 1.00f, 0.00f), new(0.00f, 0.00f, 1.00f),
            new(0.20f, -0.80f, 2.40f), new(-1.30f, 0.25f, -1.90f),
        };
        Vector3[] testVec = { new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), Vector3.Normalize(new(0.3f, 0.6f, 0.9f)) };

        // Target world direction for input v under V2's (correct) convention, expressed in our Z-up space.
        static Vector3 Target(Vector3 v, Vector3 r)
        {
            Quaternion qw = XmRollPitchYaw(r.X, r.Z, r.Y); // pitch=X, yaw=Z, roll=Y (V2's call)
            return Swap(Vector3.Transform(Swap(v), qw));
        }
        static float VecAngleDeg(Vector3 a, Vector3 b)
        {
            float d = Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b));
            return MathF.Acos(Math.Clamp(d, -1f, 1f)) * 180f / MathF.PI;
        }

        var results = new List<(string Name, float Mean, float Max)>();
        foreach (string order in CollOrders)
            foreach (int[] s in CollSigns)
            {
                double sum = 0; float mx = 0; int n = 0;
                foreach (Vector3 r in testRot)
                {
                    Quaternion q = CollBuild(order, s, r);
                    foreach (Vector3 v in testVec)
                    {
                        float e = VecAngleDeg(Vector3.Transform(v, q), Target(v, r));
                        sum += e; if (e > mx) mx = e; n++;
                    }
                }
                results.Add(($"{order} {CollSignStr(s)}", (float)(sum / n), mx));
            }
        results.Sort((a, b) => a.Mean.CompareTo(b.Mean));

        sb.AppendLine("CONVENTION SELF-CHECK (vs MafiaToolkitV2, no game data) — which Euler build matches V2?");
        sb.AppendLine("  rank  order sign   meanErr   maxErr");
        for (int i = 0; i < Math.Min(6, results.Count); i++)
            sb.AppendLine($"  {i + 1,3}.  {results[i].Name,-9} {results[i].Mean,7:F3}° {results[i].Max,7:F3}°");
        int cur = results.FindIndex(r => r.Name == "ZXY ---");
        sb.AppendLine($"  current code (ZXY ---): mean={results[cur].Mean:F3}° max={results[cur].Max:F3}° → rank {cur + 1}");
        sb.AppendLine($"  → CORRECT convention per V2: {results[0].Name}  (mean err {results[0].Mean:F3}°)");
        sb.AppendLine();

        // Inverse round-trip: euler → quat → euler must recompose to the SAME rotation (gizmo write-back path).
        double rtSum = 0; float rtMax = 0;
        foreach (Vector3 r in testRot)
        {
            Quaternion q1 = TransformMath.CollisionEulerToQuaternion(r);
            Vector3 r2 = TransformMath.CollisionEulerFromQuaternion(q1);
            Quaternion q2 = TransformMath.CollisionEulerToQuaternion(r2);
            float e = QAngleDeg(q1, q2);
            rtSum += e; if (e > rtMax) rtMax = e;
        }
        sb.AppendLine($"euler↔quat round-trip (CollisionEulerFromQuaternion inverse): mean={rtSum / testRot.Length:F4}° max={rtMax:F4}°");
        sb.AppendLine();
    }

    // Ground-truth oracle for the collision placement convention. The streamed .col positions each collision
    // mesh by (Position + Euler Rotation); the district FrameResource ALSO carries FrameObjectCollision nodes
    // that reference the SAME collision-mesh hash and whose WorldTransform is built by the same parent-walk our
    // correctly-rendered meshes use. Pairing the two by hash yields, per instance, a known-correct world matrix,
    // letting us (a) confirm the position matches and (b) EMPIRICALLY determine which Euler composition of
    // (rx,ry,rz) reproduces the frame world rotation. Yaw-only objects can't tell axis orders apart, so the fit
    // error is measured over the TILTED instances (non-trivial rx/ry) — those are what expose a wrong order.
    // Output: %TEMP%\illusion_collision_align.txt
    internal static void RunCollisionAlignProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_align.txt");
        var sb = new StringBuilder();
        try
        {
            // ── Convention-pure self-check (no game data). V2 (MafiaToolkitV2) is the reference: it renders in a
            // Y-up (Wicked) engine, swapping vertex/translation Y↔Z and building the instance rotation with
            // XMQuaternionRotationRollPitchYaw(Rotation.X, Rotation.Z, Rotation.Y). Our viewport is Z-up (Mafia
            // native, no swap), so the correct rotation is the Wicked one conjugated by the Y↔Z swap S:
            // R_ours = S · R_wicked · S. We emulate that exactly and report which Euler builder reproduces it. ──
            RunConventionSelfCheck(sb);

            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? col = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"COLLISION ALIGN ORACLE — district={district}");
            if (col == null) { sb.AppendLine("no .col found"); return; }
            CollisionFile cf = CollisionFile.Load(col);

            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;

            // Frame-graph collisions: hash -> world transforms (the ground-truth placements).
            var frameByHash = new Dictionary<ulong, List<Matrix4x4>>();
            int frameCollNodes = 0;
            var meshPos = new List<Vector3>();
            if (fr?.FrameObjects != null)
                foreach (var pair in fr.FrameObjects)
                {
                    if (pair.Value is FrameObjectCollision fc)
                    {
                        frameCollNodes++;
                        if (!frameByHash.TryGetValue(fc.Hash, out var l)) { l = new List<Matrix4x4>(); frameByHash[fc.Hash] = l; }
                        l.Add(fc.WorldTransform);
                    }
                    else if (pair.Value is FrameObjectSingleMesh sm && sm.Geometry != null)
                    {
                        meshPos.Add(sm.WorldTransform.Translation);
                    }
                }

            var uniqueHashes = new HashSet<ulong>();
            foreach (CollisionInstance inst in cf.Instances) uniqueHashes.Add(inst.Hash);

            sb.AppendLine($".col: {cf.Instances.Count} instances, {uniqueHashes.Count} unique mesh hashes, {cf.Meshes.Count} meshes");
            sb.AppendLine($"frame graph: {frameCollNodes} FrameObjectCollision nodes ({frameByHash.Count} distinct hashes), {meshPos.Count} single-meshes");
            sb.AppendLine();

            // Pair each .col instance with the nearest same-hash frame collision.
            var posErrors = new List<float>();
            var tilted = new List<(Vector3 Rot, Quaternion QFrame)>();
            int hashMatched = 0;
            foreach (CollisionInstance inst in cf.Instances)
            {
                if (!frameByHash.TryGetValue(inst.Hash, out var worlds)) continue;
                hashMatched++;
                Matrix4x4 best = worlds[0];
                float bestD = float.MaxValue;
                foreach (Matrix4x4 w in worlds)
                {
                    float d = (w.Translation - inst.Position).Length();
                    if (d < bestD) { bestD = d; best = w; }
                }
                posErrors.Add(bestD);
                if (bestD < 2f)
                {
                    Matrix4x4.Decompose(best, out _, out Quaternion qf, out _);
                    if (MathF.Abs(inst.Rotation.X) > 0.02f || MathF.Abs(inst.Rotation.Y) > 0.02f)
                        tilted.Add((inst.Rotation, qf));
                }
            }

            posErrors.Sort();
            string posStat = posErrors.Count == 0 ? "n/a"
                : $"min={posErrors[0]:F3} median={posErrors[posErrors.Count / 2]:F3} p90={posErrors[(int)(posErrors.Count * 0.9)]:F3} max={posErrors[^1]:F3} (m)";
            sb.AppendLine($"hash-matched instances: {hashMatched}/{cf.Instances.Count}; position error {posStat}");
            sb.AppendLine($"tilted matched pairs (|rx|>0.02 or |ry|>0.02, pos<2m): {tilted.Count}");
            sb.AppendLine();

            if (tilted.Count > 0)
            {
                var results = new List<(string Name, float Mean, float Max)>();
                foreach (string order in CollOrders)
                    foreach (int[] s in CollSigns)
                    {
                        double sum = 0; float mx = 0;
                        foreach ((Vector3 rot, Quaternion qf) in tilted)
                        {
                            float e = QAngleDeg(CollBuild(order, s, rot), qf);
                            sum += e; if (e > mx) mx = e;
                        }
                        results.Add(($"{order} {CollSignStr(s)}", (float)(sum / tilted.Count), mx));
                    }
                results.Sort((a, b) => a.Mean.CompareTo(b.Mean));
                sb.AppendLine("Euler-composition fit over tilted pairs (lower angle = better):");
                sb.AppendLine("  rank  order sign   meanErr   maxErr");
                for (int i = 0; i < Math.Min(12, results.Count); i++)
                    sb.AppendLine($"  {i + 1,3}.  {results[i].Name,-9} {results[i].Mean,7:F2}° {results[i].Max,7:F2}°");
                int curRank = results.FindIndex(r => r.Name == "ZXY ---");
                (string _, float curMean, float curMax) = results[curRank];
                sb.AppendLine($"  current code (ZXY ---): mean={curMean:F2}° max={curMax:F2}° → rank {curRank + 1}");
                sb.AppendLine();

                sb.AppendLine("sample tilted instances (rot in degrees):");
                for (int i = 0; i < Math.Min(8, tilted.Count); i++)
                {
                    Vector3 rd = tilted[i].Rot * (180f / MathF.PI);
                    sb.AppendLine($"  rot°=({rd.X,8:F2},{rd.Y,8:F2},{rd.Z,8:F2})");
                }
            }
            else
            {
                sb.AppendLine("No tilted hash-matched pairs — cannot disambiguate rotation order from the frame graph.");
                if (meshPos.Count > 0)
                {
                    var nd = new List<float>();
                    int take = Math.Min(cf.Instances.Count, 500);
                    for (int i = 0; i < take; i++)
                    {
                        Vector3 p = cf.Instances[i].Position;
                        float best = float.MaxValue;
                        foreach (Vector3 mp in meshPos) { float d = (mp - p).Length(); if (d < best) best = d; }
                        nd.Add(best);
                    }
                    nd.Sort();
                    sb.AppendLine($"nearest single-mesh distance over {take} sampled instances: " +
                                  $"min={nd[0]:F2} median={nd[take / 2]:F2} p90={nd[(int)(take * 0.9)]:F2} (m)");
                }
            }

            sb.AppendLine();
            sb.AppendLine("RESULT: OK");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Collision placement save (Phase 2): load a district's .col, edit placements (move an instance, change its
    // Group, duplicate another), then prove (a) the edit round-trips through CollisionFile.ToBytes and (b) every
    // untouched cooked-mesh blob + placement is byte-identical, and (c) SdsCollisionSaver writes it back through
    // the manifest atomically. All done against an in-memory model + a throwaway extracted folder — the real
    // working copy is never touched. Output: %TEMP%\illusion_collision_save.txt
    internal static void RunCollisionSaveProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_save.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"COLLISION SAVE PROBE — district={district}");
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            byte[] originalOnDisk = File.ReadAllBytes(colPath);
            CollisionFile orig = CollisionFile.Load(colPath); // reference snapshot (never re-read from a modified file)
            CollisionFile cf = CollisionFile.Load(colPath);    // the working copy we edit
            if (cf.Instances.Count < 2 || cf.Meshes.Count == 0) { sb.AppendLine("too few instances/meshes to test"); return; }
            int n0 = cf.Instances.Count;
            sb.AppendLine($"loaded {n0} instances, {cf.Meshes.Count} meshes");

            // CRITICAL: an UNEDITED re-serialization must be byte-for-byte the original — otherwise a saved .col is
            // structurally different (dropped trailing bytes / mis-sized field) and the game rejects it at load.
            byte[] rewrite = orig.ToBytes();
            long fd = FirstDiff(rewrite, originalOnDisk);
            sb.AppendLine($"BYTE-EXACT unedited roundtrip: equal={ByteEqual(rewrite, originalOnDisk)} " +
                          $"(origLen={originalOnDisk.Length} rewriteLen={rewrite.Length} firstDiff={fd})");

            // --- Edit: move instance[0], flip its Group, duplicate instance[1]. ---
            CollisionInstance moved = cf.Instances[0];
            Vector3 newPos = moved.Position + new Vector3(10f, 20f, 30f);
            byte newGroup = (byte)(moved.Group ^ 0x7);
            moved.Position = newPos;
            moved.Group = newGroup;
            CollisionInstance src = cf.Instances[1];
            var dup = new CollisionInstance
            {
                Position = src.Position + new Vector3(1f, 2f, 3f),
                Rotation = src.Rotation,
                Hash = src.Hash,
                Unk4 = src.Unk4,
                Group = src.Group,
            };
            cf.Instances.Add(dup);

            // --- Round-trip via ToBytes/Read (no filesystem). ---
            byte[] edited = cf.ToBytes();
            CollisionFile reloaded;
            using (var ms = new MemoryStream(edited, writable: false)) reloaded = CollisionFile.Read(ms);

            bool editOk = reloaded.Instances.Count == n0 + 1
                && Approx(reloaded.Instances[0].Position, newPos) && reloaded.Instances[0].Group == newGroup
                && reloaded.Instances[^1].Hash == dup.Hash && Approx(reloaded.Instances[^1].Position, dup.Position);

            // Untouched cooked blobs byte-identical, and untouched instances [1..n0-1] unchanged.
            bool blobsOk = reloaded.Meshes.Count == orig.Meshes.Count;
            for (int i = 0; blobsOk && i < orig.Meshes.Count; i++)
                blobsOk = reloaded.Meshes[i].Hash == orig.Meshes[i].Hash
                    && ByteEqual(reloaded.Meshes[i].CookedMesh, orig.Meshes[i].CookedMesh);
            bool untouchedInstOk = true;
            for (int i = 1; untouchedInstOk && i < n0; i++)
                untouchedInstOk = InstanceEqual(reloaded.Instances[i], orig.Instances[i]);

            sb.AppendLine($"edit round-trip: count {n0}→{reloaded.Instances.Count}, move+group+dup persisted = {editOk}");
            sb.AppendLine($"untouched blobs byte-identical = {blobsOk}; untouched placements identical = {untouchedInstOk}");

            // --- SdsCollisionSaver against a throwaway extracted folder (non-destructive). ---
            string temp = Path.Combine(Path.GetTempPath(), "illusion_col_save_" + district);
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            Directory.CreateDirectory(temp);
            File.WriteAllText(Path.Combine(temp, "SDSContent.xml"),
                "<?xml version=\"1.0\"?>\n<SDSResource>\n  <ResourceEntry>\n    <Type>Collisions</Type>\n    <File>test.col</File>\n  </ResourceEntry>\n</SDSResource>");
            string written = SdsCollisionSaver.SaveToExtracted(cf, temp, district + ".sds");
            byte[] writtenBytes = File.ReadAllBytes(written);
            CollisionFile fromDisk = CollisionFile.Load(written);
            bool saverOk = ByteEqual(writtenBytes, edited) && fromDisk.Instances.Count == reloaded.Instances.Count;
            Directory.Delete(temp, recursive: true);
            sb.AppendLine($"SdsCollisionSaver → {Path.GetFileName(written)}: bytes==ToBytes && reload count={fromDisk.Instances.Count} = {saverOk}");

            bool workingCopyUntouched = ByteEqual(File.ReadAllBytes(colPath), originalOnDisk);
            sb.AppendLine($"real working copy untouched = {workingCopyUntouched}");

            sb.AppendLine();
            sb.AppendLine(editOk && blobsOk && untouchedInstOk && saverOk && workingCopyUntouched ? "RESULT: PASS" : "RESULT: FAIL");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Collision scale preview: the .col instance record has no scale field, so a gizmo resize parks the scale on
    // the adapter and the renderer instances the hull at it. Proves the scale reaches the render matrices through
    // both build paths, that it is opt-in (a null resolver still renders unscaled), and — the honesty gate — that
    // it does NOT leak into the saved .col. Output: %TEMP%\illusion_collision_preview.txt
    internal static void RunCollisionPreviewProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_preview.txt");
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
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"COLLISION PREVIEW PROBE — district={district}");
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            byte[] originalOnDisk = File.ReadAllBytes(colPath);
            CollisionFile cf = CollisionFile.Load(colPath);
            var doc = new CollisionDocumentAdapter(cf, new FileInfo(sds));
            CollisionInstance inst = cf.Instances[0];
            CollisionInstanceAdapter adapter = doc.Node(inst);

            Check("a fresh placement previews unscaled", adapter.PreviewScale == Vector3.One);
            Check("its matrix carries no scale",
                Matrix4x4.Decompose(adapter.LocalTransform, out Vector3 s0, out _, out _) && Near(s0, Vector3.One));

            // --- A gizmo drag hands the adapter a matrix that carries scale.
            Vector3 pos = inst.Position;
            Vector3 rot = inst.Rotation;
            var wanted = new Vector3(2f, 2f, 2f);
            adapter.LocalTransform = TransformMath.Compose(
                TransformMath.CollisionEulerToQuaternion(rot), wanted, pos);

            Check("the scale is parked on the adapter", Near(adapter.PreviewScale, wanted),
                adapter.PreviewScale.ToString());

            // --- A per-axis drag is kept per axis. Rescaling a cooked hull stays exact under an uneven factor
            // --- because the tree's quantization coefficients are themselves per-axis triples, so pulling one
            // --- handle must resize one axis — collapsing it to uniform would move geometry nobody dragged.
            adapter.LocalTransform = TransformMath.Compose(
                TransformMath.CollisionEulerToQuaternion(rot), new Vector3(2f, 3f, 0.5f), pos);
            Check("a per-axis drag stays per-axis",
                Near(adapter.PreviewScale, new Vector3(2f, 3f, 0.5f)), adapter.PreviewScale.ToString());
            adapter.LocalTransform = TransformMath.Compose(
                TransformMath.CollisionEulerToQuaternion(rot), new Vector3(1f, 1f, 0.25f), pos);
            Check("shrinking one axis leaves the others alone",
                Near(adapter.PreviewScale, new Vector3(1f, 1f, 0.25f)), adapter.PreviewScale.ToString());

            adapter.LocalTransform = TransformMath.Compose(
                TransformMath.CollisionEulerToQuaternion(rot), wanted, pos);
            Check("position survived the round trip", Vector3.Distance(inst.Position, pos) < 1e-3f);
            Check("the document resolves the scale for that placement", Near(doc.ScaleOf(inst), wanted));
            Check("an untouched placement still resolves to identity",
                cf.Instances.Count < 2 || Near(doc.ScaleOf(cf.Instances[1]), Vector3.One));
            Check("world matrix carries the scale",
                Matrix4x4.Decompose(adapter.WorldTransform, out Vector3 sw, out _, out _) && Near(sw, wanted));

            // --- Both render paths must instance the hull at that scale.
            CollisionRenderData scaled = CollisionSceneBuilder.Build(cf, doc.ScaleOf);
            CollisionRenderMesh? mesh = scaled.Meshes.FirstOrDefault(m => m.Hash == inst.Hash);
            bool buildScaled = mesh != null && mesh.Instances.Any(w =>
                Matrix4x4.Decompose(w, out Vector3 si, out _, out _) && Near(si, wanted));
            Check("Build instances the hull at the preview scale", buildScaled);

            CollisionRenderData rebuilt = CollisionSceneBuilder.RebuildInstances(scaled, cf, doc.ScaleOf);
            CollisionRenderMesh? reMesh = rebuilt.Meshes.FirstOrDefault(m => m.Hash == inst.Hash);
            bool rebuildScaled = reMesh != null && reMesh.Instances.Any(w =>
                Matrix4x4.Decompose(w, out Vector3 si, out _, out _) && Near(si, wanted));
            Check("RebuildInstances keeps the preview scale", rebuildScaled);

            // --- Opt-in: without a resolver the .col renders exactly as the file describes it.
            CollisionRenderData plain = CollisionSceneBuilder.Build(cf);
            CollisionRenderMesh? plainMesh = plain.Meshes.FirstOrDefault(m => m.Hash == inst.Hash);
            bool plainUnscaled = plainMesh != null && plainMesh.Instances.All(w =>
                Matrix4x4.Decompose(w, out Vector3 si, out _, out _) && Near(si, Vector3.One));
            Check("no resolver → every placement renders unscaled", plainUnscaled);

            // --- THE honesty gate: a preview scale must leave no trace in the file.
            string roundTrip = Path.Combine(Path.GetTempPath(), "illusion_collision_preview_roundtrip.col");
            File.WriteAllBytes(roundTrip, cf.ToBytes());
            CollisionFile reloaded = CollisionFile.Load(roundTrip);
            File.Delete(roundTrip);
            CollisionInstance back = reloaded.Instances[0];
            Check("the preview scale does NOT reach the saved .col",
                Vector3.Distance(back.Position, inst.Position) < 1e-3f
                && Vector3.Distance(back.Rotation, inst.Rotation) < 1e-3f
                && reloaded.Meshes.Count == cf.Meshes.Count,
                $"{reloaded.Meshes.Count} meshes, pos {back.Position}");
            Check("the working copy on disk is untouched",
                ByteEqual(File.ReadAllBytes(colPath), originalOnDisk));

            sb.AppendLine();
            sb.AppendLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            sb.Insert(0, $"COLLISION PREVIEW PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;

    // Collision hull minting: a placement record has nowhere to store a scale, so a scaled placement must point at
    // a DERIVED hull. Proves the identity is deterministic and quantized (a gizmo drag cannot mint a near-duplicate
    // per frame), that an identical scale dedupes to one mesh, that sections carry over, that a failed derive is
    // reported rather than thrown, that orphan collection is exact, and that a minted hull survives a .col
    // round-trip. Uses a copy-the-blob stand-in for the real deriver. Output: %TEMP%\illusion_collision_mint.txt
    internal static void RunCollisionMintProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_mint.txt");
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
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"COLLISION MINT PROBE — district={district}");
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            CollisionFile cf = CollisionFile.Load(colPath);
            CollisionMesh? donor = cf.Meshes.FirstOrDefault(m => m.CookedMesh is { Length: > 0 } && m.Sections.Count > 0);
            if (donor == null || cf.Instances.Count == 0) { sb.AppendLine("no usable mesh/instance"); return; }
            ulong sourceHash = donor.Hash;
            int meshes0 = cf.Meshes.Count;

            // Stand-in deriver: the real one rewrites vertices and quantization coefficients (S3). Minting does not
            // care what the bytes mean, only that it gets some.
            static byte[]? CopyBlob(byte[] cooked, Vector3 scale) => (byte[])cooked.Clone();

            // --- Identity scale mints nothing: a placement at scale 1 already has its hull.
            MintedHull unit = CollisionMeshMinter.Mint(cf, sourceHash, Vector3.One, CopyBlob);
            Check("identity scale reuses the source hull",
                unit.Hash == sourceHash && unit.Added == null && unit.SkipReason == null);

            // --- Deterministic, quantized identity.
            var scale = new Vector3(2f, 2f, 2f);
            ulong h1 = CollisionMeshMinter.DeriveHash(sourceHash, scale);
            ulong h2 = CollisionMeshMinter.DeriveHash(sourceHash, scale);
            ulong hNoise = CollisionMeshMinter.DeriveHash(sourceHash, new Vector3(2.000001f, 2f, 2f));
            ulong hOther = CollisionMeshMinter.DeriveHash(sourceHash, new Vector3(2.01f, 2f, 2f));
            Check("derived hash is deterministic", h1 == h2, $"0x{h1:X16}");
            Check("derived hash differs from the source", h1 != sourceHash);
            Check("float noise quantizes to the SAME hull (no per-frame minting)", h1 == hNoise);
            Check("a genuinely different scale gets its own hull", h1 != hOther);

            // --- First mint produces a mesh, with the source's sections carried over.
            MintedHull first = CollisionMeshMinter.Mint(cf, sourceHash, scale, CopyBlob);
            Check("mint produced a hull", first.Added != null && first.Hash == h1 && first.SkipReason == null);
            if (first.Added == null) return;
            Check("sections carried over verbatim",
                first.Added.Sections.Count == donor.Sections.Count
                && first.Added.Sections[0].Start == donor.Sections[0].Start
                && first.Added.Sections[0].NumEdges == donor.Sections[0].NumEdges
                && first.Added.Sections[0].Material == donor.Sections[0].Material);
            Check("mint does NOT touch the file (the edit owns that)", cf.Meshes.Count == meshes0);

            // --- Add it the way an undoable edit would, then re-mint: must dedupe.
            cf.Meshes.Add(first.Added);
            var placed = new CollisionInstance
            {
                Position = cf.Instances[0].Position,
                Rotation = cf.Instances[0].Rotation,
                Hash = first.Hash,
                Unk4 = -1,
                Group = cf.Instances[0].Group,
            };
            cf.Instances.Add(placed);

            MintedHull again = CollisionMeshMinter.Mint(cf, sourceHash, scale, CopyBlob);
            Check("re-minting the same scale dedupes to the existing hull",
                again.Hash == first.Hash && again.Added == null);

            // --- Orphan accounting: exact, because undo has to leave the .col as it was found.
            Check("a placed hull is not an orphan", !CollisionMeshMinter.IsOrphan(cf, first.Hash));
            cf.Instances.Remove(placed);
            Check("removing the last placement orphans it", CollisionMeshMinter.IsOrphan(cf, first.Hash));
            Check("the source hull is never an orphan while its placements live",
                !CollisionMeshMinter.IsOrphan(cf, sourceHash));
            Check("orphan collection removes exactly one mesh",
                CollisionMeshMinter.RemoveMesh(cf, first.Hash) && cf.Meshes.Count == meshes0);

            // --- A deriver that refuses is reported, not thrown.
            MintedHull refused = CollisionMeshMinter.Mint(cf, sourceHash, scale, static (_, _) => null);
            Check("a deriver returning null is reported as a skip",
                refused.SkipReason != null && refused.Added == null, refused.SkipReason ?? "");
            MintedHull threw = CollisionMeshMinter.Mint(cf, sourceHash, scale,
                static (_, _) => throw new NotSupportedException("mirroring is not supported yet"));
            Check("a deriver that throws NotSupported is reported as a skip",
                threw.SkipReason == "mirroring is not supported yet", threw.SkipReason ?? "");
            MintedHull missing = CollisionMeshMinter.Mint(cf, 0xBADBADBADBADBAD1, scale, CopyBlob);
            Check("an unknown source hull is reported as a skip", missing.SkipReason != null);

            // The deriver scaling will actually use is CookedMeshScaler, and it signals a malformed blob with
            // CollisionDecodeException — which derives straight from Exception, so it slipped past a filter
            // listing only the framework types and crashed the drag instead of skipping the hull.
            MintedHull decodeThrew = CollisionMeshMinter.Mint(cf, sourceHash, scale,
                static (_, _) => throw new CollisionDecodeException("cooked mesh is truncated"));
            Check("a deriver that throws CollisionDecodeException is reported as a skip",
                decodeThrew.SkipReason == "cooked mesh is truncated" && decodeThrew.Added == null,
                decodeThrew.SkipReason ?? "");

            // The same thing end to end, with the real scaler over a deliberately truncated hull: this is the
            // exact call the gizmo drag makes, so it must come back as a skip rather than an exception.
            var truncated = new CollisionMesh { Hash = 0xBADBADBADBADBAD2, CookedMesh = donor.CookedMesh![..64] };
            cf.Meshes.Add(truncated);
            bool skipped;
            string skipDetail;
            try
            {
                MintedHull real = CollisionMeshMinter.Mint(cf, truncated.Hash, scale,
                    static (blob, v) => CookedMeshScaler.Scale(blob, v.X));
                skipped = real.SkipReason != null && real.Added == null;
                skipDetail = real.SkipReason ?? "no skip reason";
            }
            catch (Exception ex) { skipped = false; skipDetail = "THREW " + ex.GetType().Name; }
            cf.Meshes.Remove(truncated);
            Check("the real scaler on a truncated hull skips instead of throwing", skipped, skipDetail);

            // --- A minted hull survives the .col round-trip (persistence needs no new code).
            cf.Meshes.Add(first.Added);
            cf.Instances.Add(placed);
            string roundTrip = Path.Combine(Path.GetTempPath(), "illusion_collision_mint_roundtrip.col");
            File.WriteAllBytes(roundTrip, cf.ToBytes());
            CollisionFile reloaded = CollisionFile.Load(roundTrip);
            File.Delete(roundTrip);
            CollisionMesh? back = reloaded.Meshes.FirstOrDefault(m => m.Hash == first.Hash);
            Check("minted hull survives save+reload",
                back?.CookedMesh is { Length: > 0 } && back.Sections.Count == donor.Sections.Count
                && reloaded.Instances.Any(i => i.Hash == first.Hash),
                back == null ? "missing" : $"{back.CookedMesh!.Length} bytes, {back.Sections.Count} sections");

            sb.AppendLine();
            sb.AppendLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            sb.Insert(0, $"COLLISION MINT PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Applying a previewed hull resize to the FILE (S4): CollisionMintEdit adds the rescaled hull, repoints the
    // placement and resets the preview, and undo puts all of it back. Drives the real edit (not a copy of its
    // logic) through a stand-in sink, on a throwaway copy of a district's .col.
    //
    // The load-bearing assertion is byte identity after undo: a resize that cannot be taken back out exactly
    // leaves the .col permanently carrying a hull nothing references. Whole-file integrity is asserted too — the
    // saved file must re-parse to its exact length and every OTHER mesh must still decode, so an edit that
    // corrupts a neighbour cannot pass by only checking the hull it minted.
    // Output: %TEMP%\illusion_collision_scale_apply.txt
    internal static void RunCollisionScaleApplyProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_scale_apply.txt");
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
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"COLLISION SCALE-APPLY PROBE — district={district}");
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            byte[] originalBytes = File.ReadAllBytes(colPath);
            CollisionFile cf = CollisionFile.Load(colPath);

            // A placement whose hull can actually be rescaled — most can, but a probe should pick, not hope.
            // Prefer one whose hull is SHARED with another placement: sharing is the norm in shipped data
            // (7800 hulls across 26116 placements) and it is the case where mint-once/repoint-twice and the
            // orphan check have to cooperate, so the group assertions below should not be left unexercised.
            var placementsPerHash = new Dictionary<ulong, int>();
            foreach (CollisionInstance inst in cf.Instances)
                placementsPerHash[inst.Hash] = placementsPerHash.GetValueOrDefault(inst.Hash) + 1;

            CollisionInstance? target = null;
            foreach (CollisionInstance inst in cf.Instances.OrderByDescending(i => placementsPerHash[i.Hash]))
            {
                CollisionMesh? m = cf.Meshes.FirstOrDefault(x => x.Hash == inst.Hash);
                if (m?.CookedMesh is not { Length: > 0 }) continue;
                try { CookedMeshScaler.Scale(m.CookedMesh, new Vector3(2f, 4f, 0.5f)); } catch { continue; }
                target = inst;
                break;
            }
            if (target == null) { sb.AppendLine("no rescalable placement"); return; }
            sb.AppendLine($"target hull 0x{target.Hash:X16} — {placementsPerHash[target.Hash]} placement(s)");

            var doc = new CollisionDocumentAdapter(cf, new FileInfo(sds));
            CollisionInstanceAdapter adapter = doc.Node(target);
            var layer = new SceneNode("Collisions", "CollisionLayer", false) { Source = doc };
            var node = new SceneNode("placement", "CollisionInstance", false) { Source = adapter };
            layer.AddChild(node);

            var sink = new RecordingCollisionSink();
            ulong sourceHash = target.Hash;
            int meshes0 = cf.Meshes.Count;
            var scale = new Vector3(2f, 4f, 0.5f);   // uneven and exact in floats — a uniform factor would not exercise the per-axis path

            // The gizmo parks the drag on the preview; the edit is what makes it real.
            adapter.PreviewScale = scale;
            MintedHull minted = CollisionMeshMinter.Mint(cf, sourceHash, scale,
                static (blob, s) => CookedMeshScaler.Scale(blob, s));
            Check("the hull minted", minted.SkipReason == null && minted.Added != null, minted.SkipReason ?? "");
            if (minted.Added == null) return;

            var edit = new CollisionMintEdit(
                sink, doc, layer, node, adapter, sourceHash, minted.Hash, minted.Added, scale);
            edit.Redo();

            Check("the placement points at the minted hull", target.Hash == minted.Hash);
            Check("the preview is reset (the hull carries the size now)", adapter.PreviewScale == Vector3.One);
            Check("the hull was added once", cf.Meshes.Count == meshes0 + 1);
            Check("the mesh list stayed hash-ascending", IsHashAscending(cf));
            Check("the document was enlisted for save and the overlay flagged",
                sink.Enlisted == 1 && doc.RenderDirty);
            Check("the minted hull resolves through the document index", doc.MeshFor(minted.Hash) != null);

            // Scaling the same hull the same way again must reuse it rather than grow the file every drag.
            MintedHull twice = CollisionMeshMinter.Mint(cf, sourceHash, scale,
                static (blob, s) => CookedMeshScaler.Scale(blob, s));
            Check("a second identical resize dedupes", twice.Hash == minted.Hash && twice.Added == null);

            // The saved file must re-parse completely, and the edit must not have disturbed any other hull.
            string savedPath = Path.Combine(Path.GetTempPath(), "illusion_collision_scale_apply.col");
            byte[] saved = cf.ToBytes();
            File.WriteAllBytes(savedPath, saved);
            CollisionFile reloaded = CollisionFile.Load(savedPath);
            CollisionMesh? back = reloaded.Meshes.FirstOrDefault(m => m.Hash == minted.Hash);
            Check("the saved .col carries the minted hull", back?.CookedMesh is { Length: > 0 });
            Check("the saved .col re-serializes to the same length",
                reloaded.ToBytes().Length == saved.Length, $"{saved.Length} bytes");
            int undecodable = 0;
            foreach (CollisionMesh m in reloaded.Meshes)
            {
                if (m.CookedMesh is not { Length: > 0 } blob) continue;
                try { CookedTriangleMesh.Decode(blob); } catch { undecodable++; }
            }
            Check("every mesh in the edited file still decodes", undecodable == 0, $"{undecodable} bad");

            // The scaled hull is REALLY scaled — a structural check would pass on a blob that never moved.
            CookedTriangleMesh before = CookedTriangleMesh.Decode(
                cf.Meshes.First(m => m.Hash == sourceHash).CookedMesh!);
            CookedTriangleMesh after = CookedTriangleMesh.Decode(back!.CookedMesh!);
            Check("the minted hull's geometry is the source scaled per axis",
                after.Vertices.Length == before.Vertices.Length
                && after.Vertices[0] == before.Vertices[0] * scale,
                $"{before.Vertices[0]} -> {after.Vertices[0]}");

            // --- Undo: the .col must come back byte-identical, which is what makes a resize safe to try.
            edit.Undo();
            Check("undo repoints the placement back", target.Hash == sourceHash);
            Check("undo restores the previewed scale", adapter.PreviewScale == scale);
            Check("undo collects the orphaned hull", cf.Meshes.Count == meshes0);
            Check("the collected hull stops resolving", doc.MeshFor(minted.Hash) == null);
            byte[] afterUndo = cf.ToBytes();
            Check("undo leaves the .col BYTE-IDENTICAL to the original",
                afterUndo.AsSpan().SequenceEqual(originalBytes),
                $"{afterUndo.Length} vs {originalBytes.Length} bytes, first diff at {FirstDiff(afterUndo, originalBytes)}");

            // --- Redo has to land back on the minted state, or a Ctrl+Y after a Ctrl+Z resizes nothing.
            edit.Redo();
            Check("redo re-adds the hull and repoints", target.Hash == minted.Hash && cf.Meshes.Count == meshes0 + 1);
            Check("redo resets the preview again", adapter.PreviewScale == Vector3.One);
            edit.Undo();

            // --- Two placements of ONE hull, scaled together: mint once, repoint twice, and undo in reverse
            // --- must not collect the hull until the LAST reference is gone.
            CollisionInstance? sibling = cf.Instances.FirstOrDefault(i => !ReferenceEquals(i, target) && i.Hash == sourceHash);
            if (sibling != null)
            {
                CollisionInstanceAdapter siblingAdapter = doc.Node(sibling);
                var siblingNode = new SceneNode("sibling", "CollisionInstance", false) { Source = siblingAdapter };
                layer.AddChild(siblingNode);

                MintedHull first = CollisionMeshMinter.Mint(cf, sourceHash, scale,
                    static (blob, s) => CookedMeshScaler.Scale(blob, s));
                var e1 = new CollisionMintEdit(sink, doc, layer, node, adapter,
                    sourceHash, first.Hash, first.Added, scale);
                e1.Redo();
                MintedHull second = CollisionMeshMinter.Mint(cf, sourceHash, scale,
                    static (blob, s) => CookedMeshScaler.Scale(blob, s));
                var e2 = new CollisionMintEdit(sink, doc, layer, siblingNode, siblingAdapter,
                    sourceHash, second.Hash, second.Added, scale);
                e2.Redo();

                Check("a group resize of one hull mints exactly one mesh",
                    cf.Meshes.Count == meshes0 + 1 && second.Added == null);
                Check("both placements point at it", target.Hash == first.Hash && sibling.Hash == first.Hash);

                e2.Undo();
                Check("undoing one of two placements keeps the hull alive", cf.Meshes.Count == meshes0 + 1);
                e1.Undo();
                Check("undoing the last one collects it", cf.Meshes.Count == meshes0);
                Check("the group resize also undoes byte-identically",
                    cf.ToBytes().AsSpan().SequenceEqual(originalBytes));
            }
            else
            {
                sb.AppendLine("(no second placement of the same hull in this district — group case skipped)");
            }

            File.Delete(savedPath);
            sb.AppendLine();
            sb.AppendLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            sb.Insert(0, $"COLLISION SCALE-APPLY PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Removing the hulls nothing references (P2). Sweeping is only safe because every shipped placement resolves
    // inside its own file — measured by --probe-collision-census — so anything unplaced was orphaned by this
    // toolkit. Asserts the sweep takes exactly the unplaced set, leaves placements alone (the ray-picker pairs
    // placements with tree nodes by index, so touching that list would mis-resolve later picks), and that undo
    // restores the ORIGINAL mesh order, not just the original contents.
    // Output: %TEMP%\illusion_collision_orphan.txt
    internal static void RunCollisionOrphanProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_orphan.txt");
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
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"COLLISION ORPHAN PROBE — district={district}");
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            CollisionFile cf = CollisionFile.Load(colPath);
            var doc = new CollisionDocumentAdapter(cf, new FileInfo(sds));
            var layer = new SceneNode("Collisions", "CollisionLayer", false) { Source = doc };
            var sink = new RecordingCollisionSink();

            // Shipped data has no unplaced hulls at all, so there must be nothing to sweep before we make some.
            Check("a shipped .col has no unused hulls", CollisionOrphanEdit.Build(sink, doc, layer) == null);

            byte[] originalBytes = cf.ToBytes();
            int meshes0 = cf.Meshes.Count;
            int instances0 = cf.Instances.Count;
            CollisionMesh donor = cf.Meshes.First(m => m.CookedMesh is { Length: > 0 });

            // Three orphans at spread-out positions (front, middle, back) so a sweep that removes ascending —
            // shifting the indices still to be removed — cannot pass by luck.
            var orphanHashes = new ulong[] { 0x0000000000000001UL, 0x8000000000000000UL, 0xFFFFFFFFFFFFFFF0UL };
            foreach (ulong h in orphanHashes)
            {
                var mesh = new CollisionMesh { Hash = h, CookedMesh = (byte[])donor.CookedMesh!.Clone() };
                int i = 0;
                while (i < cf.Meshes.Count && cf.Meshes[i].Hash <= h) i++;
                cf.Meshes.Insert(i, mesh);
            }
            doc.InvalidateMeshIndex();
            byte[] withOrphans = cf.ToBytes();

            CollisionOrphanEdit? edit = CollisionOrphanEdit.Build(sink, doc, layer);
            Check("the sweep finds exactly the unplaced hulls", edit is { Count: 3 }, $"{edit?.Count ?? 0} found");
            if (edit == null) return;

            edit.Redo();
            Check("only the unplaced hulls are gone", cf.Meshes.Count == meshes0);
            Check("no placed hull was taken with them",
                cf.Instances.All(i => cf.Meshes.Any(m => m.Hash == i.Hash)));
            Check("placements were not touched", cf.Instances.Count == instances0);
            Check("the swept hulls stop resolving", orphanHashes.All(h => doc.MeshFor(h) == null));
            Check("the sweep enlisted the .col and flagged the overlay", sink.Enlisted >= 1 && doc.RenderDirty);
            Check("the file is back to its original bytes",
                cf.ToBytes().AsSpan().SequenceEqual(originalBytes));

            edit.Undo();
            Check("undo restores every swept hull", cf.Meshes.Count == meshes0 + 3);
            Check("undo restores their exact positions — not just their presence",
                cf.ToBytes().AsSpan().SequenceEqual(withOrphans));
            Check("restored hulls resolve again", orphanHashes.All(h => doc.MeshFor(h) != null));

            edit.Redo();
            Check("redo sweeps again", cf.Meshes.Count == meshes0);
            Check("a second sweep has nothing left to do", CollisionOrphanEdit.Build(sink, doc, layer) == null);

            sb.AppendLine();
            sb.AppendLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            sb.Insert(0, $"COLLISION ORPHAN PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static bool IsHashAscending(CollisionFile file)
    {
        for (int i = 1; i < file.Meshes.Count; i++)
            if (file.Meshes[i].Hash < file.Meshes[i - 1].Hash) return false;
        return true;
    }

    // Stands in for the app around an edit: counts the save enlistments so the probe can assert the .col really
    // gets enlisted, without a window, a renderer or a persistence stack.
    private sealed class RecordingCollisionSink : ICollisionEditSink
    {
        public int Enlisted;
        public int Refreshed;
        public void Enlist(SceneNode layer) => Enlisted++;
        public void Refresh() => Refreshed++;
    }

    // Collision mesh-set invalidation: an edit can ADD a hull to the .col (a scaled copy, once scaling lands), not
    // just move a placement. The cached decode would not contain it, and RebuildInstances iterates that cache — so
    // the new hull's placements would silently vanish from the overlay, from picking and from the selection
    // highlight, which all read the same cache. Proves CoversPlacedMeshes detects exactly that, that a re-Build
    // fixes it, and that an undecodable or unplaced mesh does NOT trip the check (which would ask for a rebuild on
    // every frame forever). In-memory only. Output: %TEMP%\illusion_collision_meshset.txt
    internal static void RunCollisionMeshSetProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_meshset.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"COLLISION MESH-SET PROBE — district={district}");
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            CollisionFile cf = CollisionFile.Load(colPath);
            CollisionMesh? donor = cf.Meshes.FirstOrDefault(m => m.CookedMesh is { Length: > 0 });
            if (donor == null || cf.Instances.Count == 0) { sb.AppendLine("no usable mesh/instance"); return; }

            CollisionRenderData decoded = CollisionSceneBuilder.Build(cf);
            int meshes0 = decoded.Meshes.Length;
            bool coversBefore = CollisionSceneBuilder.CoversPlacedMeshes(decoded, cf);
            sb.AppendLine($"baseline: {cf.Meshes.Count} meshes in .col, {meshes0} decoded, covers={coversBefore}");

            // --- Add a hull the cache cannot know about, and place it. This is exactly what minting a
            // --- scaled copy will do.
            const ulong mintedHash = 0xC0111DE500000001UL;
            var minted = new CollisionMesh { Hash = mintedHash, CookedMesh = (byte[])donor.CookedMesh!.Clone() };
            foreach (CollisionSection s in donor.Sections)
                minted.Sections.Add(new CollisionSection { Start = s.Start, NumEdges = s.NumEdges, Material = s.Material, Unk2 = s.Unk2 });
            cf.Meshes.Add(minted);
            cf.Instances.Add(new CollisionInstance
            {
                Position = cf.Instances[0].Position + new Vector3(5f, 0f, 0f),
                Rotation = cf.Instances[0].Rotation,
                Hash = mintedHash,
                Unk4 = -1,
                Group = cf.Instances[0].Group,
            });

            bool coversAfter = CollisionSceneBuilder.CoversPlacedMeshes(decoded, cf);
            sb.AppendLine($"after minting a placed hull: covers={coversAfter} (must be False — this is the trigger)");

            // The stale cache really does drop it — this is the bug the trigger exists to prevent.
            CollisionRenderData staleRebuild = CollisionSceneBuilder.RebuildInstances(decoded, cf);
            bool staleDrops = staleRebuild.Meshes.All(m => m.Hash != mintedHash);
            sb.AppendLine($"stale RebuildInstances drops the minted hull = {staleDrops} (documents WHY a re-Build is required)");

            // A re-Build picks it up, and does not lose the hulls that were already there.
            CollisionRenderData rebuilt = CollisionSceneBuilder.Build(cf);
            CollisionRenderMesh? mintedRender = rebuilt.Meshes.FirstOrDefault(m => m.Hash == mintedHash);
            bool rebuildFinds = mintedRender is { Instances.Length: 1 };
            bool keptOthers = rebuilt.Meshes.Length == meshes0 + 1;
            bool coversRebuilt = CollisionSceneBuilder.CoversPlacedMeshes(rebuilt, cf);
            sb.AppendLine($"re-Build finds it (1 instance) = {rebuildFinds}; kept the other {meshes0} hulls = {keptOthers}; covers={coversRebuilt}");

            // --- The check must NOT trip on meshes Build itself skips, or the streamer would rebuild forever.
            cf.Meshes.Add(new CollisionMesh { Hash = 0xDEAD0001, CookedMesh = null });               // no blob
            cf.Meshes.Add(new CollisionMesh { Hash = 0xDEAD0002, CookedMesh = new byte[] { 1, 2 } }); // never placed
            bool stillCovered = CollisionSceneBuilder.CoversPlacedMeshes(rebuilt, cf);
            sb.AppendLine($"blob-less and unplaced meshes do not trip the check = {stillCovered} (guards against a rebuild loop)");
            cf.Meshes.RemoveAll(m => m.Hash is 0xDEAD0001 or 0xDEAD0002);

            // --- NET-ZERO EDIT: one hull added, one removed, mesh COUNT unchanged. The streamer used to decide
            // --- a full re-decode on a count change and only then consult coverage, so this shape of edit (a
            // --- shape-accept that mints and sweeps in one step) left the new hull rendering from a stale cache
            // --- forever. Coverage alone must catch it.
            const ulong netZeroHash = 0xC0111DE500000002UL;
            var swept = cf.Meshes.First(m => m.Hash == mintedHash);
            cf.Meshes.Remove(swept);
            cf.Instances.RemoveAll(i => i.Hash == mintedHash);
            var netZero = new CollisionMesh { Hash = netZeroHash, CookedMesh = (byte[])donor.CookedMesh!.Clone() };
            cf.Meshes.Add(netZero);
            cf.Instances.Add(new CollisionInstance
            {
                Position = cf.Instances[0].Position + new Vector3(9f, 0f, 0f),
                Rotation = cf.Instances[0].Rotation,
                Hash = netZeroHash,
                Unk4 = -1,
                Group = cf.Instances[0].Group,
            });
            bool netZeroSameCount = cf.Meshes.Count == rebuilt.Meshes.Length;
            bool netZeroDetected = !CollisionSceneBuilder.CoversPlacedMeshes(rebuilt, cf);
            sb.AppendLine($"net-zero add+remove keeps the mesh count equal = {netZeroSameCount}; " +
                          $"coverage still detects it = {netZeroDetected} (the count guard alone would not)");

            // --- MeshFor invalidation, both directions. The index is lazy and never rebuilt on its own, so an
            // --- edit that appends leaves it stale-negative and one that removes leaves it stale-POSITIVE —
            // --- the second being the dangerous one, since the property panel's dangling-hash guard trusts it.
            var doc = new CollisionDocumentAdapter(cf, new FileInfo(sds));
            bool seesExisting = doc.MeshFor(donor.Hash) != null;          // builds the index
            const ulong lateHash = 0xC0111DE500000003UL;
            cf.Meshes.Add(new CollisionMesh { Hash = lateHash, CookedMesh = (byte[])donor.CookedMesh!.Clone() });
            bool staleNegative = doc.MeshFor(lateHash) == null;           // the bug, while the index is stale
            doc.InvalidateMeshIndex();
            bool seesAppended = doc.MeshFor(lateHash) != null;
            CollisionMeshMinter.RemoveMesh(cf, lateHash);
            bool stalePositive = doc.MeshFor(lateHash) != null;           // resolves a hull the file no longer has
            doc.InvalidateMeshIndex();
            bool forgetsRemoved = doc.MeshFor(lateHash) == null;
            sb.AppendLine($"MeshFor: baseline={seesExisting}; stale after append={staleNegative}→invalidate={seesAppended}; " +
                          $"stale after remove={stalePositive}→invalidate={forgetsRemoved}");

            sb.AppendLine();
            sb.AppendLine(coversBefore && !coversAfter && staleDrops && rebuildFinds && keptOthers
                && coversRebuilt && stillCovered && netZeroSameCount && netZeroDetected
                && seesExisting && seesAppended && forgetsRemoved ? "RESULT: PASS" : "RESULT: FAIL");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Collision instance editing (Phase 2): wraps a district's .col in a CollisionDocumentAdapter, resolves a
    // placement to its CollisionInstanceAdapter, and drives the CollisionPropertyCatalog descriptors the property
    // panel binds to — proving the get/set delegates read/write the underlying CollisionInstance (Position,
    // Rotation deg↔rad, Hash, Group, Unk4), the adapter is identity-cached, and its world matrix matches the
    // render convention. In-memory only. Output: %TEMP%\illusion_collision_edit.txt
    internal static void RunCollisionEditProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_edit.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"COLLISION EDIT PROBE — district={district}");
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            CollisionFile file = CollisionFile.Load(colPath);
            if (file.Instances.Count == 0) { sb.AppendLine("no instances"); return; }
            var doc = new CollisionDocumentAdapter(file, new FileInfo(sds));
            CollisionInstance inst = file.Instances[0];
            CollisionInstanceAdapter adapter = doc.Node(inst);
            bool identity = ReferenceEquals(adapter, doc.Node(inst));

            var byId = new Dictionary<string, PropertyDescriptor>();
            foreach (PropertyGroup g in adapter.GetPropertyGroups())
                foreach (PropertyDescriptor d in g.Properties) byId[d.Id] = d;
            // Position/Rotation are now edited via the Object (transform) tab (IFrameNode); the catalog contributes
            // only the collision metadata.
            string[] expect = { "Collision.Hash", "Collision.Group", "Collision.Unk4" };
            bool haveAll = expect.All(byId.ContainsKey);
            if (!haveAll) { sb.AppendLine("MISSING descriptors: " + string.Join(",", expect.Where(e => !byId.ContainsKey(e)))); }

            bool metaOk = false, hashGuardOk = false;
            if (haveAll)
            {
                byId["Collision.Group"].Set!((long)42);
                byId["Collision.Unk4"].Set!((long)7);

                // A hash that names a mesh present in this .col is accepted...
                ulong realHash = file.Meshes.Count > 0 ? file.Meshes[^1].Hash : inst.Hash;
                byId["Collision.Hash"].Set!(realHash);
                metaOk = inst.Group == 42 && inst.Unk4 == 7 && inst.Hash == realHash;

                // ...while a dangling one is refused. Accepting it would make the hull vanish from the viewport
                // (builder and picker both skip unresolvable placements) and save a broken reference into the .col.
                byId["Collision.Hash"].Set!(0xDEADBEEFUL);
                hashGuardOk = inst.Hash == realHash;
            }

            // IFrameNode transform write-back (the gizmo / numeric-field path): set a world matrix carrying a
            // scale, and confirm the position + euler round-trip through the instance while the scale is dropped.
            IFrameNode fn = adapter;
            var setPos = new Vector3(11f, 22f, 33f);
            var setRot = new Vector3(0.3f, -0.5f, 1.1f); // radians
            Quaternion setQuat = TransformMath.CollisionEulerToQuaternion(setRot);
            fn.LocalTransform = TransformMath.Compose(setQuat, new Vector3(2.5f), setPos); // 2.5x scale must be ignored
            bool xformOk = Approx(inst.Position, setPos)
                && QApprox(setQuat, TransformMath.CollisionEulerToQuaternion(inst.Rotation))
                && Approx(fn.WorldTransform.Translation, setPos);
            bool parentless = fn.Parent == null && fn.ParentWorldTransform == Matrix4x4.Identity
                && fn.WorldTransform == fn.LocalTransform;

            bool meshResolve = file.Meshes.Count == 0 || doc.MeshFor(file.Meshes[0].Hash) != null;

            sb.AppendLine($"metadata descriptors present (Hash/Group/Unk4) = {haveAll}; identity-cached adapter = {identity}");
            sb.AppendLine($"Group/Hash/Unk4 set/get = {metaOk}; dangling hash refused = {hashGuardOk}");
            sb.AppendLine($"IFrameNode LocalTransform set (scale dropped, euler round-trip) = {xformOk}; parentless world==local = {parentless}");
            sb.AppendLine($"MeshFor resolves = {meshResolve}; doc ObjectCount={doc.ObjectCount} GeometryCount={doc.GeometryCount} TypeName={adapter.TypeName}");
            sb.AppendLine();
            sb.AppendLine(haveAll && identity && metaOk && hashGuardOk && xformOk && parentless && meshResolve
                ? "RESULT: PASS" : "RESULT: FAIL");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Collision viewport picking (Phase 2): verifies the CPU ray-cast geometry — for each placement, aim a ray
    // straight at its first triangle (along that triangle's normal) and confirm the transformed-triangle test
    // reports a hit. This exercises the exact composition the viewport pick uses (instance world matrix in the
    // collision convention + Picking.IntersectTriangle), catching a wrong axis/rotation that would make clicks
    // miss the hull. Output: %TEMP%\illusion_collision_pick.txt
    internal static void RunCollisionPickProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_collision_pick.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"COLLISION PICK PROBE — district={district}");
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            CollisionFile file = CollisionFile.Load(colPath);
            CollisionRenderData data = CollisionSceneBuilder.Build(file);
            var geom = new Dictionary<ulong, CollisionRenderMesh>();
            foreach (CollisionRenderMesh m in data.Meshes) geom[m.Hash] = m;

            int tested = 0, hits = 0, misses = 0;
            foreach (CollisionInstance inst in file.Instances)
            {
                if (tested >= 300) break;
                if (!geom.TryGetValue(inst.Hash, out CollisionRenderMesh? mesh) || mesh.Indices.Length < 3) continue;
                Matrix4x4 world = TransformMath.Compose(
                    TransformMath.CollisionEulerToQuaternion(inst.Rotation), Vector3.One, inst.Position);
                Vector3[] pos = mesh.Positions;
                uint[] idx = mesh.Indices;

                Vector3 t0 = Vector3.Transform(pos[idx[0]], world);
                Vector3 t1 = Vector3.Transform(pos[idx[1]], world);
                Vector3 t2 = Vector3.Transform(pos[idx[2]], world);
                Vector3 nrm = Vector3.Cross(t1 - t0, t2 - t0);
                if (nrm.LengthSquared() < 1e-10f) continue; // degenerate first triangle — skip
                nrm = Vector3.Normalize(nrm);
                float dist = MathF.Max(2f, (mesh.LocalMax - mesh.LocalMin).Length());
                Vector3 target = (t0 + t1 + t2) / 3f;
                Vector3 camPos = target + nrm * dist;
                Vector3 dir = Vector3.Normalize(target - camPos); // = -nrm, straight at the triangle

                bool hit = false;
                for (int k = 0; k + 2 < idx.Length; k += 3)
                {
                    Vector3 a = Vector3.Transform(pos[idx[k]], world);
                    Vector3 b = Vector3.Transform(pos[idx[k + 1]], world);
                    Vector3 c = Vector3.Transform(pos[idx[k + 2]], world);
                    if (Picking.IntersectTriangle(camPos, dir, a, b, c, out _)) { hit = true; break; }
                }

                tested++;
                if (hit) hits++;
                else if (++misses <= 3) sb.AppendLine($"  MISS hash={inst.Hash:X16} tris={idx.Length / 3}");
            }

            sb.AppendLine($"aimed rays at {tested} placements: {hits} hit, {misses} missed");
            sb.AppendLine();
            sb.AppendLine(tested > 0 && misses == 0 ? "RESULT: PASS" : "RESULT: FAIL");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static bool ByteEqual(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static bool InstanceEqual(CollisionInstance a, CollisionInstance b) =>
        a.Position == b.Position && a.Rotation == b.Rotation && a.Hash == b.Hash && a.Unk4 == b.Unk4 && a.Group == b.Group;

    // Dump of district scenes: categories of scene-roots (proxy/snow/normal) and mesh examples.
    internal static void RunScenesProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_scenes.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            MapCatalog map = MapCatalog.Build(MafiaEnvironment.CityFolder, f => SdsMeshLoader.EnsureExtracted(f));
            var districtNames = map.Areas.Select(a => a.BaseName).ToList();
            (List<SdsFrameNode> roots, var meshes, _) = SdsMeshLoader.LoadHierarchy(new FileInfo(sds), districtNames);
            sb.AppendLine($"{district}.sds: {roots.Count} scene-roots, {meshes.Count} meshes\n");

            foreach (SdsFrameNode r in roots)
            {
                var names = new List<string>();
                int total = CollectMeshNames(r, names);
                sb.AppendLine($"[{r.Category,-6}] {r.Kind} '{r.Name}' — {total} meshes; e.g. {string.Join(", ", names.Take(8))}");
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static int CollectMeshNames(SdsFrameNode n, List<string> names)
    {
        int total = 0;
        if (n.Mesh != null) { total++; if (names.Count < 8) names.Add(n.Name); }
        foreach (SdsFrameNode c in n.Children) total += CollectMeshNames(c, names);
        return total;
    }

    // Investigation: link FrameNameTable.Data.Flags → frame objects (via FrameIndex) and correlate the flag
    // bits with the current NAME-based proxy/snow detection. Self-verifies the FrameIndex→object mapping by
    // the entry-name vs object-name match rate. Loads summer + winter of one or all districts and aggregates
    // a flag×category cross-tab, so we can decide whether flags are a reliable proxy/winter signal.
    internal static void RunFlagsProbe(string? districtArg)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_flags.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            MapCatalog map = MapCatalog.Build(MafiaEnvironment.CityFolder, f => SdsMeshLoader.EnsureExtracted(f));

            // Which districts to inspect: the arg (summer+winter), else every district in the catalog.
            var targets = new List<(FileInfo file, string label)>();
            if (districtArg != null)
            {
                string s = Path.Combine(MafiaEnvironment.CityFolder, districtArg + ".sds");
                if (File.Exists(s)) targets.Add((new FileInfo(s), districtArg + " (summer)"));
                string z = Path.Combine(MafiaEnvironment.CityFolder, districtArg + "_z.sds");
                if (File.Exists(z)) targets.Add((new FileInfo(z), districtArg + " (winter)"));
            }
            else
            {
                foreach (MapArea a in map.Areas)
                {
                    targets.Add((a.Summer, a.BaseName + " (summer)"));
                    if (a.Winter != null) targets.Add((a.Winter, a.BaseName + " (winter)"));
                }
            }

            // Global aggregate: flags-value → counts split by name-based category.
            var agg = new SortedDictionary<int, int[]>(); // [proxyByName, snowByName, normalByName]
            var flagSamples = new Dictionary<int, List<string>>();
            int totalEntries = 0, totalMatched = 0;

            foreach ((FileInfo file, string label) in targets)
            {
                string extracted = SdsMeshLoader.EnsureExtracted(file);
                ExtractedSds scene = SdsMeshLoader.OpenScene(extracted);
                var fnt = scene.FrameNameTable;
                var fr = scene.FrameResource;
                if (fnt?.FrameData == null || fr == null) { sb.AppendLine($"[{label}] no FrameNameTable/FrameResource"); continue; }

                int entries = fnt.FrameData.Length, matched = 0;
                // Per-district flag histogram (only entries whose FrameIndex→object name matches).
                var local = new SortedDictionary<int, int>();
                foreach (var d in fnt.FrameData)
                {
                    int flags = (int)d.Flags;
                    FrameObjectBase? obj = null;
                    try { obj = fr.GetObjectFromIndex(d.FrameIndex); } catch { }
                    string objName = obj?.Name?.ToString() ?? "";
                    bool nameMatch = obj != null && string.Equals(objName, d.Name, StringComparison.Ordinal);
                    if (nameMatch) matched++;

                    // Classify the entry name (same heuristics as SdsMeshLoader).
                    string nm = d.Name ?? "";
                    int cat = NameIsProxy(nm) ? 0 : NameIsSnow(nm) ? 1 : 2;

                    local.TryGetValue(flags, out int lc); local[flags] = lc + 1;
                    if (!agg.TryGetValue(flags, out int[]? row)) { row = new int[3]; agg[flags] = row; }
                    row[cat]++;

                    if (!flagSamples.TryGetValue(flags, out List<string>? s)) { s = new List<string>(); flagSamples[flags] = s; }
                    if (s.Count < 10) s.Add($"{nm}{(nameMatch ? "" : $" (INDEX-MISMATCH→'{objName}')")}");
                }
                totalEntries += entries; totalMatched += matched;
                string hist = string.Join(", ", local.Select(kv => $"{FlagStr(kv.Key)}={kv.Value}"));
                sb.AppendLine($"[{label}] entries={entries} nameMatch={matched}/{entries} | {hist}");
            }

            // Header: linking correctness + the decisive cross-tab.
            var head = new StringBuilder();
            head.AppendLine($"FLAGS PROBE — districts={targets.Count}, entries={totalEntries}, " +
                            $"FrameIndex→object name-match={totalMatched}/{totalEntries} " +
                            $"({(totalEntries > 0 ? 100.0 * totalMatched / totalEntries : 0):F1}%)\n");
            head.AppendLine("CROSS-TAB  flags → [proxy-by-name | snow-by-name | normal-by-name]");
            foreach (var kv in agg)
            {
                int[] r = kv.Value;
                head.AppendLine($"  {FlagStr(kv.Key),-18} proxy={r[0],-6} snow={r[1],-6} normal={r[2],-6} (total {r[0] + r[1] + r[2]})");
            }
            head.AppendLine("\nSAMPLES per flag value:");
            foreach (var kv in flagSamples.OrderBy(k => k.Key))
                head.AppendLine($"  {FlagStr(kv.Key)}: {string.Join(", ", kv.Value)}");
            head.AppendLine();
            sb.Insert(0, head.ToString());
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Human-readable flag bits, e.g. 3 → "flag_1|flag_2".
    private static string FlagStr(int flags)
    {
        if (flags == 0) return "0";
        var parts = new List<string>();
        for (int b = 0; b < 16; b++) if ((flags & (1 << b)) != 0) parts.Add("flag_" + (1 << b));
        return string.Join("|", parts);
    }

    private static bool NameIsProxy(string nm) =>
        nm.Contains("proxy", StringComparison.OrdinalIgnoreCase)
        || (nm.Length > 4 && nm.StartsWith("city", StringComparison.OrdinalIgnoreCase) && char.IsDigit(nm[4]));

    private static bool NameIsSnow(string nm) =>
        nm.Length >= 2 && (nm[0] == 'z' || nm[0] == 'Z') && char.IsDigit(nm[1]);

    // Flag semantics (see SdsMeshLoader): normal = no flags (0); snow/winter = flag_1|flag_2 (value 3);
    // proxy = any other non-zero combination (flag_2 cityNN + neighbor/LOD proxies with assorted flag_1|… bits).
    private const int SnowFlags = 3; // flag_1 | flag_2
    private static bool IsSnowFlag(int flags) => flags == SnowFlags;
    private static bool IsProxyFlag(int flags) => flags != 0 && flags != SnowFlags;

    // Structural probe: after the loader links name-table flags onto objects, dump per-scene-folder how many proxy/snow
    // objects live there and whether flagged objects have children (→ whether classification must cascade to
    // leaves). Answers: are proxies/snow grouped into their own scene folders, or mixed with normal geometry?
    internal static void RunFlagTreeProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_flagtree.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = Path.Combine(MafiaEnvironment.CityFolder, district + ".sds");
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            ExtractedSds scene = SdsMeshLoader.OpenScene(extracted);
            FrameResource? fr = scene.FrameResource;
            if (fr == null) { sb.AppendLine("no FrameResource"); return; }

            // Cascade check: among objects that carry a proxy/snow flag, how many have children?
            int flaggedProxy = 0, flaggedSnow = 0, flaggedProxyWithKids = 0, flaggedSnowWithKids = 0;
            foreach (var pair in fr.FrameObjects)
            {
                if (pair.Value is not FrameObjectBase o) continue;
                int f = (int)o.FrameNameTableFlags;
                if (IsProxyFlag(f)) { flaggedProxy++; if (o.Children.Count > 0) flaggedProxyWithKids++; }
                if (IsSnowFlag(f)) { flaggedSnow++; if (o.Children.Count > 0) flaggedSnowWithKids++; }
            }
            sb.AppendLine($"{district}.sds — flaggedProxy={flaggedProxy} (withChildren={flaggedProxyWithKids}), " +
                          $"flaggedSnow={flaggedSnow} (withChildren={flaggedSnowWithKids})");
            sb.AppendLine("(withChildren>0 means classification must cascade to descendant leaves)\n");

            // Per-scene-folder breakdown: subtree size + proxy/snow flagged counts (self + descendants).
            sb.AppendLine("SCENE FOLDERS  [name — subtreeObjs / proxyFlagged / snowFlagged / normal]");
            if (fr.FrameScenes != null)
            {
                foreach (var s in fr.FrameScenes.Values)
                {
                    int objs = 0, px = 0, sn = 0;
                    foreach (FrameObjectBase c in s.Children) CountFlags(c, ref objs, ref px, ref sn);
                    int normal = objs - px - sn;
                    string tag = px > sn && px * 2 >= objs ? "PROXY" : sn * 2 >= objs ? "SNOW" : "normal";
                    sb.AppendLine($"  [{tag,-6}] '{s.Name}' — {objs} / px={px} / sn={sn} / norm={normal}");
                }
            }

            // Roots not under any scene folder (ParentIndex1 == -1 and not a scene child).
            sb.AppendLine("\nSAMPLE proxy-flagged (flag_2) objects with their child counts:");
            int shown = 0;
            foreach (var pair in fr.FrameObjects)
            {
                if (shown >= 12) break;
                if (pair.Value is not FrameObjectBase o || !IsProxyFlag((int)o.FrameNameTableFlags)) continue;
                sb.AppendLine($"  '{o.Name}' children={o.Children.Count} parent1={o.ParentIndex1.Index}");
                shown++;
            }
            sb.AppendLine("\nSAMPLE snow-flagged (flag_1|flag_2) objects with their child counts:");
            shown = 0;
            foreach (var pair in fr.FrameObjects)
            {
                if (shown >= 12) break;
                if (pair.Value is not FrameObjectBase o || !IsSnowFlag((int)o.FrameNameTableFlags)) continue;
                sb.AppendLine($"  '{o.Name}' children={o.Children.Count} parent1={o.ParentIndex1.Index}");
                shown++;
            }
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    private static void CountFlags(FrameObjectBase n, ref int objs, ref int px, ref int sn)
    {
        objs++;
        int f = (int)n.FrameNameTableFlags;
        if (IsProxyFlag(f)) px++;
        else if (IsSnowFlag(f)) sn++;
        foreach (FrameObjectBase c in n.Children) CountFlags(c, ref objs, ref px, ref sn);
    }

    // Verifies the city_crash chain: Translokator (.tra) + frame_resource → how many objects resolve
    // by hash in frame, how many prototype-meshes and instances in total (for the instancing decision).
    internal static void RunCrashProbe(bool winter)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_crash.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            string name = winter ? "city_crash_z.sds" : "city_crash.sds";
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "city_crash", name));
            sb.AppendLine($"SDS: {sds.FullName}  exists={sds.Exists}");
            if (!sds.Exists) return;

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            string? tra = Directory.GetFiles(extracted, "*.tra", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"extracted: {extracted}");
            sb.AppendLine($".tra: {tra ?? "NOT FOUND"}");
            if (tra == null) return;

            var trans = new TranslokatorLoader(new FileInfo(tra));
            sb.AppendLine($"Translokator v{trans.Version}: {trans.ObjectGroups.Length} groups, bounds {trans.Bounds.Min} .. {trans.Bounds.Max}");

            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            sb.AppendLine($"FrameResource objects: {fr?.FrameObjects?.Count ?? 0}\n");

            int objs = 0, resolved = 0, noMesh = 0, totalInstances = 0, totalParts = 0;
            long totalRenderables = 0;
            int sampleShown = 0;
            foreach (ObjectGroup g in trans.ObjectGroups)
            {
                foreach (Formats.Translokator.Object obj in g.Objects)
                {
                    objs++;
                    totalInstances += obj.Instances.Count;
                    FrameObjectBase? groupRef = fr?.GetObjectByHash<FrameObjectBase>(obj.Name.Hash);
                    if (groupRef == null) continue;
                    resolved++;
                    if (!groupRef.HasMeshObject()) { noMesh++; continue; }

                    var parts = new List<(FrameObjectSingleMesh mesh, Matrix4x4 refT)>();
                    foreach (FrameObjectBase c in groupRef.Children) CollectParts(c, Matrix4x4.Identity, parts);
                    totalParts += parts.Count;
                    totalRenderables += (long)parts.Count * obj.Instances.Count;

                    if (sampleShown < 12 && parts.Count > 0 && obj.Instances.Count > 0)
                    {
                        sampleShown++;
                        Instance inst = obj.Instances[0];
                        Matrix4x4 instTRS = TransformMath.Compose(inst.Quaternion, new Vector3(inst.Scale), inst.Position);
                        Matrix4x4 world = parts[0].refT * instTRS;
                        sb.AppendLine($"[{g.ActorType}] '{obj.Name}' ({obj.Name.Hash:X}) inst={obj.Instances.Count} parts={parts.Count} " +
                                      $"mesh0='{parts[0].mesh.Name}' inst0.pos={inst.Position} scale={inst.Scale:F2} → world.T={world.Translation}");
                    }
                }
            }

            // Full loader path (as at runtime): LoadCrashHierarchy → MeshData.Instances.
            var (roots, allMeshes, _, _) = SdsMeshLoader.LoadCrashHierarchy(sds);
            int leaves = 0, instancedLeaves = 0;
            long loadedInstances = 0, instancedTris = 0;
            foreach (MeshData md in allMeshes)
            {
                leaves++;
                if (md.Instances != null && md.Instances.Length > 0)
                {
                    instancedLeaves++;
                    loadedInstances += md.Instances.Length;
                    instancedTris += (long)md.TriangleCount * md.Instances.Length;
                }
            }
            sb.AppendLine($"\nLoadCrashHierarchy: roots={roots.Count} meshLeaves={leaves} " +
                          $"instancedLeaves={instancedLeaves} instances={loadedInstances} instancedTris={instancedTris}");

            // Cell chunking (InstanceChunks): correctness invariant + per-cell stats + a simulated
            // frustum cull from three camera poses — verifies the per-cell culling path without a GPU.
            AppendChunkStats(sb, allMeshes, (trans.Bounds.Min + trans.Bounds.Max) * 0.5f);

            sb.Insert(0, $"OBJECTS: {objs} | resolved-in-frame: {resolved} | resolved-no-mesh: {noMesh} | " +
                         $"unresolved: {objs - resolved}\nINSTANCES total: {totalInstances} | prototype-parts: {totalParts} | " +
                         $"RENDERABLE draws (parts×instances): {totalRenderables}\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Chunks every instanced MeshData through InstanceChunks (as GpuMesh.Create does at runtime) and
    // reports: cell counts, instances/cell, the SUM(cell counts)==instances invariant, and how many
    // instances/triangles survive a frustum cull at three representative camera poses.
    private static void AppendChunkStats(StringBuilder sb, List<MeshData> allMeshes, Vector3 mapCentre)
    {
        int chunkMeshes = 0, totalCells = 0, minPerCell = int.MaxValue, maxPerCell = 0, ranged = 0;
        long sumCellCounts = 0;
        bool invariantOk = true;
        var chunked = new List<(MeshData Mesh, InstanceCell[] Cells)>();
        foreach (MeshData md in allMeshes)
        {
            if (md.Instances == null || md.Instances.Length == 0) continue;
            var lmin = new Vector3(float.MaxValue);
            var lmax = new Vector3(float.MinValue);
            foreach (Vector3 p in md.Positions) { lmin = Vector3.Min(lmin, p); lmax = Vector3.Max(lmax, p); }
            (Matrix4x4[] sorted, InstanceCell[] cells) =
                InstanceChunks.Build(md.Instances, lmin, lmax, md.InstanceDrawDistances);

            chunkMeshes++;
            totalCells += cells.Length;
            long cellSum = 0;
            foreach (InstanceCell c in cells)
            {
                cellSum += c.Count;
                minPerCell = Math.Min(minPerCell, (int)c.Count);
                maxPerCell = Math.Max(maxPerCell, (int)c.Count);
                if (c.DrawDistance > 0f) ranged++;
            }
            sumCellCounts += cellSum;
            if (cellSum != md.Instances.Length || sorted.Length != md.Instances.Length) invariantOk = false;
            chunked.Add((md, cells));
        }

        sb.AppendLine($"\nInstanceChunks (cell={InstanceChunks.CellSize:F0}m): meshes={chunkMeshes} cells={totalCells} " +
                      $"instances/cell min={(chunkMeshes > 0 ? minPerCell : 0)} " +
                      $"avg={(totalCells > 0 ? (double)sumCellCounts / totalCells : 0):F1} max={maxPerCell} " +
                      $"| SUM(cells)==instances: {(invariantOk ? "OK" : "FAIL")} " +
                      $"| cells carrying a draw range: {ranged}/{totalCells}");

        // Absolute Z everywhere: the Translokator bounds reach far below the surface, so their Z
        // centre (~-1259 m) is deep underground — offsetting from it would place poses under the map.
        // Street level near the city centre is around Z ≈ -20..2 (see the instance samples above).
        (string Pose, Vector3 Eye, Vector3 At)[] poses =
        {
            ("street", new Vector3(mapCentre.X, mapCentre.Y, 2f), new Vector3(mapCentre.X, mapCentre.Y + 100f, 5f)),
            ("mid-air 45deg", new Vector3(mapCentre.X, mapCentre.Y - 400f, 400f), new Vector3(mapCentre.X, mapCentre.Y, 0f)),
            ("top-down", new Vector3(mapCentre.X, mapCentre.Y, 1500f), new Vector3(mapCentre.X, mapCentre.Y + 1f, 0f)),
        };
        foreach ((string pose, Vector3 eye, Vector3 at) in poses)
        {
            var cam = new Camera { AspectRatio = 16f / 9f };
            cam.LookAt(eye, at);
            Frustum frustum = Frustum.FromMatrix(cam.ViewProjection);

            long visInst = 0, visTris = 0, allInst = 0, allTris = 0, rangedInst = 0;
            foreach ((MeshData md, InstanceCell[] cells) in chunked)
            {
                allInst += md.Instances!.Length;
                allTris += (long)md.TriangleCount * md.Instances.Length;
                foreach (InstanceCell c in cells)
                {
                    if (!frustum.Intersects(c.Min, c.Max)) continue;
                    visInst += c.Count;
                    visTris += (long)md.TriangleCount * c.Count;
                    // …and again with the per-object range the game itself draws at, which is the default.
                    if (c.DrawDistance > 0f && DistanceSqToAabb(eye, c.Min, c.Max) > c.DrawDistance * c.DrawDistance)
                    {
                        continue;
                    }
                    rangedInst += c.Count;
                }
            }
            sb.AppendLine($"  cull[{pose}]: instances {visInst}/{allInst} " +
                          $"({(allInst > 0 ? 100.0 * visInst / allInst : 0):F1}%), tris {visTris}/{allTris} " +
                          $"({(allTris > 0 ? 100.0 * visTris / allTris : 0):F1}%)" +
                          $" | with game draw range: {rangedInst} " +
                          $"({(allInst > 0 ? 100.0 * rangedInst / allInst : 0):F1}%)");
        }
    }

    // Squared distance from a point to an AABB (0 inside) — the renderer's own per-cell range test.
    private static float DistanceSqToAabb(Vector3 p, Vector3 min, Vector3 max)
    {
        float dx = MathF.Max(MathF.Max(min.X - p.X, 0f), p.X - max.X);
        float dy = MathF.Max(MathF.Max(min.Y - p.Y, 0f), p.Y - max.Y);
        float dz = MathF.Max(MathF.Max(min.Z - p.Z, 0f), p.Z - max.Z);
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    // Collects groupRef prototype-meshes with their local transform relative to the prototype root
    // (like InstanceTranslokatorPart in MafiaToolkit). The probe's manual Translokator walk cross-checks
    // SdsMeshLoader's traversal/accumulation; the matrix math itself is the shared TransformMath.
    private static void CollectParts(FrameObjectBase frame, Matrix4x4 parent,
        List<(FrameObjectSingleMesh mesh, Matrix4x4 refT)> parts)
    {
        Matrix4x4 refT = TransformMath.ComputeWorldTransform(frame.LocalTransform, parent);
        refT.M44 = 1.0f;
        if (frame is FrameObjectSingleMesh sm && sm.Geometry != null) parts.Add((sm, refT));
        foreach (FrameObjectBase c in frame.Children) CollectParts(c, refT, parts);
    }
}
