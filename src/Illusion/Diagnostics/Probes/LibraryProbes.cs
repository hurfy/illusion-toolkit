using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Assets;
using Illusion.Assets.Library;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Views;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The resource library: the catalog the content browser indexes the game with, and putting one archive
/// on the stage. Both touch the extracted mirror, so run them one at a time.
/// Output: %TEMP%\illusion_library_browser.txt, %TEMP%\illusion_library_stage.txt
/// </summary>
internal static class LibraryProbes
{
    /// <summary>
    /// The catalog: the folder walk finds the game's archives, the curated categories reach the folders they
    /// name, and the toolkit's own working folders stay out of the content.
    /// </summary>
    internal static void RunBrowserProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_library_browser.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            string sdsFolder = Path.Combine(MafiaEnvironment.PcFolder, "sds");
            LibraryCatalog catalog = LibraryCatalog.Build(sdsFolder);

            sb.AppendLine($"sds folder: {sdsFolder}");
            sb.AppendLine($"roots: {catalog.Roots.Count}, archives indexed: {catalog.AllEntries.Count}\n");
            foreach (LibraryFolder root in catalog.Roots)
            {
                sb.AppendLine($"— {root.Name} ({root.Kind}, {root.TotalEntries} archives, " +
                              $"{root.Folders.Count} folders, {root.Entries.Count} direct)");
                foreach (LibraryFolder sub in root.Folders)
                    sb.AppendLine($"    {sub.Name,-16} {sub.TotalEntries,5} archives   {sub.Path}");
            }
            sb.AppendLine();

