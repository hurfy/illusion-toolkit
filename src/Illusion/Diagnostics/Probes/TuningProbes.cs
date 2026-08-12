using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Assets.EntityData;
using Illusion.Formats.Actors;
using Illusion.Formats.EntityData;
using Illusion.Domain;
using Illusion.Scene;
using Illusion.ViewModels;
using Illusion.Viewport;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The car tuning table (EDS, <c>C_Car</c>, 3400 bytes) end to end: that the core's layout covers the struct
/// without overlapping itself, that the shipped corpus reads and re-emits byte for byte, that the panel puts
/// the column-major fields back together per wheel and per gear — and that a written value lands in the file
/// and comes back out with an undo.
/// <para>
/// The write half runs on a DUPLICATE of the working copy, because the working copy is the player's install.
/// Output: %TEMP%\illusion_tuning.txt
/// </para>
/// </summary>
internal static class TuningProbes
{
    // What the transcription is pinned to. The first three are offsets MafiaToolkit's own source records in
    // comments (IDA offsets inside the payload); the last is what fixes the tail's alignment — ExplodeID has
    // to be the final four bytes of the struct.
    private static readonly (string Name, uint Offset)[] Checkpoints =
    [
        ("Mass", 32),
        ("ArcadeMinCoeff", 1760),
        ("SpeedMaxEffectivity", 1788),
        ("FFRideMagnitudeCoeff", 1804),
        ("ExplodeID", 3396),
    ];

