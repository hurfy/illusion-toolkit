using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Bridge.Geometry;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;
using Illusion.Formats.Mathematics;

namespace Illusion.Assets.Bridge;

/// <summary>
/// Applies a pushed Blender mesh back onto its frame object — the count-preserving path. The core
/// contract: a vertex whose packed re-encoding equals its original bytes keeps the ORIGINAL bytes
/// verbatim (original tangents and quantization intact); only genuinely touched vertices are
/// re-encoded, with freshly generated tangent frames. Computation is side-effect-free; the caller
/// applies/undoes the mutation on the UI thread via <see cref="ApplyResult"/>.
/// </summary>
public static class BridgeMeshApplier
{
    /// <summary>The extra state a topology rebuild swaps besides the vertex buffer: the whole LOD0
    /// (fresh split info + trivial OPCODE partition), the material ranges, and the index buffer.</summary>
    internal sealed class RebuildData
    {
        internal Formats.Frames.Resources.FrameLOD OldLod = null!;
        internal Formats.Frames.Resources.FrameLOD NewLod = null!;
        internal Formats.Frames.Resources.MaterialStruct[] OldMaterials = null!;
        internal Formats.Frames.Resources.MaterialStruct[] NewMaterials = null!;
        internal int OldLodMatCount;
        internal int NewLodMatCount;
        internal IndexBuffer IndexBuffer = null!;
        internal uint[] OldIndexData = null!;
        internal uint[] NewIndexData = null!;
        internal int OldIndexFormat;
        internal int NewIndexFormat;
    }

    /// <summary>A computed geometry change, ready to flip in and out of the live frame data.</summary>
    public sealed class ApplyResult
    {
        internal FrameObjectSingleMesh Frame = null!;
        internal VertexBuffer Buffer = null!;
        internal SceneDocumentAdapter? Document;
        internal RebuildData? Rebuild;
        internal BoundingBox OldBounds;
        internal BoundingBox NewBounds;
        internal BoundingBox OldMaterialBounds;
        internal Vector3 OldDecompressionOffset;
        internal float OldDecompressionFactor;

        /// <summary>Pre-push packed vertex bytes (diagnostics/probes; also the undo payload).</summary>
        public byte[] OldVertexData { get; internal set; } = null!;

        /// <summary>Post-push packed vertex bytes.</summary>
        public byte[] NewVertexData { get; internal set; } = null!;

        /// <summary>The quantization scale after the push (same as before unless <see cref="Requantized"/>).</summary>
        public float NewDecompressionFactor { get; internal set; }

        /// <summary>The quantization origin after the push — with the scale, what
        /// <see cref="NewVertexData"/> has to be decoded against.</summary>
        public Vector3 NewDecompressionOffset { get; internal set; }

        /// <summary>Fresh render-ready mesh (null when <see cref="Unchanged"/>).</summary>
        public MeshData? NewMesh { get; internal set; }

        public int TouchedVertices { get; internal set; }

        /// <summary>How many vertices took their bone influences from Blender's vertex groups rather than
        /// from a donor. Zero when the push carried no weights (an older addon, or a mesh with no rig).</summary>
        public int SkinFromBlender { get; internal set; }

        /// <summary>The whole position range was re-quantized (the edit outgrew the old AABB).</summary>
        public bool Requantized { get; internal set; }

        /// <summary>The push was byte-identical — nothing to mutate, ack as applied.</summary>
        public bool Unchanged { get; internal set; }

        /// <summary>
        /// The mesh is SKINNED and the push carried no vertex weights at all — so every vertex group in
        /// Blender was ignored, whether it changed or not.
        ///
        /// <para>
        /// This is the failure that has no symptom of its own: re-weighting a vertex changes nothing in the
        /// file, so the push reports "nothing changed" and stays silent, and geometry with no group of its
        /// own quietly keeps the skin of whatever vertex was nearest — which is how a part modelled on the
        /// bonnet ends up riding a door. The addon only sends weights when it can find the rig and the
        /// groups are named after its bones; when it cannot, it says nothing, so this has to.
        /// </para>
        /// </summary>
        public bool SkinNotSent { get; internal set; }

        /// <summary>The push changed the mesh's topology — the whole LOD0 was rebuilt (lower LODs
        /// and collision keep their old shape until their own pipelines exist).</summary>
        public bool TopologyRebuilt => Rebuild != null;

        /// <summary>Writes the new geometry into the live frame data (initial apply and redo).</summary>
        public void ApplyNew()
        {
            Buffer.Data = NewVertexData;
            Frame.Geometry.DecompressionOffset = NewDecompressionOffset;
            Frame.Geometry.DecompressionFactor = NewDecompressionFactor;
            Frame.Boundings = NewBounds;
            Frame.Material.Bounds = NewBounds;
            Document?.MarkVertexBufferDirty(Buffer.Hash);
            if (Rebuild != null)
            {
                Frame.Geometry.LOD[0] = Rebuild.NewLod;
                Frame.Material.Materials[0] = Rebuild.NewMaterials;
                Frame.Material.LodMatCount[0] = Rebuild.NewLodMatCount;
                Rebuild.IndexBuffer.SetFormat(Rebuild.NewIndexFormat);
                Rebuild.IndexBuffer.SetData(Rebuild.NewIndexData);
                Document?.MarkIndexBufferDirty(Rebuild.IndexBuffer.Hash);
            }
        }

        /// <summary>Restores the pre-push frame data (undo). Still marks the buffers dirty — a save
        /// may already have written the pushed bytes, so the working copy must be rewritten.</summary>
        public void RestoreOriginal()
        {
            Buffer.Data = OldVertexData;
            Frame.Geometry.DecompressionOffset = OldDecompressionOffset;
            Frame.Geometry.DecompressionFactor = OldDecompressionFactor;
            Frame.Boundings = OldBounds;
            Frame.Material.Bounds = OldMaterialBounds;
            Document?.MarkVertexBufferDirty(Buffer.Hash);
            if (Rebuild != null)
            {
                Frame.Geometry.LOD[0] = Rebuild.OldLod;
                Frame.Material.Materials[0] = Rebuild.OldMaterials;
                Frame.Material.LodMatCount[0] = Rebuild.OldLodMatCount;
                Rebuild.IndexBuffer.SetFormat(Rebuild.OldIndexFormat);
                Rebuild.IndexBuffer.SetData(Rebuild.OldIndexData);
                Document?.MarkIndexBufferDirty(Rebuild.IndexBuffer.Hash);
            }
        }
    }

    /// <summary>The push entry point: the count-preserving fast path when the topology is intact,
    /// else the full LOD0 rebuild. Null with a reason only when the object genuinely cannot apply
    /// (unsupported object, malformed payload, an edge the rebuild does not cover yet).</summary>
    public static ApplyResult? TryApply(IFrameNode node, MeshObjectPayload payload, out string? skipReason)
    {
        if (UngroupedVertices(node, payload) is var stray and > 0)
        {
            skipReason = $"{stray} vertices are in no vertex group. On a rigged model every vertex has to "
                + "name the bone it belongs to; one that names none is guessed at from whatever vertex "
                + "happens to be nearest, which is how a part modelled on the bonnet ends up riding a door. "
                + "Assign them to a group named after a bone and push again.";
            return null;
        }

        ApplyResult? result = TryApplyCountPreserving(node, payload, out skipReason);
        if (result != null) return result;
        if (skipReason == null || !NeedsRebuild(skipReason)) return null;
        return TryApplyRebuild(node, payload, out skipReason);
    }