            Check("the walk finds the game's archives", catalog.AllEntries.Count > 1000,
                $"{catalog.AllEntries.Count} archives");
            var stale = catalog.AllEntries.Where(e => !e.File.Exists).ToList();
            Check("every card points at a file that exists", stale.Count == 0,
                string.Join(", ", stale.Take(4).Select(e => e.File.FullName)));
            Check("no card is indexed twice",
                catalog.AllEntries.Select(e => e.File.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    == catalog.AllEntries.Count);

            // The working folders sit right beside the archives they shadow and hold .sds copies of their own:
            // listing them would offer the same car twice, one of the two an old backup.
            string[] work = { "extracted", "backups", "BackupSDS" };
            var leaked = catalog.AllEntries
                .Where(e => e.FolderPath.Split('/').Any(p => work.Contains(p, StringComparer.OrdinalIgnoreCase)))
                .ToList();
            Check("our extracted mirror and backups stay out of the content", leaked.Count == 0,
                leaked.Count == 0 ? "" : string.Join(", ", leaked.Take(4).Select(e => e.FolderPath + "/" + e.Name)));

            // A folder row that opens onto nothing is worse than no row: the sds tree also carries pure
            // sound / text / video folders with no archive in them at all.
            Check("no folder in the tree is empty", AllFolders(catalog).All(f => f.TotalEntries > 0),
                string.Join(", ", AllFolders(catalog).Where(f => f.TotalEntries == 0).Take(4).Select(f => f.Path)));

            // The counts are the census the plan was written against — a stripped install would silently give
            // the browser a shorter list, and that is worth failing over rather than showing half the game.
            LibraryFolder? cars = Root(catalog, "Cars");
            LibraryFolder? chars = Root(catalog, "Characters");
            LibraryFolder? city = Root(catalog, "City objects");
            LibraryFolder? all = Root(catalog, "All archives");
            Check("Cars is the cars folder, flattened", cars is { Kind: LibraryFolderKind.Category } && cars.Entries.Count == cars.TotalEntries,
                cars == null ? "missing" : $"{cars.Entries.Count} direct of {cars.TotalEntries}");
            Check("Cars holds the drivable vehicles", cars is { TotalEntries: >= 100 },
                cars == null ? "missing" : $"{cars.TotalEntries} archives");
            Check("Characters keeps its source folders apart",
                chars is { Kind: LibraryFolderKind.Category, Entries.Count: 0 } && chars.Folders.Count == 5,
                chars == null ? "missing" : $"{chars.Folders.Count} folders, {chars.Entries.Count} direct");
            Check("Characters covers the street population too",
                chars != null && chars.Folders.Any(f => f.Name.Equals("traffic", StringComparison.OrdinalIgnoreCase)),
                chars == null ? "missing" : string.Join(", ", chars.Folders.Select(f => f.Name)));
            Check("City objects reaches city_crash", city is { TotalEntries: > 0 },
                city == null ? "missing" : $"{city.TotalEntries} archives");
            Check("All archives is the whole tree", all != null && all.TotalEntries == catalog.AllEntries.Count,
                all == null ? "missing" : $"{all.TotalEntries} of {catalog.AllEntries.Count}");

            // The categories are entry points into the same tree, not a second copy of it.
            if (cars != null && all != null)
            {
                LibraryFolder? carsDir = all.Folders.FirstOrDefault(f => f.Name.Equals("cars", StringComparison.OrdinalIgnoreCase));
                Check("a category shows the very same cards as its folder",
                    carsDir != null && cars.Entries.Count == carsDir.Entries.Count
                    && cars.Entries.All(e => carsDir.Entries.Contains(e)),
                    carsDir == null ? "cars folder missing under All archives" : "");
            }

            // The map → library jump resolves a file on disk back to its card.
            LibraryEntry? sample = catalog.AllEntries.FirstOrDefault(e => e.FolderPath == "sds/cars");
            Check("a file on disk resolves back to its card",
                sample != null && ReferenceEquals(catalog.Find(sample.File), sample));
            Check("a file outside the library resolves to nothing",
                catalog.Find(new FileInfo(Path.Combine(Path.GetTempPath(), "not_a_game_archive.sds"))) == null);

            // Built before the game path is picked: the browser is constructed with the window, which can open
            // on an install the launcher has not validated yet.
            LibraryCatalog empty = LibraryCatalog.Build(Path.Combine(Path.GetTempPath(), "illusion_no_such_sds"));
            Check("a missing sds folder yields an empty catalog, not a throw",
                empty.Roots.Count == 0 && empty.AllEntries.Count == 0);

            CheckBrowserControl(catalog, Check, sb);

            sb.Insert(0, $"LIBRARY BROWSER PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    /// <summary>
    /// The browser control itself, driven headlessly inside a real resource-editor window: the panes fill from
    /// the catalog, walking into a folder moves both panes together, the search box queries the whole index
    /// rather than the open folder, and revealing an archive lands on its card. Ends with a picture of the
    /// whole window for eyeballing, since none of the above says whether it is READABLE.
    /// </summary>
    private static void CheckBrowserControl(LibraryCatalog catalog, Action<string, bool, string> check,
        StringBuilder sb)
    {
        var window = new ResourceEditorWindow();
        var content = (FrameworkElement)window.Content;
        window.Browser.IsCollapsed = false;
        window.Browser.SetCatalog(catalog);

        const double w = 1280, h = 641;
        void Layout()
        {
            for (int i = 0; i < 2; i++)
            {
                content.Measure(new Size(w, h));
                content.Arrange(new Rect(0, 0, w, h));
                content.UpdateLayout();
            }
        }
        Layout();

        ContentBrowser browser = window.Browser;
        check("the browser opens on a folder rather than on nothing",
            browser.FolderTree.Items.Count == catalog.Roots.Count && Rows(browser).Count > 0,
            $"{browser.FolderTree.Items.Count} roots, {Rows(browser).Count} rows");

        // Cars is flat: every row in the pane is an archive card.
        LibraryFolder? cars = catalog.Roots.FirstOrDefault(r => r.Name == "Cars");
        check("a flat category lists its archives",
            cars != null && Rows(browser).Count == cars.Entries.Count && Rows(browser).All(o => o is LibraryEntry),
            cars == null ? "no Cars" : $"{Rows(browser).Count} rows of {cars.Entries.Count} archives");

        // Characters gathers folders: the pane lists those, so the category is not a dead end.
        LibraryFolder? chars = catalog.Roots.FirstOrDefault(r => r.Name == "Characters");
        if (chars != null)
        {
            browser.OpenFolder(chars);
            Layout();
            check("a category that only gathers folders still opens onto them",
                Rows(browser).Count == chars.Folders.Count && Rows(browser).All(o => o is LibraryFolder),
                $"{Rows(browser).Count} rows, {chars.Folders.Count} folders");

            browser.OpenFolder(chars.Folders[0]);
            Layout();
            check("walking into a sub-folder shows its archives",
                Rows(browser).Count == chars.Folders[0].Entries.Count,
                $"{Rows(browser).Count} rows in {chars.Folders[0].Name}");
        }

        // The search is over the whole game, not the open folder — the folder above holds no cars at all.
        browser.SearchBox.Text = "shubert";
        Layout();
        var hits = Rows(browser).OfType<LibraryEntry>().ToList();
        check("search queries the whole index, not the open folder",
            browser.IsSearching && hits.Count > 5 && hits.All(e => e.Name.Contains("shubert", StringComparison.OrdinalIgnoreCase)),
            $"{hits.Count} hits");
        int misses = SearchCount(browser, "zzz_no_such_archive");
        check("a search miss shows nothing rather than the folder again", misses == 0, $"{misses} rows");

        browser.SearchBox.Text = "";
        Layout();
        check("clearing the search puts the folder back", !browser.IsSearching && Rows(browser).Count > 0,
            $"{Rows(browser).Count} rows");

        // The map → library jump: an archive on disk resolves to its card and the browser lands on it.
        LibraryEntry? car = catalog.AllEntries.FirstOrDefault(e => e.Name == "shubert_38");
        if (car != null)
        {
            bool revealed = browser.Reveal(car);
            Layout();
            check("revealing an archive selects its card", revealed && ReferenceEquals(browser.SelectedEntry, car),
                revealed ? browser.SelectedEntry?.Name ?? "nothing selected" : "not found");
        }

        // The double click is the whole interaction: a card asks the host to stage it, a folder row walks in.
        // Raised on the list itself with the row as the source, which is what a real click delivers.
        LibraryEntry? asked = null;
        browser.EntryActivated += x => asked = x;
        if (Row(browser, 0) is { } firstCard && browser.Contents.Items[0] is LibraryEntry expected)
        {
            browser.Contents.SelectedItem = expected;
            DoubleClick(browser.Contents, firstCard);
            check("double-clicking a card asks the host to stage it", ReferenceEquals(asked, expected),
                asked?.Name ?? "nothing was asked for");
        }
        else
        {
            check("double-clicking a card asks the host to stage it", false, "no card row was realized");
        }

        // A folder row is not a stage request — it is a step into the folder, in both panes.
        if (chars != null)
        {
            browser.OpenFolder(chars);
            Layout();
            asked = null;
            if (Row(browser, 0) is { } folderRow && browser.Contents.Items[0] is LibraryFolder sub)
            {
                browser.Contents.SelectedItem = sub;
                DoubleClick(browser.Contents, folderRow);
                Layout();
                check("double-clicking a folder walks into it instead of staging anything",
                    asked == null && ReferenceEquals(browser.FolderTree.SelectedItem, sub),
                    $"asked={asked?.Name ?? "nothing"}, tree={(browser.FolderTree.SelectedItem as LibraryFolder)?.Name}");
            }
        }

        // Folding gives the height back — asserted numerically by --probe-layout; here it only has to not throw.
        browser.IsCollapsed = true;
        Layout();
        browser.IsCollapsed = false;
        Layout();

        try
        {
            if (content is Panel root) root.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(content);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            string png = Path.Combine(Path.GetTempPath(), "illusion_library_browser.png");
            using (FileStream fs = File.Create(png)) enc.Save(fs);
            sb.AppendLine($"\nrendered {w}x{h}px -> {png}");
        }
        catch (Exception ex) { sb.AppendLine("\nrender skipped — " + ex.Message); }
    }

    private static ListBoxItem? Row(ContentBrowser browser, int index) =>
        browser.Contents.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;

    private static void DoubleClick(IInputElement target, object source) =>
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = source,
        });

