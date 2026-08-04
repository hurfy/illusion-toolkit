using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Illusion.Assets;
using Illusion.Assets.Frames;
using Illusion.Assets.Library;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Scene;
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

            // What an archive is MADE OF, straight off its manifest — the model the browser bands into
            // sections. A car is the useful sample: it is the one archive that carries almost every type.
            LibraryEntry? shubert = catalog.AllEntries.FirstOrDefault(e => e.Name == "shubert_38");
            if (shubert != null)
            {
                ArchiveContents contents = ArchiveContents.Read(shubert.File);
                var byKind = contents.Resources.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());
                sb.AppendLine("\n— shubert_38 announces " + contents.Resources.Count + " resources: " +
                              string.Join(", ", byKind.OrderByDescending(p => p.Value)
                                  .Select(p => $"{p.Value} {p.Key}")));

                Check("an archive's manifest reads as typed resources",
                    contents.Resources.Count > 20
                    && byKind.ContainsKey(SdsResourceKind.Mesh) && byKind.ContainsKey(SdsResourceKind.Texture)
                    && byKind.ContainsKey(SdsResourceKind.Shape),
                    $"{contents.Resources.Count} resources, {byKind.Count} kinds");
                // An unrecognised type is the one thing this table cannot be sure of, and the section it
                // lands in is where it would show up — so the count of them is worth an assertion.
                var strangers = contents.Resources.Where(r => r.Kind == SdsResourceKind.Unknown).ToList();
                Check("every type a car carries is one this build knows", strangers.Count == 0,
                    string.Join(", ", strangers.Select(r => r.Type).Distinct()));
                Check("every resource announced is a file on disk",
                    contents.Resources.All(r => r.Size > 0),
                    string.Join(", ", contents.Resources.Where(r => r.Size == 0).Select(r => r.Name).Take(4)));
                Check("a resource is filed under its kind's section",
                    contents.Resources.All(r =>
                        r.Section == SdsResourceKinds.SectionOf(r.Kind)));
            }

            // A second archive, for the entries whose manifest name is NOT what the extractor put on disk —
            // a car carries none of them. The XML handler appends a suffix it does not record, and a Script
            // entry names the package rather than a file and lists its pieces under elements of its own.
            LibraryEntry? gui = catalog.AllEntries
                .FirstOrDefault(e => e.Name == "gui" && e.FolderPath == "sds/gui");
            if (gui != null)
            {
                ArchiveContents guiContents = ArchiveContents.Read(gui.File);
                var xml = guiContents.Resources.Where(r => r.Kind == SdsResourceKind.Xml).ToList();
                Check("an XML resource resolves to the file the extractor wrote, suffix and all",
                    xml.Count > 0 && xml.TrueForAll(r => r.Size > 0),
                    $"{xml.Count(r => r.Size > 0)} of {xml.Count} found on disk");
                var scripts = guiContents.Resources.Where(r => r.Kind == SdsResourceKind.Script).ToList();
                Check("a script package is not called a missing file",
                    scripts.Count > 0 && scripts.TrueForAll(r => !r.NamesFile),
                    $"{scripts.Count} scripts");
                Check("everything else in a second archive is on disk too",
                    guiContents.Resources.Where(r => r.NamesFile).All(r => r.Size > 0),
                    string.Join(", ", guiContents.Resources
                        .Where(r => r.NamesFile && r.Size == 0).Select(r => r.Type + " " + r.Name).Take(4)));
            }

            // The browser catches whatever a read throws and says so instead of falling over — which is only
            // worth anything if a read of something that is not an archive does throw.
            bool threw = false;
            try
            {
                ArchiveContents.Read(new FileInfo(
                    Path.Combine(Path.GetTempPath(), "illusion_not_an_archive.sds")));
            }
            catch (Exception) { threw = true; }
            Check("reading something that is not an archive throws, which is what the browser catches", threw);

            // Every type the extractor knows how to unpack has to have a home here, or an archive would open
            // onto a band called Other with no way to tell what landed in it.
            string[] engineTypes =
            {
                "IndexBufferPool", "VertexBufferPool", "Texture", "FrameResource", "Effects", "FrameNameTable",
                "Actors", "EntityDataStorage", "Table", "NAV_OBJ_DATA", "NAV_AIWORLD_DATA", "PREFAB",
                "AnimalTrafficPaths", "Animation2", "NAV_HPD_DATA", "AudioSectors", "MemFile", "Collisions",
                "ItemDesc", "FxActor", "FxAnimSet", "Script", "Sound", "Speech", "Cutscene", "SoundTable",
                "XML", "Translokator", "Mipmap", "Animated Texture",
            };
            var unclassified = engineTypes.Where(t => SdsResourceKinds.Of(t) == SdsResourceKind.Unknown)
                .ToList();
            Check("every resource type the extractor knows is classified", unclassified.Count == 0,
                string.Join(", ", unclassified));

            CheckBrowserControl(catalog, Check, sb);

            sb.Insert(0, $"LIBRARY BROWSER PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    /// <summary>
    /// The browser control itself, driven headlessly inside a real resource-editor window: the panes fill from
    /// the catalog, walking into a folder moves both panes together, the library search queries the whole
    /// index rather than the open folder and folds the tree down to the branches a hit is inside, the folder
    /// filter narrows only what is already showing, sorting reorders it, and revealing an archive lands on its
    /// tile. Ends with a picture of each of the browser's two shapes — a folder open, and a search on — since
    /// none of the above says whether either is READABLE.
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

        // ...and it folds the tree down to the branches a hit is inside, which is what makes the hit list
        // placeable: 28 cars mean nothing until the tree says they are all in one folder. Asked again
        // because the miss above left every branch folded away, which is the same thing said about nothing.
        browser.SearchBox.Text = "shubert";
        Layout();
        TreeViewItem? carsRow = Branch(browser, cars);
        TreeViewItem? charsRow = Branch(browser, chars);
        check("the search folds the tree down to the branches a hit is inside",
            carsRow?.Visibility == Visibility.Visible && charsRow?.Visibility == Visibility.Collapsed,
            $"cars={carsRow?.Visibility}, characters={charsRow?.Visibility}");

        // Picking one of them is the end of the search — the filter is there to FIND the folder, and leaving
        // the query on would keep the pane showing hits from everywhere else.
        if (carsRow != null)
        {
            carsRow.IsSelected = true;
            Layout();
            check("picking a folder in the folded tree ends the search",
                !browser.IsSearching && browser.SearchBox.Text.Length == 0
                && ReferenceEquals(browser.FolderTree.SelectedItem, cars)
                && charsRow?.Visibility == Visibility.Visible,
                $"searching={browser.IsSearching}, tree={(browser.FolderTree.SelectedItem as LibraryFolder)?.Name}");
        }

        browser.SearchBox.Text = "";
        Layout();
        check("clearing the search puts the folder back", !browser.IsSearching && Rows(browser).Count > 0,
            $"{Rows(browser).Count} rows");

        // The OTHER search: the folder filter narrows what is already in the pane and touches neither the
        // library nor the tree. Needled off the data rather than a literal, so a modded install still runs it.
        if (cars != null && cars.Entries.Count > 1)
        {
            browser.OpenFolder(cars);
            Layout();
            int whole = Rows(browser).Count;
            string needle = cars.Entries[0].Name[..3];
            browser.FilterBox.Text = needle;
            Layout();
            var kept = Rows(browser).OfType<LibraryEntry>().ToList();
            check("the folder filter narrows the open folder, not the library",
                kept.Count is > 0 && kept.Count <= whole
                && kept.TrueForAll(e => e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                && ReferenceEquals(browser.FolderTree.SelectedItem, cars),
                $"{kept.Count} of {whole} on “{needle}”");

            browser.FilterBox.Text = "zzz_no_such_archive";
            Layout();
            check("a filter miss empties the pane without losing the folder",
                Rows(browser).Count == 0 && ReferenceEquals(browser.FolderTree.SelectedItem, cars),
                $"{Rows(browser).Count} rows");

            browser.FilterBox.Text = "";
            Layout();
            check("clearing the filter puts the whole folder back", Rows(browser).Count == whole,
                $"{Rows(browser).Count} of {whole}");

            // Sorting reorders those same rows. Size runs over the archives...
            browser.SortBy(BrowserSort.SizeDescending);
            Layout();
            var bySize = Rows(browser).OfType<LibraryEntry>().ToList();
            bool falling = true;
            for (int i = 1; i < bySize.Count; i++)
                if (bySize[i].Size > bySize[i - 1].Size) falling = false;
            check("sorting by size reorders the tiles", bySize.Count > 1 && falling,
                bySize.Count > 1 ? $"{bySize[0].Size / 1024} KB first, {bySize[^1].Size / 1024} KB last" : "too few");

            // A click on a tree row is the primary way to change folders, and it has to leave the filter
            // behind: one typed for the folder you left would otherwise empty the one you just picked.
            if (chars != null && Branch(browser, chars) is { } charsRow2)
            {
                browser.OpenFolder(cars);
                browser.FilterBox.Text = "zzz_no_such_archive";
                Layout();
                charsRow2.IsSelected = true;
                Layout();
                check("a tree click leaves the folder filter behind",
                    browser.FilterBox.Text.Length == 0 && Rows(browser).Count == chars.Folders.Count,
                    $"filter=“{browser.FilterBox.Text}”, {Rows(browser).Count} rows");
            }
        }

        // ...and over a folder it is how much is inside it, since a folder has no size of its own.
        if (chars != null)
        {
            browser.OpenFolder(chars);
            Layout();
            var byWeight = Rows(browser).OfType<LibraryFolder>().ToList();
            check("a folder tile is weighed by what is inside it",
                byWeight.Count == chars.Folders.Count && byWeight.Count > 1
                && byWeight[0].TotalEntries >= byWeight[^1].TotalEntries,
                byWeight.Count > 1 ? $"{byWeight[0].Name} {byWeight[0].TotalEntries} first" : "too few");
        }
        browser.SortBy(BrowserSort.NameAscending);
        Layout();

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

        // Opening a resource is two things at once: the host stages it, and the pane steps INTO it. The read
        // runs off the UI thread, so the probe has to pump its own dispatcher to see the answer arrive.
        if (car != null)
        {
            browser.Reveal(car);
            Layout();
            int at = browser.Contents.Items.IndexOf(car);
            if (at >= 0 && Row(browser, at) is { } carTile)
            {
                browser.Contents.SelectedItem = car;
                DoubleClick(browser.Contents, carTile);
                bool opened = PumpUntil(() => Rows(browser).Exists(o => o is SdsResource));
                Layout();

                var inside = Rows(browser).OfType<SdsResource>().ToList();
                check("opening an archive shows the resources it announces",
                    opened && inside.Count > 20
                    && inside.Exists(r => r.Kind == SdsResourceKind.Mesh)
                    && inside.Exists(r => r.Kind == SdsResourceKind.Texture),
                    $"{inside.Count} resources");

                // Banded, and the bands in their canonical order however the tiles inside them are sorted —
                // the sort is over the tiles, not over the sections.
                var bands = new List<SdsResourceSection>();
                foreach (SdsResource r in inside)
                    if (bands.Count == 0 || bands[^1] != r.Section) bands.Add(r.Section);
                bool rising = true;
                for (int i = 1; i < bands.Count; i++) if (bands[i] <= bands[i - 1]) rising = false;
                check("the resources come out banded, in section order", bands.Count > 2 && rising,
                    string.Join(" · ", bands));

                browser.SortBy(BrowserSort.SizeDescending);
                Layout();
                var resorted = Rows(browser).OfType<SdsResource>().ToList();
                var afterSort = new List<SdsResourceSection>();
                foreach (SdsResource r in resorted)
                    if (afterSort.Count == 0 || afterSort[^1] != r.Section) afterSort.Add(r.Section);
                check("sorting inside an archive reorders tiles, not sections",
                    afterSort.Count == bands.Count && !resorted.SequenceEqual(inside),
                    $"{afterSort.Count} bands, {string.Join(" · ", afterSort)}");
                browser.SortBy(BrowserSort.NameAscending);
                Layout();

                string needle = inside[0].Name[..3];
                browser.FilterBox.Text = needle;
                Layout();
                var narrowed = Rows(browser).OfType<SdsResource>().ToList();
                check("the filter narrows an archive's resources too",
                    narrowed.Count is > 0 && narrowed.Count <= inside.Count
                    && narrowed.TrueForAll(r => r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)),
                    $"{narrowed.Count} of {inside.Count} on “{needle}”");
                browser.FilterBox.Text = "";
                Layout();

                // A texture on the stage. The archive is loaded underneath by definition — stepping into one
                // is what stages it — so this is exactly the case a picture that only showed on an empty
                // stage could never reach, which is how it shipped broken the first time.
                SdsResource? tex = inside.Find(r => r.Kind == SdsResourceKind.Texture);
                int texRow = tex == null ? -1 : browser.Contents.Items.IndexOf(tex);
                if (tex != null && texRow >= 0 && Row(browser, texRow) is { } texTile)
                {
                    // Stood up by hand because the probe cannot stream one — that needs a GPU and a shown
                    // window. Without a scene underneath, the check would pass on the broken build too: the
                    // bug WAS that the picture only showed on an empty stage, and here the stage is empty.
                    var loaded = new SceneNode("cars", "Folder", true);
                    var loadedSds = new SceneNode("shubert_38", "Sds", true);
                    var loadedFr = new SceneNode("FrameResource", "FrameResource", true);
                    loadedFr.AddChild(new SceneNode("body", "Model", false));
                    loadedSds.AddChild(loadedFr);
                    loaded.AddChild(loadedSds);
                    window.Stage.Tree.Roots.Add(loaded);
                    window.Stage.Tree.RebuildStageRoots();

                    browser.Contents.SelectedItem = tex;
                    DoubleClick(browser.Contents, texTile);
                    Layout();
                    check("double-clicking a texture puts it on the stage",
                        window.TextureStage.Visibility == Visibility.Visible
                        && window.EmptyStage.Visibility == Visibility.Collapsed,
                        $"texture={window.TextureStage.Visibility}, empty={window.EmptyStage.Visibility}");
                    check("a texture on the stage says so in the hierarchy too",
                        window.Scene.EmptyScene.Visibility == Visibility.Visible
                        && window.Scene.EmptyTitle.Text.Contains("texture", StringComparison.OrdinalIgnoreCase),
                        $"{window.Scene.EmptyScene.Visibility}, “{window.Scene.EmptyTitle.Text}”");

                    // A resource with nothing to show is not a way out — the stage stays as it was.
                    SdsResource? shape = inside.Find(r => r.Kind == SdsResourceKind.Shape);
                    int shapeRow = shape == null ? -1 : browser.Contents.Items.IndexOf(shape);
                    if (shape != null && shapeRow >= 0 && Row(browser, shapeRow) is { } shapeTile)
                    {
                        browser.Contents.SelectedItem = shape;
                        DoubleClick(browser.Contents, shapeTile);
                        Layout();
                        check("a resource with nothing to show leaves the stage alone",
                            window.TextureStage.Visibility == Visibility.Visible,
                            window.TextureStage.Visibility.ToString());
                    }

                    // ...but the frame resource IS one: it stands for the scene, so opening it takes the
                    // picture back off the stage. Without it a texture is a door that only opens inward.
                    SdsResource? mesh = inside.Find(r => r.Kind == SdsResourceKind.Mesh);
                    int meshRow = mesh == null ? -1 : browser.Contents.Items.IndexOf(mesh);
                    if (mesh != null && meshRow >= 0 && Row(browser, meshRow) is { } meshTile)
                    {
                        browser.Contents.SelectedItem = mesh;
                        DoubleClick(browser.Contents, meshTile);
                        Layout();
                        check("opening the frame resource switches back from a texture to the scene",
                            window.TextureStage.Visibility == Visibility.Collapsed
                            && window.EmptyStage.Visibility == Visibility.Collapsed,
                            $"texture={window.TextureStage.Visibility}, empty={window.EmptyStage.Visibility}");
                        check("and the hierarchy goes back to listing the scene",
                            window.Scene.EmptyScene.Visibility == Visibility.Collapsed,
                            window.Scene.EmptyScene.Visibility.ToString());
                    }
                    window.Stage.Tree.Clear();
                }

                // ...and out again. The archive's own folder is where up lands, not wherever the tree was.
                check("up is live while an archive is open", browser.UpBtn.IsEnabled, "disabled");
                browser.UpBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Layout();
                check("up walks out of an archive back to the folder holding it",
                    Rows(browser).Count > 0 && Rows(browser).TrueForAll(o => o is LibraryEntry)
                    && ReferenceEquals(browser.FolderTree.SelectedItem, cars),
                    $"{Rows(browser).Count} rows, tree={(browser.FolderTree.SelectedItem as LibraryFolder)?.Name}");
                check("up is dead at a root", !browser.UpBtn.IsEnabled, "still enabled");
            }
        }

        // An archive opened out of a SEARCH HIT still has to show the archive. The pane answers the search
        // before it answers an open archive, so a query left standing would keep the hit list up forever.
        if (car != null)
        {
            browser.SearchBox.Text = "shubert_38";
            Layout();
            int hit = browser.Contents.Items.IndexOf(car);
            if (hit >= 0 && Row(browser, hit) is { } hitTile)
            {
                browser.Contents.SelectedItem = car;
                DoubleClick(browser.Contents, hitTile);
                bool shown = PumpUntil(() => Rows(browser).Exists(o => o is SdsResource));
                Layout();
                check("opening an archive out of a search hit still shows the archive",
                    shown && !browser.IsSearching && browser.SearchBox.Text.Length == 0,
                    $"searching={browser.IsSearching}, {Rows(browser).Count} rows");
                browser.UpBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Layout();
            }
            browser.SearchBox.Text = "";
            Layout();
        }

        // ...and up out of a folder lands on its parent.
        if (chars is { Folders.Count: > 0 })
        {
            browser.OpenFolder(chars.Folders[0]);
            Layout();
            check("up is live inside a sub-folder", browser.UpBtn.IsEnabled, "disabled");
            browser.UpBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Layout();
            check("up out of a folder lands on its parent",
                ReferenceEquals(browser.FolderTree.SelectedItem, chars),
                (browser.FolderTree.SelectedItem as LibraryFolder)?.Name ?? "nothing");
        }

        // The same folder object hangs under two roots — hchar is a row under Characters AND a row under
        // All archives. Up has to leave by the branch you came in, which only the selected ROW knows; asking
        // the catalog who the parent is answers for whichever root it happens to reach first.
        LibraryFolder? all = catalog.Roots.FirstOrDefault(r => r.Name == "All archives");
        LibraryFolder? shared = chars?.Folders.Count > 0 ? chars.Folders[0] : null;
        if (all != null && shared != null && Branch(browser, all) is { } allRow)
        {
            allRow.IsExpanded = true;
            Layout();
            if (allRow.ItemContainerGenerator.ContainerFromItem(shared) is TreeViewItem sharedRow)
            {
                sharedRow.IsSelected = true;
                Layout();
                browser.UpBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Layout();
                check("up leaves a shared folder by the branch you came in",
                    ReferenceEquals(browser.FolderTree.SelectedItem, all),
                    (browser.FolderTree.SelectedItem as LibraryFolder)?.Name ?? "nothing");
            }
        }

        // The hierarchy the resource editor shows is the scene without its folder / SDS / FrameResource spine.
        // Built here by hand rather than by staging an archive: streaming needs a GPU and a shown window, and
        // the flattening itself is pure tree work that can be asked directly.
        var folder = new SceneNode("cars", "Folder", true);
        var sdsNode = new SceneNode("shubert_38", "Sds", true);
        var frameRes = new SceneNode("FrameResource", "FrameResource", true);
        frameRes.AddChild(new SceneNode("body", "Model", false));
        frameRes.AddChild(new SceneNode("wheels", "Model", false));
        sdsNode.AddChild(frameRes);
        sdsNode.AddChild(new SceneNode("Collisions", "Collision", true));
        folder.AddChild(sdsNode);
        window.Stage.Tree.Roots.Add(folder);
        window.Stage.Tree.RebuildStageRoots();

        var flat = window.Stage.Tree.StageRoots.ToList();
        check("the hierarchy drops the folder, the archive and the frame resource",
            flat.Count == 3 && flat[0].Name == "body" && flat[1].Name == "wheels"
            && flat[2].Name == "Collisions",
            string.Join(" · ", flat.Select(n => n.Name)));
        check("the real tree keeps its spine — unload and the scene filters read it",
            window.Stage.Tree.Roots.Count == 1 && window.Stage.Tree.Roots[0].Children.Count == 1,
            $"{window.Stage.Tree.Roots.Count} roots");
        window.Stage.Tree.Clear();
        check("clearing the scene clears the flattened view with it",
            window.Stage.Tree.StageRoots.Count == 0, $"{window.Stage.Tree.StageRoots.Count} left");

        // Folding gives the height back — how much is --probe-layout's business. What matters here is that
        // there is still something to click: the tab hangs OUTSIDE the pane on a negative margin, so a folded
        // browser is a hairline with the tab standing above it. Anything that starts clipping the control
        // (a ClipToBounds, a Border with a corner radius around the body) would leave no way back in.
        browser.IsCollapsed = true;
        Layout();
        Point tab = browser.CollapseBtn.TransformToAncestor(browser).Transform(new Point(0, 0));
        check("folded, the tab is still drawn and hangs outside the pane",
            browser.CollapseBtn.ActualHeight > 8 && tab.Y < 0 && browser.ActualHeight < 8,
            $"tab {browser.CollapseBtn.ActualHeight:F0}px at y={tab.Y:F0}, pane {browser.ActualHeight:F0}px");
        browser.IsCollapsed = false;
        Layout();

        // The editing affordances. Everything they DO is checked by --probe-content-edit against a scratch
        // copy; what matters here is that they only offer themselves where they mean something — an import
        // needs an archive to import INTO, and outside one the button is dead rather than gone (a control
        // that comes and goes moves the three beside it, and this row is full at the window's floor size).
        check("the tiles take more than one selection", browser.Contents.SelectionMode == SelectionMode.Extended,
            browser.Contents.SelectionMode.ToString());
        check("importing is offered only inside an archive", !browser.CanImport, "listing a folder");
        check("the tiles accept a drop", browser.Contents.AllowDrop, "");

        // Three pictures, because the pane takes three shapes worth eyeballing: a folder open, the inside of
        // an archive banded by section, and a search — where the tree folds away and the tiles come from
        // everywhere at once.
        if (chars is { Folders.Count: > 0 }) browser.OpenFolder(chars.Folders[0]);
        Layout();
        Shoot("illusion_library_browser.png");

        if (car != null)
        {
            browser.Reveal(car);
            Layout();
            int shot = browser.Contents.Items.IndexOf(car);
            if (shot >= 0 && Row(browser, shot) is { } shotTile)
            {
                browser.Contents.SelectedItem = car;
                DoubleClick(browser.Contents, shotTile);
                PumpUntil(() => Rows(browser).Exists(o => o is SdsResource));
                Layout();
                Shoot("illusion_library_archive.png");

                check("...and inside one, importing is offered", browser.CanImport, "");
                check("a drag of textures reads as importable",
                    browser.DropAnswer(["a.dds", "b.dds"]) == "2/2",
                    browser.DropAnswer(["a.dds", "b.dds"]) ?? "(refused)");
                check("a drag of something else is refused outright",
                    browser.DropAnswer(["a.exe"]) == null, "");
                check("a mixed drag counts only what can come in",
                    browser.DropAnswer(["a.dds", "b.exe", "c.fsb"]) == "2/3",
                    browser.DropAnswer(["a.dds", "b.exe", "c.fsb"]) ?? "(refused)");
            }
        }

        browser.SearchBox.Text = "shubert";
        Layout();
        Shoot("illusion_library_search.png");
        browser.SearchBox.Text = "";
        Layout();

        void Shoot(string name)
        {
            try
            {
                if (content is Panel root) root.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
                var rtb = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(content);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                string png = Path.Combine(Path.GetTempPath(), name);
                using (FileStream fs = File.Create(png)) enc.Save(fs);
                sb.AppendLine($"\nrendered {w}x{h}px -> {png}");
            }
            catch (Exception ex) { sb.AppendLine("\nrender skipped — " + ex.Message); }
        }
    }

    // Runs the dispatcher until the condition holds. The browser opens an archive on a background thread and
    // posts the answer back, and a probe has no message loop of its own to deliver it on — without this the
    // check would read the pane before it had been filled and call a working feature broken.
    private static bool PumpUntil(Func<bool> done, int timeoutMs = 30000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        return done();
    }

    // A root branch of the folder tree, by the folder it stands for. Only the roots are reachable this way,
    // which is all the search checks need — they are the branches a fold is visible on.
    private static TreeViewItem? Branch(ContentBrowser browser, LibraryFolder? folder) =>
        folder == null
            ? null
            : browser.FolderTree.ItemContainerGenerator.ContainerFromItem(folder) as TreeViewItem;

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

            RenderStage(meshes, folders, min, max, sb, roots);

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
        Vector3 min, Vector3 max, StringBuilder sb, IReadOnlyList<SdsFrameNode> roots)
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

            // A second frame with the rig on. Bones are the only place a car's doors and axles exist, and a
            // line list that lands in the wrong space still looks like a plausible tangle in the numbers —
            // only a picture over the body says whether it sits where the parts are.
            // Built by the streamer itself, not by a copy of it here — a second implementation would be free
            // to drift, and this picture is the only thing that says the first one is right.
            HelperGlyphRenderData rig = HelperGlyphBuilder.BuildRig(Viewport.DistrictStreamer.CollectSkeletons(roots));
            if (!rig.IsEmpty)
            {
                renderer.ShowSkeleton = true;
                renderer.ShowHelpers = true;
                renderer.SetSkeletonDistrict("probe", rig);
                HelperGlyphRenderData helpers = HelperGlyphBuilder.BuildFrames(roots);
                renderer.SetHelperDistrict("probe", helpers);
                renderer.Render(target);
                string rigPng = Path.Combine(Path.GetTempPath(), "illusion_library_stage_rig.png");
                GpuProbes.SavePng(Rendering.Gpu.RenderTargetReadback.Read(gpu, target), w, h, rigPng);
                sb.AppendLine($"rig: {rig.GlyphCount} bones in {rig.SegmentCount} segments; " +
                              $"helpers: {helpers.GlyphCount} glyphs in {helpers.SegmentCount} segments " +
                              $"({helpers.HiddenCount} placeholders left out) -> {rigPng}");
            }
        }
        catch (Exception ex) { sb.AppendLine("render skipped — " + ex.Message); }
        finally
        {
            target?.Dispose();
            renderer?.Dispose();
            gpu?.Dispose();
        }
    }

    // Bone→parent segments plus a tick at every joint, in world space — the same list the viewport uploads.
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