    /// <summary>
    /// Whether a failed fast path is one the REBUILD can still answer. Two reasons mean the same thing here:
    /// the mesh in Blender no longer lines up with the one in the archive, vertex for vertex.
    /// <para>
    /// "Topology changed" is the obvious case — geometry added or removed. An out-of-range source index is
    /// the STALE case, and it used to be fatal: a push that rebuilt the archive's mesh left the Blender scene
    /// still carrying the OLD <c>_orig_index</c> mapping, and since a rebuild usually ends up with fewer split
    /// vertices than it started with, the very next push pointed past the end and was refused outright. From
    /// the user's side that is "I move a vertex, or take one out of a group, press push, and nothing happens"
    /// — with no way to tell that the scene had gone stale. The rebuild derives everything from the payload
    /// and needs no mapping at all, so it is exactly the right answer to a mapping that has expired.
    /// </para>
    /// </summary>
    /// <summary>
    /// How many of a rigged mesh's DRAWN vertices carry no bone influence at all.
    ///
    /// <para>
    /// Zero for anything that is not a skinned model, and zero when the push carried no weights at all —
    /// that is a different fault with its own report (<see cref="ApplyResult.SkinNotSent"/>), and treating it
    /// as "every vertex is ungrouped" would refuse a push nobody could fix from inside Blender.
    /// </para>
    /// <para>
    /// Only vertices something actually draws are counted: a Blender scene accumulates loose vertices that no
    /// face uses, and refusing a push over geometry that is not even in the mesh would be the toolkit being
    /// pedantic about nothing.
    /// </para>
    /// </summary>
    private static int UngroupedVertices(IFrameNode node, MeshObjectPayload payload)
    {
        if (node is not FrameNodeAdapter { Frame: FrameObjectModel }) return 0;
        float[] weights = payload.BoneWeights;
        if (weights.Length < payload.Positions.Length * 4) return 0;

        var drawn = new bool[payload.Positions.Length];
        foreach (uint index in payload.LoopVertexIndices)
        {
            if (index < drawn.Length) drawn[index] = true;
        }

        int stray = 0;
        for (int v = 0; v < payload.Positions.Length; v++)
        {
            if (!drawn[v]) continue;
            float total = weights[(v * 4) + 0] + weights[(v * 4) + 1]
                + weights[(v * 4) + 2] + weights[(v * 4) + 3];
            if (total <= 0f) stray++;
        }
        return stray;
    }

    private static bool NeedsRebuild(string reason) =>
        reason.StartsWith("topology changed", StringComparison.Ordinal)
        || reason.Contains("source vertex index out of range", StringComparison.Ordinal);

    /// <summary>Computes the count-preserving application of <paramref name="payload"/> to
    /// <paramref name="node"/>'s mesh. Null with a reason when it cannot apply (topology changed,
    /// unsupported object, malformed payload) — the caller reports it as a per-object skip.</summary>
    public static ApplyResult? TryApplyCountPreserving(IFrameNode node, MeshObjectPayload payload, out string? skipReason)
    {
        skipReason = null;
        // A skinned model is accepted HERE and only here: this path keeps the vertex count, so every vertex
        // has a donor to take its four bone influences from and the skin survives untouched. The rebuild
        // path below cannot say the same.
        if (node is not FrameNodeAdapter adapter
            || adapter.Frame is not FrameObjectSingleMesh frame
            || (frame.GetType() != typeof(FrameObjectSingleMesh) && frame is not FrameObjectModel))
        {
            skipReason = "unsupported object";
            return null;
        }

        DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(frame);
        if (decoded == null)
        {
            skipReason = "mesh has no usable LOD0 buffers";
            return null;
        }

        ResplitResult? resplit = VertexResplitter.TryResplitCountPreserving(payload, decoded.NumVerts, out string? reason);
        if (resplit == null)
        {
            skipReason = reason;
            return null;
        }

        // The resplit only proves every pushed corner maps onto a source vertex — DELETED or
        // reshaped faces would sail through it as "nothing changed". Re-derive the face set the
        // exporter sent (same weld, same degenerate/duplicate filter) and require the push to cover
        // exactly it; any difference is a topology change for the rebuild path of a later phase.
        if (!FaceSetMatches(decoded, payload, out string? topologyReason))
        {
            skipReason = topologyReason;
            return null;
        }

        // Merged per-split-vertex attributes: pushed where a loop carried them, original elsewhere.
        // Normals are direction-snapped: Blender re-normalizes custom normals, so an untouched
        // normal comes home unit-length while the decoded original is not — same DIRECTION means
        // unchanged, and the original (with its exact bytes) is kept.
        var newPositions = new Vector3[decoded.NumVerts];
        var newNormals = new Vector3[decoded.NumVerts];
        var newUvs = new Vector2[decoded.NumVerts];
        for (int i = 0; i < decoded.NumVerts; i++)
        {
            newPositions[i] = resplit.Seen[i] ? resplit.Positions[i] : decoded.Positions[i];
            newUvs[i] = resplit.Seen[i] ? resplit.Uvs[i] : decoded.UVs[i];
            newNormals[i] = resplit.Seen[i] && !SameDirection(resplit.Normals[i], decoded.Normals[i])
                ? resplit.Normals[i]
                : decoded.Normals[i];
        }

        int stride = decoded.Stride;
        byte[] original = decoded.RawVertexData;

        // Pass 1 — decode the whole buffer once, apply the pushed positions/normals/UVs, and
        // re-encode once over the ORIGINAL bytes with the ORIGINAL quantization. A vertex whose
        // re-encoded slice is byte-equal to the original is untouched (the compare is naturally
        // quantization-tolerant — sub-quantum float drift lands on the same bytes). One native
        // crossing each way, not two per vertex.
        Vertex[] vertices = VertexTranslator.DecompressBuffer(
            original, decoded.NumVerts, decoded.Declaration,
            decoded.DecompressionOffset, decoded.DecompressionFactor);
        for (int i = 0; i < decoded.NumVerts; i++)
        {
            vertices[i].Position = newPositions[i];
            vertices[i].Normal = newNormals[i];
            vertices[i].UVs[0] = new Half2(newUvs[i].X, newUvs[i].Y);
        }

        // Re-weighting without touching a single vertex position comes through HERE, and ignoring the
        // vertex groups on this path would leave the same silence that made a hood panel ride the door.
        int fromBlender = 0;
        byte[]? reweighted = null;
        bool skinExpected = frame is FrameObjectModel && decoded.Declaration.HasFlag(VertexFlags.Skin);
        bool skinSent = payload.BoneIndices.Length >= payload.Positions.Length * 4
            && payload.BoneWeights.Length >= payload.Positions.Length * 4;
        if (frame is FrameObjectModel weighted
            && decoded.Declaration.HasFlag(VertexFlags.Skin)
            && payload.BoneIndices is { } pushedIds && payload.BoneWeights is { } pushedWeights
            && pushedIds.Length >= payload.Positions.Length * 4
            && pushedWeights.Length >= payload.Positions.Length * 4
            && BoneCountOf(weighted) is int rigBones and > 0)
        {
            MaterialStruct[] mats = frame.Material.Materials[0];
            byte[]? currentGlobal = SdsMeshLoader.ResolveBoneRemap(
                weighted, SdsMeshLoader.BuildParts(weighted, decoded.Indices.Length), decoded);
            if (currentGlobal != null)
            {
                var global = new byte[decoded.NumVerts * 4];
                Array.Copy(currentGlobal, global, Math.Min(currentGlobal.Length, global.Length));
                for (int i = 0; i < decoded.NumVerts; i++)
                {
                    if (!resplit.Seen[i]) continue;
                    if (TakePushedSkin(pushedIds, pushedWeights, resplit.Welded[i], rigBones,
                            vertices[i], global, i))
                    {
                        fromBlender++;
                    }
                }
                // Back into the model's own pools. The material set did not change on this path, so this
                // only re-localizes the ids — but a weight moved onto a bone the pool cannot name has to
                // be refused rather than written as whatever sits at that offset.
                if (fromBlender > 0)
                {
                    if (!RemapBlendInfo(weighted, vertices, global, decoded.Indices, mats, mats,
                            out string? weightReason))
                    {
                        skipReason = weightReason;
                        return null;
                    }
                    reweighted = global;
                }
            }
        }
        byte[] candidate = VertexCompressor.CompressBuffer(
            original, vertices, decoded.Declaration,
            decoded.DecompressionOffset, decoded.DecompressionFactor);

        var touched = new bool[decoded.NumVerts];
        int touchedCount = 0;
        for (int i = 0; i < decoded.NumVerts; i++)
        {
            if (!candidate.AsSpan(i * stride, stride).SequenceEqual(original.AsSpan(i * stride, stride)))
            {
                touched[i] = true;
                touchedCount++;
            }
        }

        if (touchedCount == 0)
        {
            // "Nothing changed" and "the vertex groups never arrived" look identical from here, and the
            // second one is the whole reason a re-weight can be pressed all day with no effect. Say which.
            return new ApplyResult
            {
                Unchanged = true,
                TouchedVertices = 0,
                SkinNotSent = skinExpected && !skinSent,
            };
        }

        // Quantization range: a touched vertex may have left the old AABB → recompute offset/factor
        // over the new positions (15-bit Z rule) and re-encode everything.
        bool requantize = NeedsRequantize(newPositions, decoded.DecompressionOffset, decoded.DecompressionFactor);
        Vector3 newOffset = decoded.DecompressionOffset;
        float newFactor = decoded.DecompressionFactor;
        if (requantize)
        {
            (newOffset, newFactor) = ComputeQuantization(newPositions);
        }

        // Regenerated tangent frames — applied ONLY to touched vertices; untouched ones keep their
        // original frames (bytes or byte-identical re-encodes).
        bool hasTangent = decoded.Declaration.HasFlag(VertexFlags.Tangent);
        Vector3[]? regenT = null, regenB = null;
        if (hasTangent)
        {
            (regenT, regenB) = TangentGenerator.Compute(newPositions, newNormals, newUvs, decoded.Indices);
        }

        byte[] newData;
        if (requantize || (hasTangent && touchedCount > 0))
        {
            // Re-encode the whole buffer: either the lattice changed (all vertices), or touched
            // vertices need their regenerated tangent frame. Untouched vertices under an unchanged
            // lattice re-encode to their exact original bytes (proven by --probe-bridge-vertex).
            if (hasTangent)
            {
                for (int i = 0; i < decoded.NumVerts; i++)
                {
                    if (!touched[i]) continue;
                    vertices[i].Tangent = regenT![i];
                    vertices[i].Binormal = regenB![i];
                }
            }
            newData = VertexCompressor.CompressBuffer(
                original, vertices, decoded.Declaration, newOffset, newFactor);
        }
        else
        {
            // No requantize and no tangents: the pass-1 candidate already has touched vertices
            // re-encoded at the original lattice and untouched vertices at their original bytes.
            newData = candidate;
        }

        (Vector3 min, Vector3 max) = Aabb(newPositions);
        var result = new ApplyResult
        {
            Frame = frame,
            Buffer = frame.GetVertexBuffer(0)!,
            Document = adapter.Document,
            OldVertexData = original,
            NewVertexData = newData,
            OldBounds = frame.Boundings,
            OldMaterialBounds = frame.Material.Bounds,
            NewBounds = new BoundingBox { Min = min, Max = max },
            OldDecompressionOffset = decoded.DecompressionOffset,
            OldDecompressionFactor = decoded.DecompressionFactor,
            NewDecompressionOffset = newOffset,
            NewDecompressionFactor = newFactor,
            TouchedVertices = touchedCount,
            SkinFromBlender = fromBlender,
            SkinNotSent = skinExpected && !skinSent,
            Requantized = requantize,
        };

        // Render-ready mesh from the merged arrays (tangents mixed: regenerated where touched).
        Vector3[]? tangents = null, binormals = null;
        if (hasTangent)
        {
            tangents = new Vector3[decoded.NumVerts];
            binormals = new Vector3[decoded.NumVerts];
            for (int i = 0; i < decoded.NumVerts; i++)
            {
                tangents[i] = touched[i] ? regenT![i] : decoded.Tangents![i];
                binormals[i] = touched[i] ? regenB![i] : decoded.Binormals![i];
            }
        }
        // The replacement mesh must stay SKINNED. A count-preserving reshape does not touch a vertex's
        // influences, but the mesh handed to the renderer is built from scratch — and one built without them
        // is uploaded without a skin buffer, after which the body silently stops following its bones for the
        // rest of the session while the rig still moves.
        MeshPart[] countParts = SdsMeshLoader.BuildParts(frame, decoded.Indices.Length);
        var countSkin = SdsMeshLoader.SkinOf(frame, decoded, countParts);
        byte[]? countIds = countSkin.Indices;
        float[]? countWeights = countSkin.Weights;
        if (reweighted != null)
        {
            // The renderer has to see what Blender said, not what is still on the wire — the wire bytes are
            // only replaced when the caller commits, and reading them here would show the OLD binding.
            countIds = reweighted;
            countWeights = new float[decoded.NumVerts * 4];
            for (int i = 0; i < decoded.NumVerts; i++)
                for (int k = 0; k < 4; k++) countWeights[(i * 4) + k] = vertices[i].BoneWeights[k];
        }
        result.NewMesh = new MeshData
        {
            Name = frame.Name?.ToString() ?? "mesh",
            World = frame.WorldTransform,
            Positions = newPositions,
            Normals = newNormals,
            UVs = newUvs,
            Tangents = tangents,
            Binormals = binormals,
            Indices = decoded.Indices,
            Parts = countParts,
            BoneIndices = countIds,
            BoneWeights = countWeights,
            Skeleton = countSkin.Rig,
            LiveRest = countSkin.LiveRest,
        };
        return result;
    }