    internal static void RunTuningProbe(string? focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_tuning.txt");
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

            FileInfo car = new(Path.Combine(
                MafiaEnvironment.PcFolder, "sds", "cars", (focus ?? "shubert_38") + ".sds"));
            sb.AppendLine($"CAR TUNING PROBE — {car.Name}\n");

            CheckCorpus(Check, sb);
            CheckPanel(car, Check, sb);
            CheckRows(car, Check, sb);
            CheckTab(Check);
            CheckPicker(car, Check, sb);
            CheckEditing(car, Check, sb);
        }
        catch (Exception ex)
        {
            Check("unexpected exception", false, ex.ToString());
        }
        finally
        {
            sb.Insert(0, $"CAR TUNING PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    /// <summary>
    /// Every car table the game ships: does the layout describe it without holes it does not admit to, and
    /// does reading it leave the bytes alone.
    /// </summary>
    private static void CheckCorpus(Action<string, bool, string> check, StringBuilder sb)
    {
        string root = MafiaEnvironment.ResourcesFolder!;
        if (!Directory.Exists(root)) { check("the resources mirror is unpacked", false, root); return; }

        int storages = 0, tables = 0, fixpoint = 0, sameCount = 0, expected = -1;
        int overlapping = 0, outOfOrder = 0, past = 0, uncovered = -1;
        string firstOdd = "";

        foreach (string file in Directory.GetFiles(root, "*.eds", SearchOption.AllDirectories))
        {
            EntityDataStorageFile storage;
            try { storage = EntityDataStorageFile.Load(file); }
            catch (Exception) { continue; }
            if (storage.EntityType != 18) continue;

            storages++;
            try
            {
                if (storage.ToBytes().AsSpan().SequenceEqual(File.ReadAllBytes(file))) fixpoint++;
                else if (firstOdd.Length == 0) firstOdd = "re-emit differs: " + Path.GetFileName(file);
            }
            catch (Exception) { /* counted as not a fixpoint */ }

            foreach (EntityDataTable table in storage.Tables)
            {
                tables++;
                if (expected < 0) expected = table.Fields.Count;
                if (table.Fields.Count == expected) sameCount++;

                // The layout must be a partition, not a pile: every field inside the payload, each starting
                // after the one before it ends. An overlap would mean two names for one number and a write
                // through one of them silently moving the other.
                uint at = 0;
                int covered = 0;
                foreach (ActorPropertyField field in table.Fields)
                {
                    uint width = Width(field);
                    if (field.Offset < at) { overlapping++; break; }
                    if (field.Offset + width > table.PayloadSize) { past++; break; }
                    at = field.Offset + width;
                    covered += (int)width;
                }
                if (uncovered < 0) uncovered = table.PayloadSize - covered;
            }
        }

        sb.AppendLine($"corpus: {storages} car storages, {tables} tables, {expected} fields each\n");
        check("every car storage in the game reads", storages > 0, $"{storages} storages");
        check("reading a table leaves the file alone", fixpoint == storages, $"{fixpoint}/{storages} {firstOdd}");
        check("every table is described by the same field list", sameCount == tables, $"{sameCount}/{tables}");
        check("no two fields claim the same bytes", overlapping == 0 && outOfOrder == 0, $"{overlapping} overlaps");
        check("no field runs past the end of the table", past == 0, $"{past} past the end");
        // What the layout does NOT name: 112 bytes MafiaToolkit reads as a raw run, 12 more it calls
        // UnknownDifferential, and three 2-byte alignment pads after the boolean pairs. They ride byte for
        // byte; the number is asserted so the day someone names one of them, this line is what says so.
        check("only the 130 unnamed bytes are left out", uncovered == 130, $"{uncovered} B unnamed");
    }

    private static uint Width(ActorPropertyField field) => field.Kind switch
    {
        ActorPropertyKind.Bool or ActorPropertyKind.Int8 or ActorPropertyKind.UInt8 => 1,
        ActorPropertyKind.Int16 or ActorPropertyKind.UInt16 => 2,
        ActorPropertyKind.Vector3 => 12,
        ActorPropertyKind.Int64 or ActorPropertyKind.UInt64 or ActorPropertyKind.Hash64 => 8,
        ActorPropertyKind.Text => field.Capacity,
        _ => 4,
    };

    /// <summary>What the tab is handed: the checkpoints the transcription is pinned to, the bands, and the
    /// column-major fields put back together per wheel and per gear.</summary>
    private static void CheckPanel(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        CarTuning? tuning = CarTuning.Read(car);
        if (tuning == null) { check("the car's tuning reads", false, car.FullName); return; }

        TuningTableView table = tuning.Tables[0];
        List<TuningFieldView> rows = tuning.Tables[0].Bands
            .SelectMany(b => b.Elements).SelectMany(e => e.Rows).ToList();

        check("the car's storage is on the panel", tuning.Tables.Count > 0,
            $"{tuning.Tables.Count} tables, {tuning.FieldCount} fields");
        check("a table is the size the layout expects", table.Size == 3400, $"{table.Size} B");

        foreach ((string name, uint offset) in Checkpoints)
        {
            TuningFieldView? field = rows.FirstOrDefault(r => r.Name == name);
            check($"{name} sits at {offset}", field?.Offset == offset,
                field == null ? "(missing)" : field.Offset.ToString());
        }

        // The bands, and the traps in assigning them: a field goes to the band whose LONGEST prefix it starts
        // with, so the sound fields do not fall into the gearbox and the brakes do not fall into the sound.
        string BandOf(string name) => table.Bands
            .FirstOrDefault(b => b.Elements.Any(e => e.Rows.Any(r => r.Name == name)))?.Title ?? "(none)";

        check("Mass is in the body band", BandOf("Mass") == "Body", BandOf("Mass"));
        check("a gear ratio is in the gearbox", BandOf("Gear2.GearRatio") == "Gearbox", BandOf("Gear2.GearRatio"));
        check("the gearbox SOUND is not in the gearbox",
            BandOf("GearboxShiftSndId1") == "Car sound", BandOf("GearboxShiftSndId1"));
        check("the handbrake TORQUE is not in the handbrake sound",
            BandOf("HandBrakeTorque") == "Brakes", BandOf("HandBrakeTorque"));
        check("the rain EFFECT is not in the rain sound",
            BandOf("RainID") == "Effects" && BandOf("RainVolumeLo") == "Car sound",
            $"{BandOf("RainID")} / {BandOf("RainVolumeLo")}");
        check("a wheel's camber is in the wheels band",
            BandOf("Wheel3.Camber") == "Wheels", BandOf("Wheel3.Camber"));

        // The point of the tab. In the file all ten wheels' models come first, then all ten tyres; a wheel's
        // own fields are 800 bytes apart. On the panel they are one plate.
        TuningBandView wheels = table.Bands.First(b => b.Title == "Wheels");
        TuningElementView? wheel3 = wheels.Elements.FirstOrDefault(e => e.Title == "Wheel 3");
        check("each wheel is one plate", wheels.Elements.Count == 10, $"{wheels.Elements.Count} plates");
        check("a wheel plate carries all of that wheel's fields", wheel3?.Rows.Count == 23,
            $"{wheel3?.Rows.Count} rows");
        check("...gathered from all over the payload",
            wheel3 != null && wheel3.Rows.Max(r => r.Offset) - wheel3.Rows.Min(r => r.Offset) > 700,
            wheel3 == null ? "" : $"{wheel3.Rows.Min(r => r.Offset)}..{wheel3.Rows.Max(r => r.Offset)}");

        TuningElementView? gear2 = table.Bands.First(b => b.Title == "Gearbox").Elements
            .FirstOrDefault(e => e.Title == "Gear 2");
        check("a gear plate carries its six fields", gear2?.Rows.Count == 6, $"{gear2?.Rows.Count} rows");

        // The wheel prototype: the one resource NAME in the whole struct, and the car's only link to the
        // wheels in cars_universal.
        TuningFieldView? model = rows.FirstOrDefault(r => r.Name == "Wheel0.WheelModel");
        check("the wheel prototype is a name buffer",
            model is { Kind: TuningFieldKind.Text, Capacity: 32 }, $"{model?.Kind} cap {model?.Capacity}");
        check("...and it names a wheel", model?.Text.StartsWith("wheel", StringComparison.OrdinalIgnoreCase) == true,
            model?.Text ?? "(none)");

        sb.AppendLine();
        sb.AppendLine($"bands: {string.Join(", ", table.Bands.Select(b => $"{b.Title} {b.FieldCount}"))}");
        sb.AppendLine($"wheel 0: {string.Join("  ", wheels.Elements[0].Rows.Take(6).Select(r => r.Label + "=" + r.Value))}");
        foreach (TuningTableView t in tuning.Tables)
        {
            TuningFieldView? mass = t.Bands.SelectMany(b => b.Elements).SelectMany(e => e.Rows)
                .FirstOrDefault(r => r.Name == "Mass");
            TuningFieldView? power = t.Bands.SelectMany(b => b.Elements).SelectMany(e => e.Rows)
                .FirstOrDefault(r => r.Name == "Power");
            sb.AppendLine($"  {t.Title} {t.HashText}  Mass={mass?.Value} Power={power?.Value}  {t.TwinOf}");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// The rows as the panel binds them. The five widgets of a row share one stack and are told apart by
    /// visibility alone, so exactly one flag per row has to be true — two would draw two boxes over one
    /// number, and none would draw a label with nothing under it.
    /// </summary>
    private static void CheckRows(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        CarTuning? tuning = CarTuning.Read(car);
        if (tuning == null) return;

        var bands = new List<TuningBandRowsViewModel>();
        int commits = 0;
        foreach (TuningBandView band in tuning.Tables[0].Bands)
        {
            var elements = band.Elements
                .Select(e => new TuningElementRowsViewModel(e.Title, e.Rows
                    .Select(r => new TuningRowViewModel(r, (_, _) => { commits++; return true; }))
                    .ToList()))
                .ToList();
            bands.Add(new TuningBandRowsViewModel(band.Title, elements));
        }
        List<TuningRowViewModel> rows = bands.SelectMany(b => b.Elements).SelectMany(e => e.Rows).ToList();

        check("every row draws exactly one widget",
            rows.All(r => Widgets(r) == 1),
            $"{rows.Count(r => Widgets(r) != 1)} of {rows.Count} rows do not");
        check("a vector draws its own name, everything else gets a caption",
            rows.Where(r => r.IsVector).All(r => !r.HasCaption)
            && rows.Where(r => !r.IsVector).All(r => r.HasCaption), "");
        check("a name box knows the width of its slot",
            rows.Where(r => r.IsText || r.IsChoiceText).All(r => r.MaxLength is 31 or 7),
            string.Join(",", rows.Where(r => r.IsText || r.IsChoiceText).Select(r => r.MaxLength).Distinct()));
        check("the bands start closed", bands.All(b => !b.IsExpanded), "");

        // The search is the only way through 771 fields, and it has to answer both "where is the camber" and
        // "which slot holds wheel_civ09".
        foreach (TuningBandRowsViewModel band in bands) band.Search("camber");
        TuningBandRowsViewModel wheels = bands.First(b => b.Title == "Wheels");
        check("a search by field name finds it and opens the band",
            wheels is { IsVisible: true, IsExpanded: true }
            && wheels.Elements.SelectMany(e => e.Rows).All(r => r.Label.Contains("Camber", StringComparison.Ordinal)),
            $"{wheels.Elements.Sum(e => e.Rows.Count)} rows");
        check("...and the bands with no hit fold away",
            bands.Where(b => b.Title != "Wheels").All(b => !b.IsVisible), "");

        foreach (TuningBandRowsViewModel band in bands) band.Search("wheel_civ09");
        check("a search by VALUE finds the slot that holds it",
            wheels.IsVisible && wheels.Elements.SelectMany(e => e.Rows).Any(r => r.Name.EndsWith("WheelModel", StringComparison.Ordinal)),
            "");

        foreach (TuningBandRowsViewModel band in bands) band.Search("");
        check("clearing the search puts every band back, closed",
            bands.All(b => b.IsVisible && !b.IsExpanded), "");

        // A field only commits when the value really moved: retyping the same number must not put an entry on
        // the undo stack or mark the archive for a rebuild.
        TuningRowViewModel mass = rows.First(r => r.Name == "Mass");
        float was = mass.Number;
        mass.Number = was;
        check("retyping the same number writes nothing", commits == 0, $"{commits} commits");
        mass.Number = was + 1;
        check("changing it does write", commits == 1, $"{commits} commits");

        sb.AppendLine($"rows: {rows.Count} in {bands.Count} bands");
        sb.AppendLine();
    }

    private static int Widgets(TuningRowViewModel row) =>
        (row.IsNumber ? 1 : 0) + (row.IsInteger ? 1 : 0) + (row.IsFlag ? 1 : 0)
        + (row.IsVector ? 1 : 0) + (row.IsText ? 1 : 0) + (row.IsChoiceText ? 1 : 0);

    /// <summary>
    /// The table picker, driven through the real view-model: one table on screen, the labels tell the
    /// variants apart, and switching keeps where the user was — the open bands and the search.
    /// </summary>
    private static void CheckPicker(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        (List<SdsFrameNode> roots, _, ISceneDocument? document) =
            SdsMeshLoader.LoadHierarchy(car);
        if (document == null || roots.Count == 0)
        {
            check("the car loads a scene document to hang the tab on", false, "no document");
            return;
        }

        var host = new D3DImageHost();
        SceneNode folder = host.Tree.GetOrCreateFolder("probe");
        var sdsNode = new SceneNode(car.Name, "Sds", true);
        var frNode = new SceneNode("FrameResource", "FrameResource", true) { Source = document };
        var leaves = new List<SceneNode>();
        foreach (SdsFrameNode r in roots) frNode.AddChild(SceneTree.BuildSceneTree(r, leaves));
        sdsNode.AddChild(frNode);
        folder.AddChild(sdsNode);
        host.Tree.RebuildStageRoots();

        var vm = new SelectionViewModel(host);
        vm.RefreshTuning();
        Pump(() => vm.HasTuning);
        check("the tuning tab is reachable with nothing selected", vm.HasTuning, vm.TuningSummary);
        if (!vm.HasTuning) return;

        check("the picker offers every table", vm.TuningTables.Count > 1 && vm.HasManyTuningTables,
            $"{vm.TuningTables.Count} tables");
        check("one table is on screen, not all of them",
            vm.SelectedTuningTable != null && ReferenceEquals(vm.SelectedTuningTable, vm.TuningTables[0]),
            vm.SelectedTuningTable?.Title ?? "(none)");
        // Labels are the only way to tell the variants apart — their names are hashes of definitions kept
        // in another archive, so a picker of six rows reading "Table N" would be six rows of nothing.
        check("the strip's buttons are numbered from one",
            vm.TuningTables.Select(t => t.Number).SequenceEqual(["1", "2", "3", "4", "5", "6"]),
            string.Join(",", vm.TuningTables.Select(t => t.Number)));
        check("...and the entries say what tells them apart",
            vm.TuningTables.Select(t => t.Label).Distinct(StringComparer.Ordinal).Count() > 1
            && vm.TuningTables.All(t => t.Label.Contains("Mass", StringComparison.Ordinal)),
            string.Join(" | ", vm.TuningTables.Select(t => t.Label)));

        // Open a band, switch table, and it is still open: "what is the camber here, and in the tuned one"
        // is one question asked twice.
        TuningBandRowsViewModel wheels = vm.SelectedTuningTable!.Bands.First(b => b.Title == "Wheels");
        wheels.IsExpanded = true;
        vm.SelectedTuningTable = vm.TuningTables[3];
        check("switching tables moves what is on screen",
            ReferenceEquals(vm.SelectedTuningTable, vm.TuningTables[3]), vm.SelectedTuningTable?.Title ?? "");
        check("...and the band that was open stays open",
            vm.SelectedTuningTable!.Bands.First(b => b.Title == "Wheels").IsExpanded, "");

        // The same for a search: it follows the switch rather than being typed again.
        vm.TuningSearch = "camber";
        check("a search narrows the table on screen",
            vm.SelectedTuningTable!.Bands.Where(b => b.IsVisible).Select(b => b.Title).SequenceEqual(["Wheels"]),
            string.Join(",", vm.SelectedTuningTable!.Bands.Where(b => b.IsVisible).Select(b => b.Title)));
        vm.SelectedTuningTable = vm.TuningTables[5];
        check("...and follows the switch to another table",
            vm.SelectedTuningTable!.Bands.Where(b => b.IsVisible).Select(b => b.Title).SequenceEqual(["Wheels"])
            && !vm.TuningNothingFound, "");
        vm.TuningSearch = "";

        // An edit rebuilds every row. Landing back on table 1 after retuning table 6 would be the panel
        // moving the user somewhere they did not ask to go.
        vm.RefreshTuning();
        Pump(() => vm.HasTuning);
        check("the chosen table survives a rebuild",
            vm.SelectedTuningTable?.Title == "Table 6", vm.SelectedTuningTable?.Title ?? "(none)");

        sb.AppendLine("picker: " + string.Join(" | ", vm.TuningTables.Select(t => t.Label)));
        Render(car, host, check, sb);
        sb.AppendLine();
    }

    /// <summary>
    /// A picture of the tab, open on a band. Everything above says the rows are RIGHT; none of it says the
    /// strip of numbers and the plates are readable in a panel this narrow, and that is the only question a
    /// layout can fail on its own terms.
    /// </summary>
    private static void Render(
        FileInfo car, Viewport.D3DImageHost host, Action<string, bool, string> check, StringBuilder sb)
    {
        try
        {
            var panel = new Views.ScenePanel();
            panel.Attach(host);
            SelectionViewModel vm = panel.Selection;
            vm.RefreshTuning();
            Pump(() => vm.HasTuning);
            if (!vm.HasTuning) { check("the tab fills in the panel", false, "no tuning"); return; }

            vm.SelectedTuningTable = vm.TuningTables[3];
            // Opened on the body: eight rows, one of them a vector — enough to see that a row, a number box
            // and a three-field position all reach the same right edge, which a closed band cannot show.
            vm.SelectedTuningTable!.Bands.First(b => b.Title == "Body").IsExpanded = true;
            panel.Tabs.PropertyTabs.SelectedItem = panel.Tabs.PropertyTabs.Items
                .OfType<System.Windows.Controls.TabItem>()
                .First(t => (t.Header as string) == "Tuning");

            const double w = 340, h = 900;
            panel.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20));
            for (int i = 0; i < 3; i++)
            {
                panel.Measure(new System.Windows.Size(w, h));
                panel.Arrange(new System.Windows.Rect(0, 0, w, h));
                panel.UpdateLayout();
            }

            // An opened band FADES in over 140 ms, and a snapshot taken before that finishes catches its
            // plates at zero opacity — present, measured, and invisible. Let the clock run before drawing.
            DateTime until = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < until)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.Render);
                Thread.Sleep(16);
            }
            panel.UpdateLayout();

            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)w, (int)h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(panel);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            string path = Path.Combine(Path.GetTempPath(), "illusion_tuning.png");
            using (FileStream fs = File.Create(path)) enc.Save(fs);

