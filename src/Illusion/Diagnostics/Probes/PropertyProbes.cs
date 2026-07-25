using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Domain.Materials;
using Illusion.Domain.Properties;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.ViewModels;
using Illusion.Views;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Property-descriptor probe: builds the property catalog for every frame-object type and asserts it stays
/// complete (every editable field reflected on the vendor types is either described or explicitly hidden),
/// that descriptors read and round-trip, and that the Name lock follows the name-table membership. With game
/// data present it also proves an identity write-back through every editable descriptor is a serialization
/// fixpoint. Output: %TEMP%\illusion_properties.txt
/// </summary>
internal static class PropertyProbes
{
    // Vendor rw properties intentionally NOT exposed as a 1:1 editable descriptor — excluded (owned by the gizmo,
    // derived, graph/bookkeeping, block-constructing getters, length counters) or shown read-only as an aggregate
    // (bounds min/max, texture arrays, plane/lens/hitbox lists). New scalar fields NOT listed here fail coverage.
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
    {
        // graph / bookkeeping / gizmo-owned / derived
        "Parent", "Root", "Children", "Index", "LocalTransform", "WorldTransform", "ParentIndex1", "ParentIndex2",
        // intentionally not surfaced (per UX): base unknown shorts, name-table flags, OM texture
        "Unk3", "Unk6", "FrameNameTableFlags", "OMTextureHash",
        // length counters + node-data array
        "DataSize", "Data",
        // block-constructing getters (never touched by display code) + bounds aggregate
        "Geometry", "Material", "Skeleton", "BlendInfo", "SkeletonHierarchy", "Boundings",
        // model arrays shown read-only / summarized
        "BlendMeshSplits", "RestTransform", "AttachmentReferences", "HitBoxes",
        // light texture array + box aggregate
        "TextureHashes", "UnkBox",
        // camera lens array
        "Lens",
        // dummy/area/sector bounds aggregates + plane arrays + counters
        "Bounds", "BoundaryBoxMinimum", "BoundaryBoxMaximum", "PlaneSize", "PlanesSize", "Planes",
    };

    internal static void RunPropertiesProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_properties.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            var fr = new FrameResource();
            var doc = new SceneDocumentAdapter(fr, new FileInfo(Path.Combine(Path.GetTempPath(), "illusion_properties_probe.sds")));

            // One representative of every frame-object type (fresh — arrays may be null, which the catalog must survive).
            (string Name, FrameObjectBase Obj)[] specimens =
            {
                ("Point", new FrameObjectPoint(fr)),
                ("SingleMesh", new FrameObjectSingleMesh(fr)),
                ("Frame", new FrameObjectFrame(fr)),
                ("Light", new FrameObjectLight(fr)),
                ("Camera", new FrameObjectCamera(fr)),
                ("Component_U005", new FrameObjectComponent_U005(fr)),
                ("Sector", new FrameObjectSector(fr)),
                ("Dummy", new FrameObjectDummy(fr)),
                ("Deflector", new FrameObjectDeflector(fr)),
                ("Area", new FrameObjectArea(fr)),
                ("Target", new FrameObjectTarget(fr)),
                ("Model", new FrameObjectModel(fr)),
                ("Collision", new FrameObjectCollision(fr)),
            };

