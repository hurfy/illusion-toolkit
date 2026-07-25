using Illusion.Formats.StreamMap;

namespace Illusion.Assets.World;

/// <summary>
/// Builds a scene tree for the viewport selector from <see cref="StreamMapFile"/>: line-group → line →
/// asset set. The line↔loader link is the same as in the toolkit: a loader is active if LineID ∈ [Start, End].
/// </summary>
public sealed class StreamMapCatalog
{
    public IReadOnlyList<StreamScene> Scenes { get; }
    public int LineCount { get; }
    /// <summary>The line with the most renderable assets — a sensible default (a non-empty viewport). Null if the StreamMap has no lines.</summary>
    public StreamSceneLine? RichestLine { get; }

    // Non-geometry types — their .sds yield no meshes, no point extracting them.
    private static readonly HashSet<StreamGroupType> NonGeometry = new()
    {
        StreamGroupType.Null, StreamGroupType.Base_Anim, StreamGroupType.GUI, StreamGroupType.Sky,
        StreamGroupType.Tables, StreamGroupType.Default_Sound, StreamGroupType.Particles,
        StreamGroupType.Game_Script, StreamGroupType.Mission_Script, StreamGroupType.Script,
        StreamGroupType.Script_Sounds, StreamGroupType.Director_Lua, StreamGroupType.Sound_City,
        StreamGroupType.Anims_City, StreamGroupType.Generic_Speech_Normal,
        StreamGroupType.Generic_Speech_Gangster, StreamGroupType.Generic_Speeh_Various,
        StreamGroupType.Generic_Speech_Story, StreamGroupType.Generic_Speech_Police,
        StreamGroupType.Big_Script, StreamGroupType.Big_Mission_Script, StreamGroupType.Text,
        StreamGroupType.Ingame_GUI, StreamGroupType.Dabing,
    };

    private StreamMapCatalog(IReadOnlyList<StreamScene> scenes, int lineCount, StreamSceneLine? richest)
    {
        Scenes = scenes;
        LineCount = lineCount;
        RichestLine = richest;
    }

    /// <param name="streamMapPath">Path to StreamMapa.bin.</param>
    /// <param name="pcFolder">The game's <c>pc</c> folder — base for loader paths (<c>/sds/...</c>).</param>
    public static StreamMapCatalog Build(string streamMapPath, string pcFolder)
    {
        StreamMapFile map = StreamMapFile.Load(streamMapPath);

        // Disk resolve and File.Exists — once per loader (not per line×loader pair:
        // a loader is active for a wide line range, otherwise there would be tens of thousands of extra stats).
        int nLoaders = map.Loaders.Length;
        var loaderDisk = new string[nLoaders];
        var loaderRenderable = new bool[nLoaders];
        for (int i = 0; i < nLoaders; i++)
        {
            StreamMapLoader ld = map.Loaders[i];
            loaderDisk[i] = ResolveDisk(pcFolder, ld.Path);
            loaderRenderable[i] = IsGeometry(ld.Type)
                                  && ld.Path.EndsWith(".sds", StringComparison.OrdinalIgnoreCase)
                                  && File.Exists(loaderDisk[i]);
        }

        // Asset set per line = loaders whose [Start,End] covers LineID; dedup by file on disk
        // (one line may reference an .sds via several loaders).
        var lineAssets = new Dictionary<int, List<StreamAsset>>();
        foreach (StreamMapLine line in map.Lines)
        {
            var assets = new List<StreamAsset>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nLoaders; i++)
            {
                StreamMapLoader ld = map.Loaders[i];
                if (line.LineID < ld.Start || line.LineID > ld.End) continue;
                if (!seen.Add(loaderDisk[i])) continue;

                assets.Add(new StreamAsset
                {
                    Type = ld.Type,
                    Path = ld.Path,
                    Entity = ld.Entity,
                    DiskPath = loaderDisk[i],
                    Renderable = loaderRenderable[i],
                });
            }
            lineAssets[line.LineID] = assets;
        }

        // Group lines by line-group (GroupID → GroupHeaders), preserving header order.
        var scenes = new List<StreamScene>();
        StreamSceneLine? richest = null;

        var byGroup = map.Lines.GroupBy(l => l.GroupID).ToDictionary(g => g.Key, g => g.ToList());
        for (int gid = 0; gid < map.GroupHeaders.Length; gid++)
        {
            if (!byGroup.TryGetValue(gid, out List<StreamMapLine>? groupLines)) continue;

            string sceneName = map.GroupHeaders[gid];
            var sceneLines = new List<StreamSceneLine>(groupLines.Count);
            foreach (StreamMapLine l in groupLines)
            {
                List<StreamAsset> assets = lineAssets[l.LineID];
                int renderable = assets.Count(a => a.Renderable);
                var sl = new StreamSceneLine
                {
                    SceneName = sceneName,
                    Name = l.Name,
                    LineID = l.LineID,
                    Assets = assets,
                    RenderableCount = renderable,
                };
                sceneLines.Add(sl);
                if (richest == null || sl.RenderableCount > richest.RenderableCount) richest = sl;
            }
            scenes.Add(new StreamScene { Name = sceneName, Lines = sceneLines });
        }

        return new StreamMapCatalog(scenes, map.Lines.Length, richest);
    }

    private static bool IsGeometry(StreamGroupType t) => !NonGeometry.Contains(t);

    // "/sds/city/x.sds" → "<pc>\sds\city\x.sds"
    private static string ResolveDisk(string pcFolder, string relPath)
    {
        string rel = relPath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(pcFolder, rel);
    }
}