    // ── Topology rebuild ──
    //
    // Rebuilds LOD0 from the pushed mesh wholesale: fresh split vertices (keyed by welded position +
    // quantized normal + UV, the same splitting the game format implies), an index buffer re-grouped
    // into contiguous per-material ranges, fresh quantization when needed, and a stock-shaped LOD0
    // whose split table and OPCODE partition the native core builds (one split + one burst per
    // material — see FrameLOD.CreateRebuilt). Lower LODs and the separate collision resource keep
    // their old shape — the caller warns the user once.
    private static ApplyResult? TryApplyRebuild(IFrameNode node, MeshObjectPayload payload, out string? skipReason)
    {
        skipReason = null;
        // A skinned model may be re-topologised: below, its remap pools AND its per-bone face ranges
        // (BlendMeshSplits) are rebuilt from the geometry that came back. Leaving either stale is what
        // smeared a repacked car across the horizon.
        if (node is not FrameNodeAdapter adapter
            || adapter.Frame is not FrameObjectSingleMesh frame
            || (frame.GetType() != typeof(FrameObjectSingleMesh) && frame is not FrameObjectModel))
        {
            skipReason = "unsupported object";
            return null;
        }
        DecodedMesh? decoded = SdsMeshLoader.DecodeLod0(frame);
        if (decoded == null)
        {
            skipReason = "mesh has no usable LOD0 buffers";
            return null;
        }

        int loops = payload.LoopOrigIndex.Length;
        int faces = loops / 3;
        if (payload.LoopVertexIndices.Length != loops || payload.LoopNormals.Length != loops
            || payload.LoopUvs.Length != loops || payload.FaceMaterials.Length != faces || faces == 0)
        {
            skipReason = "malformed payload (array lengths disagree)";
            return null;
        }

        // Target material slots: the PUSHED slot list (hash-identified — re-pointing a Blender slot
        // at another bridge material is a real material change) when present, else the existing
        // table. Every hash must be a game material; slots no face uses are dropped and the faces
        // renumbered (Blender scenes accumulate unused slots).
        Formats.Frames.Resources.MaterialStruct[] existingMats = frame.Material.Materials[0];
        ulong[] slotHashes;
        if (payload.Materials.Count > 0)
        {
            MafiaMaterials.EnsureLoaded();
            slotHashes = new ulong[payload.Materials.Count];
            for (int slot = 0; slot < payload.Materials.Count; slot++)
            {
                MeshMaterialInfo info = payload.Materials[slot];
                if (!TryParseMaterialHash(info.Hash, out ulong parsed))
                {
                    skipReason = $"slot '{info.Name ?? slot.ToString(System.Globalization.CultureInfo.InvariantCulture)}'"
                        + " is not a game material — assign materials that came from the toolkit";
                    return null;
                }
                if (!MafiaMaterials.KnowsMaterial(parsed) && Array.TrueForAll(existingMats, m => m.MaterialHash != parsed))
                {
                    skipReason = $"material '{info.Name ?? info.Hash}' is unknown to the game's MTL libraries";
                    return null;
                }
                slotHashes[slot] = parsed;
            }
        }
        else
        {
            slotHashes = new ulong[existingMats.Length];
            for (int slot = 0; slot < existingMats.Length; slot++) slotHashes[slot] = existingMats[slot].MaterialHash;
        }

        var facesPerSlot = new int[slotHashes.Length];
        foreach (ushort slot in payload.FaceMaterials)
        {
            if (slot >= slotHashes.Length)
            {
                skipReason = "a face uses a material slot the mesh does not have";
                return null;
            }
            facesPerSlot[slot]++;
        }
        var slotRemap = new int[slotHashes.Length];
        int keptSlots = 0;
        for (int slot = 0; slot < slotHashes.Length; slot++)
            slotRemap[slot] = facesPerSlot[slot] > 0 ? keptSlots++ : -1;
        if (keptSlots == 0)
        {
            skipReason = "mesh has no faces";
            return null;
        }

        // 1) New split vertices: unique (source vertex, welded position, quantized normal, UV half
        // bits) tuples. The source index IS part of the identity — two original split vertices that
        // agree on pos/normal/uv0 can still differ in channels Blender never saw (colors, extra UV
        // sets, damage groups), and merging them would corrupt those. Only Blender-born corners
        // (orig −1) deduplicate purely by attributes.
        var keyToSplit = new Dictionary<(int Orig, uint Welded, int NormalKey, uint UvKey), int>(loops);
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var donors = new List<int>();
        // Which WELDED vertex each split vertex came from — the row the pushed skin is indexed by.
        var welds = new List<int>();
        var loopSplit = new int[loops];
        for (int i = 0; i < loops; i++)
        {
            uint welded = payload.LoopVertexIndices[i];
            if (welded >= payload.Positions.Length)
            {
                skipReason = "malformed payload (welded vertex index out of range)";
                return null;
            }
            int orig = payload.LoopOrigIndex[i] >= 0 && payload.LoopOrigIndex[i] < decoded.NumVerts
                ? payload.LoopOrigIndex[i] : -1;
            Vector3 normal = payload.LoopNormals[i];
            Vector2 uv = new(payload.LoopUvs[i].X, 1f - payload.LoopUvs[i].Y);
            var key = (orig, welded, PackNormalKey(normal), PackUvKey(uv));
            if (!keyToSplit.TryGetValue(key, out int split))
            {
                split = positions.Count;
                keyToSplit[key] = split;
                positions.Add(payload.Positions[welded]);
                normals.Add(normal);
                uvs.Add(uv);
                donors.Add(orig);
                welds.Add((int)welded);
            }
            loopSplit[i] = split;
        }
        // Face-mate donor fill: a brand-new vertex borrows its unmodeled channels (colors, extra
        // UV sets, damage groups) from a source vertex of the same face.
        for (int f = 0; f < faces; f++)
        {
            int faceDonor = -1;
            for (int c = 0; c < 3 && faceDonor < 0; c++) faceDonor = donors[loopSplit[f * 3 + c]];
            if (faceDonor < 0) continue;
            for (int c = 0; c < 3; c++)
                if (donors[loopSplit[f * 3 + c]] < 0) donors[loopSplit[f * 3 + c]] = faceDonor;
        }

        int newCount = positions.Count;
        Vector3[] newPositions = positions.ToArray();
        Vector3[] newNormals = normals.ToArray();
        Vector2[] newUvs = uvs.ToArray();

        // 2) Index buffer re-grouped into contiguous per-material ranges (stable by slot).
        IndexBuffer? indexBuffer = frame.GetIndexBuffer(0);
        if (indexBuffer == null)
        {
            skipReason = "mesh has no index buffer";
            return null;
        }
        var faceOrder = Enumerable.Range(0, faces).OrderBy(f => slotRemap[payload.FaceMaterials[f]]).ToArray();
        var newIndexData = new uint[loops];
        var newMats = new Formats.Frames.Resources.MaterialStruct[keptSlots];
        {
            int at = 0;
            int currentSlot = -1;
            foreach (int f in faceOrder)
            {
                int sourceSlot = payload.FaceMaterials[f];
                int slot = slotRemap[sourceSlot];
                if (slot != currentSlot)
                {
                    currentSlot = slot;
                    // Reuse the existing struct for a matching hash (keeps its Unk3); a slot pointed
                    // at a DIFFERENT game material gets a fresh entry with that hash.
                    Formats.Frames.Resources.MaterialStruct? donorStruct =
                        Array.Find(existingMats, m => m.MaterialHash == slotHashes[sourceSlot]);
                    newMats[slot] = donorStruct != null
                        ? new Formats.Frames.Resources.MaterialStruct(donorStruct)
                        : new Formats.Frames.Resources.MaterialStruct { MaterialHash = slotHashes[sourceSlot] };
                    newMats[slot].StartIndex = at;
                    newMats[slot].NumFaces = facesPerSlot[sourceSlot];
                }
                newIndexData[at++] = (uint)loopSplit[f * 3 + 0];
                newIndexData[at++] = (uint)loopSplit[f * 3 + 1];
                newIndexData[at++] = (uint)loopSplit[f * 3 + 2];
            }
        }

        // 3) Quantization: keep the old lattice while everything fits (donor bytes then re-encode
        // identically), else re-derive it from the new AABB.
        bool requantize = NeedsRequantize(newPositions, decoded.DecompressionOffset, decoded.DecompressionFactor);
        (Vector3 newOffset, float newFactor) = requantize
            ? ComputeQuantization(newPositions)
            : (decoded.DecompressionOffset, decoded.DecompressionFactor);

        // 4) Tangent frames over the rebuilt mesh; donor-matched vertices keep the donor's frame.
        bool hasTangent = decoded.Declaration.HasFlag(VertexFlags.Tangent);
        Vector3[]? regenT = null, regenB = null;
        if (hasTangent)
        {
            (regenT, regenB) = TangentGenerator.Compute(newPositions, newNormals, newUvs, newIndexData);
        }

        // 5) Encode the new vertex buffer. Decode the donor buffer once (one native crossing), build
        // the output Vertex[] plus a per-vertex base buffer (each new vertex over its donor's original
        // bytes, or zeros for a vertex with no donor), then re-encode the whole thing once.
        int stride = decoded.Stride;
        Vertex[] donorAll = VertexTranslator.DecompressBuffer(
            decoded.RawVertexData, decoded.NumVerts, decoded.Declaration,
            decoded.DecompressionOffset, decoded.DecompressionFactor);

        bool isSkinned = decoded.Declaration.HasFlag(VertexFlags.Skin);
        // The donors' bone ids as they sit in the buffer are POOL-LOCAL — an index into whichever remap pool
        // the face group they were drawn in uses. Resolving them to the model's own bone list gives a reading
        // that survives regrouping, which is what the renderer and the face-range rebuild below both need.
        // The ids written back to the FILE stay the donors' own, re-localized against the pool of whichever
        // group ends up drawing them.
        byte[]? globalIds = isSkinned && frame is FrameObjectModel skinnedModel
            ? SdsMeshLoader.ResolveBoneRemap(
                skinnedModel, SdsMeshLoader.BuildParts(skinnedModel, decoded.Indices.Length), decoded)
            : null;
        byte[]? newGlobal = globalIds != null ? new byte[newCount * 4] : null;

        // The skin Blender is sending back, if it sent one: four influences per WELDED vertex, naming bones
        // of the model's own list. This is what a vertex group means — assign a new part to the hood and it
        // must ride the hood, not whatever bone the nearest old vertex happened to use.
        int rigBones = frame is FrameObjectModel boned ? BoneCountOf(boned) : 0;
        byte[]? pushedIds = payload.BoneIndices;
        float[]? pushedWeights = payload.BoneWeights;
        bool pushedSkin = isSkinned && rigBones > 0
            && pushedIds != null && pushedWeights != null
            && pushedIds.Length >= payload.Positions.Length * 4
            && pushedWeights.Length >= payload.Positions.Length * 4;
        int fromBlender = 0;

        var outVerts = new Vertex[newCount];
        var baseData = new byte[newCount * stride];
        int touched = 0;
        Vector3[]? meshTangents = hasTangent ? new Vector3[newCount] : null;
        Vector3[]? meshBinormals = hasTangent ? new Vector3[newCount] : null;
        for (int v = 0; v < newCount; v++)
        {
            int donor = donors[v];
            Vertex vert;
            if (donor >= 0)
            {
                Array.Copy(decoded.RawVertexData, donor * stride, baseData, v * stride, stride);
                Vertex donorVert = donorAll[donor];
                bool unchanged = newPositions[v] == decoded.Positions[donor]
                    && newUvs[v] == decoded.UVs[donor]
                    && SameDirection(newNormals[v], decoded.Normals[donor]);
                vert = new Vertex
                {
                    Position = newPositions[v],
                    Normal = unchanged ? decoded.Normals[donor] : newNormals[v],
                    Tangent = unchanged || !hasTangent ? donorVert.Tangent : regenT![v],
                    Binormal = unchanged || !hasTangent ? donorVert.Binormal : regenB![v],
                    BBCoeffs = donorVert.BBCoeffs,
                    DamageGroup = donorVert.DamageGroup,
                };
                donorVert.UVs.CopyTo(vert.UVs, 0);
                donorVert.BoneWeights.CopyTo(vert.BoneWeights, 0);
                donorVert.BoneIDs.CopyTo(vert.BoneIDs, 0);
                CopyGlobalBones(globalIds, donor, newGlobal, v);
                donorVert.Color0.CopyTo(vert.Color0, 0);
                donorVert.Color1.CopyTo(vert.Color1, 0);
                vert.UVs[0] = new Half2(newUvs[v].X, newUvs[v].Y);
                if (!unchanged) touched++;
                if (hasTangent)
                {
                    meshTangents![v] = vert.Tangent;
                    meshBinormals![v] = vert.Binormal;
                }
            }
            else
            {
                // baseData slice stays zero — a vertex with no donor has no unmodeled bits to keep.
                vert = new Vertex
                {
                    Position = newPositions[v],
                    Normal = newNormals[v],
                    Tangent = hasTangent ? regenT![v] : new Vector3(1f, 0f, 0f),
                    Binormal = hasTangent ? regenB![v] : Vector3.Zero,
                };
                vert.UVs[0] = new Half2(newUvs[v].X, newUvs[v].Y);

                // On a SKINNED mesh a vertex with no influences is not merely unshaded — it collapses
                // onto the first bone, which on a car drags the new geometry to the model's origin. A
                // vertex Blender added has no donor to inherit from, so it takes the skin of the
                // nearest source vertex, which is the only answer that keeps it attached to the part it
                // was modelled on. Its DAMAGE GROUP comes from there too: the channel is what says which
                // panel a vertex crumples with (measured on shubert_38 by --probe-damage — 39 groups, each
                // one a panel and its deform_ bone), and no group at all is not one of the answers.
                if (isSkinned)
                {
                    int near = NearestSourceVertex(decoded.Positions, newPositions[v]);
                    if (near >= 0)
                    {
                        donorAll[near].BoneWeights.CopyTo(vert.BoneWeights, 0);
                        donorAll[near].BoneIDs.CopyTo(vert.BoneIDs, 0);
                        CopyGlobalBones(globalIds, near, newGlobal, v);
                        vert.DamageGroup = donorAll[near].DamageGroup;
                        vert.BBCoeffs = donorAll[near].BBCoeffs;
                    }
                }
                touched++;
                if (hasTangent)
                {
                    meshTangents![v] = vert.Tangent;
                    meshBinormals![v] = vert.Binormal;
                }
            }
            // Blender's own weights win wherever it has them. A vertex group is an INSTRUCTION — the part
            // the modeller says this vertex belongs to — while the donor and nearest-vertex fills are only
            // guesses at one, and a guess is what put a new hood panel on the left door.
            if (pushedSkin && TakePushedSkin(pushedIds!, pushedWeights!, welds[v], rigBones, vert, newGlobal, v))
                fromBlender++;

            outVerts[v] = vert;
        }

        // The skin's own bookkeeping, all of it driven by the GLOBAL reading collected above.
        byte[]? renderIds = null;
        float[]? renderWeights = null;
        SkeletonData? renderRig = null;
        if (newGlobal != null && frame is FrameObjectModel rebuiltModel)
        {
            if (!RebuildMeshSplits(rebuiltModel, outVerts, newGlobal, newIndexData, newMats,
                    decoded.Indices, donors, out string? splitReason))
            {
                skipReason = splitReason;
                return null;
            }

            // What the RENDERER gets — it addresses the model's own bone list, not a remap pool. Without it
            // the replacement mesh uploads unskinned and the body stops following its bones for the rest of
            // the session.
            renderIds = newGlobal;
            renderWeights = new float[outVerts.Length * 4];
            for (int v = 0; v < outVerts.Length; v++)
                for (int k = 0; k < 4; k++) renderWeights[(v * 4) + k] = outVerts[v].BoneWeights[k];
            renderRig = SdsMeshLoader.RigOf(rebuiltModel);

            if (!RemapBlendInfo(rebuiltModel, outVerts, newGlobal, newIndexData, newMats, existingMats,
                    out string? blendReason))
            {
                skipReason = blendReason;
                return null;
            }
        }

        byte[] newData = VertexCompressor.CompressBuffer(
            baseData, outVerts, decoded.Declaration, newOffset, newFactor);

        // 6) Fresh LOD0: the stock-shaped split info + trivial OPCODE partition come from the
        // native builder (mf_frames_rebuild_lod) — byte-identical to the old manual assembly.
        Formats.Frames.Resources.FrameLOD oldLod = frame.Geometry.LOD[0];
        if (newMats.Length == 0)
        {
            // The builder accepts a slotless request (that is the placeholder a brand-new mesh
            // carries), so a rebuild has to say for itself that a drawable mesh needs a material.
            skipReason = "no material slot survived the push";
            return null;
        }
        int newFormat = newCount > 65535 ? 2 : indexBuffer.IndexFormat;
        var slots = new Formats.Frames.Resources.FrameLOD.RebuiltMaterialSlot[newMats.Length];
        for (int slot = 0; slot < newMats.Length; slot++)
        {
            (Vector3 min, Vector3 max) = SlotAabb(newPositions, newIndexData, newMats[slot]);
            slots[slot] = new Formats.Frames.Resources.FrameLOD.RebuiltMaterialSlot(
                newMats[slot].MaterialHash, newMats[slot].StartIndex, newMats[slot].NumFaces, min, max);
        }
        Formats.Frames.Resources.FrameLOD newLod = Formats.Frames.Resources.FrameLOD.CreateRebuilt(
            oldLod.Distance, oldLod.IndexBufferRef, oldLod.VertexBufferRef,
            oldLod.VertexDeclaration, newCount, newFormat == 2 ? 4 : 2, faces, slots);

        (Vector3 meshMin, Vector3 meshMax) = Aabb(newPositions);
        var result = new ApplyResult
        {
            Frame = frame,
            Buffer = frame.GetVertexBuffer(0)!,
            Document = adapter.Document,
            OldVertexData = decoded.RawVertexData,
            NewVertexData = newData,
            OldBounds = frame.Boundings,
            OldMaterialBounds = frame.Material.Bounds,
            NewBounds = new BoundingBox { Min = meshMin, Max = meshMax },
            OldDecompressionOffset = decoded.DecompressionOffset,
            OldDecompressionFactor = decoded.DecompressionFactor,
            NewDecompressionOffset = newOffset,
            NewDecompressionFactor = newFactor,
            TouchedVertices = touched,
            SkinFromBlender = fromBlender,
            SkinNotSent = isSkinned && rigBones > 0 && !pushedSkin,
            Requantized = requantize,
            Rebuild = new RebuildData
            {
                OldLod = oldLod,
                NewLod = newLod,
                OldMaterials = existingMats,
                NewMaterials = newMats,
                OldLodMatCount = existingMats.Length,
                NewLodMatCount = newMats.Length,
                IndexBuffer = indexBuffer,
                OldIndexData = indexBuffer.GetData(),
                NewIndexData = newIndexData,
                OldIndexFormat = indexBuffer.IndexFormat,
                NewIndexFormat = newFormat,
            },
        };

        var parts = new MeshPart[newMats.Length];
        MafiaMaterials.EnsureLoaded();
        for (int slot = 0; slot < newMats.Length; slot++)
        {
            MafiaMaterials.MaterialTextures tex = MafiaMaterials.GetMaterialTextures(newMats[slot].MaterialHash);
            parts[slot] = new MeshPart(newMats[slot].StartIndex, newMats[slot].NumFaces * 3,
                tex.Diffuse, tex.Normal, tex.Specular);
        }
        result.NewMesh = new MeshData
        {
            Name = frame.Name?.ToString() ?? "mesh",
            World = frame.WorldTransform,
            Positions = newPositions,
            Normals = newNormals,
            UVs = newUvs,
            Tangents = meshTangents,
            Binormals = meshBinormals,
            Indices = newIndexData,
            Parts = parts,
            // Captured above, before the ids were localized into the remap pool.
            BoneIndices = renderIds,
            BoneWeights = renderWeights,
            Skeleton = renderRig,
            LiveRest = frame is FrameObjectModel posed ? posed.RestTransform : null,
        };
        return result;
    }

