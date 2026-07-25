using System.Diagnostics;
using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Assets.World;

namespace Illusion.Viewport;

/// <summary>
/// Environment and map catalogs of the viewport: the map-area list (districts + interiors from
/// cityareas.bin), the streaming zones and the sky texture. Built once in the background after the
/// renderer initializes; nothing can load before <see cref="D3DImageHost.CatalogReady"/> fires.
/// </summary>
internal sealed class ViewportCatalogs
{
    private readonly D3DImageHost _host;

    public ViewportCatalogs(D3DImageHost host) => _host = host;

    /// <summary>Main catalog: map areas (districts + interiors from cityareas) for the selector.</summary>
    public IReadOnlyList<MapArea> Areas { get; private set; } = Array.Empty<MapArea>();

    public MapCatalog? Map { get; private set; }

    /// <summary>District base names — for detecting neighbor proxy meshes in LoadHierarchy.</summary>
    public List<string> DistrictNames { get; private set; } = new();

    /// <summary>Streaming zones (AREA boxes city_univers ⋈ cityareas) for Whole map mode.</summary>
    public List<AreaZone>? Zones { get; private set; }

    /// <summary>Extracted path of the game's sky panorama (FreeRide.dds), or null when unavailable —
    /// secondary viewports (the material preview) load the same sky as the map.</summary>
    public string? SkyTexturePath { get; private set; }

    // Prepare the environment (sky) and build the map areas catalog. We load nothing into the viewport:
    // content arrives via location selection through LoadArea. The heavy part (city_univers unpack,
    // zone parse, sky extraction, first .mtl load) runs in the background so the first launch doesn't
    // freeze the window; results marshal back to the UI thread. Nothing can load before the catalogs
    // land: LoadArea/EnqueueCrashLayer bail while Map is null, and CatalogReady re-populates the UI.
    public void InitAsync()
    {
        Task.Run(() =>
        {
            try
            {
                // The launcher initializes the environment before opening the viewport; without it
                // there is nothing to load here.
                if (!MafiaEnvironment.IsInitialized)
                {
                    Debug.WriteLine("Mafia II environment is not initialized — launcher must set the game path first.");
                    return;
                }

                // Mafia sky (equirect panorama FreeRide.dds from skies\freeride.sds).
                string? skyTex = null;
                string skySds = Path.Combine(MafiaEnvironment.PcFolder, "sds", "skies", "freeride.sds");
                if (File.Exists(skySds))
                {
                    string tex = Path.Combine(SdsMeshLoader.EnsureExtracted(new FileInfo(skySds)), "FreeRide.dds");
                    if (File.Exists(tex)) skyTex = tex;
                }

                // First .mtl load happens on THIS thread, and no SDS load can start until CatalogReady —
                // the "first load is single-threaded" invariant of MafiaMaterials holds.
                MafiaMaterials.EnsureLoaded();

                // Global .dds index for the material editor (its own background task — a full-mirror scan
                // must not delay the catalogs). Ready long before the first material click, typically.
                Assets.Textures.TextureSearchIndex.WarmUp();

                // Main catalog: map areas from cityareas.bin (city_univers), resolve names to files.
                MapCatalog map = MapCatalog.Build(MafiaEnvironment.CityFolder, f => SdsMeshLoader.EnsureExtracted(f));

                // Streaming zones (AREA boxes city_univers ⋈ cityareas) for Whole map mode.
                List<AreaZone> zones;
                try
                {
                    zones = AreaZones.Load(f => SdsMeshLoader.EnsureExtracted(f),
                        map.Areas.Select(a => a.BaseName).ToList());
                }
                catch { zones = new List<AreaZone>(); }

                _host.Dispatcher.Invoke(() =>
                {
                    if (_host.Rnd == null) return; // disposed while initializing
                    Map = map;
                    Areas = map.Areas;
                    DistrictNames = Areas.Select(a => a.BaseName).ToList();
                    Zones = zones;
                    SkyTexturePath = skyTex;
                    if (skyTex != null) _host.LoadSky(skyTex);
                    BuildZoneBoxes();
                    _host.RaiseCatalogReady();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Catalog init error: " + ex);
            }
        });
    }

    // Zone boxes for the debug overlay: world AABB of the zone + color by its (first) district.
    public void BuildZoneBoxes()
    {
        if (Zones == null || Zones.Count == 0) return;

        var hue = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (MapArea a in Areas) if (!hue.ContainsKey(a.BaseName)) hue[a.BaseName] = hue.Count;
        int count = Math.Max(1, hue.Count);

        var boxes = new List<(Vector3 Min, Vector3 Max, Vector4 Color)>(Zones.Count);
        foreach (AreaZone z in Zones)
        {
            string? d = z.Districts.Count > 0 ? z.Districts[0] : null;
            float h = d != null && hue.TryGetValue(d, out int i) ? (float)i / count : 0.5f;
            boxes.Add((z.Min, z.Max, HueToColor(h, 0.16f)));
        }
        _host.Rnd!.SetZoneBoxes(boxes);
    }

    // HSV(h,0.7,0.95) → RGBA. Even hue by district index → neighboring districts are distinguishable.
    private static Vector4 HueToColor(float h, float alpha)
    {
        const float s = 0.7f, v = 0.95f;
        float hh = (h - MathF.Floor(h)) * 6f;
        int sec = (int)hh % 6;
        float f = hh - MathF.Floor(hh);
        float p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        (float r, float g, float b) = sec switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return new Vector4(r, g, b, alpha);
    }
}
