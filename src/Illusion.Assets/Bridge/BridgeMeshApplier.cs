using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Bridge.Geometry;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
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
        internal Vector3 NewDecompressionOffset;

        /// <summary>Pre-push packed vertex bytes (diagnostics/probes; also the undo payload).</summary>
        public byte[] OldVertexData { get; internal set; } = null!;

        /// <summary>Post-push packed vertex bytes.</summary>
        public byte[] NewVertexData { get; internal set; } = null!;

        /// <summary>The quantization scale after the push (same as before unless <see cref="Requantized"/>).</summary>
        public float NewDecompressionFactor { get; internal set; }

        /// <summary>Fresh render-ready mesh (null when <see cref="Unchanged"/>).</summary>
        public MeshData? NewMesh { get; internal set; }

        public int TouchedVertices { get; internal set; }

        /// <summary>The whole position range was re-quantized (the edit outgrew the old AABB).</summary>
        public bool Requantized { get; internal set; }

        /// <summary>The push was byte-identical — nothing to mutate, ack as applied.</summary>
        public bool Unchanged { get; internal set; }

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
        ApplyResult? result = TryApplyCountPreserving(node, payload, out skipReason);
        if (result != null) return result;
        if (skipReason == null || !skipReason.StartsWith("topology changed", StringComparison.Ordinal))
            return null;
        return TryApplyRebuild(node, payload, out skipReason);
    }

    /// <summary>Computes the count-preserving application of <paramref name="payload"/> to
    /// <paramref name="node"/>'s mesh. Null with a reason when it cannot apply (topology changed,
    /// unsupported object, malformed payload) — the caller reports it as a per-object skip.</summary>
    public static ApplyResult? TryApplyCountPreserving(IFrameNode node, MeshObjectPayload payload, out string? skipReason)
    {
        skipReason = null;
        if (node is not FrameNodeAdapter adapter
            || adapter.Frame is not FrameObjectSingleMesh frame
            || frame.GetType() != typeof(FrameObjectSingleMesh))
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
            return new ApplyResult { Unchanged = true, TouchedVertices = 0 };
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
            Parts = SdsMeshLoader.BuildParts(frame, decoded.Indices.Length),
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
        if (node is not FrameNodeAdapter adapter
            || adapter.Frame is not FrameObjectSingleMesh frame
            || frame.GetType() != typeof(FrameObjectSingleMesh))
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
                touched++;
                if (hasTangent)
                {
                    meshTangents![v] = vert.Tangent;
                    meshBinormals![v] = vert.Binormal;
                }
            }
            outVerts[v] = vert;
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
        WeldedMesh exported = WeldMapBuilder.Build(
            BridgeMeshExporter.BuildWeldKeys(decoded), decoded.Positions, decoded.Normals, null, decoded.Indices);

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