            foreach ((string name, FrameObjectBase obj) in specimens)
            {
                FrameNodeAdapter node = doc.Node(obj);
                IReadOnlyList<PropertyGroup> groups = node.GetPropertyGroups();
                List<PropertyDescriptor> descs = groups.SelectMany(g => g.Properties).ToList();

                Check($"{name}: yields property groups", groups.Count > 0, $"{groups.Count} groups, {descs.Count} props");

                // Ids are stable across rebuilds (undo/redo keys on them).
                List<string> ids1 = descs.Select(d => d.Id).ToList();
                List<string> ids2 = node.GetPropertyGroups().SelectMany(g => g.Properties).Select(d => d.Id).ToList();
                Check($"{name}: descriptor ids are stable across rebuilds", ids1.SequenceEqual(ids2));
                Check($"{name}: descriptor ids are unique", ids1.Distinct().Count() == ids1.Count);

                // Every Get() reads; every editable descriptor round-trips a representative value.
                bool allGetOk = true, allRoundTrip = true;
                string rtDetail = "";
                foreach (PropertyDescriptor d in descs)
                {
                    object? orig;
                    try { orig = d.Get(); }
                    catch (Exception ex) { allGetOk = false; rtDetail = $"{d.Id} Get(): {ex.Message}"; continue; }

                    if (d.IsReadOnly || d.Set == null) continue;
                    object? rep = RepValue(d);
                    if (rep == null) continue;

                    d.Set(rep);
                    object? read = d.Get();
                    bool ok = d.Kind == PropertyKind.HashName
                        ? read is HashNameValue hv && hv.Name == "probe_name"
                        : Equals(read, rep);
                    if (!ok) { allRoundTrip = false; rtDetail = $"{d.Id}: set {rep} got {read}"; }
                    d.Set(orig); // leave the specimen pristine
                }
                Check($"{name}: all Get() succeed", allGetOk, rtDetail);
                Check($"{name}: editable descriptors round-trip", allRoundTrip, rtDetail);

                // Coverage: every rw property in the chain is described (by id suffix) or intentionally hidden.
                var covered = descs.Select(d => Suffix(d.Id)).ToHashSet(StringComparer.Ordinal);
                List<string> uncovered = RwProps(obj.GetType())
                    .Select(p => p.Name)
                    .Where(n => !covered.Contains(n) && !Hidden.Contains(n))
                    .Distinct()
                    .ToList();
                Check($"{name}: descriptor coverage is complete", uncovered.Count == 0,
                    uncovered.Count == 0 ? "" : "uncovered: " + string.Join(", ", uncovered));
            }

            // Base identity/flags editability + shape.
            var baseMesh = new FrameObjectSingleMesh(fr) { IsOnFrameTable = true };
            IReadOnlyList<PropertyGroup> baseGroups = doc.Node(baseMesh).GetPropertyGroups();
            Check("Name is editable (on or off the name table)", FindDesc(baseGroups, "Base.Name") is { IsReadOnly: false });
            Check("On-name-table is editable", FindDesc(baseGroups, "Base.IsOnFrameTable") is { IsReadOnly: false });
            PropertyDescriptor? secFlags = FindDesc(baseGroups, "Base.SecondaryFlags");
            Check("Secondary flags is a 1..4096 flag list", secFlags is { Kind: PropertyKind.Flags, FlagItems.Count: 13 });

            // Serialization fixpoint on a real district (best-effort — needs game data).
            FixpointOnDistrict(district, Check, sb);

            // UI render (headless): the property list builds and lays out for a Light's full field set.
            RenderLightPanel(Check, sb);

            // Materials resolution + panel render on a real district (best-effort — needs game data + MTL).
            MaterialsOnDistrict(district, Check, sb);

