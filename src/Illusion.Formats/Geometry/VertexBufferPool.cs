namespace Illusion.Formats.Geometry;

/// <summary>One vertex buffer: FNV64 name hash + raw packed vertex bytes (decoded per-LOD by
/// <see cref="VertexTranslator"/> using the LOD's declaration and stride).</summary>
public sealed class VertexBuffer
{
    private byte[] _data = Array.Empty<byte>();

    public ulong Hash { get; set; }

    public byte[] Data
    {
        get => _data;
        set => _data = value;
    }

    public VertexBuffer(ulong hash) => Hash = hash;

}

/// <summary>One VertexBufferPool_*.vbp file: up to 128 buffers behind a version/size header.</summary>
public sealed class VertexBufferPool
{
    public Dictionary<ulong, VertexBuffer> Buffers { get; } = new();

    public VertexBufferPool() { }

    public VertexBufferPool(MemoryStream stream) => ReadFromFile(stream);

    // The pool codec runs in the native core (little-endian only — the toolkit is PC-only).

    public void ReadFromFile(MemoryStream stream)
    {
        byte[] bytes = new byte[stream.Length - stream.Position];
        stream.ReadExactly(bytes);
        Native.Model.VertexPoolModel wire = Native.Frames.NativeFrames.LoadVertexPool(bytes);
        foreach (Native.Model.VertexBufferW source in wire.Buffers)
        {
            var buffer = new VertexBuffer(source.Hash) { Data = source.Data };
            Buffers.TryAdd(buffer.Hash, buffer);
        }
    }

    public void WriteToFile(MemoryStream stream)
    {
        var wire = new Native.Model.VertexPoolModel();
        foreach (VertexBuffer buffer in Buffers.Values)
        {
            wire.Buffers.Add(new Native.Model.VertexBufferW { Hash = buffer.Hash, Data = buffer.Data });
        }
        byte[] bytes = Native.Frames.NativeFrames.SaveVertexPool(wire);
        stream.Write(bytes, 0, bytes.Length);
    }

}

/// <summary>All vertex buffers of a scene, merged from its pool files and looked up by name hash.</summary>
public sealed class VertexBufferManager
{
    private readonly List<BufferPoolSource> _sources = new();

    public Dictionary<ulong, VertexBuffer> Buffers { get; } = new();

    /// <summary>The pool files the buffers were merged from, with their per-file hash order —
    /// the write-back grouping.</summary>
    public IReadOnlyList<BufferPoolSource> Sources => _sources;

    public VertexBufferManager(List<FileInfo> files)
    {
        foreach (FileInfo file in files)
        {
            using var stream = new MemoryStream(File.ReadAllBytes(file.FullName), false);
            var pool = new VertexBufferPool(stream);
            _sources.Add(new BufferPoolSource(file.FullName, new List<ulong>(pool.Buffers.Keys)));
            foreach (KeyValuePair<ulong, VertexBuffer> pair in pool.Buffers)
            {
                Buffers.TryAdd(pair.Key, pair.Value);
            }
        }
    }

    public VertexBuffer? GetBuffer(ulong hash) => Buffers.GetValueOrDefault(hash);

    /// <summary>Registers a brand-new buffer into a pool file with spare capacity, opening another pool file
    /// when every existing one is full. False only when the hash already exists, or when there is no pool to
    /// take the name from.</summary>
    public bool TryAddToPool(VertexBuffer buffer)
    {
        if (Buffers.ContainsKey(buffer.Hash)) return false;
        BufferPoolSource? target = null;
        foreach (BufferPoolSource source in _sources)
            if (source.HashList.Count < IndexBufferPool.MaxBuffersPerPool) { target = source; break; }
        if (target == null)
        {
            target = BufferPoolNaming.NextPool(_sources, "VertexBufferPool", ".vbp");
            if (target == null) return false;
            _sources.Add(target);
        }
        target.HashList.Add(buffer.Hash);
        Buffers.Add(buffer.Hash, buffer);
        return true;
    }

    /// <summary>Unregisters a buffer added by <see cref="TryAddToPool"/> (undo of a creation).</summary>
    public void Remove(ulong hash)
    {
        Buffers.Remove(hash);
        foreach (BufferPoolSource source in _sources) source.HashList.Remove(hash);
    }
}