    private static int PackNormalKey(Vector3 normal)
    {
        const float scale = 0.007874f;
        int x = Math.Clamp((int)MathF.Round(normal.X / scale) + 127, 0, 255);
        int y = Math.Clamp((int)MathF.Round(normal.Y / scale) + 127, 0, 255);
        int z = Math.Clamp((int)MathF.Round(normal.Z / scale) + 127, 0, 255);
        return x | (y << 8) | (z << 16);
    }

    private static uint PackUvKey(Vector2 uv) =>
        BitConverter.HalfToUInt16Bits((Half)uv.X) | ((uint)BitConverter.HalfToUInt16Bits((Half)uv.Y) << 16);

    private static (Vector3 Min, Vector3 Max) SlotAabb(
        Vector3[] positions, uint[] indices, Formats.Frames.Resources.MaterialStruct mat)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int end = mat.StartIndex + mat.NumFaces * 3;
        for (int i = mat.StartIndex; i < end && i < indices.Length; i++)
        {
            Vector3 p = positions[indices[i]];
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    private static bool FaceSetMatches(DecodedMesh decoded, MeshObjectPayload payload, out string? reason)
    {
        reason = null;
        // The SAME weld the export used, skin key included — a weld that splits differently here would
        // report a topology change on a mesh nobody touched.
        WeldedMesh exported = WeldMapBuilder.Build(
            BridgeMeshExporter.BuildWeldKeys(decoded), decoded.Positions, decoded.Normals, null,
            decoded.Indices, BridgeMeshExporter.BuildSkinKeys(decoded));

        if (payload.LoopOrigIndex.Length != exported.LoopOrigIndex.Length)
        {
            reason = $"topology changed (face count {payload.LoopOrigIndex.Length / 3} vs {exported.LoopOrigIndex.Length / 3})";
            return false;
        }

        var originalFaces = new HashSet<(int, int, int)>(exported.LoopOrigIndex.Length / 3);
        for (int i = 0; i + 2 < exported.LoopOrigIndex.Length; i += 3)
            originalFaces.Add(Sort3(exported.LoopOrigIndex[i], exported.LoopOrigIndex[i + 1], exported.LoopOrigIndex[i + 2]));
        for (int i = 0; i + 2 < payload.LoopOrigIndex.Length; i += 3)
        {
            if (!originalFaces.Contains(Sort3(payload.LoopOrigIndex[i], payload.LoopOrigIndex[i + 1], payload.LoopOrigIndex[i + 2])))
            {
                reason = "topology changed (faces were reshaped)";
                return false;
            }
        }

        // Per-face material REASSIGNMENT also routes through the rebuild (it re-groups the index
        // buffer into fresh contiguous ranges) — the count-preserving path never touches ranges.
        Formats.Frames.Resources.MaterialStruct[]? mats = decoded.Frame.Material?.Materials is { Count: > 0 } list
            ? list[0] : null;
        if (mats is { Length: > 0 } && payload.FaceMaterials.Length == exported.KeptTriangles.Length)
        {
            var perSourceFace = new ushort[decoded.Indices.Length / 3];
            for (int slot = 0; slot < mats.Length; slot++)
            {
                int firstFace = mats[slot].StartIndex / 3;
                for (int f = 0; f < mats[slot].NumFaces && firstFace + f < perSourceFace.Length; f++)
                    perSourceFace[firstFace + f] = (ushort)slot;
            }
            for (int k = 0; k < exported.KeptTriangles.Length; k++)
            {
                if (payload.FaceMaterials[k] != perSourceFace[exported.KeptTriangles[k]])
                {
                    reason = "topology changed (material assignment changed)";
                    return false;
                }
            }
        }

        // Slot IDENTITY changes (a Blender slot re-pointed at another game material) also need the
        // rebuild — the count-preserving path never touches the material table.
        if (mats is { Length: > 0 } && payload.Materials.Count > 0)
        {
            if (payload.Materials.Count != mats.Length)
            {
                reason = "topology changed (material slot count changed)";
                return false;
            }
            for (int slot = 0; slot < mats.Length; slot++)
            {
                if (!TryParseMaterialHash(payload.Materials[slot].Hash, out ulong parsed)
                    || parsed != mats[slot].MaterialHash)
                {
                    reason = "topology changed (material assignment changed)";
                    return false;
                }
            }
        }
        return true;
    }

    private static bool TryParseMaterialHash(string? text, out ulong hash)
    {
        hash = 0;
        if (string.IsNullOrEmpty(text)) return false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out hash);
    }

