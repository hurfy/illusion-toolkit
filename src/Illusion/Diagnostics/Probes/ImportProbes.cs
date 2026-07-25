using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Collisions;
using Illusion.Assets.Import;
using Illusion.Assets.Sds;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Collisions;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Materials;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the File → Import pipeline: the glTF reader + routing/conversion, the render-mesh
/// route (payload → BridgeObjectFactory), the collision route (payload → CollisionPushAcceptor), and the
/// game-material creation the import offers for missing names.</summary>
internal static class ImportProbes
{
    // ── GLB fixture builder ──
    //
    // Composes a real binary .glb in memory: a unit cube split into two primitives (top face on one
    // material, the rest on another) under "MESH_<name>", plus a COL_-prefixed copy of the same cube on
    // one surface material. Node transforms exercise TRS + hierarchy. glTF axes (+Y up).
    private static byte[] BuildFixtureGlb(string meshMaterialA, string meshMaterialB, string surfaceName)
    {
        // Cube corners (Y-up): 8 positions. Top face (y=+0.5) = corners 4..7.
        float[] positions =
        {
            -0.5f, -0.5f, -0.5f,  0.5f, -0.5f, -0.5f,  0.5f, -0.5f, 0.5f,  -0.5f, -0.5f, 0.5f, // bottom (y-)
            -0.5f, 0.5f, -0.5f,  0.5f, 0.5f, -0.5f,  0.5f, 0.5f, 0.5f,  -0.5f, 0.5f, 0.5f,     // top (y+)
        };
        ushort[] topFace = { 4, 6, 5, 4, 7, 6 };
        ushort[] rest =
        {
            0, 1, 2, 0, 2, 3,       // bottom
            0, 4, 5, 0, 5, 1,       // z- side
            1, 5, 6, 1, 6, 2,       // x+ side
            2, 6, 7, 2, 7, 3,       // z+ side
            3, 7, 4, 3, 4, 0,       // x- side
        };
        float[] uvs = new float[8 * 2];
        for (int i = 0; i < 8; i++) { uvs[i * 2] = (i & 1); uvs[i * 2 + 1] = (i & 2) >> 1; }

        var bin = new MemoryStream();
        int posOffset = Write(bin, positions);
        int uvOffset = Write(bin, uvs);
        int topOffset = WriteU16(bin, topFace);
        int restOffset = WriteU16(bin, rest);
        int binLength = Pad4(bin);

        string json = $$"""
        {
          "asset": { "version": "2.0" },
          "scene": 0,
          "scenes": [ { "nodes": [0, 2] } ],
          "nodes": [
            { "name": "root", "translation": [10, 20, 30], "children": [1] },
            { "name": "MESH_crate", "mesh": 0, "translation": [0, 2, 0],
              "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] },
            { "name": "COL_crate", "mesh": 1, "translation": [10, 22, 30] }
          ],
          "meshes": [
            { "name": "crate", "primitives": [
                { "attributes": { "POSITION": 0, "TEXCOORD_0": 1 }, "indices": 2, "material": 0 },
                { "attributes": { "POSITION": 0, "TEXCOORD_0": 1 }, "indices": 3, "material": 1 } ] },
            { "name": "crate_col", "primitives": [
                { "attributes": { "POSITION": 0 }, "indices": 3, "material": 2 },
                { "attributes": { "POSITION": 0 }, "indices": 2, "material": 2 } ] }
          ],
          "materials": [
            { "name": "{{meshMaterialA}}" },
            { "name": "{{meshMaterialB}}" },
            { "name": "{{surfaceName}}" }
          ],
          "buffers": [ { "byteLength": {{binLength}} } ],
          "bufferViews": [
            { "buffer": 0, "byteOffset": {{posOffset}}, "byteLength": 96 },
            { "buffer": 0, "byteOffset": {{uvOffset}}, "byteLength": 64 },
            { "buffer": 0, "byteOffset": {{topOffset}}, "byteLength": 12 },
            { "buffer": 0, "byteOffset": {{restOffset}}, "byteLength": 60 }
          ],
          "accessors": [
            { "bufferView": 0, "componentType": 5126, "count": 8, "type": "VEC3" },
            { "bufferView": 1, "componentType": 5126, "count": 8, "type": "VEC2" },
            { "bufferView": 2, "componentType": 5123, "count": 6, "type": "SCALAR" },
            { "bufferView": 3, "componentType": 5123, "count": 30, "type": "SCALAR" }
          ]
        }
        """;
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPadded = (jsonBytes.Length + 3) & ~3;

        var glb = new MemoryStream();
        var w = new BinaryWriter(glb);
        w.Write(0x46546C67u);                            // "glTF"
        w.Write(2u);
        w.Write(12 + 8 + jsonPadded + 8 + binLength);
        w.Write(jsonPadded);
        w.Write(0x4E4F534Au);                            // "JSON"
        w.Write(jsonBytes);
        for (int i = jsonBytes.Length; i < jsonPadded; i++) w.Write((byte)' ');
        w.Write(binLength);
        w.Write(0x004E4942u);                            // "BIN"
        bin.Position = 0;
        bin.CopyTo(glb);
        return glb.ToArray();

        static int Write(MemoryStream s, float[] values)
        {
            int at = Pad4(s);
            foreach (float v in values) s.Write(BitConverter.GetBytes(v));
            return at;
        }
        static int WriteU16(MemoryStream s, ushort[] values)
        {
            int at = Pad4(s);
            foreach (ushort v in values) s.Write(BitConverter.GetBytes(v));
            return at;
        }
        static int Pad4(MemoryStream s)
        {
            while (s.Length % 4 != 0) s.WriteByte(0);
            return (int)s.Length;
        }
    }

