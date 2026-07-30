using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Bridge.Geometry;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;

namespace Illusion.Assets.Bridge;

/// <summary>
/// Turns one scene object into a kind="mesh" exchange payload: full-channel LOD0 decode, bit-exact
/// weld (keyed on the RAW quantized position bytes, handedness bit masked), per-loop attributes with
/// the Blender UV V-flip, and material slots resolved to absolute .dds paths in the document's
/// extracted folder.
/// </summary>
public static class BridgeMeshExporter
{
    /// <summary>Exports a node's mesh; null with a human-readable <paramref name="skipReason"/> when
    /// the object cannot ride the bridge (instanced content is the caller's check — it needs the GPU
    /// mesh, which this layer never sees).</summary>
    public static MeshObjectPayload? TryExport(IFrameNode node, ISceneDocument document, out string? skipReason)
    {
        skipReason = null;

        if (node is not FrameNodeAdapter adapter)
        {
            skipReason = "not a frame-backed object";
            return null;
        }
        // Exact type: FrameObjectModel (skinned) derives from FrameObjectSingleMesh and its extra
        // blend data would not survive a plain-mesh roundtrip.
        if (adapter.Frame is not FrameObjectSingleMesh frame || frame.GetType() != typeof(FrameObjectSingleMesh))
        {
            skipReason = $"unsupported frame type {adapter.Frame.GetType().Name}";
            return null;
        }

        DecodedMesh? decoded;
        try
        {
            decoded = SdsMeshLoader.DecodeLod0(frame);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            skipReason = "undecodable mesh: " + ex.Message;
            return null;
        }
        if (decoded == null)
        {
            skipReason = "mesh has no usable LOD0 buffers";
            return null;
        }
        if (decoded.Declaration.HasFlag(VertexFlags.Skin))
        {
            skipReason = "skinned vertex data";
            return null;
        }

        WeldedMesh welded = WeldMapBuilder.Build(
            BuildWeldKeys(decoded), decoded.Positions, decoded.Normals, FlipV(decoded.UVs), decoded.Indices);

        List<MeshMaterialInfo> materials = ResolveMaterials(frame, document, decoded.Indices.Length);

        // Material slot per KEPT triangle (degenerates were filtered by the weld — see WeldedMesh).
        ushort[] perSourceTriangle = BuildFaceMaterials(materials, decoded.Indices.Length);
        var faceMaterials = new ushort[welded.KeptTriangles.Length];
        for (int i = 0; i < faceMaterials.Length; i++)
            faceMaterials[i] = perSourceTriangle[welded.KeptTriangles[i]];

        return new MeshObjectPayload
        {
            Id = MakeId(frame, document),
            Name = frame.Name?.ToString() ?? "mesh",
            // The NODE's world, not the frame's: an actor-placed object is a prototype parked at the origin,
            // and its spawn matrix lives in the .act. Sending the frame's own world would drop it at (0,0,0)
            // in Blender while the viewport shows it in the street — and, worse, the push-back compares the
            // returned matrix against this same placement-aware world, so every untouched prototype would
            // read as moved and have the inverse placement baked into its local transform. The two are the
            // same matrix for every frame no actor places.
            World = adapter.WorldTransform,
            Local = frame.LocalTransform,
            Positions = welded.Positions,
            LoopVertexIndices = welded.LoopVertexIndices,
            LoopNormals = welded.LoopNormals,
            LoopUvs = welded.LoopUvs,
            LoopOrigIndex = welded.LoopOrigIndex,
            FaceMaterials = faceMaterials,
            Materials = materials,
            DroppedDegenerateFaces = welded.DroppedDegenerateTriangles,
            DroppedDuplicateFaces = welded.DroppedDuplicateTriangles,
            VertexDeclaration = (uint)decoded.Declaration,
            DecompressionOffset = decoded.DecompressionOffset,
            DecompressionFactor = decoded.DecompressionFactor,
        };
    }

    /// <summary>Stable-within-session object id: archive-relative path + frame name + runtime RefID.
    /// RefID is NOT stable across toolkit runs — the session controller resolves ids only through its
    /// own export map, and the session GUID guards against stale pushes.</summary>
    public static string MakeId(FrameObjectBase frame, ISceneDocument document)
    {
        string rel = MafiaEnvironment.IsInitialized
            ? Path.GetRelativePath(MafiaEnvironment.GameRoot, document.SourceArchive.FullName)
            : document.SourceArchive.Name;
        return $"{rel.Replace('\\', '/')}|{frame.Name}|{frame.RefID}";
    }