            sb.Insert(0, $"PROPERTIES PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "PROPERTIES PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Applies an identity write-back (Set(Get())) through every editable non-HashName descriptor of every frame
    // object, then asserts the FrameResource re-serializes byte-identically — proving no descriptor coerces or
    // drops precision. HashName is skipped: re-deriving a hash from its name is only identity when the stored hash
    // already equals FNV64(name), which is not guaranteed for every archive.
    private static void FixpointOnDistrict(string district, Action<string, bool, string> check, StringBuilder sb)
    {
        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("fixpoint skipped — no game path (" + (err ?? "") + ")");
                return;
            }

            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            if (!sds.Exists)
            {
                sb.AppendLine($"fixpoint skipped — {sds.FullName} missing");
                return;
            }

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            if (fr?.FrameObjects is not { Count: > 0 })
            {
                sb.AppendLine("fixpoint skipped — no frame objects");
                return;
            }

            byte[] a = fr.WriteToStream();
            var doc = new SceneDocumentAdapter(fr, sds);
            int touched = 0;
            foreach (FrameObjectBase obj in fr.FrameObjects.Values.OfType<FrameObjectBase>())
            {
                FrameNodeAdapter node = doc.Node(obj);
                foreach (PropertyGroup g in node.GetPropertyGroups())
                    foreach (PropertyDescriptor d in g.Properties)
                        if (!d.IsReadOnly && d.Set != null && d.Kind != PropertyKind.HashName)
                        {
                            d.Set(d.Get());
                            touched++;
                        }
            }
            byte[] b = fr.WriteToStream();
            bool identical = a.SequenceEqual(b);
            check($"Identity write-back is a serialization fixpoint ({district}, {touched} descriptors)", identical,
                identical ? $"{a.Length:N0} bytes identical"
                : a.Length == b.Length ? $"first diff @ {FirstDiff(a, b)}" : $"len {a.Length} vs {b.Length}");
        }
        catch (Exception ex) { sb.AppendLine("fixpoint EXCEPTION: " + ex.Message); }
    }

    // Builds the property panel for a Light (the richest type — 60+ rows, every kind incl. the collapsed Unknown
    // group) and lays it out at the real panel width, then renders it to a PNG. A headless check that the XAML
    // templates load and bind. Output: %TEMP%\illusion_properties.png
    private static void RenderLightPanel(Action<string, bool, string> check, StringBuilder sb)
    {
        try
        {
            var fr = new FrameResource();
            var light = new FrameObjectLight(fr);
            var doc = new SceneDocumentAdapter(fr, new FileInfo(Path.Combine(Path.GetTempPath(), "illusion_properties_ui.sds")));
            FrameNodeAdapter node = doc.Node(light);

            List<PropertyGroupViewModel> groups = node.GetPropertyGroups()
                .Select(g => new PropertyGroupViewModel(g, null))
                .ToList();
            int rowCount = groups.Sum(g => g.Rows.Count);
            check("Light panel builds its full field set (incl. Unknown)", rowCount > 40, $"{rowCount} rows");

            // A HashName row exposes an editable name + a read-only hex hash.
            PropertyRowViewModel? proj = groups.SelectMany(g => g.Rows)
                .FirstOrDefault(r => r.Kind == PropertyKind.HashName);
            check("HashName row exposes a hex hash", proj != null && proj.HashHex.StartsWith("0x", StringComparison.Ordinal),
                proj?.HashHex ?? "none");

            const int width = 340;
            // Wrap in the app's dark panel background so the PNG is legible (dim labels / read-only fields would
            // otherwise blend into a transparent/black render).
            var host = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26)),
                Padding = new Thickness(10),
                Width = width,
                Child = new PropertyList { Groups = groups },
            };
            host.Measure(new Size(width, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
            host.UpdateLayout();
            double h = host.DesiredSize.Height;
            check("Light panel lays out to a finite height", h > 0 && !double.IsInfinity(h), $"{h:F0}px");

            var rtb = new RenderTargetBitmap(width, Math.Max(1, (int)Math.Ceiling(h)), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string png = Path.Combine(Path.GetTempPath(), "illusion_properties.png");
            using (FileStream fs = File.Create(png)) enc.Save(fs);
            sb.AppendLine($"panel rendered {width}x{(int)Math.Ceiling(h)}px -> {png}");
        }
        catch (Exception ex) { sb.AppendLine("UI render skipped — " + ex.Message); }
    }

    // Resolves a real mesh's materials through IMaterialListSource, asserts the join produced friendly-named
    // texture slots, and renders the Materials panel to a PNG. Needs game data + the MTL library. Output:
    // %TEMP%\illusion_materials.png
    private static void MaterialsOnDistrict(string district, Action<string, bool, string> check, StringBuilder sb)
    {
        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("materials skipped — no game path (" + (err ?? "") + ")");
                return;
            }

            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            if (!sds.Exists) { sb.AppendLine($"materials skipped — {sds.FullName} missing"); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            if (fr?.FrameObjects is not { Count: > 0 }) { sb.AppendLine("materials skipped — no frame objects"); return; }

            var doc = new SceneDocumentAdapter(fr, sds);
            IReadOnlyList<MaterialInfo>? found = null;
            foreach (FrameObjectSingleMesh mesh in fr.FrameObjects.Values.OfType<FrameObjectSingleMesh>())
            {
                IReadOnlyList<MaterialInfo> mats = ((IMaterialListSource)doc.Node(mesh)).GetMaterials();
                if (mats.Count > 0) { found = mats; break; }
            }
            if (found is null) { sb.AppendLine("materials skipped — no mesh with materials"); return; }

            check($"Mesh resolves its materials ({district})", found.Count > 0, $"{found.Count} materials");
            int resolved = found.Count(m => m.Resolved);
            check("At least one material resolves against the MTL library", resolved > 0, $"{resolved}/{found.Count} resolved");

            MaterialInfo? withSlots = found.FirstOrDefault(m => m.Resolved && m.TextureSlots.Count > 0);
            if (withSlots is not null)
                check("Texture slots carry friendly names (e.g. S000 → DiffuseTexture)",
                    withSlots.TextureSlots.Any(s => s.FriendlyName != s.SlotId),
                    string.Join(", ", withSlots.TextureSlots.Select(s => $"{s.SlotId}={s.FriendlyName}")));

            var vms = found.Select(m => new MaterialViewModel(m)).ToList();
            const int width = 340;
            var host = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26)),
                Padding = new Thickness(10),
                Width = width,
                Child = new MaterialsView { Materials = vms },
            };
            host.Measure(new Size(width, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
            host.UpdateLayout();
            double h = host.DesiredSize.Height;
            check("Materials panel lays out to a finite height", h > 0 && !double.IsInfinity(h), $"{h:F0}px");

            var rtb = new RenderTargetBitmap(width, Math.Max(1, (int)Math.Ceiling(h)), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string png = Path.Combine(Path.GetTempPath(), "illusion_materials.png");
            using (FileStream fs = File.Create(png)) enc.Save(fs);
            sb.AppendLine($"materials panel rendered {width}x{(int)Math.Ceiling(h)}px ({found.Count} materials) -> {png}");
        }
        catch (Exception ex) { sb.AppendLine("materials EXCEPTION: " + ex.Message); }
    }

    private static object? RepValue(PropertyDescriptor d) => d.Kind switch
    {
        PropertyKind.Int => Math.Clamp(3L, d.Min, d.Max),
        PropertyKind.UInt64Hex => (ulong)0xABCD,
        PropertyKind.Float => 1.5f,
        PropertyKind.Bool => true,
        PropertyKind.Vector3 => new Vector3(1f, 2f, 3f),
        PropertyKind.HashName => new HashNameValue(0, "probe_name"),
        PropertyKind.Flags => d.FlagItems is { Count: > 0 } ? d.FlagItems[0].Value : 0L,
        _ => null, // Text / Matrix / StructList are read-only — nothing to write
    };

    private static string Suffix(string id)
    {
        int dot = id.LastIndexOf('.');
        return dot < 0 ? id : id[(dot + 1)..];
    }

    private static PropertyDescriptor? FindDesc(IReadOnlyList<PropertyGroup> groups, string id) =>
        groups.SelectMany(g => g.Properties).FirstOrDefault(d => d.Id == id);

    // Public rw instance properties declared between the concrete type and FrameObjectBase (inclusive) — the
    // fields a Save re-serializes, and therefore the surface the catalog must account for.
    private static IEnumerable<PropertyInfo> RwProps(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
                        && p.DeclaringType != null && typeof(FrameObjectBase).IsAssignableFrom(p.DeclaringType));
}