    private static List<object> Rows(ContentBrowser browser) =>
        (browser.Contents.ItemsSource as IEnumerable<object>)?.ToList() ?? new List<object>();

    private static int SearchCount(ContentBrowser browser, string query)
    {
        browser.SearchBox.Text = query;
        return Rows(browser).Count;
    }

    /// <summary>
    /// One archive on the stage: the loader that only ever saw city districts opens a stand-alone .sds outside
    /// <c>sds\city</c> and produces meshes the viewport could draw. This is the S1 spike — cars and characters
    /// are skinned (<c>FrameObjectModel</c>), and no skinned vertex buffer had gone through the loader before.
    /// </summary>
    /// <param name="relative">Archive path under <c>pc\sds</c>, e.g. <c>cars/shubert_38.sds</c>.</param>
    internal static void RunStageProbe(string relative)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_library_stage.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            var file = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds",
                relative.Replace('/', Path.DirectorySeparatorChar)));
            sb.AppendLine($"archive: {file.FullName}");
            if (!file.Exists) { sb.AppendLine("RESULT: SKIPPED — no such archive"); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(file);
            sb.AppendLine($"extracted: {extracted}\n");

            // districtNames is null here, as it is for any archive outside sds\city — proxy classification
            // degrades to the literal 'city' prefix check and nothing else, which is the point of passing it.
            (List<SdsFrameNode> roots, List<MeshData> meshes, ISceneDocument? document) =
                SdsMeshLoader.LoadHierarchy(file);

            Check("a stand-alone archive loads outside sds\\city", roots.Count > 0,
                $"{roots.Count} roots");
            Check("it is a save unit like any district", document != null);

            // The frame tree, so what is actually inside an archive can be read off the report — this is also
            // how a folder's content is identified without opening the game.
            int nodes = 0, meshNodes = 0;
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SdsFrameNode r in roots) Walk(r, 0, sb, kinds, ref nodes, ref meshNodes);
            sb.AppendLine($"\nnodes: {nodes} ({meshNodes} with geometry); kinds: " +
                          string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}")));

            long verts = 0, tris = 0;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            int nanMeshes = 0, untextured = 0, parts = 0;
            var bare = new List<string>();
            foreach (MeshData m in meshes)
            {
                verts += m.VertexCount;
                tris += m.TriangleCount;
                bool bad = !IsFinite(m.World);
                foreach (Vector3 p in m.Positions)
                {
                    Vector3 w = Vector3.Transform(p, m.World);
                    if (!IsFinite(w)) { bad = true; break; }
                    min = Vector3.Min(min, w);
                    max = Vector3.Max(max, w);
                }
                if (bad) nanMeshes++;
                foreach (MeshPart part in m.Parts)
                {
                    parts++;
                    if (!string.IsNullOrEmpty(part.DiffuseTexture)) continue;
                    untextured++;
                    bare.Add($"{m.Name}#{parts} hash={part.MaterialHash:X16} tris={part.IndexCount / 3}");
                }
            }

            // Plenty of archives carry no geometry at all (tables, textures, a bare holder like
            // city\carcyclopedia). The browser still has to open them, so this is a reported outcome, not a
            // failure — only the assertions that need geometry are skipped.
            if (meshes.Count == 0)
            {
                sb.AppendLine("\ngeometry: none — nothing for the stage to draw");
                sb.Insert(0, $"LIBRARY STAGE PROBE ({relative}): {pass} passed, {fail} failed\n\n");
                return;
            }

            Vector3 size = max - min;
            sb.AppendLine($"geometry: {verts} vertices, {tris} triangles, {parts} material parts");
            // Every part by name: what an archive is actually made of, material by material. The one place
            // that answers "what is that patch on the roof".
            foreach (MeshData m in meshes)
            {
                foreach (MeshPart part in m.Parts)
                {
                    string material = Assets.MafiaMaterials.GetMaterialName(part.MaterialHash) ?? "(unknown)";
                    string tint = part.Tint == System.Numerics.Vector4.One
                        ? "" : $" paint rgb({part.Tint.X:0.##} {part.Tint.Y:0.##} {part.Tint.Z:0.##})";
                    sb.AppendLine($"    {m.Name,-16} {material,-24} {part.IndexCount / 3,6} tris  " +
                                  $"{part.DiffuseTexture ?? "-"}{tint}");
                }
            }
            sb.AppendLine($"bounds: ({min.X:F2},{min.Y:F2},{min.Z:F2})..({max.X:F2},{max.Y:F2},{max.Z:F2})");
            sb.AppendLine($"size: {size.X:F2} x {size.Y:F2} x {size.Z:F2} (diagonal {size.Length():F2})");
            if (bare.Count > 0)
            {
                sb.AppendLine("parts with no diffuse map: " + string.Join("; ", bare));
                // A material with no albedo is not always a fault: a car's paint is a shader that takes its
                // colour from the game, not from a texture. Naming the material and its actual slots is what
                // tells the two cases apart.
                foreach (ulong hash in meshes.SelectMany(m => m.Parts).Select(p => p.MaterialHash)
                             .Where(h => h != 0).Distinct())
                {
                    if (meshes.SelectMany(m => m.Parts).Any(p => p.MaterialHash == hash
                        && !string.IsNullOrEmpty(p.DiffuseTexture))) continue;
                    Assets.Materials.MafiaMaterialCatalog catalog = Assets.Materials.MafiaMaterialCatalog.Instance;
                    Domain.Materials.MaterialInfo? info = catalog.GetMaterial(hash);
                    sb.AppendLine($"  material {hash:X16} = {info?.Name ?? "(not in any library)"} " +
                                  $"in {catalog.LibraryOf(hash) ?? "-"}; slots: " +
                                  (info == null ? "-" : string.Join(", ",
                                      info.TextureSlots.Select(t => $"{t.SlotId}({t.FriendlyName})={t.TextureName ?? "-"}"))));
                    if (info != null)
                        sb.AppendLine($"      shader {info.ShaderId:X}/{info.ShaderHash:X}, flags [{string.Join(" ", info.Flags)}], params: " +
                                      string.Join(", ", info.Parameters.Select(p =>
                                          $"{p.ParamId}({p.FriendlyName})=[{string.Join(" ", p.Values.Select(v => v.ToString("0.###")))}]")));
                }
            }

            // A texture the library cannot find is silently drawn white, so the names the parts ask for are
            // reported next to whether the archive's own folder actually holds them.
            var wanted = meshes.SelectMany(m => m.Parts)
                .SelectMany(p => new[] { p.DiffuseTexture, p.NormalTexture, p.SpecularTexture })
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Some archives are not meant to satisfy their own materials — a car takes its generic textures
            // from the shared car library — so the companions count as "here" too.
            var folders = new List<string> { extracted };
            foreach (FileInfo companion in StageCompanions.For(file))
                folders.Add(SdsMeshLoader.EnsureExtracted(companion));
            var ownMissing = wanted.Where(t => !File.Exists(Path.Combine(extracted, t!))).ToList();
            var missing = wanted.Where(t => !folders.Any(f => File.Exists(Path.Combine(f, t!)))).ToList();
            sb.AppendLine($"textures: {wanted.Count} referenced, {ownMissing.Count} not in the archive's own " +
                          $"folder, {missing.Count} not found even with its companions " +
                          $"({folders.Count - 1} companion(s))");
            if (missing.Count > 0) sb.AppendLine("  nowhere: " + string.Join(", ", missing.Take(12)));

            Check("no mesh decodes to NaN", nanMeshes == 0, $"{nanMeshes} of {meshes.Count} meshes");
            // Not every part is meant to carry one: a material with no S000 slot (a shadow catcher, a decal
            // driven from a second map) is legitimate, and the city has them too. What must not happen is a
            // mesh where NOTHING resolves — that is a materials library that failed to load.
            Check("the archive's materials resolve", untextured < parts,
                $"{untextured} of {parts} parts have no diffuse map");
            Check("the archive occupies a finite box", IsFinite(min) && IsFinite(max) && max.X > min.X);
            // A car or a character is authored around its own origin. A part left behind at (0,0,0) while the
            // body sits elsewhere is what a missing bone transform would look like, and the box is where that
            // shows: the origin would sit on a face of the box instead of inside it.
            Check("the archive is authored around its own origin",
                min.X <= 0 && max.X >= 0 && min.Y <= 0 && max.Y >= 0,
                $"x {min.X:F2}..{max.X:F2}, y {min.Y:F2}..{max.Y:F2}");

            RenderStage(meshes, folders, min, max, sb);

            sb.Insert(0, $"LIBRARY STAGE PROBE ({relative}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    /// <summary>
    /// Draws the staged archive once, windowless, from an angled three-quarter view and saves the frame. The
    /// numbers above prove the geometry decoded; only a picture answers whether it decoded into the right
    /// SHAPE — which is the whole question about skinned models, since the loader applies one world matrix and
    /// never a bone transform. Best-effort: a machine with no usable device leaves the assertions above green.
    /// </summary>
    private static void RenderStage(List<MeshData> meshes, IReadOnlyList<string> textureFolders,
        Vector3 min, Vector3 max, StringBuilder sb)
    {
        string png = Path.Combine(Path.GetTempPath(), "illusion_library_stage.png");
        Rendering.Gpu.GpuContext? gpu = null;
        Rendering.Passes.SceneRenderer? renderer = null;
        Rendering.Gpu.SharedRenderTarget? target = null;
        try
        {
            gpu = new Rendering.Gpu.GpuContext();
            renderer = new Rendering.Passes.SceneRenderer(gpu)
            {
                Mode = Rendering.Passes.RenderMode.MaterialPreview,
                ShowSky = false,
            };
            foreach (string folder in textureFolders) renderer.Textures.AddFolder(folder);
            foreach (MeshData md in meshes) renderer.AddMesh(md);

            Vector3 center = (min + max) * 0.5f;
            float radius = MathF.Max((max - min).Length() * 0.5f, 0.5f);
            renderer.Camera.Far = radius * 40f + 100f;
            renderer.Camera.LookAt(center + new Vector3(radius * 1.6f, -radius * 1.9f, radius * 1.1f), center);

            const int w = 900, h = 600;
            target = new Rendering.Gpu.SharedRenderTarget(gpu, w, h);
            renderer.Render(target);
            GpuProbes.SavePng(Rendering.Gpu.RenderTargetReadback.Read(gpu, target), w, h, png);
            sb.AppendLine($"rendered {w}x{h}px -> {png}");
        }
        catch (Exception ex) { sb.AppendLine("render skipped — " + ex.Message); }
        finally
        {
            target?.Dispose();
            renderer?.Dispose();
            gpu?.Dispose();
        }
    }

    private static void Walk(SdsFrameNode n, int depth, StringBuilder sb, Dictionary<string, int> kinds,
        ref int nodes, ref int meshNodes)
    {
        nodes++;
        if (n.Mesh != null) meshNodes++;
        kinds[n.Kind] = kinds.GetValueOrDefault(n.Kind) + 1;
        if (depth <= 2)
        {
            sb.AppendLine($"{new string(' ', depth * 2)}{n.Kind,-14} {n.Name}" +
                          (n.Mesh != null ? $"   [{n.Mesh.VertexCount} v, {n.Mesh.TriangleCount} t]" : ""));
        }
        foreach (SdsFrameNode c in n.Children) Walk(c, depth + 1, sb, kinds, ref nodes, ref meshNodes);
    }

    private static IEnumerable<LibraryFolder> AllFolders(LibraryCatalog catalog)
    {
        var stack = new Stack<LibraryFolder>(catalog.Roots);
        while (stack.Count > 0)
        {
            LibraryFolder f = stack.Pop();
            yield return f;
            foreach (LibraryFolder c in f.Folders) stack.Push(c);
        }
    }

    private static LibraryFolder? Root(LibraryCatalog catalog, string name) =>
        catalog.Roots.FirstOrDefault(r => r.Name == name);

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFinite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M22) && float.IsFinite(m.M33)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43);
}
