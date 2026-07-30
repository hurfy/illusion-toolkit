using Illusion.Formats.Archive;
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
                Announce(source, "VertexBufferPool", redirect);
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
                Announce(source, "IndexBufferPool", redirect);
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// Puts a pool file the toolkit invented into the archive's SDSContent.xml. Packing goes by the manifest,
    /// not by the folder, so without this the new pool is silently dropped at Build and the archive ends up
    /// naming buffers nothing carries — a district that no longer loads. Redirected writes (the probes) are a
    /// scratch copy that is never packed, so their manifest is left alone.
    /// </summary>
    private static void Announce(BufferPoolSource source, string typeName, Func<string, string>? redirect)
    {
        if (!source.IsNew || redirect != null) return;
        string? folder = Path.GetDirectoryName(source.FilePath);
        if (folder == null || !File.Exists(Path.Combine(folder, "SDSContent.xml"))) return;

        // Version 2 is what every shipped pool entry carries.
        SdsManifest.Load(folder).AddEntry(typeName, Path.GetFileName(source.FilePath), version: 2);
        source.MarkRegistered();
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
