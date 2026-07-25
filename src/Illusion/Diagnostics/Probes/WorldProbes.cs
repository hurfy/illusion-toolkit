using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the world catalogs: streaming zones, AREA boxes, the map catalog and StreamMap.</summary>
internal static class WorldProbes
{
    // Streaming zones: box⋈cityareas, lookup of desired-districts by position, coordinate check against geometry.
    internal static void RunStreamProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_stream.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            MapCatalog map = MapCatalog.Build(MafiaEnvironment.CityFolder, f => SdsMeshLoader.EnsureExtracted(f));
            var bases = map.Areas.Select(a => a.BaseName).ToList();

            var sw = Stopwatch.StartNew();
            var zones = AreaZones.Load(f => SdsMeshLoader.EnsureExtracted(f), bases);
            sw.Stop();

            var covered = zones.SelectMany(z => z.Districts).Distinct().OrderBy(x => x).ToList();
            sb.AppendLine($"Streaming zones: {zones.Count} in {sw.ElapsedMilliseconds} ms; districts covered: {covered.Count}");
            sb.AppendLine("Covered districts: " + string.Join(", ", covered) + "\n");

            // Lookup check: for the center of several zones — which districts are desired (∪ of containing zones).
            foreach (AreaZone z in zones.Take(6))
            {
                Vector3 c = (z.Min + z.Max) * 0.5f;
                var desired = zones.Where(x => x.Contains(c)).SelectMany(x => x.Districts).Distinct().ToList();
                sb.AppendLine($"{z.Name,-28} center=({c.X,7:F0},{c.Y,7:F0},{c.Z,6:F0}) → desired: {string.Join(", ", desired)}");
            }

