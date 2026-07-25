namespace Illusion.Formats.Geometry;

/// <summary>One pool file's identity — its path and the buffer hashes it carries, in file order.
/// The managers keep this grouping (which their flat merge otherwise loses) so edited buffers can
/// be written back into exactly the pool files they came from; brand-new buffers append to a pool
/// with spare capacity.</summary>
public sealed class BufferPoolSource
{
    internal readonly List<ulong> HashList;

    internal BufferPoolSource(string filePath, List<ulong> hashes)
    {
        FilePath = filePath;
        HashList = hashes;
    }

    public string FilePath { get; }

    public IReadOnlyList<ulong> Hashes => HashList;
}

/// <summary>One index buffer: FNV64 name hash + 16- or 32-bit indices (format 1 / 2). Indices are
/// widened to uint in memory either way.</summary>
public sealed class IndexBuffer
{
    private uint[] _data = Array.Empty<uint>();

    public ulong Hash { get; set; }
    public int IndexFormat { get; private set; } = 1;

    public IndexBuffer(ulong hash) => Hash = hash;

    public uint[] GetData() => _data;

    public void SetData(uint[] data) => _data = data;

    public void SetFormat(int format) => IndexFormat = format;

    /// <summary>Payload size in bytes for the current format.</summary>
    public uint GetLength() => (uint)(IndexFormat == 2 ? _data.Length * 4 : _data.Length * 2);
}

/// <summary>One IndexBufferPool_*.ibp file: up to 128 index buffers, decoded and re-encoded by the
/// native core.</summary>
public sealed class IndexBufferPool
{
    public const int MaxBuffersPerPool = 128;

    public Dictionary<ulong, IndexBuffer> Buffers { get; } = new();

    public IndexBufferPool() { }

    public IndexBufferPool(MemoryStream stream) => ReadFromFile(stream);

    // The pool codec runs in the native core (little-endian only — the toolkit is PC-only).

    public void ReadFromFile(MemoryStream stream)
    {
        byte[] bytes = new byte[stream.Length - stream.Position];
        stream.ReadExactly(bytes);
        Native.Model.IndexPoolModel wire = Native.Frames.NativeFrames.LoadIndexPool(bytes);
        foreach (Native.Model.IndexBufferW source in wire.Buffers)
        {
            var buffer = new IndexBuffer(source.Hash);
            buffer.SetFormat(source.IndexFormat);
            buffer.SetData([.. source.Indices]);
            Buffers.TryAdd(buffer.Hash, buffer);
        }
    }

    public void WriteToFile(MemoryStream stream)
    {
        var wire = new Native.Model.IndexPoolModel();
        foreach (IndexBuffer buffer in Buffers.Values)
        {
            wire.Buffers.Add(new Native.Model.IndexBufferW
            {
                Hash = buffer.Hash,
                IndexFormat = buffer.IndexFormat,
                Indices = [.. buffer.GetData()],
            });
        }
        byte[] bytes = Native.Frames.NativeFrames.SaveIndexPool(wire);
        stream.Write(bytes, 0, bytes.Length);
    }

}

/// <summary>All index buffers of a scene, merged from its pool files and looked up by name hash.</summary>
public sealed class IndexBufferManager
{
    private readonly List<BufferPoolSource> _sources = new();

    public Dictionary<ulong, IndexBuffer> Buffers { get; } = new();

    /// <summary>The pool files the buffers were merged from, with their per-file hash order —
    /// the write-back grouping.</summary>
    public IReadOnlyList<BufferPoolSource> Sources => _sources;

    public IndexBufferManager(List<FileInfo> files)
    {
        foreach (FileInfo file in files)
        {
            using var stream = new MemoryStream(File.ReadAllBytes(file.FullName), false);
            var pool = new IndexBufferPool(stream);
            _sources.Add(new BufferPoolSource(file.FullName, new List<ulong>(pool.Buffers.Keys)));
            foreach (KeyValuePair<ulong, IndexBuffer> pair in pool.Buffers)
            {
                Buffers.TryAdd(pair.Key, pair.Value);
            }
        }
    }

    public IndexBuffer? GetBuffer(ulong hash) => Buffers.GetValueOrDefault(hash);

    /// <summary>Registers a brand-new buffer into a pool file with spare capacity (pools hold at
    /// most <see cref="IndexBufferPool.MaxBuffersPerPool"/> buffers). False when every pool is full
    /// or the hash already exists.</summary>
    public bool TryAddToPool(IndexBuffer buffer)
    {
        if (Buffers.ContainsKey(buffer.Hash)) return false;
        BufferPoolSource? target = null;
        foreach (BufferPoolSource source in _sources)
            if (source.HashList.Count < IndexBufferPool.MaxBuffersPerPool) { target = source; break; }
        if (target == null) return false;
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