    private static (int, int, int) Sort3(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    // Records a source vertex's GLOBAL bone ids against the new vertex that inherited from it. Kept beside the
    // vertices rather than on them: the ids the FILE wants are the donor's own pool-local ones, and overwriting
    // those with a global reading is what put a repacked car's bones out of range. No-op for an unskinned mesh.
    private static void CopyGlobalBones(byte[]? globalIds, int source, byte[]? into, int target)
    {
        if (globalIds == null || into == null || source < 0
            || ((source * 4) + 3) >= globalIds.Length || ((target * 4) + 3) >= into.Length)
        {
            return;
        }
        for (int k = 0; k < 4; k++) into[(target * 4) + k] = globalIds[(source * 4) + k];
    }

    /// <summary>
    /// Rewrites a re-topologised skinned model's BlendMeshSplits — per bone, per material, the ranges of
    /// faces the game deforms as one piece. Stale ranges are what smear a repacked car across the horizon:
    /// they name faces the mesh no longer has.
    /// <para>
    /// Measured on the shipped cars (<c>--probe-skinning</c>): a split is a BONE (BlendIndex is its index),
    /// a burst's StartIndex is an index-buffer offset and NumFaces a triangle count, and the ranges of all
    /// splits together partition the triangle list — on shubert_38 exactly, on ascot_baileys200_pha not
    /// quite, so nothing here relies on being handed a clean partition.
    /// </para>
    /// <para>
    /// The splits and their PIECES are kept exactly as they ship — each piece carries a hit box (the core
    /// reads one per piece) whose quantization is not understood, and the pieces are the units the damage
    /// system deforms. Only the face RANGES move, and a face keeps the piece it already belonged to: it is
    /// matched to the original triangle it came from through its donor vertices. A face the mesh did not have
    /// before has no such answer and falls back to the bone carrying most of its weight, first piece.
    /// </para>
    /// </summary>
    private static bool RebuildMeshSplits(
        FrameObjectModel model, Vertex[] vertices, byte[] globalOf, uint[] indices, MaterialStruct[] mats,
        uint[] oldIndices, IReadOnlyList<int> donors, out string? reason)
    {
        reason = null;
        FrameObjectModel.WeightedByMeshSplit[] splits = model.BlendMeshSplits ?? [];
        if (splits.Length == 0) return true; // nothing to keep in step

        // Which split (if any) speaks for each bone.
        var splitOfBone = new Dictionary<int, int>(splits.Length);
        for (int s = 0; s < splits.Length; s++) splitOfBone.TryAdd(splits[s].BlendIndex, s);

        // The piece each ORIGINAL face sat in, read off the shipped table before it is rewritten, plus a way
        // to find the original face a new one came from (its three donor vertices, in any order).
        int oldFaces = oldIndices.Length / 3;
        var oldOwner = new (int Split, int Piece)[oldFaces];
        Array.Fill(oldOwner, (-1, -1));
        for (int s = 0; s < splits.Length; s++)
        {
            FrameObjectModel.BlendMeshSplitInfo[] pieces = splits[s].Data ?? [];
            for (int p = 0; p < pieces.Length; p++)
            {
                foreach (FrameObjectModel.MiniMaterialBurst burst in pieces[p].Data ?? [])
                {
                    foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                    {
                        int from = range.StartIndex / 3;
                        for (int f = from; f < from + range.NumFaces && f < oldFaces; f++)
                            if (oldOwner[f].Split < 0) oldOwner[f] = (s, p);
                    }
                }
            }
        }
        var oldOfFace = new Dictionary<(int, int, int), int>(oldFaces);
        for (int f = 0; f < oldFaces; f++)
        {
            oldOfFace.TryAdd(
                Sort3((int)oldIndices[f * 3], (int)oldIndices[(f * 3) + 1], (int)oldIndices[(f * 3) + 2]), f);
        }

        // face -> (split, piece, material slot).
        int faces = mats.Sum(m => m.NumFaces);
        var owner = new (int Split, int Piece, int Slot)[faces];
        var weightOfBone = new Dictionary<int, float>(8);
        for (int slot = 0; slot < mats.Length; slot++)
        {
            int first = mats[slot].StartIndex / 3;
            for (int f = first; f < first + mats[slot].NumFaces && f < faces; f++)
            {
                int split = -1, piece = -1;

                // The original triangle this one came from, if it is one the mesh already had.
                int a = DonorOf(indices, donors, (f * 3) + 0);
                int b = DonorOf(indices, donors, (f * 3) + 1);
                int c = DonorOf(indices, donors, (f * 3) + 2);
                if (a >= 0 && b >= 0 && c >= 0 && oldOfFace.TryGetValue(Sort3(a, b, c), out int wasFace))
                {
                    (split, piece) = oldOwner[wasFace];
                }

                if (split < 0)
                {
                    weightOfBone.Clear();
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int at = (f * 3) + corner;
                        if (at >= indices.Length) continue;
                        int vertex = (int)indices[at];
                        if (vertex < 0 || vertex >= vertices.Length) continue;
                        for (int k = 0; k < 4; k++)
                        {
                            float weight = vertices[vertex].BoneWeights[k];
                            if (weight <= 0f) continue;
                            int bone = globalOf[(vertex * 4) + k];
                            weightOfBone[bone] = weightOfBone.GetValueOrDefault(bone) + weight;
                        }
                    }
                    foreach ((int bone, float _) in weightOfBone.OrderByDescending(p => p.Value))
                    {
                        if (splitOfBone.TryGetValue(bone, out split)) break;
                        split = -1;
                    }
                    piece = 0;
                }

                // A face whose bones all lack a split still has to land somewhere: the shipped shubert_38
                // covers every triangle exactly once, and leaving holes in the table is untested territory.
                // The first split takes them.
                if (split < 0) (split, piece) = (0, 0);
                if (piece < 0 || piece >= (splits[split].Data?.Length ?? 0)) piece = 0;
                owner[f] = (split, piece, slot);
            }
        }

        // Runs of consecutive faces sharing a split, a piece and a slot become one burst.
        var runs = new Dictionary<(int Split, int Piece, int Slot), List<(int First, int Count)>>();
        for (int f = 0; f < faces;)
        {
            int end = f;
            while (end + 1 < faces && owner[end + 1] == owner[f]) end++;
            if (((long)f * 3) + ((end - f + 1) * 3) > ushort.MaxValue)
            {
                reason = "the mesh has more triangles than a face range can address (65535 indices)";
                return false;
            }
            if (!runs.TryGetValue(owner[f], out List<(int, int)>? list)) runs[owner[f]] = list = [];
            list.Add((f, end - f + 1));
            f = end + 1;
        }

        // Write them back, piece by piece — a piece with no faces left is emptied rather than left pointing
        // at triangles that are gone.
        for (int s = 0; s < splits.Length; s++)
        {
            FrameObjectModel.BlendMeshSplitInfo[] pieces = splits[s].Data ?? [];
            for (int p = 0; p < pieces.Length; p++)
            {
                var bursts = new List<FrameObjectModel.MiniMaterialBurst>();
                for (int slot = 0; slot < mats.Length; slot++)
                {
                    if (!runs.TryGetValue((s, p, slot), out List<(int First, int Count)>? list)) continue;
                    bursts.Add(new FrameObjectModel.MiniMaterialBurst
                    {
                        MaterialIndex = (ushort)slot,
                        Data = [.. list.Select(r => new FrameObjectModel.FacesBurst
                        {
                            StartIndex = (ushort)(r.First * 3),
                            NumFaces = (ushort)r.Count,
                        })],
                    });
                }
                pieces[p].Data = [.. bursts];
            }
        }

        // The stored size of the block we just rewrote. Everything the file holds after it is found by
        // walking past it, so a size left at the old table's makes the whole model unreadable — the car
        // stops appearing in game altogether. The formula reproduces the shipped value on 92 of 92 models
        // (--probe-skinning), which is what makes recomputing it safe rather than a guess.
        model.RecomputeSplitCounters();
        return true;
    }