            // KEY check: whether the world coordinates of district meshes and AREA-zones match.
            // The center of midtown geometry must fall into a zone referencing midtown.
            MapArea? mid = map.Areas.FirstOrDefault(a => a.BaseName == "midtown");
            if (mid != null)
            {
                var meshes = SdsMeshLoader.LoadSds(mid.Summer);
                var mn = new Vector3(float.MaxValue);
                var mx = new Vector3(float.MinValue);
                foreach (var mesh in meshes)
                    foreach (var p in mesh.Positions)
                    {
                        Vector3 w = Vector3.Transform(p, mesh.World);
                        mn = Vector3.Min(mn, w);
                        mx = Vector3.Max(mx, w);
                    }
                sb.AppendLine($"\n[ALIGN] midtown geom-AABB XY=({mn.X:F0},{mn.Y:F0})..({mx.X:F0},{mx.Y:F0})");
                // 5×5 grid over midtown footprint: at how many points does midtown fall into desired?
                int hitsMidtown = 0, total = 0;
                for (int ix = 0; ix <= 4; ix++)
                    for (int iy = 0; iy <= 4; iy++)
                    {
                        float x = mn.X + (mx.X - mn.X) * ix / 4f;
                        float y = mn.Y + (mx.Y - mn.Y) * iy / 4f;
                        var p = new Vector3(x, y, 0);
                        var d = zones.Where(z => z.Contains(p)).SelectMany(z => z.Districts).Distinct().ToList();
                        total++;
                        if (d.Contains("midtown")) hitsMidtown++;
                    }
                sb.AppendLine($"[ALIGN] midtown in desired at {hitsMidtown}/{total} footprint grid points  (coordinates match if >0)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // AREA boxes (FrameObjectArea) from city_univers: positions, local AABBs, planes.
    internal static void RunAreasProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_areas.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(MafiaEnvironment.CityUniversSds));
            ExtractedSds scene = SdsMeshLoader.OpenScene(extracted);

            int n = 0;
            int withName = 0;
            foreach (var pair in scene.FrameResource!.FrameObjects)
            {
                if (pair.Value is not FrameObjectArea area) continue;
                n++;
                Vector3 t = area.WorldTransform.Translation;
                var min = area.Bounds.Min;
                var max = area.Bounds.Max;
                string name = area.Name?.ToString() ?? "?";
                if (!string.IsNullOrEmpty(name) && name != "?") withName++;
                if (n <= 30)
                    sb.AppendLine($"{name,-28} pos=({t.X,8:F0},{t.Y,8:F0},{t.Z,8:F0})  local=({min.X:F0},{min.Y:F0},{min.Z:F0})..({max.X:F0},{max.Y:F0},{max.Z:F0}) planes={area.Planes?.Length}");
            }
            sb.Insert(0, $"FrameObjectArea in city_univers: {n} (with name: {withName})\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // Location catalog (Location × Season) + actual load of the first district.
    internal static void RunMapProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_map.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }

            var sw = Stopwatch.StartNew();
            MapCatalog cat = MapCatalog.Build(MafiaEnvironment.CityFolder, f => SdsMeshLoader.EnsureExtracted(f));
            sw.Stop();
            string src = cat.FromCityAreas ? "cityareas.bin" : "folder scan";
            sb.AppendLine($"Catalog in {sw.ElapsedMilliseconds} ms: {cat.Areas.Count} areas (source: {src})\n");

            foreach (MapArea a in cat.Areas)
            {
                string kind = a.IsInterior ? "interior" : "district";
                string nb = a.Neighbors.Count > 0 ? "  neighbors: " + string.Join(", ", a.Neighbors) : "";
                sb.AppendLine($"[{kind}] {a.BaseName}{(a.HasWinter ? " +_z" : "")}{nb}");
            }

            // Actually load the first district (summer) — verify path+meshes.
            MapArea? first = cat.Areas.FirstOrDefault(a => !a.IsInterior);
            if (first != null)
            {
                var meshes = SdsMeshLoader.LoadSds(first.FileFor(false));
                sb.AppendLine($"\nLoad {first.BaseName} (summer): {meshes.Count} meshes");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // StreamMap catalog: scenes/lines, the richest line and actual load of its asset.
    internal static void RunStreamMapProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_streammap.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }

            sb.AppendLine("StreamMapFile: " + MafiaEnvironment.StreamMapPath);
            sb.AppendLine("exists: " + File.Exists(MafiaEnvironment.StreamMapPath));

            var sw = Stopwatch.StartNew();
            StreamMapCatalog cat = StreamMapCatalog.Build(MafiaEnvironment.StreamMapPath, MafiaEnvironment.PcFolder);
            sw.Stop();
            sb.AppendLine($"Catalog built in {sw.ElapsedMilliseconds} ms: {cat.Scenes.Count} scenes, {cat.LineCount} lines");

            // Top-5 scenes by number of lines.
            sb.AppendLine("\nTop scenes by number of lines:");
            foreach (StreamScene s in cat.Scenes.OrderByDescending(s => s.Lines.Count).Take(5))
                sb.AppendLine($"  {s.Name}: {s.Lines.Count} lines");

            StreamSceneLine? rich = cat.RichestLine;
            sb.AppendLine($"\nRichest line: '{rich?.SceneName}' / '{rich?.Name}' (lineID={rich?.LineID}) — {rich?.RenderableCount} renderable assets");
            if (rich != null)
            {
                foreach (StreamAsset a in rich.Assets)
                    sb.AppendLine($"    [{(a.Renderable ? "R" : " ")}] {a.Type,-14} {a.Path}  -> exists={File.Exists(a.DiskPath)}");

                // Actually load the first renderable asset — verify that the path resolves and meshes read.
                StreamAsset? first = rich.Assets.FirstOrDefault(a => a.Renderable);
                if (first != null)
                {
                    var meshes = SdsMeshLoader.LoadSds(new FileInfo(first.DiskPath));
                    sb.AppendLine($"\nLoad '{first.Path}': {meshes.Count} meshes");
                }
                else
                {
                    sb.AppendLine("\nThe richest line has no renderable assets (?!)");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