    // Weld key = the raw quantized position triple (x | y<<16 | z<<32) with the binormal-handedness
    // top bit of Z masked off — two split vertices weld iff the game data itself agrees on position.
    // Internal: the applier re-derives the exported face set from the same keys to detect topology
    // changes (deleted/reshaped faces) in a pushed mesh.
    internal static ulong[] BuildWeldKeys(DecodedMesh decoded)
    {
        Dictionary<VertexFlags, VertexOffset> offsets = VertexLayout.ComputeOffsets(decoded.Declaration, out _);
        int posOffset = offsets[VertexFlags.Position].Offset;
        byte[] data = decoded.RawVertexData;

        var keys = new ulong[decoded.NumVerts];
        for (int i = 0; i < keys.Length; i++)
        {
            int at = i * decoded.Stride + posOffset;
            ulong x = (ulong)(data[at + 0] | (data[at + 1] << 8));
            ulong y = (ulong)(data[at + 2] | (data[at + 3] << 8));
            ulong z = (ulong)(data[at + 4] | (data[at + 5] << 8)) & 0x7FFF;
            keys[i] = x | (y << 16) | (z << 32);
        }
        return keys;
    }

    // D3D's UV origin is top-left, Blender's bottom-left — the container carries Blender's convention.
    private static Vector2[] FlipV(Vector2[] uvs)
    {
        var flipped = new Vector2[uvs.Length];
        for (int i = 0; i < uvs.Length; i++) flipped[i] = new Vector2(uvs[i].X, 1f - uvs[i].Y);
        return flipped;
    }

    private static List<MeshMaterialInfo> ResolveMaterials(
        FrameObjectSingleMesh frame, ISceneDocument document, int indexCount)
    {
        var result = new List<MeshMaterialInfo>();
        List<string> textureDirs = TextureSearchDirs(document);

        FrameMaterial fm = frame.Material;
        if (fm?.Materials is { Count: > 0 } && fm.Materials[0] is { Length: > 0 } mats)
        {
            MafiaMaterials.EnsureLoaded();
            foreach (MaterialStruct mat in mats)
            {
                MafiaMaterials.MaterialTextures tex = MafiaMaterials.GetMaterialTextures(mat.MaterialHash);
                result.Add(new MeshMaterialInfo
                {
                    Hash = "0x" + mat.MaterialHash.ToString("X16"),
                    // The frame stream stores only the hash — the display name lives in the MTL libraries.
                    Name = string.IsNullOrEmpty(mat.MaterialName)
                        ? MafiaMaterials.GetMaterialName(mat.MaterialHash)
                        : mat.MaterialName,
                    Diffuse = ResolveTexture(textureDirs, tex.Diffuse),
                    Normal = ResolveTexture(textureDirs, tex.Normal),
                    // Mafia II ships its S001 normal maps DXT5nm-swizzled (X in alpha) — flag them so
                    // the addon unswizzles for preview.
                    NormalIsDxt5nm = tex.Normal != null,
                    Specular = ResolveTexture(textureDirs, tex.Specular),
                    StartIndex = mat.StartIndex,
                    NumFaces = mat.NumFaces,
                });
            }
            return result;
        }

        // No material table — one implicit slot covering the whole mesh.
        result.Add(new MeshMaterialInfo { Hash = "0x0", StartIndex = 0, NumFaces = indexCount / 3 });
        return result;
    }

    // The same folder set the viewport's TextureLibrary probes: the document's own extracted
    // folder plus the shared season ground archives — many district materials reference textures
    // that physically live there.
    private static List<string> TextureSearchDirs(ISceneDocument document)
    {
        var dirs = new List<string> { SdsMeshLoader.EnsureExtracted(document.SourceArchive) };
        if (MafiaEnvironment.IsInitialized)
        {
            foreach (string ground in new[] { "ground_leto", "ground_zima" })
            {
                string sds = Path.Combine(MafiaEnvironment.PcFolder, "sds", "ground", ground + ".sds");
                if (File.Exists(sds)) dirs.Add(SdsMeshLoader.EnsureExtracted(new FileInfo(sds)));
            }
        }
        return dirs;
    }

    private static string? ResolveTexture(List<string> textureDirs, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (string dir in textureDirs)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // Material slot per triangle, from the LOD0 index ranges the slots cover.
    private static ushort[] BuildFaceMaterials(List<MeshMaterialInfo> materials, int indexCount)
    {
        var faces = new ushort[indexCount / 3];
        for (int slot = 0; slot < materials.Count; slot++)
        {
            MeshMaterialInfo mat = materials[slot];
            int firstFace = mat.StartIndex / 3;
            for (int f = 0; f < mat.NumFaces && firstFace + f < faces.Length; f++)
                faces[firstFace + f] = (ushort)slot;
        }
        return faces;
    }
}
