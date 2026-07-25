using Illusion.Formats.Frames;
using Illusion.Formats.Geometry;

namespace Illusion.Assets.Bridge;

/// <summary>
/// Writes edited vertex/index buffers back into the extracted folder's pool files — the missing
/// half of Save for geometry pushes. Only pool files that actually CONTAIN a dirty buffer are
/// rewritten (untouched pools keep their original bytes), each temp-then-atomic-move like the
/// FrameResource save. The archive layer needs no changes: Build packs pool files as raw bytes.
/// </summary>
public static class SdsGeometrySaver
{
    /// <summary>Rewrites every pool file holding a dirty buffer. <paramref name="redirect"/> maps a
    /// pool's original path to the write target (probes point it at TEMP; production passes null).
    /// Returns the number of pool files written.</summary>
    public static int SaveDirtyPools(FrameResource frame, IReadOnlyCollection<ulong> dirtyVertexBuffers,
        IReadOnlyCollection<ulong> dirtyIndexBuffers, Func<string, string>? redirect = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        int written = 0;

        if (dirtyVertexBuffers.Count > 0)
        {
            foreach (BufferPoolSource source in frame.VertexBuffers.Sources)
            {
                if (!source.Hashes.Any(dirtyVertexBuffers.Contains)) continue;
                WritePool(source, redirect, stream =>
                {
                    var pool = new VertexBufferPool();
                    foreach (ulong hash in source.Hashes)
                        pool.Buffers[hash] = frame.VertexBuffers.GetBuffer(hash)
                            ?? throw new InvalidOperationException($"Vertex buffer 0x{hash:X16} vanished from its manager.");
                    pool.WriteToFile(stream);
                });
                written++;
            }
        }

        if (dirtyIndexBuffers.Count > 0)
        {
            foreach (BufferPoolSource source in frame.IndexBuffers.Sources)
            {
                if (!source.Hashes.Any(dirtyIndexBuffers.Contains)) continue;
                WritePool(source, redirect, stream =>
                {
                    var pool = new IndexBufferPool();
                    foreach (ulong hash in source.Hashes)
                        pool.Buffers[hash] = frame.IndexBuffers.GetBuffer(hash)
                            ?? throw new InvalidOperationException($"Index buffer 0x{hash:X16} vanished from its manager.");
                    pool.WriteToFile(stream);
                });
                written++;
            }
        }

        return written;
    }

    private static void WritePool(BufferPoolSource source, Func<string, string>? redirect, Action<MemoryStream> serialize)
    {
        using var stream = new MemoryStream();
        serialize(stream);

        string target = redirect?.Invoke(source.FilePath) ?? source.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        AtomicFile.WriteAllBytes(target, stream.ToArray());
    }
}