            check("the tab draws", new FileInfo(path).Length > 0, path);
            sb.AppendLine($"rendered {car.Name} -> {path}");
        }
        catch (Exception ex)
        {
            // A render that cannot run is not the layout being wrong — say so and leave the asserts above.
            sb.AppendLine("render skipped — " + ex.Message);
        }
    }

    /// <summary>
    /// What the rail's template actually put on the tab: the geometry the icon Path ended up drawing, or null
    /// when nothing reached it. Needs the template applied, so the panel goes into an off-screen window and
    /// is laid out.
    /// </summary>
    private static System.Windows.Media.Geometry? DrawnIcon(Views.ScenePanel panel, string header)
    {
        if (panel.Parent == null)
        {
            var window = new System.Windows.Window
            {
                Width = 500,
                Height = 700,
                Left = -20_000,
                Top = -20_000,
                ShowInTaskbar = false,
                WindowStyle = System.Windows.WindowStyle.None,
                Content = panel,
            };
            window.Show();
            window.UpdateLayout();
        }

        System.Windows.Controls.TabItem? tab = panel.Tabs.PropertyTabs.Items
            .OfType<System.Windows.Controls.TabItem>()
            .FirstOrDefault(t => (t.Header as string) == header);
        if (tab == null) return null;
        tab.ApplyTemplate();
        return (tab.Template?.FindName("Vector", tab) as System.Windows.Shapes.Path)?.Data;
    }

    private static string DrawnIconDetail(Views.ScenePanel panel, string header) =>
        DrawnIcon(panel, header) == null ? $"the {header} tab's Path drew nothing" : "";

    private static void Pump(Func<bool> until)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (!until() && DateTime.UtcNow < end)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(10);
        }
    }

    /// <summary>The tab itself is in the property rail — the XAML block is really in the built control, with
    /// its own icon rather than a bare header.</summary>
    private static void CheckTab(Action<string, bool, string> check)
    {
        var panel = new Views.ScenePanel();
        System.Windows.Controls.TabItem? tab = panel.Tabs.PropertyTabs.Items
            .OfType<System.Windows.Controls.TabItem>()
            .FirstOrDefault(t => (t.Header as string) == "Tuning");
        check("the Tuning tab is in the property rail", tab != null, "");
        check("...and carries the entity-data icon rather than a bare header",
            tab != null && Views.TabIcon.GetIcon(tab) != null, "");

        // Set on the tab is not the same as DRAWN on the tab: the rail's template reaches the icon through a
        // binding path, and a path that goes nowhere fails at runtime with a logged warning and an empty
        // Path — nothing a build would ever object to. The only way to see it is to run the template.
        check("...and the icon reaches the rail's template rather than binding to nothing",
            DrawnIcon(panel, "Tuning") != null, DrawnIconDetail(panel, "Tuning"));

        // The table picker is a strip of numbered buttons rather than a drop-down: with six tables, one
        // click each beats open-list-read-pick every time. It sits in the header card, outside any data
        // template, so it is in the logical tree whether or not the tab has ever been drawn.
        System.Windows.Controls.ListBox? strip = tab == null ? null : Strip(tab);
        check("the tables are picked from a numbered strip",
            strip != null && strip.ItemTemplate != null, "");
        check("...whose buttons split the width evenly, like a segmented control",
            strip?.ItemsPanel?.LoadContent() is System.Windows.Controls.Primitives.UniformGrid { Rows: 1 }, "");

        // Tuning stands under Render rather than appearing with a selection: it describes the whole archive,
        // not whatever happens to be clicked. It used to be checked beside the Prefab tab, which is gone —
        // a car's ASSEMBLY is the component tree now, and the rail is left describing the car class and the
        // archive. That the assembly has no tab is asserted here rather than left to be noticed.
        var top = panel.Tabs.PropertyTabs.Items.OfType<System.Windows.Controls.TabItem>()
            .Take(3).Select(t => t.Header as string).ToList();
        check("Render and Tuning are the standing tabs, in that order",
            top is ["Render", "Tuning", ..], string.Join(" ", top));

        var headers = panel.Tabs.PropertyTabs.Items.OfType<System.Windows.Controls.TabItem>()
            .Select(t => t.Header as string).ToList();
        check("nothing on the rail describes how the car is ASSEMBLED — that is the component tree",
            !headers.Contains("Prefab"), string.Join(" ", headers));
    }

    // The first ListBox under the tab that carries the strip's own style — walked logically, because a tab
    // that has never been selected has no visual tree at all.
    private static System.Windows.Controls.ListBox? Strip(System.Windows.DependencyObject at)
    {
        if (at is System.Windows.Controls.ListBox { Style: not null } list
            && ReferenceEquals(list.Style, System.Windows.Application.Current?.TryFindResource("NumberStrip")))
        {
            return list;
        }
        foreach (object child in System.Windows.LogicalTreeHelper.GetChildren(at))
        {
            if (child is System.Windows.DependencyObject node && Strip(node) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// The write path, on a duplicate of the working copy: a value typed in reaches the file, only its own
    /// bytes move, and an undo puts the file back exactly as it was.
    /// </summary>
    private static void CheckEditing(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_tuning_edit");
        try
        {
            string source = MafiaEnvironment.ExtractedDir(car);
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, scratch, StringComparison.Ordinal));
            foreach (string f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(f, f.Replace(source, scratch, StringComparison.Ordinal), overwrite: true);

            CarTuning? before = CarTuning.ReadFrom(scratch, ["wheel_civ08", "wheel_sport"]);
            if (before == null) { check("the scratch copy still has a table", false, scratch); return; }

            TuningTableView table = before.Tables[0];
            byte[] original = File.ReadAllBytes(table.Path);

            // A float, an integer, a flag, a vector and a name — one of each shape the panel writes.
            Edit(check, table, before, "Mass", TuningEditing.TuningValue.Of(1234.5f), original, "1234.5");
            Edit(check, table, before, "GearCount", TuningEditing.TuningValue.Of(5L), original, "5");
            Edit(check, table, before, "ESM", TuningEditing.TuningValue.Of(1L), original, "true");
            Edit(check, table, before, "CenterOfMass", TuningEditing.TuningValue.Of(0.5f, 1.5f, 2.5f),
                original, "0.5, 1.5, 2.5");
            Edit(check, table, before, "Wheel0.WheelModel", TuningEditing.TuningValue.Of("wheel_sport"),
                original, "wheel_sport");

            // A write moves bytes INSIDE its own field and nowhere else — not four bytes, because two floats
            // often share one: 1420 and 999 differ in three. That every moved byte is inside the field is the
            // whole promise of the overlay, and it covers the padding, the 130 unnamed bytes and the other
            // 770 fields at once.
            TuningFieldView mass = Row(before, "Mass")!;
            TuningEditing.Change? change = TuningEditing.Set(
                table.Path, table.Index, mass.Offset, TuningEditing.TuningValue.Of(999f), "Mass");
            byte[] edited = File.ReadAllBytes(table.Path);
            long start = edited.Length - original.Length;      // the table run sits at the end of the storage
            List<long> moved = original.Length != edited.Length
                ? [-1]
                : Enumerable.Range(0, original.Length).Where(i => original[i] != edited[i])
                    .Select(i => (long)i).ToList();
            long tableAt = TableStart(original, table.Index);
            check("a written number moves nothing outside its own field",
                start == 0 && moved.Count > 0
                && moved.All(i => i >= tableAt + mass.Offset && i < tableAt + mass.Offset + 4),
                $"{moved.Count} byte(s) at {string.Join(",", moved)}");

            // Another table of the same storage must not have moved with it — a table is addressed by its
            // index, and getting that wrong would retune every variant of the car at once.
            if (before.Tables.Count > 1)
            {
                TuningTableView other = before.Tables[1];
                CarTuning? now = CarTuning.ReadFrom(scratch);
                TuningFieldView? otherMass = now?.Tables[1].Bands.SelectMany(b => b.Elements)
                    .SelectMany(e => e.Rows).FirstOrDefault(r => r.Name == "Mass");
                TuningFieldView? wasMass = other.Bands.SelectMany(b => b.Elements)
                    .SelectMany(e => e.Rows).FirstOrDefault(r => r.Name == "Mass");
                check("editing one table leaves the others alone",
                    otherMass != null && otherMass.Value == wasMass?.Value,
                    $"{otherMass?.Value} (was {wasMass?.Value})");
            }

            check("an undo puts the file back byte for byte",
                change != null && TuningEditing.Restore(change, change.Before)
                && File.ReadAllBytes(table.Path).AsSpan().SequenceEqual(original), "");

            // A name longer than the slot is cut by the core rather than overrunning it.
            TuningFieldView model = Row(before, "Wheel0.WheelModel")!;
            TuningEditing.Change? longName = TuningEditing.Set(table.Path, table.Index, model.Offset,
                TuningEditing.TuningValue.Of(new string('x', 64)), "Wheel0.WheelModel");
            CarTuning? afterLong = CarTuning.ReadFrom(scratch);
            TuningFieldView? cut = Row(afterLong, "Wheel0.WheelModel");
            check("a name too long for its slot is cut, not overrun",
                cut != null && cut.Text.Length <= 31 && File.ReadAllBytes(table.Path).Length == original.Length,
                $"{cut?.Text.Length} chars");
            if (longName != null) TuningEditing.Restore(longName, longName.Before);

            check("everything put back leaves the storage as it was",
                File.ReadAllBytes(table.Path).AsSpan().SequenceEqual(original), "");
        }
        catch (Exception ex)
        {
            check("the edit round trip runs", false, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* a locked scratch folder is not a failure of the thing under test */ }
        }
    }

    // Write one field, read the whole table back off disk, and take it back — the shape every panel edit has.
    private static void Edit(
        Action<string, bool, string> check, TuningTableView table, CarTuning before, string name,
        TuningEditing.TuningValue value, byte[] original, string expected)
    {
        TuningFieldView? field = Row(before, name);
        if (field == null) { check($"{name} is on the panel", false, "(missing)"); return; }

        TuningEditing.Change? change = TuningEditing.Set(table.Path, table.Index, field.Offset, value, name);
        if (change == null) { check($"{name} takes a write", false, "(refused)"); return; }

        CarTuning? after = CarTuning.ReadFrom(Path.GetDirectoryName(table.Path)!);
        TuningFieldView? now = Row(after, name);
        check($"{name} reads back as what was written", now?.Value == expected,
            $"{now?.Value} (wanted {expected})");

        TuningEditing.Restore(change, change.Before);
        check($"...and undoing {name} restores the file",
            File.ReadAllBytes(table.Path).AsSpan().SequenceEqual(original), "");
    }

    // Where a table's payload starts in the storage file: the 20-byte header, the hash array, then the tables
    // back to back. Read off the file rather than assumed, so the check is against the real layout.
    private static long TableStart(byte[] storage, int index)
    {
        int tableSize = BitConverter.ToInt32(storage, 12);
        uint count = BitConverter.ToUInt32(storage, 16);
        return 20 + count * 8 + (long)index * tableSize;
    }

    private static TuningFieldView? Row(CarTuning? tuning, string name) =>
        tuning?.Tables[0].Bands.SelectMany(b => b.Elements).SelectMany(e => e.Rows)
            .FirstOrDefault(r => r.Name == name);
}