    private static string FirstSurfaceToken()
    {
        foreach (CollisionMaterial m in CollisionMaterialCatalog.All)
            if (m.Token != "unknown") return m.Token;
        return CollisionMaterialCatalog.All[0].Token;
    }

    // glTF reader + routing/conversion — in-memory fixture, no game data needed beyond the surface
    // catalog. Output: %TEMP%\illusion_gltf.txt
    internal static void RunGltfProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_gltf.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            string surface = FirstSurfaceToken();
            byte[] glb = BuildFixtureGlb("mat_brick", "mat_wood", surface);
            List<GltfMeshInstance>? meshes = GltfFile.TryLoad(glb, null, out string? err);
            Check("Fixture GLB parses", meshes != null, err ?? "");
            if (meshes == null) return;
            Check("Both mesh nodes load", meshes.Count == 2, $"{meshes.Count}");

            GltfMeshInstance render = meshes.First(m => m.Name == "MESH_crate");
            GltfMeshInstance hull = meshes.First(m => m.Name == "COL_crate");
            Check("Hierarchy transform composes (root 10,20,30 + child 0,2,0)",
                Approx(render.World.Translation, new Vector3(10f, 22f, 30f)),
                render.World.Translation.ToString());
            Check("Primitives keep their materials",
                render.Primitives.Count == 2
                && render.Primitives[0].MaterialName == "mat_brick"
                && render.Primitives[1].MaterialName == "mat_wood");
            Check("Missing normals are computed unit-length",
                render.Primitives[1].Normals.All(n => MathF.Abs(n.Length() - 1f) < 1e-4f));

            // Routing + resolution.
            List<ImportItem> items = ModelImport.Plan(meshes);
            ImportItem meshItem = items.First(i => i.Kind == ImportKind.RenderMesh);
            ImportItem hullItem = items.First(i => i.Kind == ImportKind.CollisionHull);
            Check("COL_ prefix routes to collision, prefix stripped",
                hullItem.Name == "crate" && meshItem.Name == "crate");
            Check("Unknown game materials resolve as Missing (creatable)",
                meshItem.Materials.Count == 2 && meshItem.Materials.All(m => m.State == MaterialState.Missing));
            Check("Surface name resolves against the physics catalog",
                hullItem.Refusal == null
                && hullItem.Materials.Count == 1 && hullItem.Materials[0].State == MaterialState.Surface);

