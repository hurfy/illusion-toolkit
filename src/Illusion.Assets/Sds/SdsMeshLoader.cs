using System.Numerics;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Domain;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using Illusion.Formats.Translokator;

namespace Illusion.Assets.Sds;

/// <summary>
/// Reads a single .sds and produces a list of <see cref="MeshData"/>: SdsArchive (unpack) →
/// SceneData (FrameResource + buffer pools) → VertexTranslator (vertex unpacking).
/// </summary>
public static class SdsMeshLoader
{
    // Extraction is check-then-act filesystem work (unpack into a shared legacy folder, then
    // delete+move) and now runs on background loaders that can outlive their viewport — after a
    // close-and-reopen two instances may request the same .sds concurrently. One process-wide lock
    // serializes the cold path; the warm path stays lock-free.
    private static readonly object ExtractSync = new();

    /// <summary>
    /// Ensures the SDS is unpacked into the mirror <c>&lt;root&gt;\resources\…\&lt;name&gt;.sds\</c>, and
    /// returns that folder. A folder left in the legacy <c>&lt;dir&gt;\extracted\</c> location (older
    /// MafiaToolkit runs) is adopted by moving it into /resources instead of re-extracting.
    /// </summary>
    public static string EnsureExtracted(FileInfo sdsFile)
    {
        string target = MafiaEnvironment.ExtractedDir(sdsFile);
        if (File.Exists(Path.Combine(target, "SDSContent.xml")))
        {
            return target;
        }

        lock (ExtractSync)
        {
            if (File.Exists(Path.Combine(target, "SDSContent.xml")))
            {
                return target; // extracted by whoever held the lock before us
            }

            string legacy = Path.Combine(sdsFile.DirectoryName!, "extracted", sdsFile.Name);
            if (File.Exists(Path.Combine(legacy, "SDSContent.xml")))
            {
                RelocateToResources(legacy, target);
                return target;
            }

            SdsArchive.Open(sdsFile.FullName).Extract(target);
            return target;
        }
    }