    /// <summary>
    /// Re-points a re-topologised skinned model's vertices at the remap pools it already ships with, and says
    /// which pool each new face group draws from. Returns false with a reason when the pools cannot answer for
    /// the geometry that came back.
    /// <para>
    /// The pools themselves are NOT rebuilt. A car does not put its whole rig in one pool — shubert_38 ships
    /// 59 bones in pool 0 and 33 in pool 1 for 83 bones total (<c>--probe-skinning</c>) — and replacing that
    /// with a single pool over every bone is what tore a repacked car apart: a draw can only reach so far into
    /// the palette, so ids past the end came back as garbage transforms. Keeping the shipped pools also keeps
    /// the skin channel byte-identical for every vertex that kept its material, which is the common case.
    /// </para>
    /// </summary>
    private static bool RemapBlendInfo(
        FrameObjectModel model, Vertex[] vertices, byte[] globalOf, uint[] indices,
        MaterialStruct[] newMats, MaterialStruct[] oldMats, out string? reason)
    {
        reason = null;
        FrameBlendInfo blend;
        try { blend = model.GetBlendInfoObject(); }
        catch (Exception) { reason = "the model's blend info cannot be read"; return false; }

        FrameBlendInfo.BoneIndexInfo[] lods = blend.BoneIndexInfos ?? [];
        if (lods.Length == 0) { reason = "the model carries no remap pools"; return false; }
        FrameBlendInfo.BoneIndexInfo lod0 = lods[0];
        byte[] sizes = lod0.BonesPerRemapPool ?? [];
        byte[] remap = lod0.BoneRemapIDs ?? [];
        FrameBlendInfo.SkinnedMaterialInfo[] oldGroups = lod0.SkinnedMaterialInfo ?? [];

        // Pool p is a run of sizes[p] ids in the flat remap table.
        var poolStart = new int[sizes.Length];
        int poolCount = 0, at = 0;
        for (int p = 0; p < sizes.Length; p++)
        {
            poolStart[p] = at;
            at += sizes[p];
            if (sizes[p] > 0) poolCount = p + 1;
        }
        if (at > remap.Length) { reason = "the model's remap pools overrun its remap table"; return false; }
        if (poolCount == 0) { reason = "the model carries no remap pools"; return false; }

        var localOf = new Dictionary<byte, byte>[poolCount];
        for (int p = 0; p < poolCount; p++)
        {
            var map = new Dictionary<byte, byte>(sizes[p]);
            for (int i = 0; i < sizes[p]; i++) map.TryAdd(remap[poolStart[p] + i], (byte)i);
            localOf[p] = map;
        }

        // A slot that kept its material keeps that material's pool — draw order is what ties a face group to
        // a pool, so the shipped answer is the right one wherever it still applies.
        var groups = new FrameBlendInfo.SkinnedMaterialInfo[newMats.Length];
        for (int slot = 0; slot < newMats.Length; slot++)
        {
            int was = Array.FindIndex(oldMats, m => m.MaterialHash == newMats[slot].MaterialHash);
            FrameBlendInfo.SkinnedMaterialInfo donor = was >= 0 && was < oldGroups.Length
                ? oldGroups[was]
                : new FrameBlendInfo.SkinnedMaterialInfo { AssignedPoolIndex = 0, NumWeightsPerVertex = 1 };
            groups[slot] = new FrameBlendInfo.SkinnedMaterialInfo
            {
                AssignedPoolIndex = donor.AssignedPoolIndex < poolCount ? donor.AssignedPoolIndex : (byte)0,
                NumWeightsPerVertex = Math.Clamp(donor.NumWeightsPerVertex, (byte)1, (byte)4),
            };
        }

        // Which bones each slot actually draws, and how many influences its heaviest vertex carries.
        var bonesOfSlot = new HashSet<byte>[newMats.Length];
        for (int slot = 0; slot < newMats.Length; slot++)
        {
            HashSet<byte> set = bonesOfSlot[slot] = [];
            int influences = 1;
            int from = newMats[slot].StartIndex;
            int to = Math.Min(from + (newMats[slot].NumFaces * 3), indices.Length);
            for (int i = from; i < to; i++)
            {
                int v = (int)indices[i];
                if (v < 0 || v >= vertices.Length) continue;
                int here = 0;
                for (int k = 0; k < 4; k++)
                {
                    if (vertices[v].BoneWeights[k] <= 0f) continue;
                    here++;
                    set.Add(globalOf[(v * 4) + k]);
                }
                influences = Math.Max(influences, here);
            }
            groups[slot].NumWeightsPerVertex =
                Math.Clamp(Math.Max(groups[slot].NumWeightsPerVertex, (byte)influences), (byte)1, (byte)4);

            // A slot whose bones its inherited pool cannot name (Blender moved faces between materials, or
            // the slot is new) takes any pool that can.
            if (set.All(b => localOf[groups[slot].AssignedPoolIndex].ContainsKey(b))) continue;
            int fit = -1;
            for (int p = 0; p < poolCount && fit < 0; p++) if (set.All(b => localOf[p].ContainsKey(b))) fit = p;
            if (fit < 0)
            {
                reason = "the pushed mesh weights a material to bones no single remap pool of the model "
                    + "covers — move those faces back onto the material they came from";
                return false;
            }
            groups[slot].AssignedPoolIndex = (byte)fit;
        }

        // A vertex carries one set of ids, so every group drawing it must read them against the same pool.
        var poolOfVertex = new int[vertices.Length];
        Array.Fill(poolOfVertex, -1);
        for (int slot = 0; slot < newMats.Length; slot++)
        {
            int pool = groups[slot].AssignedPoolIndex;
            int from = newMats[slot].StartIndex;
            int to = Math.Min(from + (newMats[slot].NumFaces * 3), indices.Length);
            for (int i = from; i < to; i++)
            {
                int v = (int)indices[i];
                if (v < 0 || v >= vertices.Length) continue;
                if (poolOfVertex[v] >= 0 && poolOfVertex[v] != pool)
                {
                    reason = "a vertex is shared by two materials that read their bones from different "
                        + "pools — split the mesh along that material boundary and push again";
                    return false;
                }
                poolOfVertex[v] = pool;
            }
        }

        // Ids back to pool-local. A zero-weight slot keeps the donor's byte: it names nothing, and rewriting
        // it would move bytes for no reason.
        for (int v = 0; v < vertices.Length; v++)
        {
            int pool = poolOfVertex[v];
            if (pool < 0) continue; // nothing draws this vertex
            for (int k = 0; k < 4; k++)
            {
                if (vertices[v].BoneWeights[k] <= 0f) continue;
                if (!localOf[pool].TryGetValue(globalOf[(v * 4) + k], out byte local))
                {
                    reason = "a vertex came back weighted to a bone its material's remap pool does not name";
                    return false;
                }
                vertices[v].BoneIDs[k] = local;
            }
        }

        // Read back what was just written, the way the GAME reads it: pool start + the vertex's own id must
        // land on the global bone that vertex was meant to have.
        //
        // Nothing above proves that. Every step here is locally sensible and the composition can still come
        // out unreadable — and when it does there is no error anywhere: the car is packed, the game resolves
        // the ids against a table that no longer answers for them, and the body tears into spikes. Worse, the
        // next PULL sees the same unresolvable skin and falls back to the raw pool-local ids, so the vertex
        // groups in Blender come back mislabelled and every later push builds on the wrong names. Refusing is
        // the only outcome that leaves the archive as good as it was.
        for (int v = 0; v < vertices.Length; v++)
        {
            int pool = poolOfVertex[v];
            if (pool < 0) continue;
            for (int k = 0; k < 4; k++)
            {
                if (vertices[v].BoneWeights[k] <= 0f) continue;
                int slot = poolStart[pool] + vertices[v].BoneIDs[k];
                if (slot < remap.Length && remap[slot] == globalOf[(v * 4) + k]) continue;
                reason = "the rebuilt skin does not read back — a vertex's bone id resolves to the wrong "
                    + "bone through its own remap pool. Nothing was written; the mesh is as it was.";
                return false;
            }
        }

        // LOD0 only: the other LODs keep their own vertex buffers and the pools that go with them. The pools
        // and the remap table go back exactly as they came.
        lods[0] = new FrameBlendInfo.BoneIndexInfo
        {
            BonesPerRemapPool = sizes,
            BoneRemapIDs = remap,
            SkinnedMaterialInfo = groups,
        };
        blend.BoneIndexInfos = lods;
        return true;
    }

