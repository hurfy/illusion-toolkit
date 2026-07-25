using System.Numerics;
using System.Text.Json;

namespace Illusion.Assets.Import;

/// <summary>One triangle primitive of a glTF mesh: its own vertex arrays and one material name.</summary>
public sealed class GltfPrimitive
{
    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();   // computed (smooth) when the file has none
    public Vector2[] Uvs = Array.Empty<Vector2>();       // glTF convention (top-left origin); zero when absent
    public uint[] Indices = Array.Empty<uint>();
    public string? MaterialName;
}

/// <summary>One mesh-carrying node instance: name, world transform (glTF axes, +Y up), primitives.</summary>
public sealed class GltfMeshInstance
{
    public string Name = "";
    public Matrix4x4 World = Matrix4x4.Identity;
    public List<GltfPrimitive> Primitives = new();
}

/// <summary>
/// Minimal glTF 2.0 reader for the import dialog — self-written, no dependencies. Covers what DCC
/// exporters actually emit for static meshes: .glb and .gltf (external .bin or base64 data URIs),
/// float/normalized attribute accessors, u8/u16/u32 (or absent) indices, node hierarchies with matrix
/// or TRS transforms, per-primitive material names. Compressed/sparse data (Draco, meshopt, sparse
/// accessors) is refused with a reason naming the culprit — never silently mis-read.
/// </summary>
public static class GltfFile
{
    /// <summary>Loads every mesh-carrying node of the file's scenes. Null with a reason on failure.</summary>
    public static List<GltfMeshInstance>? TryLoad(string path, out string? error)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return TryLoad(bytes, Path.GetDirectoryName(path), out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Parses glTF content from memory; <paramref name="baseDirectory"/> resolves external buffer
    /// files (null → external URIs are refused).</summary>
    public static List<GltfMeshInstance>? TryLoad(byte[] bytes, string? baseDirectory, out string? error)
    {
        try
        {
            byte[] json;
            byte[]? binChunk = null;
            if (bytes.Length >= 12 && BitConverter.ToUInt32(bytes, 0) == 0x46546C67) // "glTF"
            {
                (json, binChunk) = ReadGlbChunks(bytes);
            }
            else
            {
                json = bytes;
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            return Parse(doc.RootElement, binChunk, baseDirectory, out error);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException
            or IndexOutOfRangeException or ArgumentException or KeyNotFoundException)
        {
            error = "not a valid glTF file — " + ex.Message;
            return null;
        }
    }

    // ── GLB container ──

    private static (byte[] Json, byte[]? Bin) ReadGlbChunks(byte[] bytes)
    {
        uint version = BitConverter.ToUInt32(bytes, 4);
        if (version != 2) throw new InvalidDataException($"GLB version {version} (only 2 is supported)");

        byte[]? json = null;
        byte[]? bin = null;
        int at = 12;
        while (at + 8 <= bytes.Length)
        {
            int length = checked((int)BitConverter.ToUInt32(bytes, at));
            uint kind = BitConverter.ToUInt32(bytes, at + 4);
            at += 8;
            if (at + length > bytes.Length) throw new InvalidDataException("GLB chunk overruns the file");
            var chunk = new byte[length];
            Array.Copy(bytes, at, chunk, 0, length);
            if (kind == 0x4E4F534A) json = chunk;      // "JSON"
            else if (kind == 0x004E4942) bin = chunk;  // "BIN"
            at += length;
        }
        return (json ?? throw new InvalidDataException("GLB has no JSON chunk"), bin);
    }

    // ── Document ──

    private static List<GltfMeshInstance>? Parse(
        JsonElement root, byte[]? binChunk, string? baseDirectory, out string? error)
    {
        error = null;

        // Required extensions we cannot honor mean the data is not readable as plain accessors.
        if (root.TryGetProperty("extensionsRequired", out JsonElement required))
        {
            foreach (JsonElement ext in required.EnumerateArray())
            {
                string name = ext.GetString() ?? "";
                error = $"the file requires the '{name}' extension (compressed data) — " +
                    "re-export without compression";
                return null;
            }
        }

        byte[][] buffers = ReadBuffers(root, binChunk, baseDirectory);
        JsonElement views = root.TryGetProperty("bufferViews", out JsonElement bv) ? bv : default;
        JsonElement accessors = root.TryGetProperty("accessors", out JsonElement acc) ? acc : default;

        // Material names by index ("material N" when unnamed — still a distinct identity).
        var materialNames = new List<string>();
        if (root.TryGetProperty("materials", out JsonElement mats))
        {
            int i = 0;
            foreach (JsonElement m in mats.EnumerateArray())
            {
                materialNames.Add(m.TryGetProperty("name", out JsonElement n)
                    ? n.GetString() ?? $"material_{i}" : $"material_{i}");
                i++;
            }
        }

        // Meshes → primitives (decoded lazily per node reference, cached by mesh index).
        if (!root.TryGetProperty("meshes", out JsonElement meshes)) { error = "the file has no meshes"; return null; }
        var meshCache = new Dictionary<int, (string Name, List<GltfPrimitive> Primitives)>();
        (string, List<GltfPrimitive>) DecodeMesh(int index)
        {
            if (meshCache.TryGetValue(index, out var cached)) return cached;
            JsonElement mesh = meshes[index];
            string name = mesh.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? $"mesh_{index}" : $"mesh_{index}";
            var primitives = new List<GltfPrimitive>();
            foreach (JsonElement prim in mesh.GetProperty("primitives").EnumerateArray())
            {
                if (prim.TryGetProperty("extensions", out JsonElement pext)
                    && (pext.TryGetProperty("KHR_draco_mesh_compression", out _)))
                {
                    throw new InvalidDataException($"mesh '{name}' is Draco-compressed — re-export without compression");
                }
                int mode = prim.TryGetProperty("mode", out JsonElement pm) ? pm.GetInt32() : 4;
                if (mode != 4)
                {
                    throw new InvalidDataException($"mesh '{name}' has a non-triangle primitive (mode {mode})");
                }

                JsonElement attrs = prim.GetProperty("attributes");
                int posIndex = attrs.GetProperty("POSITION").GetInt32();
                Vector3[] positions = ReadVec3(accessors, views, buffers, posIndex);
                Vector3[]? normals = attrs.TryGetProperty("NORMAL", out JsonElement na)
                    ? ReadVec3(accessors, views, buffers, na.GetInt32()) : null;
                Vector2[]? uvs = attrs.TryGetProperty("TEXCOORD_0", out JsonElement ta)
                    ? ReadVec2(accessors, views, buffers, ta.GetInt32()) : null;

                uint[] indices = prim.TryGetProperty("indices", out JsonElement ia)
                    ? ReadIndices(accessors, views, buffers, ia.GetInt32())
                    : SequentialIndices(positions.Length);

                var p = new GltfPrimitive
                {
                    Positions = positions,
                    Uvs = uvs ?? new Vector2[positions.Length],
                    Indices = indices,
                    Normals = normals ?? SmoothNormals(positions, indices),
                    MaterialName = prim.TryGetProperty("material", out JsonElement mi)
                        && mi.GetInt32() < materialNames.Count ? materialNames[mi.GetInt32()] : null,
                };
                if (normals != null)
                {
                    for (int v = 0; v < p.Normals.Length; v++)
                    {
                        float len = p.Normals[v].Length();
                        p.Normals[v] = len > 1e-12f ? p.Normals[v] / len : new Vector3(0f, 1f, 0f);
                    }
                }
                primitives.Add(p);
            }
            return meshCache[index] = (name, primitives);
        }

        // Nodes: world transforms down the hierarchy; every mesh-carrying node becomes one instance.
        var result = new List<GltfMeshInstance>();
        JsonElement nodes = root.TryGetProperty("nodes", out JsonElement nn) ? nn : default;
        void Walk(int nodeIndex, Matrix4x4 parentWorld)
        {
            JsonElement node = nodes[nodeIndex];
            Matrix4x4 world = LocalTransform(node) * parentWorld;
            if (node.TryGetProperty("mesh", out JsonElement meshRef))
            {
                (string meshName, List<GltfPrimitive> primitives) = DecodeMesh(meshRef.GetInt32());
                string name = node.TryGetProperty("name", out JsonElement nname)
                    ? nname.GetString() ?? meshName : meshName;
                result.Add(new GltfMeshInstance { Name = name, World = world, Primitives = primitives });
            }
            if (node.TryGetProperty("children", out JsonElement children))
                foreach (JsonElement c in children.EnumerateArray()) Walk(c.GetInt32(), world);
        }

        if (root.TryGetProperty("scenes", out JsonElement scenes))
        {
            int sceneIndex = root.TryGetProperty("scene", out JsonElement s) ? s.GetInt32() : 0;
            JsonElement scene = scenes[sceneIndex];
            if (scene.TryGetProperty("nodes", out JsonElement roots))
                foreach (JsonElement r in roots.EnumerateArray()) Walk(r.GetInt32(), Matrix4x4.Identity);
        }
        else if (nodes.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < nodes.GetArrayLength(); i++) Walk(i, Matrix4x4.Identity);
        }

        if (result.Count == 0) { error = "the file's scene references no meshes"; return null; }
        return result;
    }

    // glTF composes column-vector matrices world = parent * local with local = T*R*S; in Numerics'
    // row-vector convention that is world = local * parent with local = S*R*T. The 16-float matrix is
    // column-major, which linear-copies straight into the row-vector Matrix4x4 (translation lands in M4x).
    private static Matrix4x4 LocalTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out JsonElement m))
        {
            var f = new float[16];
            int i = 0;
            foreach (JsonElement v in m.EnumerateArray()) f[i++] = v.GetSingle();
            return new Matrix4x4(
                f[0], f[1], f[2], f[3],
                f[4], f[5], f[6], f[7],
                f[8], f[9], f[10], f[11],
                f[12], f[13], f[14], f[15]);
        }
        Vector3 scale = node.TryGetProperty("scale", out JsonElement s) ? ReadV3(s) : Vector3.One;
        Quaternion rotation = node.TryGetProperty("rotation", out JsonElement r)
            ? new Quaternion(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle())
            : Quaternion.Identity;
        Vector3 translation = node.TryGetProperty("translation", out JsonElement t) ? ReadV3(t) : Vector3.Zero;
        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(translation);
    }

    private static Vector3 ReadV3(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());

    // ── Buffers and accessors ──

    private static byte[][] ReadBuffers(JsonElement root, byte[]? binChunk, string? baseDirectory)
    {
        if (!root.TryGetProperty("buffers", out JsonElement buffers)) return Array.Empty<byte[]>();
        var result = new List<byte[]>();
        int index = 0;
        foreach (JsonElement b in buffers.EnumerateArray())
        {
            if (b.TryGetProperty("uri", out JsonElement uriEl))
            {
                string uri = uriEl.GetString() ?? "";
                if (uri.StartsWith("data:", StringComparison.Ordinal))
                {
                    int comma = uri.IndexOf(',');
                    if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.Ordinal))
                        throw new InvalidDataException($"buffer {index} has an unsupported data URI encoding");
                    result.Add(Convert.FromBase64String(uri[(comma + 1)..]));
                }
                else
                {
                    if (baseDirectory == null)
                        throw new InvalidDataException($"buffer {index} references an external file '{uri}'");
                    string path = Path.Combine(baseDirectory, Uri.UnescapeDataString(uri));
                    if (!File.Exists(path))
                        throw new InvalidDataException($"buffer file '{uri}' is missing next to the .gltf");
                    result.Add(File.ReadAllBytes(path));
                }
            }
            else
            {
                result.Add(binChunk ?? throw new InvalidDataException($"buffer {index} expects a GLB BIN chunk"));
            }
            index++;
        }
        return result.ToArray();
    }

    private readonly record struct AccessorInfo(
        byte[] Buffer, int Offset, int Stride, int Count, int ComponentType, bool Normalized, string Type);

    private static AccessorInfo Accessor(JsonElement accessors, JsonElement views, byte[][] buffers, int index)
    {
        JsonElement a = accessors[index];
        if (a.TryGetProperty("sparse", out _))
            throw new InvalidDataException("sparse accessors are not supported — re-export without them");
        string type = a.GetProperty("type").GetString() ?? "";
        int componentType = a.GetProperty("componentType").GetInt32();
        int count = a.GetProperty("count").GetInt32();
        bool normalized = a.TryGetProperty("normalized", out JsonElement nz) && nz.GetBoolean();
        int accessorOffset = a.TryGetProperty("byteOffset", out JsonElement ao) ? ao.GetInt32() : 0;

        if (!a.TryGetProperty("bufferView", out JsonElement viewRef))
            throw new InvalidDataException("an accessor has no bufferView (zero-filled accessors are not supported)");
        JsonElement view = views[viewRef.GetInt32()];
        byte[] buffer = buffers[view.GetProperty("buffer").GetInt32()];
        int viewOffset = view.TryGetProperty("byteOffset", out JsonElement vo) ? vo.GetInt32() : 0;
        int elementSize = ComponentSize(componentType) * ComponentCount(type);
        int stride = view.TryGetProperty("byteStride", out JsonElement st) ? st.GetInt32() : elementSize;
        return new AccessorInfo(buffer, viewOffset + accessorOffset, stride, count, componentType, normalized, type);
    }

    private static int ComponentSize(int componentType) => componentType switch
    {
        5120 or 5121 => 1, // byte / ubyte
        5122 or 5123 => 2, // short / ushort
        5125 or 5126 => 4, // uint / float
        _ => throw new InvalidDataException($"unsupported accessor component type {componentType}"),
    };

    private static int ComponentCount(string type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        _ => throw new InvalidDataException($"unsupported accessor type '{type}'"),
    };

    private static float Component(AccessorInfo a, int element, int component)
    {
        int at = a.Offset + element * a.Stride + component * ComponentSize(a.ComponentType);
        return a.ComponentType switch
        {
            5126 => BitConverter.ToSingle(a.Buffer, at),
            5121 => a.Normalized ? a.Buffer[at] / 255f : a.Buffer[at],
            5123 => a.Normalized
                ? BitConverter.ToUInt16(a.Buffer, at) / 65535f
                : BitConverter.ToUInt16(a.Buffer, at),
            5120 => a.Normalized
                ? MathF.Max((sbyte)a.Buffer[at] / 127f, -1f)
                : (sbyte)a.Buffer[at],
            5122 => a.Normalized
                ? MathF.Max(BitConverter.ToInt16(a.Buffer, at) / 32767f, -1f)
                : BitConverter.ToInt16(a.Buffer, at),
            5125 => BitConverter.ToUInt32(a.Buffer, at),
            _ => throw new InvalidDataException($"unsupported accessor component type {a.ComponentType}"),
        };
    }

    private static Vector3[] ReadVec3(JsonElement accessors, JsonElement views, byte[][] buffers, int index)
    {
        AccessorInfo a = Accessor(accessors, views, buffers, index);
        var result = new Vector3[a.Count];
        for (int i = 0; i < a.Count; i++)
            result[i] = new Vector3(Component(a, i, 0), Component(a, i, 1), Component(a, i, 2));
        return result;
    }

    private static Vector2[] ReadVec2(JsonElement accessors, JsonElement views, byte[][] buffers, int index)
    {
        AccessorInfo a = Accessor(accessors, views, buffers, index);
        var result = new Vector2[a.Count];
        for (int i = 0; i < a.Count; i++)
            result[i] = new Vector2(Component(a, i, 0), Component(a, i, 1));
        return result;
    }

    private static uint[] ReadIndices(JsonElement accessors, JsonElement views, byte[][] buffers, int index)
    {
        AccessorInfo a = Accessor(accessors, views, buffers, index);
        var result = new uint[a.Count];
        for (int i = 0; i < a.Count; i++) result[i] = (uint)Component(a, i, 0);
        return result;
    }

    private static uint[] SequentialIndices(int count)
    {
        var result = new uint[count];
        for (uint i = 0; i < count; i++) result[i] = i;
        return result;
    }

    // Smooth, area-weighted normals for primitives that ship without any (the unnormalized cross
    // product of a triangle IS its area weight).
    private static Vector3[] SmoothNormals(Vector3[] positions, uint[] indices)
    {
        var accum = new Vector3[positions.Length];
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            uint a = indices[t], b = indices[t + 1], c = indices[t + 2];
            if (a >= positions.Length || b >= positions.Length || c >= positions.Length)
                throw new InvalidDataException("an index accessor points past the position array");
            Vector3 cross = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            accum[a] += cross;
            accum[b] += cross;
            accum[c] += cross;
        }
        for (int i = 0; i < accum.Length; i++)
        {
            float len = accum[i].Length();
            accum[i] = len > 1e-12f ? accum[i] / len : new Vector3(0f, 1f, 0f);
        }
        return accum;
    }
}