            // Conversion: axes, scale, offset, UV flip, per-face slots.
            var options = new ModelImport.Options(Scale: 2f, Offset: new Vector3(100f, 0f, 0f));
            MeshObjectPayload payload = ModelImport.ToMeshPayload(meshItem, options);
            Check("Y-up cube converts to Z-up at scale 2 (corner -1,1,-1)",
                Approx(payload.Positions[0], new Vector3(-1f, 1f, -1f)), payload.Positions[0].ToString());
            Check("World translation converts, scales and offsets ((10,22,30) → (120,-60,44))",
                Approx(payload.World.Translation, new Vector3(120f, -60f, 44f)),
                payload.World.Translation.ToString());
            Check("Faces carry their material slots (6 top + 30 rest)",
                payload.FaceMaterials.Length == 12
                && payload.FaceMaterials.Count(f => f == 0) == 2
                && payload.FaceMaterials.Count(f => f == 1) == 10);
            Check("UVs are V-flipped into the payload convention",
                MathF.Abs(payload.LoopUvs[0].Y - (1f - render.Primitives[0].Uvs[render.Primitives[0].Indices[0]].Y)) < 1e-6f);
            Check("Every corner is new (orig index -1)", payload.LoopOrigIndex.All(i => i == -1));

            CollisionObjectPayload colPayload = ModelImport.ToCollisionPayload(hullItem, options);
            Check("Hull placement keeps position, scale baked into vertices",
                Approx(colPayload.World.Translation, new Vector3(120f, -60f, 44f))
                && colPayload.Positions.All(p =>
                    MathF.Abs(MathF.Abs(p.X) - 1f) < 1e-4f
                    && MathF.Abs(MathF.Abs(p.Y) - 1f) < 1e-4f
                    && MathF.Abs(MathF.Abs(p.Z) - 1f) < 1e-4f));
            Check("Hull faces all carry the one surface",
                colPayload.FaceMaterials.Length == 12 && colPayload.Materials.Count == 1);

            // Refusals: Draco and truncated files must fail with a reason, not mis-read.
            Check("Garbage is refused",
                GltfFile.TryLoad(new byte[] { 1, 2, 3, 4 }, null, out string? garbageErr) == null
                && garbageErr != null, garbageErr ?? "");
            byte[] draco = Encoding.UTF8.GetBytes(
                """{ "asset": {"version": "2.0"}, "extensionsRequired": ["KHR_draco_mesh_compression"], "meshes": [] }""");
            Check("Draco-compressed files are refused with the extension named",
                GltfFile.TryLoad(draco, null, out string? dracoErr) == null
                && dracoErr?.Contains("KHR_draco_mesh_compression", StringComparison.Ordinal) == true,
                dracoErr ?? "");

