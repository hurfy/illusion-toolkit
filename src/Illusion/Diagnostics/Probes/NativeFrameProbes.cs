using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Formats.Frames;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Twin-free verification of the native frames codecs (the managed reference decoders
/// were deleted at the P6 cutover; the golden snapshot is the cross-run judge).</summary>
internal static class NativeFrameProbes
{
    /// <summary>
    /// Every .fnt of the extracted install: the native re-save must be byte-identical to the
    /// file on disk. Output: %TEMP%\illusion_native_fnt.txt
    /// </summary>
    internal static void RunFntParityProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_native_fnt.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root))
            {
                sb.AppendLine("resources not unpacked: " + root);
                return;
            }

            string[] files = Directory.GetFiles(root, "*.fnt", SearchOption.AllDirectories);
            Check("the install offers .fnt files", files.Length > 0, $"{files.Length} files");

            int fixpoint = 0;
            var details = new StringBuilder();
            foreach (string file in files)
            {
                byte[] bytes = File.ReadAllBytes(file);
                Illusion.Formats.Native.Model.NameTableModel wire =
                    Illusion.Formats.Native.Frames.NativeFrames.LoadNameTable(bytes);
                byte[] resaved = Illusion.Formats.Native.Frames.NativeFrames.SaveNameTable(wire);
                if (resaved.AsSpan().SequenceEqual(bytes)) fixpoint++;
                else details.AppendLine($"FIXPOINT DIFF {Path.GetFileName(file)}");
            }

            Check("native re-save is byte-identical to disk", fixpoint == files.Length,
                $"{fixpoint}/{files.Length}");

            if (details.Length > 0)
            {
                sb.AppendLine();
                sb.Append(details);
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"NATIVE FNT PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    /// <summary>
    /// Every extracted .fr through the native pipeline: the generation must be stable
    /// (read → write → re-read → write again, unchanged). Optional arg filters by path substring.
    /// Output: %TEMP%\illusion_native_fr.txt
    /// </summary>
    internal static void RunFrParityProbe(string? filter)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_native_fr.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root))
            {
                sb.AppendLine("resources not unpacked: " + root);
                return;
            }

            List<string> files = [.. Directory.GetFiles(root, "*.fr", SearchOption.AllDirectories)];
            if (!string.IsNullOrEmpty(filter))
            {
                files = [.. files.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase))];
            }
            Check("the install offers .fr files", files.Count > 0, $"{files.Count} files");

            int stable = 0, errors = 0;
            var details = new StringBuilder();
            foreach (string file in files)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(file);

                    var resource = new FrameResource();
                    using (var stream = new MemoryStream(bytes, writable: false))
                    {
                        resource.ReadFromFile(stream);
                    }
                    byte[] generation = resource.WriteToStream();

                    var reread = new FrameResource();
                    using (var stream = new MemoryStream(generation, writable: false))
                    {
                        reread.ReadFromFile(stream);
                    }
                    byte[] second = reread.WriteToStream();
                    if (second.AsSpan().SequenceEqual(generation)) stable++;
                    else details.AppendLine($"UNSTABLE {Path.GetFileName(file)}");
                }
                catch (Exception ex)
                {
                    errors++;
                    details.AppendLine($"ERROR {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            Check("no file errored", errors == 0, $"{errors} errors");
            Check("the native generation is stable", stable == files.Count - errors,
                $"{stable}/{files.Count}");

            if (details.Length > 0)
            {
                sb.AppendLine();
                sb.Append(details);
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sw.Stop();
        sb.AppendLine();
        sb.AppendLine($"elapsed: {sw.Elapsed.TotalSeconds:F1} s");
        sb.Insert(0, $"NATIVE FR PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    /// <summary>
    /// Every extracted .ibp/.vbp through the native codec: the re-save must be byte-identical
    /// to the file on disk. Output: %TEMP%\illusion_native_pools.txt
    /// </summary>
    internal static void RunPoolParityProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_native_pools.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root))
            {
                sb.AppendLine("resources not unpacked: " + root);
                return;
            }

            string[] ibps = Directory.GetFiles(root, "*.ibp", SearchOption.AllDirectories);
            string[] vbps = Directory.GetFiles(root, "*.vbp", SearchOption.AllDirectories);
            Check("the install offers pool files", ibps.Length > 0 && vbps.Length > 0,
                $"{ibps.Length} ibp, {vbps.Length} vbp");

            var details = new StringBuilder();
            int ibpFix = CountFixpoints(ibps, isIndex: true, details);
            int vbpFix = CountFixpoints(vbps, isIndex: false, details);

            // Two stock pools carry historical canonicalization diffs (measured before the
            // cutover, --probe-native-pools 1634/1636); the count is informational, the hard
            // gate is that nothing errored and the count stays at its measured floor.
            Check("ibp fixpoint holds its measured floor", ibpFix >= ibps.Length - 2,
                $"{ibpFix}/{ibps.Length}");
            Check("vbp fixpoint holds its measured floor", vbpFix >= vbps.Length - 2,
                $"{vbpFix}/{vbps.Length}");

            if (details.Length > 0)
            {
                sb.AppendLine();
                sb.Append(details);
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"NATIVE POOLS PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    private static int CountFixpoints(string[] files, bool isIndex, StringBuilder details)
    {
        int fixpoint = 0;
        foreach (string file in files)
        {
            byte[] bytes = File.ReadAllBytes(file);
            byte[] resaved;
            if (isIndex)
            {
                var pool = new Illusion.Formats.Geometry.IndexBufferPool();
                using var stream = new MemoryStream(bytes, writable: false);
                pool.ReadFromFile(stream);
                using var output = new MemoryStream();
                pool.WriteToFile(output);
                resaved = output.ToArray();
            }
            else
            {
                var pool = new Illusion.Formats.Geometry.VertexBufferPool();
                using var stream = new MemoryStream(bytes, writable: false);
                pool.ReadFromFile(stream);
                using var output = new MemoryStream();
                pool.WriteToFile(output);
                resaved = output.ToArray();
            }
            if (resaved.AsSpan().SequenceEqual(bytes)) fixpoint++;
            else if (details.Length < 4000) details.AppendLine($"POOL DIFF {Path.GetFileName(file)}");
        }
        return fixpoint;
    }

    /// <summary>
    /// The two vertex decode paths must not drift: for every geometry LOD's vertex buffer, the
    /// narrow load-path decode (mf_vtx_decompress_channels, straight into channel arrays) must be
    /// bit-identical to the full-fidelity wire decode on every channel the viewport consumes.
    /// Optional arg filters by .fr path substring. Output: %TEMP%\illusion_native_vtx.txt
    /// </summary>
    internal static void RunVertexChannelProbe(string? filter)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_native_vtx.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }
            string root = MafiaEnvironment.ResourcesFolder!;
            if (!Directory.Exists(root))
            {
                sb.AppendLine("resources not unpacked: " + root);
                return;
            }

            List<string> frFiles = [.. Directory.GetFiles(root, "*.fr", SearchOption.AllDirectories)];
            if (!string.IsNullOrEmpty(filter))
            {
                frFiles = [.. frFiles.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase))];
            }
            Check("the install offers .fr files", frFiles.Count > 0, $"{frFiles.Count} files");

            long buffers = 0, equal = 0, vertices = 0, errors = 0;
            var details = new StringBuilder();
            foreach (string frFile in frFiles)
            {
                try
                {
                    string dir = Path.GetDirectoryName(frFile)!;
                    var pool = new Dictionary<ulong, Illusion.Formats.Geometry.VertexBuffer>();
                    foreach (string vbpFile in Directory.GetFiles(dir, "*.vbp"))
                    {
                        var vbp = new Illusion.Formats.Geometry.VertexBufferPool();
                        using var stream = new MemoryStream(File.ReadAllBytes(vbpFile), writable: false);
                        vbp.ReadFromFile(stream);
                        foreach ((ulong hash, Illusion.Formats.Geometry.VertexBuffer buffer) in vbp.Buffers)
                        {
                            pool[hash] = buffer;
                        }
                    }
                    if (pool.Count == 0) continue;

                    var resource = new FrameResource();
                    using (var stream = new MemoryStream(File.ReadAllBytes(frFile), writable: false))
                    {
                        resource.ReadFromFile(stream);
                    }

                    foreach (Illusion.Formats.Frames.Resources.FrameGeometry geom
                        in resource.FrameGeometries.Values)
                    {
                        foreach (Illusion.Formats.Frames.Resources.FrameLOD lod in geom.LOD ?? [])
                        {
                            if (!pool.TryGetValue(lod.VertexBufferRef.Hash,
                                    out Illusion.Formats.Geometry.VertexBuffer? buffer)
                                || buffer.Data == null)
                            {
                                continue;
                            }
                            lod.GetVertexOffsets(out int stride);
                            int numVerts = lod.NumVerts;
                            if (stride <= 0 || numVerts <= 0
                                || (long)numVerts * stride > buffer.Data.Length)
                            {
                                continue;
                            }
                            buffers++;
                            vertices += numVerts;

                            byte[] raw = new byte[numVerts * stride];
                            Array.Copy(buffer.Data, raw, raw.Length);

                            Illusion.Formats.Geometry.Vertex[] full =
                                Illusion.Formats.Geometry.VertexTranslator.DecompressBuffer(
                                    raw, numVerts, lod.VertexDeclaration,
                                    geom.DecompressionOffset, geom.DecompressionFactor);

                            bool hasTangent = lod.VertexDeclaration.HasFlag(
                                Illusion.Formats.Geometry.VertexFlags.Tangent);
                            var positions = new System.Numerics.Vector3[numVerts];
                            var normals = new System.Numerics.Vector3[numVerts];
                            var uvs = new System.Numerics.Vector2[numVerts];
                            System.Numerics.Vector3[]? tangents =
                                hasTangent ? new System.Numerics.Vector3[numVerts] : null;
                            System.Numerics.Vector3[]? binormals =
                                hasTangent ? new System.Numerics.Vector3[numVerts] : null;
                            Illusion.Formats.Geometry.VertexTranslator.DecompressChannels(
                                raw, numVerts, lod.VertexDeclaration,
                                geom.DecompressionOffset, geom.DecompressionFactor,
                                positions, normals, uvs, tangents, binormals);

                            string? diff = FirstChannelDiff(
                                full, positions, normals, uvs, tangents, binormals,
                                lod.VertexDeclaration);
                            if (diff == null) equal++;
                            else if (details.Length < 8000)
                            {
                                details.AppendLine(
                                    $"CHANNEL DIFF {Path.GetFileName(frFile)} {lod.VertexBufferRef.Hash:X16}: {diff}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    details.AppendLine($"ERROR {Path.GetFileName(frFile)}: {ex.Message}");
                }
            }

            Check("no file errored", errors == 0, $"{errors} errors");
            Check("buffers were exercised", buffers > 0, $"{buffers} buffers, {vertices} vertices");
            Check("the channel decode is bit-identical to the wire decode",
                equal == buffers, $"{equal}/{buffers}");

            if (details.Length > 0)
            {
                sb.AppendLine();
                sb.Append(details);
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sw.Stop();
        sb.AppendLine();
        sb.AppendLine($"elapsed: {sw.Elapsed.TotalSeconds:F1} s");
        sb.Insert(0, $"NATIVE VTX CHANNEL PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    // Bitwise, channel by channel — a float that merely "looks the same" is a drift.
    private static string? FirstChannelDiff(Illusion.Formats.Geometry.Vertex[] full,
        System.Numerics.Vector3[] positions, System.Numerics.Vector3[] normals,
        System.Numerics.Vector2[] uvs, System.Numerics.Vector3[]? tangents,
        System.Numerics.Vector3[]? binormals, Illusion.Formats.Geometry.VertexFlags declaration)
    {
        static bool Bits(float a, float b) =>
            BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        static bool Vec(System.Numerics.Vector3 a, System.Numerics.Vector3 b) =>
            Bits(a.X, b.X) && Bits(a.Y, b.Y) && Bits(a.Z, b.Z);

        bool hasPosition = declaration.HasFlag(Illusion.Formats.Geometry.VertexFlags.Position);
        bool hasNormals = declaration.HasFlag(Illusion.Formats.Geometry.VertexFlags.Normals);
        bool hasUv0 = declaration.HasFlag(Illusion.Formats.Geometry.VertexFlags.TexCoords0);
        bool hasTangent = declaration.HasFlag(Illusion.Formats.Geometry.VertexFlags.Tangent);

        for (int i = 0; i < full.Length; i++)
        {
            if (hasPosition && !Vec(full[i].Position, positions[i])) return $"[{i}].Position";
            if (hasNormals && !Vec(full[i].Normal, normals[i])) return $"[{i}].Normal";
            if (hasUv0 && (!Bits((float)full[i].UVs[0].X, uvs[i].X)
                || !Bits((float)full[i].UVs[0].Y, uvs[i].Y)))
            {
                return $"[{i}].UV0";
            }
            if (hasTangent && tangents != null && !Vec(full[i].Tangent, tangents[i]))
            {
                return $"[{i}].Tangent";
            }
            if (hasPosition && binormals != null && hasTangent
                && !Vec(full[i].Binormal, binormals[i]))
            {
                return $"[{i}].Binormal";
            }
        }
        return null;
    }

    /// <summary>
    /// The native LOD capsule builder, twin-free. The deterministic case matrix (slot counts,
    /// strides, bounds with clamping/negative/fractional/NaN coordinates) is pinned by hash: the
    /// managed capsule serialization it was proven equal to (400/400) went away with the rest of
    /// the layout knowledge, so the pin freezes that verified output. On top of it the placeholder
    /// shape is exercised and a real FrameResource proves the codec accepts and preserves what the
    /// builder emits. Output: %TEMP%\illusion_native_lod.txt
    /// </summary>
    internal static void RunLodBuilderParityProbe()
    {
        // The chained SHA-256 of all 400 cases, captured from the build that was byte-for-byte
        // equal to the managed capsule serialization. A change here means the builder changed.
        const string PinnedMatrixHash =
            "FC573B5A0F944DB9D1FBBC1858FBFE0C206198B4C77B34A4E9633511522002E0";
        // The same pin for the placeholder LOD a freshly created mesh carries (no material slots);
        // its bytes were cross-checked against the capsules the pre-cutover managed path emitted.
        const string PinnedPlaceholderHash =
            "48D4542CA4190C364514657EC4C5FFC8945676FDACB329884F59C4E9F6AF2610";

        string outFile = Path.Combine(Path.GetTempPath(), "illusion_native_lod.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        static string HashOf(params byte[][] blobs)
        {
            using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            foreach (byte[] blob in blobs) sha.AppendData(blob);
            return Convert.ToHexString(sha.GetHashAndReset());
        }

        try
        {
            // A small deterministic LCG: the cases must be identical run to run.
            uint state = 0x1234ABCD;
            uint Next() => state = state * 1664525u + 1013904223u;
            float NextCoord()
            {
                uint pick = Next() % 6;
                return pick switch
                {
                    0 => (Next() % 200000) / 3.0f - 33000.0f, // clamps at either end
                    1 => -(Next() % 500) / 7.0f,
                    2 => (Next() % 500) / 7.0f,
                    3 => (Next() % 32) - 16.0f,               // integral
                    4 => float.NaN,
                    _ => (Next() % 60000) / 11.0f,
                };
            }

            int cases = 0;
            using var matrix = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            for (int caseNo = 0; caseNo < 400; caseNo++)
            {
                int slots = 1 + (int)(Next() % 8);
                int stride = (Next() & 1) == 0 ? 2 : 4;
                int numVerts = 1 + (int)(Next() % 200000);
                int numFaces = 0;

                var request = new Illusion.Formats.Native.Model.LodRebuildRequestW
                {
                    IndexStride = stride,
                    NumVerts = numVerts,
                };
                for (int slot = 0; slot < slots; slot++)
                {
                    ulong materialHash = ((ulong)Next() << 32) | Next();
                    int slotFaces = (int)(Next() % 70000);
                    int baseIndex = numFaces * 3;
                    var min = new System.Numerics.Vector3(NextCoord(), NextCoord(), NextCoord());
                    var max = new System.Numerics.Vector3(NextCoord(), NextCoord(), NextCoord());
                    numFaces += slotFaces;

                    request.Slots.Add(new Illusion.Formats.Native.Model.LodSlotW
                    {
                        MaterialHash = materialHash,
                        BaseIndex = baseIndex,
                        NumFaces = slotFaces,
                        BoundsMin = min,
                        BoundsMax = max,
                    });
                }
                request.NumFaces = numFaces;

                Illusion.Formats.Native.Model.LodRebuildResultW built =
                    Illusion.Formats.Native.Frames.NativeFrames.RebuildLod(request);
                matrix.AppendData(built.OpcodeCapsule);
                matrix.AppendData(built.SplitCapsule);
                cases++;
            }

            string matrixHash = Convert.ToHexString(matrix.GetHashAndReset());
            Check("cases were exercised", cases == 400, $"{cases}");
            Check("the case matrix still builds the pinned bytes", matrixHash == PinnedMatrixHash,
                matrixHash);

            // The placeholder a newly created mesh carries until the first push rebuilds it.
            Illusion.Formats.Native.Model.LodRebuildResultW placeholder =
                Illusion.Formats.Native.Frames.NativeFrames.RebuildLod(
                    new Illusion.Formats.Native.Model.LodRebuildRequestW
                    {
                        IndexStride = 2,
                        NumVerts = 3,
                        NumFaces = 1,
                    });
            string placeholderHash = HashOf(placeholder.OpcodeCapsule, placeholder.SplitCapsule);
            Check("the slotless placeholder still builds the pinned bytes",
                placeholderHash == PinnedPlaceholderHash, placeholderHash);

            bool refused;
            try
            {
                Illusion.Formats.Native.Frames.NativeFrames.RebuildLod(
                    new Illusion.Formats.Native.Model.LodRebuildRequestW { IndexStride = 3 });
                refused = false;
            }
            catch (InvalidDataException)
            {
                refused = true;
            }
            Check("a malformed request is refused with a real error", refused);

            // Codec acceptance: what the builder emits must survive a real FrameResource write and
            // read unchanged — the capsule walkers have to measure it exactly as the builder laid it.
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("(codec acceptance skipped — " + err + ")");
            }
            else
            {
                string root = MafiaEnvironment.ResourcesFolder!;
                string? carrier = Directory.Exists(root)
                    ? Directory.EnumerateFiles(root, "*.fr", SearchOption.AllDirectories)
                        .FirstOrDefault(f => Illusion.Formats.Native.Frames.NativeFrames
                            .LoadFrameResource(File.ReadAllBytes(f)).Geometries.Any(g => g.Lods.Count > 0))
                    : null;
                if (carrier == null)
                {
                    sb.AppendLine("(codec acceptance skipped — no .fr with a LOD in the install)");
                }
                else
                {
                    Illusion.Formats.Native.Model.FrameModel model =
                        Illusion.Formats.Native.Frames.NativeFrames.LoadFrameResource(
                            File.ReadAllBytes(carrier));
                    Illusion.Formats.Native.Model.FrameLodW target =
                        model.Geometries.First(g => g.Lods.Count > 0).Lods[0];

                    Illusion.Formats.Native.Model.LodRebuildResultW fresh =
                        Illusion.Formats.Native.Frames.NativeFrames.RebuildLod(
                            new Illusion.Formats.Native.Model.LodRebuildRequestW
                            {
                                IndexStride = 2,
                                NumVerts = target.NumVerts,
                                NumFaces = 12,
                                Slots =
                                {
                                    new Illusion.Formats.Native.Model.LodSlotW
                                    {
                                        MaterialHash = 0x0123456789ABCDEFul,
                                        BaseIndex = 0,
                                        NumFaces = 12,
                                        BoundsMin = new System.Numerics.Vector3(-1.5f, -2.5f, -3.5f),
                                        BoundsMax = new System.Numerics.Vector3(1.5f, 2.5f, 3.5f),
                                    },
                                },
                            });
                    target.OpcodeCapsule = fresh.OpcodeCapsule;
                    target.SplitCapsule = fresh.SplitCapsule;

                    byte[] written = Illusion.Formats.Native.Frames.NativeFrames.SaveFrameResource(model);
                    Illusion.Formats.Native.Model.FrameModel reread =
                        Illusion.Formats.Native.Frames.NativeFrames.LoadFrameResource(written);
                    Illusion.Formats.Native.Model.FrameLodW back =
                        reread.Geometries.First(g => g.Lods.Count > 0).Lods[0];

                    Check("a rebuilt LOD survives a FrameResource write and read",
                        back.OpcodeCapsule.AsSpan().SequenceEqual(fresh.OpcodeCapsule)
                        && back.SplitCapsule.AsSpan().SequenceEqual(fresh.SplitCapsule),
                        Path.GetFileName(carrier));
                    Check("and re-writes to the same bytes",
                        Illusion.Formats.Native.Frames.NativeFrames.SaveFrameResource(reread)
                            .AsSpan().SequenceEqual(written));
                }
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"NATIVE LOD PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }
}