    private static void RelocateToResources(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(to)) Directory.Delete(to, true);
        Directory.Move(from, to); // same volume → instant
    }

    // Opens an already-extracted scene folder (little-endian PC data; console archives would pass
    // true). Public for the diagnostics probes; app code goes through the Load* entry points.
    public static ExtractedSds OpenScene(string extractedDir)
    {
        return ExtractedSds.Load(extractedDir);
    }

    // Shared loader prologue: extract (if needed), warm up materials, build the scene, and return its
    // FrameResource — or null when the scene carries no frame objects. `placements` is where the archive's
    // actor pack puts the prototype objects it spawns (empty when there is none).
    private static FrameResource? OpenFrameResource(FileInfo sdsFile, out string extracted,
        out ActorPlacements placements)
    {
        extracted = EnsureExtracted(sdsFile);
        MafiaMaterials.EnsureLoaded();
        ExtractedSds scene = OpenScene(extracted);
        FrameResource? fr = scene.FrameResource;
        if (fr?.FrameObjects == null)
        {
            placements = ActorPlacements.Empty;
            return null;
        }
        placements = ActorPlacements.Load(scene.Manifest, fr);
        return fr;
    }

    public static List<MeshData> LoadSds(FileInfo sdsFile)
    {
        var result = new List<MeshData>();
        FrameResource? fr = OpenFrameResource(sdsFile, out _, out ActorPlacements placements);
        if (fr == null) return result;

        foreach (var pair in fr.FrameObjects)
        {
            if (pair.Value is FrameObjectSingleMesh mesh && mesh.Geometry != null)
            {
                MeshData? md = TryConvert(mesh, placement: placements.For(mesh));
                if (md != null) result.Add(md);
            }
        }
        return result;
    }

    /// <summary>
    /// Reads a .sds and returns the INTERNAL FrameResource hierarchy (frame tree) + a flat list of
    /// meshes + the loaded document (the save/build unit, null when the archive carries no frame
    /// objects). Children come from <c>FrameObjectBase.Children</c> — the union of the frames linked
    /// through both parent slots — and each frame is claimed the first time it is placed, so a frame
    /// reachable through more than one link appears exactly once. Roots are the scene folders plus the
    /// frames whose two parent slots are both empty.
    /// </summary>
    public static (List<SdsFrameNode> Roots, List<MeshData> Meshes, ISceneDocument? Document) LoadHierarchy(
        FileInfo sdsFile, IReadOnlyCollection<string>? districtNames = null)
    {
        // Names of OTHER districts (to detect neighbor proxy meshes), except the one being loaded.
        string self = Path.GetFileNameWithoutExtension(sdsFile.Name);
        var others = new List<string>();
        if (districtNames != null)
            foreach (string d in districtNames)
                if (!d.Equals(self, StringComparison.OrdinalIgnoreCase)) others.Add(d);

        var meshes = new List<MeshData>();
        FrameResource? fr = OpenFrameResource(sdsFile, out _, out ActorPlacements placements);
        if (fr == null) return (new List<SdsFrameNode>(), meshes, null);

        var document = new SceneDocumentAdapter(fr, sdsFile, placements);
        var roots = BuildRoots(fr, document, others, meshes, null);
        return (roots, meshes, document);
    }

    /// <summary>
    /// city_crash: the same frame objects as a regular SDS (folder → hierarchy), but prototype meshes
    /// referenced by the Translokator table become INSTANCED — their copies are placed according to
    /// the table (see <see cref="MeshData.Instances"/>). Meshes without references are drawn as usual.
    /// </summary>
    public static (List<SdsFrameNode> Roots, List<MeshData> Meshes, ISceneDocument? Document,
        CrashPlacements? Placements) LoadCrashHierarchy(FileInfo crashSds)
    {
        var meshes = new List<MeshData>();
        FrameResource? fr = OpenFrameResource(crashSds, out string extracted, out ActorPlacements actors);
        if (fr == null) return (new List<SdsFrameNode>(), meshes, null, null);

        var document = new SceneDocumentAdapter(fr, crashSds, actors);
        CrashPlacements? placements = LoadPlacements(crashSds, extracted, fr);
        var roots = BuildRoots(fr, document, Array.Empty<string>(), meshes, placements?.BuildClouds());
        return (roots, meshes, document, placements);
    }

    // Reads the Translokator table (and the other season's, so an edit can be mirrored into it) and resolves its
    // rows against the frame resource. Null when the archive carries no table — then the crash SDS just draws its
    // prototypes where they stand.
    private static CrashPlacements? LoadPlacements(FileInfo crashSds, string extracted, FrameResource fr)
    {
        string? tra = SdsTranslokatorSaver.ResolvePath(extracted);
        if (tra == null) return null;

        TranslokatorLoader table;
        try { table = new TranslokatorLoader(new FileInfo(tra)); }
        catch { return null; } // a table we cannot read is a table we must not write back

        (TranslokatorLoader? twin, FileInfo? twinArchive) = LoadTwinTable(crashSds);
        var document = new TranslokatorDocumentAdapter(table, crashSds, twin, twinArchive);
        return CrashPlacements.Build(fr, document);
    }

    /// <summary>
    /// The other season's table for a crash archive: <c>city_crash</c> ↔ <c>city_crash_z</c>. Both ship the same
    /// placements, so loading the twin here — on the background loader, where extracting it costs no interaction —
    /// is what lets an edit be applied to both seasons at once. Best-effort: an archive with no twin (Sicily), or
    /// one that cannot be extracted, simply edits its own season.
    /// </summary>
    private static (TranslokatorLoader? Table, FileInfo? Archive) LoadTwinTable(FileInfo crashSds)
    {
        string stem = Path.GetFileNameWithoutExtension(crashSds.Name);
        string twinStem = stem.EndsWith("_z", StringComparison.OrdinalIgnoreCase) ? stem[..^2] : stem + "_z";
        var twinArchive = new FileInfo(Path.Combine(crashSds.DirectoryName!, twinStem + ".sds"));
        if (!twinArchive.Exists) return (null, null);

        try
        {
            string twinExtracted = EnsureExtracted(twinArchive);
            string? twinTra = SdsTranslokatorSaver.ResolvePath(twinExtracted);
            return twinTra == null ? (null, null) : (new TranslokatorLoader(new FileInfo(twinTra)), twinArchive);
        }
        catch
        {
            return (null, null);
        }
    }

    // Builds tree roots from FrameResource. instanceMap (if provided) marks prototype meshes as instanced.
    private static List<SdsFrameNode> BuildRoots(FrameResource fr, SceneDocumentAdapter document,
        IReadOnlyCollection<string> others,
        List<MeshData> meshes, IReadOnlyDictionary<FrameObjectSingleMesh, CrashPlacements.Cloud>? instanceMap)
    {
        var roots = new List<SdsFrameNode>();

        var objs = new List<FrameObjectBase>();
        foreach (var pair in fr.FrameObjects)
            if (pair.Value is FrameObjectBase o) objs.Add(o);

        // Take the child lists the loader already built. FrameObjectBase.Children is the union of the frames
        // linked through ParentIndex1 (the hierarchy parent) and ParentIndex2 (the anchor), which is the same set
        // the reference toolkit walks. Deriving it from o.Parent alone loses every anchor-linked frame — those
        // then match the "no parent" test below and float to the top of the tree.
        var childrenOf = new Dictionary<FrameObjectBase, List<FrameObjectBase>>();
        foreach (FrameObjectBase o in objs)
        {
            if (o.Children.Count > 0) childrenOf[o] = new List<FrameObjectBase>(o.Children);
        }

        var claimed = new HashSet<FrameObjectBase>();

        // Real scene folders (FrameHeaderScene) — top level of the hierarchy; their children are the scene's root objects.
        if (fr.FrameScenes != null)
        {
            foreach (FrameHeaderScene s in fr.FrameScenes.Values)
            {
                string sceneName = s.Name?.ToString() ?? "scene";
                var sn = new SdsFrameNode { Name = sceneName, Kind = "Scene", Source = new FrameSceneAdapter(s) };
                foreach (FrameObjectBase obj in s.Children)
                    if (claimed.Add(obj)) sn.Children.Add(BuildNode(obj, document, childrenOf, meshes, instanceMap, claimed));
                if (sn.Children.Count > 0)
                {
                    sn.Category = CategorizeScene(sn, others);
                    roots.Add(sn);
                }
            }
        }

        // True top-level frames: both parent slots empty. An object that merely lacks a hierarchy parent is still
        // anchored somewhere (~35 % of the shipped game is exactly that shape), and hoisting it here is what made
        // an edited mesh appear to lose its parent.
        foreach (FrameObjectBase o in objs)
            if (o.ParentIndex1.Index < 0 && o.ParentIndex2.Index < 0 && claimed.Add(o))
                roots.Add(BuildNode(o, document, childrenOf, meshes, instanceMap, claimed));

        // Anything still unplaced is anchored to something the walk above never reached — a malformed hierarchy.
        // Show it rather than dropping it silently, but keep it distinguishable from a genuine root.
        foreach (FrameObjectBase o in objs)
            if (claimed.Add(o))
            {
                SdsFrameNode orphan = BuildNode(o, document, childrenOf, meshes, instanceMap, claimed);
                orphan.Name += "  (unanchored)";
                roots.Add(orphan);
            }

        return roots;
    }

    private static SdsFrameNode BuildNode(FrameObjectBase obj, SceneDocumentAdapter document,
        Dictionary<FrameObjectBase, List<FrameObjectBase>> childrenOf, List<MeshData> meshes,
        IReadOnlyDictionary<FrameObjectSingleMesh, CrashPlacements.Cloud>? instanceMap,
        HashSet<FrameObjectBase> claimed)
    {
        var node = new SdsFrameNode { Name = obj.Name?.ToString() ?? "?", Kind = KindOf(obj), Source = document.Node(obj) };

        if (obj is FrameObjectSingleMesh sm && sm.Geometry != null)
        {
            CrashPlacements.Cloud cloud = default;
            instanceMap?.TryGetValue(sm, out cloud);
            MeshData? md = TryConvert(sm, cloud.Matrices, cloud.DrawDistances, document.Placements.For(sm));
            if (md != null) { node.Mesh = md; meshes.Add(md); }
        }

        // Claim descendants as we go: the caller uses the same set to decide what is still unplaced, and a
        // malformed hierarchy can otherwise reach the same frame twice (claimed also breaks any cycle).
        if (childrenOf.TryGetValue(obj, out List<FrameObjectBase>? kids))
            foreach (FrameObjectBase k in kids)
                if (claimed.Add(k))
                    node.Children.Add(BuildNode(k, document, childrenOf, meshes, instanceMap, claimed));

        return node;
    }

    // Scene category for streaming filters, from the majority season class of its mesh leaves (FrameNameTable
    // flags, cascaded to unflagged children — see ClassifyNode):
    //  Proxy — most meshes are proxies (cityNN / neighbor-district / LOD backdrops);
    //  Snow  — most meshes are winter geometry (flag_1|flag_2);
    //  otherwise Normal.
    private static string CategorizeScene(SdsFrameNode scene, IReadOnlyCollection<string> others)
    {
        if ((scene.Name ?? "").Contains("proxy", StringComparison.OrdinalIgnoreCase)) return "Proxy";

        int total = 0, proxy = 0, snow = 0;
        CountScene(scene, others, ref total, ref proxy, ref snow, SeasonClass.Normal);
        if (total == 0) return "Normal";
        if (proxy * 2 >= total) return "Proxy";
        if (snow * 2 >= total) return "Snow";
        return "Normal";
    }

    private static void CountScene(SdsFrameNode n, IReadOnlyCollection<string> others,
        ref int total, ref int proxy, ref int snow, SeasonClass inherited)
    {
        SeasonClass cls = ClassifyNode(n, others, inherited);
        if (n.Mesh != null)
        {
            total++;
            if (cls == SeasonClass.Proxy) proxy++;
            else if (cls == SeasonClass.Snow) snow++;
        }
        foreach (SdsFrameNode c in n.Children) CountScene(c, others, ref total, ref proxy, ref snow, cls);
    }

    private enum SeasonClass { Normal, Proxy, Snow }

    // FrameNameTable flag semantics (verified across every Mafia II district via --probe-flags/--probe-flagtree):
    //   normal      = no flags        (value 0)
    //   snow/winter = flag_1 | flag_2 (value 3)      — z-prefixed winter geometry (its own scene4XX folder)
    //   proxy       = any other non-zero combination — cityNN (flag_2) plus neighbor-district / LOD proxies,
    //                 which carry assorted flag_1|256|512|1024|2048|4096 bits.
    // Flags live on the NAMED (frame-name-table) objects; a proxy/snow group's mesh children are NOT on the
    // table and carry no flag, so they inherit their nearest flagged ancestor's class (cascade). Objects that
    // are on the table are authoritative by their own flag. Objects with no flagged ancestor (interiors, stray
    // nodes) fall through to the legacy name heuristic.
    private const int SnowFlags = 3;   // flag_1 | flag_2

    private static SeasonClass ClassifyNode(SdsFrameNode n, IReadOnlyCollection<string> others, SeasonClass inherited)
    {
        if (n.Source is IFrameNode o && o.IsOnNameTable)
        {
            int f = o.NameTableFlags;
            return f == 0 ? SeasonClass.Normal : f == SnowFlags ? SeasonClass.Snow : SeasonClass.Proxy;
        }
        // Not on the frame name table: inherit the ancestor's class; only when there is no flagged ancestor
        // (still Normal) does the name heuristic get to upgrade a stray proxy/snow node (legacy fallback).
        if (inherited != SeasonClass.Normal) return inherited;
        string nm = n.Name ?? "";
        if (IsProxyMesh(nm, others)) return SeasonClass.Proxy;
        if (IsSnowMesh(nm)) return SeasonClass.Snow;
        return SeasonClass.Normal;
    }

    // Proxy mesh: proxy_… / cityNN / <neighbor district name>+digit (chinatown900, uppertown18…).
    private static bool IsProxyMesh(string nm, IReadOnlyCollection<string> others)
    {
        if (nm.Contains("proxy", StringComparison.OrdinalIgnoreCase)) return true;
        if (StartsWithNameThenDigit(nm, "city")) return true;
        foreach (string d in others)
            if (StartsWithNameThenDigit(nm, d)) return true;
        return false;
    }

    // Snow: name starts with z/Z, then a digit (z10_64_…).
    private static bool IsSnowMesh(string nm) =>
        nm.Length >= 2 && (nm[0] == 'z' || nm[0] == 'Z') && char.IsDigit(nm[1]);

    private static bool StartsWithNameThenDigit(string nm, string name) =>
        nm.Length > name.Length
        && nm.StartsWith(name, StringComparison.OrdinalIgnoreCase)
        && char.IsDigit(nm[name.Length]);

    // Object type = class name without the FrameObject prefix (SingleMesh→Mesh). No switch-by-type —
    // that has a trap of unreachable patterns due to inheritance (Area:Joint, Frame:Joint, etc.).
    private static string KindOf(FrameObjectBase o)
    {
        string t = o.GetType().Name;
        if (t.StartsWith("FrameObject", StringComparison.Ordinal)) t = t.Substring(11);
        return t == "SingleMesh" ? "Mesh" : t;
    }

    /// <summary>
    /// Full-fidelity LOD0 decode: float channels plus the raw packed bytes and quantization
    /// parameters. Shared by the viewport conversion below and the Blender bridge exporter; null for
    /// a mesh without usable LOD0 buffers. Public for the bridge and the diagnostics probes.
    /// </summary>
    public static DecodedMesh? DecodeLod0(FrameObjectSingleMesh mesh)
    {
        FrameGeometry geom = mesh.Geometry;
        if (geom.LOD == null || geom.LOD.Length == 0)
        {
            return null;
        }

        // LOD0 — maximum detail.
        FrameLOD lod = geom.LOD[0];
        var vertexBuffer = mesh.GetVertexBuffer(0);
        var indexBuffer = mesh.GetIndexBuffer(0);
        if (vertexBuffer?.Data == null || indexBuffer == null)
        {
            return null;
        }

        lod.GetVertexOffsets(out int stride);
        int numVerts = lod.NumVerts;
        if (stride <= 0 || numVerts <= 0 || (long)numVerts * stride > vertexBuffer.Data.Length)
        {
            return null;
        }

        byte[] raw = new byte[numVerts * stride];
        Array.Copy(vertexBuffer.Data, raw, raw.Length);

        var positions = new Vector3[numVerts];
        var normals = new Vector3[numVerts];
        var uvs = new Vector2[numVerts];
        // Tangent frame is only present when the vertex declaration advertises it; otherwise the shader
        // falls back to the vertex normal (flat-normal path), so we leave these null.
        bool hasTangent = lod.VertexDeclaration.HasFlag(VertexFlags.Tangent);
        Vector3[]? tangents = hasTangent ? new Vector3[numVerts] : null;
        Vector3[]? binormals = hasTangent ? new Vector3[numVerts] : null;
        // Straight into the channel arrays: no wire, no Vertex per vertex. A district is millions
        // of vertices, and the full-fidelity path allocated one object per vertex plus a ~124-byte
        // wire record for a 16-20 byte packed vertex — enough LOH churn to stall the render thread.
        // (Binormals already carry the handedness sign applied by the decoder.)
        VertexTranslator.DecompressChannels(
            raw, numVerts, lod.VertexDeclaration, geom.DecompressionOffset, geom.DecompressionFactor,
            positions, normals, uvs, tangents, binormals);

        return new DecodedMesh
        {
            Frame = mesh,
            Declaration = lod.VertexDeclaration,
            Stride = stride,
            NumVerts = numVerts,
            DecompressionOffset = geom.DecompressionOffset,
            DecompressionFactor = geom.DecompressionFactor,
            RawVertexData = raw,
            Positions = positions,
            Normals = normals,
            UVs = uvs,
            Tangents = tangents,
            Binormals = binormals,
            Indices = indexBuffer.GetData(),
        };
    }

    // Internal for the frame duplicator, which needs a render-ready MeshData for a freshly cloned object.
    internal static MeshData? TryConvert(FrameObjectSingleMesh mesh, Matrix4x4[]? instances = null,
        float[]? drawDistances = null, Matrix4x4? placement = null)
    {
        try
        {
            DecodedMesh? decoded = DecodeLod0(mesh);
            if (decoded == null)
            {
                return null;
            }

            MeshPart[] parts = BuildParts(mesh, decoded.Indices.Length);

            // An actor-placed mesh carries an identity matrix of its own — the actor pack holds where it
            // stands (see ActorPlacements), so the placement goes in front of the frame's own world transform.
            //
            // An INSTANCED mesh is a different animal and gets none of this. A translokator copy's matrix is
            // already an absolute world placement (CrashPlacements.CloudFor: the row's own offset times the
            // .tra record — the format has no parent at all), so composing it with an actor placement adds two
            // unrelated world transforms together and scatters the whole row. It bites for real: city_crash.sds
            // ships its own actor packs which claim the very prototypes the .tra table instances. Picking and
            // the selection outline build their matrices straight from the table, so folding here would also
            // put the geometry somewhere the ray never looks.
            // The test matches GpuMesh's own "is this a cloud" rule, so the two never disagree about which
            // matrix moves the geometry.
            Matrix4x4 place = instances is { Length: > 0 } ? Matrix4x4.Identity : placement ?? Matrix4x4.Identity;

            return new MeshData
            {
                Name = mesh.Name?.ToString() ?? "mesh",
                World = mesh.WorldTransform * place,
                Positions = decoded.Positions,
                Normals = decoded.Normals,
                UVs = decoded.UVs,
                Tangents = decoded.Tangents,
                Binormals = decoded.Binormals,
                Indices = decoded.Indices,
                Parts = parts,
                Instances = instances,
                InstanceDrawDistances = drawDistances,
            };
        }
        catch
        {
            // Skip a broken/non-standard mesh, don't crash the rest of the scene.
            return null;
        }
    }

    // Split the mesh indices into ranges by material (LOD0) and resolve the diffuse texture.
    // Internal for the bridge applier, which rebuilds a MeshData after a geometry push.
    internal static MeshPart[] BuildParts(FrameObjectSingleMesh mesh, int indexCount)
    {
        FrameMaterial fm = mesh.Material;
        if (fm?.Materials != null && fm.Materials.Count > 0 && fm.Materials[0] != null && fm.Materials[0].Length > 0)
        {
            MaterialStruct[] mats = fm.Materials[0];
            var parts = new MeshPart[mats.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                var tex = MafiaMaterials.GetMaterialTextures(mats[i].MaterialHash);
                parts[i] = new MeshPart(mats[i].StartIndex, mats[i].NumFaces * 3, tex.Diffuse, tex.Normal, tex.Specular,
                    mats[i].MaterialHash);
            }
            return parts;
        }

        // No material table — draw the whole mesh as one part without a texture.
        return new[] { new MeshPart(0, indexCount, null) };
    }
}