            sb.Insert(0, $"GLTF PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "GLTF PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Render-mesh import route against a real district: fixture cube (2 materials, one existing + one
    // created into a TEMP-copied library path is not possible — creation is probed separately, so here
    // both slots use EXISTING game materials) → BridgeObjectFactory → save survives, exports over the
    // bridge, undo is byte-faithful. Output: %TEMP%\illusion_import_mesh.txt
    internal static void RunMeshProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_import_mesh.txt");
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
            MafiaMaterials.EnsureLoaded();
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            if (!sds.Exists) { sb.AppendLine($"no such district: {sds.FullName}"); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            if (fr?.FrameObjects is not { Count: > 0 }) { sb.AppendLine("no frame objects"); return; }
            var doc = new SceneDocumentAdapter(fr, sds);

            // Two real game materials off the district's own objects, taken by name so the fixture's
            // material names resolve as Found.
            var names = new List<string>();
            foreach (Formats.Frames.Resources.FrameMaterial material in fr.FrameMaterials.Values)
            {
                foreach (Formats.Frames.Resources.MaterialStruct[] lod in material.Materials)
                    foreach (Formats.Frames.Resources.MaterialStruct entry in lod)
                    {
                        string? name = MafiaMaterials.GetMaterialName(entry.MaterialHash);
                        if (name != null && !names.Contains(name)) names.Add(name);
                        if (names.Count == 2) break;
                    }
                if (names.Count == 2) break;
            }
            Check("District offers two named game materials", names.Count == 2, string.Join(", ", names));
            if (names.Count < 2) return;

            byte[] glb = BuildFixtureGlb(names[0], names[1], FirstSurfaceToken());
            List<GltfMeshInstance> meshes = GltfFile.TryLoad(glb, null, out string? loadErr)
                ?? throw new InvalidOperationException(loadErr);
            List<ImportItem> items = ModelImport.Plan(meshes);
            ImportItem meshItem = items.First(i => i.Kind == ImportKind.RenderMesh);
            Check("Existing game materials resolve as Found",
                meshItem.Refusal == null && meshItem.Materials.All(m => m.State == MaterialState.Found));

            var options = new ModelImport.Options(Scale: 1f, Offset: new Vector3(25f, -40f, 5f));
            MeshObjectPayload payload = ModelImport.ToMeshPayload(meshItem, options);

            byte[] bytes0 = fr.WriteToStream();
            int objects0 = fr.FrameObjects.Count;

            BridgeObjectFactory.CreatedObject? created = BridgeObjectFactory.TryCreate(doc, payload, out string? reason);
            Check("TryCreate accepts the import payload", created != null, reason ?? "");
            if (created == null) return;

            FrameObjectSingleMesh frame = fr.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                .First(o => o.Name.String == "crate");
            Check("Object registered", fr.FrameObjects.Count == objects0 + 1);

            // The drawable stock shape (census: 2516/2516 drawable meshes): on the name table — the
            // game's spawn list — anchored to the main scene via ParentIndex2, always-draw LOD range.
            Illusion.Formats.Frames.Resources.FrameHeaderScene? mainScene = null;
            int bestScore = -1;
            foreach (Illusion.Formats.Frames.Resources.FrameHeaderScene s in fr.FrameScenes.Values)
            {
                int score = s.Children.Count(c => c.IsOnFrameTable && (int)c.FrameNameTableFlags == 0);
                if (score > bestScore) { bestScore = score; mainScene = s; }
            }
            Check("Created object is on the frame name table", frame.IsOnFrameTable);
            Check("Created object is anchored to the main scene",
                mainScene != null
                && frame.Refs.TryGetValue(FrameEntryRefTypes.Parent2, out int p2) && p2 == mainScene.RefID
                && mainScene.Children.Contains(frame),
                mainScene?.Name.ToString() ?? "no scenes");
            Check("Created object carries the anchored-mesh flag",
                frame.SingleMeshFlags.HasFlag(SingleMeshFlags.ParentIndex2_Flag),
                $"0x{(uint)frame.SingleMeshFlags:X8}");
            Check("LOD0 uses the always-draw distance",
                frame.Geometry.LOD[0].Distance == 999999995904f,
                frame.Geometry.LOD[0].Distance.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var table = new FrameNameTable();
            byte[] preTable = fr.WriteToStream(); // finalize indices before the rebuild reads them
            table.BuildDataFromResource(fr);
            Check("Rebuilt name table gains the import's spawn entry",
                table.FrameData != null && preTable.Length > 0
                && fr.FrameObjects.Values.OfType<FrameObjectBase>().Count(o => o.IsOnFrameTable)
                    == table.FrameData.Length);

            DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(frame);
            Check("Created mesh decodes with both material ranges",
                decoded != null && frame.Material.Materials[0].Length == 2,
                decoded == null ? "no decode" : $"ranges={frame.Material.Materials[0].Length}");
            Check("Cube geometry survives the rebuild (36 indices)",
                decoded != null && decoded.Indices.Length == 36,
                decoded == null ? "" : $"indices={decoded.Indices.Length}");

            MeshObjectPayload? exported = BridgeMeshExporter.TryExport(doc.Node(frame), doc, out string? exportSkip);
            Check("Imported object exports to Blender", exported != null, exportSkip ?? "");

            byte[] bytes1 = fr.WriteToStream();
            var fr1 = new FrameResource();
            using (var ms = new MemoryStream(bytes1, false)) fr1.ReadFromFile(ms);
            Check("Save carries the import", fr1.FrameObjects.Count == objects0 + 1);

            created.Detach();
            byte[] bytes2 = fr.WriteToStream();
            Check("Undo save is byte-identical to the pre-import save",
                bytes2.AsSpan().SequenceEqual(bytes0),
                bytes2.Length == bytes0.Length
                    ? $"first diff at {FirstDiff(bytes0, bytes2)}"
                    : $"length {bytes0.Length} vs {bytes2.Length}");

            sb.Insert(0, $"IMPORT MESH PROBE ({district}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "IMPORT MESH PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Collision import route against a real district's .col: fixture COL_ cube → CollisionPushAcceptor
    // (sections, cook, mint) → the minted hull decodes back. Skips cleanly when the PhysX runtime is
    // not installed. Non-destructive. Output: %TEMP%\illusion_import_collision.txt
    internal static void RunCollisionProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_import_collision.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            CookAvailability cook = PhysXRuntimeLocator.Check();
            if (!cook.Available)
            {
                sb.AppendLine($"IMPORT COLLISION PROBE: SKIP — {cook.Detail}");
                return;
            }

            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            if (!sds.Exists) { sb.AppendLine($"no such district: {sds.FullName}"); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            if (colPath == null) { sb.AppendLine("no .col found"); return; }
            CollisionFile collision = CollisionFile.Load(colPath);
            var document = new CollisionDocumentAdapter(collision, sds);
            int meshes0 = collision.Meshes.Count;

            string surface = FirstSurfaceToken();
            byte[] glb = BuildFixtureGlb("a", "b", surface);
            List<GltfMeshInstance> meshes = GltfFile.TryLoad(glb, null, out string? loadErr)
                ?? throw new InvalidOperationException(loadErr);
            ImportItem hullItem = ModelImport.Plan(meshes).First(i => i.Kind == ImportKind.CollisionHull);
            Check("The COL_ item plans clean", hullItem.Refusal == null, hullItem.Refusal ?? "");

            var options = new ModelImport.Options(Scale: 3f, Offset: Vector3.Zero);
            CollisionObjectPayload payload = ModelImport.ToCollisionPayload(hullItem, options);

            CollisionPushAcceptor.Result accepted = CollisionPushAcceptor.TryAccept(document, payload);
            Check("The cube cooks into a hull", accepted.Minted is { SkipReason: null }, accepted.Refusal ?? "");
            if (accepted.Minted is not { } minted) return;
            Check("The mint is a brand-new mesh", minted.Added != null && minted.Hash != 0);
            Check("TryAccept leaves the file untouched", collision.Meshes.Count == meshes0);

            if (minted.Added is { CookedMesh: { } cookedBytes } addedMesh)
            {
                CookedTriangleMesh decoded = CookedTriangleMesh.Decode(cookedBytes);
                Check("Cooked hull decodes to the cube (8 verts / 12 triangles)",
                    decoded.Vertices.Length == 8 && decoded.TriangleCount == 12,
                    $"verts={decoded.Vertices.Length} tris={decoded.TriangleCount}");
                bool scaled = decoded.Vertices.All(v =>
                    MathF.Abs(MathF.Abs(v.X) - 1.5f) < 1e-4f
                    && MathF.Abs(MathF.Abs(v.Y) - 1.5f) < 1e-4f
                    && MathF.Abs(MathF.Abs(v.Z) - 1.5f) < 1e-4f);
                Check("Scale is baked into the cooked vertices (±1.5 m cube)", scaled);
                int expectedRaw = hullItem.Materials[0].SurfaceRawId;
                Check("Every triangle carries the named surface",
                    addedMesh.Sections.Count == 1
                    && addedMesh.Sections[0].Material == (uint)(expectedRaw - CollisionSectionBuilder.SectionMaterialBias),
                    $"sections={addedMesh.Sections.Count}");
            }

            sb.Insert(0, $"IMPORT COLLISION PROBE ({district}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "IMPORT COLLISION PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Game-material creation on a TEMP COPY of the real default.mtl — the game's file is never touched.
    // Proves: an untouched library rewrites byte-identically (the writer is a fixpoint — the precondition
    // for ever letting the import write the real file), a created material round-trips through
    // write/reload with the FNV64 hash the payloads use, and duplicates are refused.
    // Output: %TEMP%\illusion_import_materials.txt
    internal static void RunMaterialsProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_import_materials.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string root = Path.Combine(Path.GetTempPath(), "illusion_mtl_probe");
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string source = Path.Combine(MafiaEnvironment.GameRoot!, "edit", "materials", "default.mtl");
            if (!File.Exists(source)) { sb.AppendLine("no default.mtl in the game folder"); return; }

            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            string work = Path.Combine(root, "default.mtl");
            File.Copy(source, work);
            byte[] original = File.ReadAllBytes(work);

            // 1) Fixpoint: reading and rewriting the untouched library must be byte-identical.
            var library = new MaterialLibrary(MaterialVersion.V_57);
            library.ReadMatFile(work);
            int count0 = library.Materials.Count;
            string rewrite = Path.Combine(root, "rewrite.mtl");
            library.WriteMatFile(rewrite);
            byte[] rewritten = File.ReadAllBytes(rewrite);
            Check("Untouched library rewrites byte-identically",
                rewritten.AsSpan().SequenceEqual(original),
                rewritten.Length == original.Length
                    ? $"first diff at {FirstDiff(original, rewritten)}"
                    : $"length {original.Length} vs {rewritten.Length}");

            // 2) Creation: a default-preset material lands under the FNV64 hash of its name.
            const string newName = "probe_import_material";
            var created = GameMaterialCreator.AddDefault(library, newName);
            ulong expectedHash = Formats.Hashing.Fnv64.Hash(newName);
            Check("AddDefault creates the material", created != null);
            Check("The hash is the FNV64 of the name (what payloads carry)",
                created != null && created.GetMaterialHash() == expectedHash,
                created == null ? "" : $"0x{created.GetMaterialHash():x16}");
            Check("A duplicate name is refused", GameMaterialCreator.AddDefault(library, newName) == null);

            // 3) Backup + atomic write + reload: the material survives, everything else intact.
            string? backup = GameMaterialCreator.BackupAndWrite(library, work);
            Check("A timestamped backup of the previous file is kept",
                backup != null && File.Exists(backup) && File.ReadAllBytes(backup).AsSpan().SequenceEqual(original),
                backup ?? "null");

            var reloaded = new MaterialLibrary(MaterialVersion.V_57);
            reloaded.ReadMatFile(work);
            Check("Reloaded library carries the new material",
                reloaded.Materials.Count == count0 + 1
                && reloaded.LookupMaterialByHash(expectedHash)?.GetMaterialName() == newName,
                $"{count0} → {reloaded.Materials.Count}");
            var slot = reloaded.LookupMaterialByHash(expectedHash)?.GetTextureByID("S000");
            Check("The new material has an empty diffuse slot (textures stay with the modder)",
                slot != null && string.IsNullOrEmpty(slot.String));

            sb.Insert(0, $"IMPORT MATERIALS PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "IMPORT MATERIALS PROBE: FAIL\n\n");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