    /// <summary>How many bones the model's rig has, or 0 when it cannot be read.</summary>
    private static int BoneCountOf(FrameObjectModel model)
    {
        try { return model.GetSkeletonObject().BoneNames?.Length ?? 0; }
        catch (Exception) { return 0; }
    }

    /// <summary>
    /// Puts the influences Blender sent for one welded vertex onto the new vertex, renormalized. False —
    /// leaving whatever the donor fill worked out — when the vertex is in no group at all, or names a bone
    /// this model does not have: a partial answer is worse than the guess it would replace.
    /// </summary>
    private static bool TakePushedSkin(
        byte[] ids, float[] weights, int welded, int boneCount, Vertex vert, byte[]? global, int target)
    {
        int at = welded * 4;
        if (welded < 0 || at + 3 >= ids.Length || at + 3 >= weights.Length) return false;

        float total = 0f;
        for (int k = 0; k < 4; k++)
        {
            if (weights[at + k] <= 0f) continue;
            if (ids[at + k] >= boneCount) return false;
            total += weights[at + k];
        }
        if (total <= 0f) return false;

        for (int k = 0; k < 4; k++)
        {
            float weight = weights[at + k];
            vert.BoneWeights[k] = weight > 0f ? weight / total : 0f;
            if (global != null) global[(target * 4) + k] = weight > 0f ? ids[at + k] : (byte)0;
        }
        return true;
    }

