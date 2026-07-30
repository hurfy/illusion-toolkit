using System.IO;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Bridge;

/// <summary>
/// Which archives hold a copy of a given LOD0 vertex buffer.
///
/// The buffer pools are content-addressed: identical geometry gets an identical name, and the game ships the
/// same mesh into every district that shows it — the wanted poster's buffer lives in 41 archives under one
/// name. The engine resolves a buffer by that name, so once any copy is loaded it serves every archive asking
/// for it, while the toolkit rewrites only the archive it was told to. Editing one copy therefore leaves the
/// others holding different bytes under the same name, and which shape the game draws comes down to what
/// streamed first.
///
/// Nothing here fixes that — it is what tells the modder it is happening.
/// </summary>
internal static class SharedBufferIndex
{
    private static readonly object Sync = new();
    private static Dictionary<ulong, List<string>>? index;

    /// <summary>
    /// The other archives carrying this buffer, alphabetically. Empty when it is unique, when the index cannot
    /// be built, or before the install has been unpacked.
    /// </summary>
    /// <param name="exclude">The archive the edit was made in — it is not "another" copy.</param>
    public static IReadOnlyList<string> OtherArchivesWith(ulong hash, string exclude)
    {
        Dictionary<ulong, List<string>> built;
        lock (Sync)
        {
            built = index ??= Build();
        }
        if (!built.TryGetValue(hash, out List<string>? archives)) return [];
        return [.. archives.Where(a => !string.Equals(a, exclude, StringComparison.OrdinalIgnoreCase))];
    }

    // One pass over the city archives that are ALREADY unpacked. Extracting one here would turn a push into a
    // disk-filling operation nobody asked for, so an archive the user has never opened simply does not
    // contribute — the count then understates, which is the safe direction for a warning.
    private static Dictionary<ulong, List<string>> Build()
    {
        var map = new Dictionary<ulong, List<string>>();
        try
        {
            if (!MafiaEnvironment.IsInitialized || !Directory.Exists(MafiaEnvironment.CityFolder)) return map;

            foreach (string path in Directory.GetFiles(MafiaEnvironment.CityFolder, "*.sds"))
            {
                var file = new FileInfo(path);
                string extracted = MafiaEnvironment.ExtractedDir(file);
                if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

                FrameResource? fr;
                try { fr = SdsMeshLoader.OpenScene(extracted).FrameResource; }
                catch (Exception) { continue; }
                if (fr?.FrameObjects == null) continue;

                var seen = new HashSet<ulong>();
                foreach (object? value in fr.FrameObjects.Values)
                {
                    if (value is not FrameObjectSingleMesh mesh || mesh.Geometry is not { LOD.Length: > 0 } geometry)
                    {
                        continue;
                    }
                    if (!seen.Add(geometry.LOD[0].VertexBufferRef.Hash)) continue;
                    if (!map.TryGetValue(geometry.LOD[0].VertexBufferRef.Hash, out List<string>? archives))
                    {
                        map[geometry.LOD[0].VertexBufferRef.Hash] = archives = [];
                    }
                    archives.Add(file.Name);
                }
            }

            foreach (List<string> archives in map.Values) archives.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A survey that cannot be taken warns about nothing; it must never break a push.
        }
        return map;
    }
}