    /// <summary>The original vertex a new mesh's corner descends from, or -1 when Blender made it up.</summary>
    private static int DonorOf(uint[] indices, IReadOnlyList<int> donors, int corner)
    {
        if (corner < 0 || corner >= indices.Length) return -1;
        int v = (int)indices[corner];
        return v >= 0 && v < donors.Count ? donors[v] : -1;
    }

    /// <summary>Index of the source vertex closest to <paramref name="to"/>, or -1 when there are none.
    /// Linear: this runs only for vertices Blender ADDED to a skinned mesh, which is a handful next to a
    /// body's thousands, and a spatial index would be more machinery than the case is worth.</summary>
    private static int NearestSourceVertex(Vector3[] source, Vector3 to)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < source.Length; i++)
        {
            float d = Vector3.DistanceSquared(source[i], to);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    // Same direction within far less than the byte lattice can express (≈0.5°) — covers Blender's
    // unit re-normalization and its own custom-normal quantization without masking real edits.
    private static bool SameDirection(Vector3 a, Vector3 b)
    {
        float la = a.Length(), lb = b.Length();
        if (la < 1e-9f || lb < 1e-9f) return la < 1e-9f && lb < 1e-9f;
        return Vector3.Dot(a / la, b / lb) > 1f - 2e-6f;
    }

    private static bool NeedsRequantize(Vector3[] positions, Vector3 offset, float factor)
    {
        foreach (Vector3 p in positions)
        {
            Vector3 raw = (p - offset) / factor;
            if (raw.X < -0.5f || raw.X > 65535.5f
                || raw.Y < -0.5f || raw.Y > 65535.5f
                || raw.Z < -0.5f || raw.Z > 32767.5f)
            {
                return true;
            }
        }
        return false;
    }

    // Fresh quantization over the new AABB: offset = min corner, factor sized so the largest axis
    // fits its raw range (Z has only 15 bits — the top bit carries binormal handedness). A hair of
    // headroom keeps boundary verts off the clamp.
    private static (Vector3 Offset, float Factor) ComputeQuantization(Vector3[] positions)
    {
        (Vector3 min, Vector3 max) = Aabb(positions);
        Vector3 extent = max - min;
        float factor = MathF.Max(extent.X / 65535f, MathF.Max(extent.Y / 65535f, extent.Z / 32767f));
        if (factor <= 0f) factor = 1e-5f; // a degenerate (single-point) mesh still needs a scale
        return (min, factor * 1.0001f);
    }

    private static (Vector3 Min, Vector3 Max) Aabb(Vector3[] positions)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (Vector3 p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }
}
