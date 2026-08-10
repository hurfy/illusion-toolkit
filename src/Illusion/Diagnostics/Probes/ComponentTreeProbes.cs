using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Cars;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Scene;
using Illusion.Settings;
using Illusion.ViewModels;
using Illusion.Viewport;
using Illusion.Views;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Whether opening a car shows the CAR rather than the file. The scene panel is built headless around a
/// staged archive and asked the questions a modder would ask of it: is every component here, named after its
/// own bone and nested the way the prefab says; are the bare ones — licence plates, lights, wipers — there
/// beside the rest; does the <c>Components | Raw</c> switch put the frame tree back exactly as it was; does
/// a selection survive the switch in both directions; and does an archive that is not a car still open on
/// its frames.
/// <para>
/// It reads the shipped corpus and never writes to it. It DOES write the real settings.json, because that is
/// where the switch's per-archive position lives, and it puts the user's list back afterwards.
/// </para>
/// <para>
/// No GPU: no window is ever shown, so the D3D surface is never created and every render path the panel
/// touches early-outs on a null renderer. Output: %TEMP%\illusion_component_tree.txt
/// </para>
/// </summary>
internal static class ComponentTreeProbes
{
    /// <summary>An archive in the very same folder as the cars that is NOT one: the shared catalog of wheels
    /// and shop parts. It carries a frame resource and no car prefab, which is exactly the case "archives
    /// that are not cars are unaffected" is about.</summary>
    private const string NotACar = "cars_universal";

    internal static void RunComponentTreeProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_component_tree.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string focusKey = (focus + ".sds").ToLowerInvariant();
        bool focusWasRaw = UserSettings.Current.RawSceneTree.Contains(focusKey, StringComparer.Ordinal);
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            var car = new FileInfo(Path.Combine(folder, focus + ".sds"));
            sb.AppendLine($"COMPONENT TREE PROBE — {car.Name}\n");

            // The switch's memory is the one thing here that persists, and it is the USER's. Only the focus
            // car's own entry is touched — a probe killed halfway can then cost at most that one position,
            // rather than every archive they had left on Raw.
            Forget(focusKey);

            CheckTree(car, Check, sb);
            CheckSwitch(car, Check, sb);
            CheckSelection(car, Check, sb);
            CheckCollisions(car, Check, sb);
            CheckRemembered(car, Check, sb);
            CheckNotACar(folder, Check, sb);
            CheckRestitch(car, Check, sb);
            CheckBroken(car, Check, sb);
            CheckFaults(car, folder, Check, sb);
            Render(car, Check, sb);
        }
        catch (Exception ex)
        {
            Check("unexpected exception", false, ex.ToString());
        }
        finally
        {
            Forget(focusKey);
            if (focusWasRaw) UserSettings.Update(settings => settings.RawSceneTree.Add(focusKey));
            sb.Insert(0, $"COMPONENT TREE PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static void Forget(string archiveKey) => UserSettings.Update(settings =>
        settings.RawSceneTree.RemoveAll(a => string.Equals(a, archiveKey, StringComparison.Ordinal)));

    // ── the tree itself ──

    /// <summary>
    /// A car opens on its components: one row per component, named after its own bone, nested by the
    /// prefab's parent link, with the bare ones alongside.
    ///
    /// <para>
    /// The names are checked against the RAW tree's own bone rows rather than against the aggregate that
    /// produced them — asking one reading the same question twice measures nothing.
    /// </para>
    /// </summary>
    private static void CheckTree(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        sb.AppendLine("════ the car opens on its components ════");
        if (!Stage(car, out ScenePanel? panel, out D3DImageHost? host))
        {
            check("the car stages", false, car.FullName);
            return;
        }

        ComponentTreeViewModel components = panel!.Components;
        check("a car opens on the component tree", components.HasCar && components.ShowsComponents,
            components.HasCar ? "showing components" : "no car was stitched");
        if (components.Car is not { } stitched) return;

        List<ComponentRowViewModel> rows =
            [.. components.Roots.SelectMany(r => r.SelfAndDescendants())];
        sb.AppendLine($"  {rows.Count} rows over {stitched.Components.Count} components, "
            + $"{components.Roots.Count} of them at the top");

        check("every component the aggregate stitched has exactly one row",
            rows.Count == stitched.Components.Count
            && rows.Select(r => r.Id.Value).Distinct().Count() == rows.Count
            && stitched.Components.All(c => components.RowOf(c.Id) != null),
            $"{rows.Count} rows, {stitched.Components.Count} components");

        // The nesting the tree draws is the prefab's own parent link, not a re-derivation of it.
        int nested = rows.Count(r => r.Parent != null);
        int agreeing = rows.Count(r =>
            ReferenceEquals(r.Parent?.Component, r.Component.Parent));
        check("a row hangs off the component the prefab's parent link names",
            nested > 0 && agreeing == rows.Count,
            $"{nested} nested, {agreeing} of {rows.Count} agreeing with the aggregate");

        // Bare components — licence plates, lights, wipers. No prefab row names them, so no parent link can
        // place them: they stand at the top, beside the parts.
        List<ComponentRowViewModel> bare = [.. rows.Where(r => r.IsBare)];
        check("bare components are in the tree, alongside the rest",
            bare.Count > 0 && bare.Count == stitched.Components.Count(c => c.IsBare)
            && bare.All(r => r.Parent == null),
            $"{bare.Count} bare rows, all at the top: {bare.All(r => r.Parent == null)}");
        sb.AppendLine("  bare, first eight: " + string.Join(", ", bare.Take(8).Select(r => r.Name)));

        // The independent reading: the bone rows of the RAW tree. Every component whose bone resolves is a
        // bone the frame tree also shows, under exactly that name.
        Dictionary<string, SceneNode> bones = BoneRows(host!);
        int resolved = rows.Count(r => !r.IsBroken);
        int named = rows.Count(r => !r.IsBroken && bones.ContainsKey(r.Name));
        check("a row is named after a bone the frame tree shows under the same name",
            resolved > 0 && named == resolved,
            $"{named} of {resolved} rows found among {bones.Count} bone rows");

        // The kind is SHOWN, never inferred from the name: a cover names doorBL on seven shipped cars.
        var kinds = rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());
        sb.AppendLine("  kinds: " + string.Join(", ", kinds.OrderByDescending(k => k.Value)
            .Select(k => $"{k.Key}×{k.Value}")));
        check("every row says what kind of part it is",
            rows.All(r => r.Kind.Length > 0) && kinds.Count > 1 && kinds.ContainsKey("body"),
            $"{kinds.Count} kinds");
        sb.AppendLine();
    }

    // ── the switch ──

    private static void CheckSwitch(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        sb.AppendLine("════ Components | Raw ════");
        if (!Stage(car, out ScenePanel? panel, out D3DImageHost? host) || panel == null || host == null)
        {
            return;
        }

        // What the frame tree shows BEFORE the component tree has ever been in front of it. Raw has to put
        // exactly this back — that is the whole promise of the switch.
        object? source = panel.Tree.SceneTree.ItemsSource;
        List<string> before = [.. panel.Tree.SceneTree.Items.OfType<SceneNode>().Select(n => n.Name)];

        check("the switch is above the tree, and the components hold the row",
            panel.TreeModeSwitch.Visibility == Visibility.Visible
            && panel.ComponentTree.Visibility == Visibility.Visible
            && panel.Tree.Visibility == Visibility.Collapsed,
            $"switch={panel.TreeModeSwitch.Visibility}, components={panel.ComponentTree.Visibility}");

        panel.Components.IsRaw = true;
        List<string> after = [.. panel.Tree.SceneTree.Items.OfType<SceneNode>().Select(n => n.Name)];
        check("Raw shows exactly what the tree shows today",
            panel.Tree.Visibility == Visibility.Visible
            && panel.ComponentTree.Visibility == Visibility.Collapsed
            && ReferenceEquals(panel.Tree.SceneTree.ItemsSource, source)
            && ReferenceEquals(source, host.StageRoots)
            && before.SequenceEqual(after, StringComparer.Ordinal),
            $"{after.Count} rows, the viewport's own stage roots: "
            + $"{ReferenceEquals(source, host.StageRoots)}");

        panel.Components.IsRaw = false;
        check("…and the switch comes back",
            panel.ComponentTree.Visibility == Visibility.Visible
            && panel.Tree.Visibility == Visibility.Collapsed, "");

        // Everything that acts on a row and is not a component's OWN — sweeping unused hulls, rolling the
        // archive back — is on the frame tree's menu, and a car opening on its components would otherwise
        // hide the only path to those behind a switch nobody has been told about.
        MenuItem? back = panel.ComponentTree.ComponentTree.ContextMenu?.Items
            .OfType<MenuItem>()
            .FirstOrDefault(m => (m.Header as string) == "Show in Raw tree");
        check("the component tree offers the way back to the frame tree's own menu",
            back != null, back?.Header as string ?? "no context menu");
        if (back != null)
        {
            back.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, back));
            check("…and taking it puts the frame tree in the row",
                panel.Components.IsRaw && panel.Tree.Visibility == Visibility.Visible,
                $"IsRaw={panel.Components.IsRaw}");
            panel.Components.IsRaw = false;
        }
        sb.AppendLine($"  the frame tree's roots, unchanged across the switch: {string.Join(", ", after)}");
        sb.AppendLine();
    }

    // ── selection, in both directions and across the switch ──

    private static void CheckSelection(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        sb.AppendLine("════ selection resolves a frame to a component and back ════");
        if (!Stage(car, out ScenePanel? panel, out D3DImageHost? host)) return;
        ComponentTreeViewModel components = panel!.Components;
        if (components.Car is not { } stitched) { check("the car stitched", false, ""); return; }

        // A part with a bone of its own that is not the body — a door, a cover — so that "the component" and
        // "the body" cannot be confused for one another below.
        ComponentRowViewModel? part = components.Roots
            .SelectMany(r => r.SelfAndDescendants())
            .FirstOrDefault(r => !r.IsBare && !r.IsBroken && r.Component.PartType != 1);
        if (part == null) { check("the car has a part to select", false, ""); return; }

        // Tree → viewport: selecting a component highlights it in the scene, which for a car means its BONE
        // — the parts of a car are weight groups inside one mesh, and the bone is the only thing there is.
        components.Select(part);
        SceneNode? picked = host!.SelectedNode;
        check("selecting a component selects its bone in the viewport",
            picked?.Source is BoneNodeAdapter bone && bone.BoneName == part.Name
            && ReferenceEquals(components.Selected, part),
            $"{part.Name} → {picked?.Name ?? "(nothing)"}");

        // Viewport → tree: another bone, selected the way a viewport pick selects one.
        ComponentRowViewModel? other = components.Roots
            .SelectMany(r => r.SelfAndDescendants())
            .FirstOrDefault(r => !r.IsBroken && !ReferenceEquals(r, part));
        SceneNode? otherNode = other == null ? null : components.NodeOf(other.Component);
        if (otherNode != null)
        {
            host.Select(otherNode);
            check("picking a bone in the viewport selects its component in the tree",
                ReferenceEquals(components.Selected, other) && other!.IsSelected,
                $"{otherNode.Name} → {components.Selected?.Name ?? "(nothing)"}");
        }

        // Clicking a row the viewport ALREADY holds. The viewport raises nothing when the node is already
        // its sole selection, so a tree that waited for the report back would go dead on the second click.
        components.Select(part);
        components.Select(part);
        check("re-clicking the selected row keeps it selected rather than going dead",
            ReferenceEquals(components.Selected, part) && part.IsSelected,
            components.Selected?.Name ?? "(nothing)");

        // Clicking the car's GEOMETRY. A car is one skinned mesh, so a click on it can only ever name the
        // model as a whole — and the body is the component that owns it.
        SceneNode? model = ModelRow(host);
        if (model != null)
        {
            host.Select(model);
            check("clicking the car's geometry selects the body",
                components.Selected != null && stitched.Body != null
                && ReferenceEquals(components.Selected.Component, stitched.Body),
                $"{model.Name} → {components.Selected?.Name ?? "(nothing)"}");
        }

        // Across the switch, both ways. The selection is the viewport's, so what has to survive is the
        // resolve at each end: the frame row stays highlighted in Raw, the component row in Components.
        components.Select(part);
        SceneNode? held = host.SelectedNode;
        panel.Components.IsRaw = true;
        check("switching to Raw keeps the place: the component's own frame is the selected row",
            ReferenceEquals(host.SelectedNode, held) && held is { IsSelected: true },
            held?.Name ?? "(nothing)");

        panel.Components.IsRaw = false;
        check("…and switching back lands on the same component",
            ReferenceEquals(components.Selected, part) && part.IsSelected,
            components.Selected?.Name ?? "(nothing)");
        sb.AppendLine($"  selection carried across the switch: \"{part.Name}\" ({part.Kind})\n");
    }

    // ── a component lists what it is solid with ──

    /// <summary>
    /// Every collision of the car appears under the component that carries it, by ROLE and SHAPE — and
    /// nothing on the row says type 5, whose bone space the matrix is in, or that one kind of volume states a
    /// full size while the other states half of one.
    ///
    /// <para>
    /// Read-only here. What an edit WRITES is measured at the seam, by <c>--probe-collision-role</c>, against
    /// a mirror of the car under the temp directory — this probe reads the player's own install and must not
    /// write a byte of it.
    /// </para>
    /// </summary>
    private static void CheckCollisions(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        sb.AppendLine("════ a component lists its collisions by role and shape ════");
        if (!Stage(car, out ScenePanel? panel, out D3DImageHost? host)) return;
        ComponentTreeViewModel components = panel!.Components;
        if (components.Car is not { } stitched) { check("the car stitched", false, ""); return; }

        List<ComponentRowViewModel> rows = [.. components.Roots.SelectMany(r => r.SelfAndDescendants())];
        List<CollisionRowViewModel> collisions = [.. rows.SelectMany(r => r.Collisions)];
        int held = stitched.Components.Sum(c => c.Collisions.Count);

        sb.AppendLine($"  {collisions.Count} collision rows over {rows.Count} components: "
            + string.Join("  ", collisions.GroupBy(c => c.Label).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key} ×{g.Count()}")));

        check("every collision the aggregate stitched has a row under its own component",
            held > 0 && collisions.Count == held, $"{collisions.Count} rows over {held} collisions");
        check("each row says what it is and what form it takes",
            collisions.All(c => c.Label.Contains('·', StringComparison.Ordinal) && c.Size.EndsWith(" m",
                StringComparison.Ordinal)),
            collisions.FirstOrDefault()?.Label ?? "(none)");
        check("a row belongs to the component whose collision it is",
            collisions.All(c => c.Component.Component.Collisions.Contains(c.Collision)), "");

        // A cooked hull cannot be re-cooked at another size, so its row says so rather than letting a modder
        // find out by typing a number that goes nowhere.
        CollisionRowViewModel? hull =
            collisions.FirstOrDefault(c => c.Collision.Shape == Assets.Cars.CarCollisionShape.Hull);
        sb.AppendLine($"  read-only rows: {collisions.Count(c => c.IsReadOnly)}"
            + (hull == null ? "" : $" — e.g. \"{hull.Label}\": {hull.ReadOnlyReason}"));
        check("a hull's row is read-only and carries the reason",
            hull != null && hull.IsReadOnly && hull.ReadOnlyReason is { Length: > 0 },
            hull == null ? "this car carries no hull" : hull.ReadOnlyReason ?? "no reason");

        // Selecting a collision: the tree's highlight is the collision's, and the viewport stays on the
        // component's bone — a self-describing volume has no frame at all, and the mirror stub of a solid one
        // is a copy the modder is deliberately never shown.
        CollisionRowViewModel? first =
            collisions.FirstOrDefault(c => !c.Component.IsBroken && !c.Component.IsBare);
        if (first != null)
        {
            components.Select(first);
            check("selecting a collision lights its own row and leaves the viewport on its component",
                ReferenceEquals(components.SelectedCollision, first) && first.IsSelected
                && components.Selected == null
                && host!.SelectedNode?.Source is BoneNodeAdapter bone
                && bone.BoneName == first.Component.Name,
                $"{first.Label} → {host!.SelectedNode?.Name ?? "(nothing)"}");

            components.Select(first.Component);
            check("…and selecting a component again clears it, so the menu acts on exactly one row",
                components.SelectedCollision == null && !first.IsSelected
                && ReferenceEquals(components.Selected, first.Component), "");
        }

        // The menu offers what the row can actually do. An item shown while it cannot do anything is a
        // promise the menu does not keep.
        ContextMenu? menu = panel.ComponentTree.ComponentTree.ContextMenu;
        string[] headers = [.. menu?.Items.OfType<MenuItem>().Select(m => m.Header as string ?? "") ?? []];
        sb.AppendLine($"  the component tree's menu: {string.Join(" · ", headers)}");
        check("the menu can add a collision to a component and resize or remove one of its own",
            headers.Contains("Add collision…", StringComparer.Ordinal)
            && headers.Contains("Size and position…", StringComparer.Ordinal)
            && headers.Contains("Remove collision", StringComparer.Ordinal),
            string.Join(" · ", headers));

        if (first != null && menu != null)
        {
            components.Select(first.Component);
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
            MenuItem? add = menu.Items.OfType<MenuItem>()
                .FirstOrDefault(m => (m.Header as string) == "Add collision…");
            MenuItem? resize = menu.Items.OfType<MenuItem>()
                .FirstOrDefault(m => (m.Header as string) == "Size and position…");
            check("on a component the menu offers only what a component can be given",
                add?.Visibility == Visibility.Visible && resize?.Visibility == Visibility.Collapsed, "");

            components.Select(first);
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
            check("on a collision it offers only what that collision can be done to",
                add?.Visibility == Visibility.Collapsed && resize?.Visibility == Visibility.Visible
                && resize.IsEnabled != first.IsReadOnly, "");
        }
        sb.AppendLine();
    }

    // ── the switch's position is remembered per archive ──

    private static void CheckRemembered(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        sb.AppendLine("════ the switch is remembered per archive ════");
        if (!Stage(car, out ScenePanel? panel, out _)) return;

        panel!.Components.IsRaw = true;
        check("choosing Raw is written down",
            UserSettings.Current.RawSceneTree.Contains(car.Name.ToLowerInvariant()),
            string.Join(", ", UserSettings.Current.RawSceneTree));

        // Reopened: the same archive comes back on Raw without the user asking again.
        if (!Stage(car, out ScenePanel? again, out _)) return;
        check("reopening the same car opens on Raw",
            again!.Components.IsRaw && again.Tree.Visibility == Visibility.Visible
            && again.ComponentTree.Visibility == Visibility.Collapsed,
            $"IsRaw={again.Components.IsRaw}");

        again.Components.IsRaw = false;
        check("choosing Components again takes the entry away",
            !UserSettings.Current.RawSceneTree.Contains(car.Name.ToLowerInvariant()),
            string.Join(", ", UserSettings.Current.RawSceneTree));

        if (!Stage(car, out ScenePanel? third, out _)) return;
        check("…and the car opens on its components once more",
            !third!.Components.IsRaw && third.ComponentTree.Visibility == Visibility.Visible, "");
        sb.AppendLine();
    }

    // ── an archive that is not a car ──

    private static void CheckNotACar(string folder, Action<string, bool, string> check, StringBuilder sb)
    {
        sb.AppendLine("════ an archive that is not a car ════");
        var other = new FileInfo(Path.Combine(folder, NotACar + ".sds"));
        if (!Stage(other, out ScenePanel? panel, out _))
        {
            check("the non-car archive stages", false, other.FullName);
            return;
        }

        check("an archive with no car prefab has no components",
            !panel!.Components.HasCar && panel.Components.Roots.Count == 0,
            $"{panel.Components.Roots.Count} rows");
        check("…so it still opens on the frame tree, with no switch above it",
            panel.Tree.Visibility == Visibility.Visible
            && panel.ComponentTree.Visibility == Visibility.Collapsed
            && panel.TreeModeSwitch.Visibility == Visibility.Collapsed,
            $"tree={panel.Tree.Visibility}, switch={panel.TreeModeSwitch.Visibility}");
        sb.AppendLine($"  {other.Name}: the hierarchy is unaffected\n");
    }

    // ── what a re-stitch is allowed to disturb ──

    /// <summary>
    /// The scene changes on every ordinary edit — a duplicate, a delete, an undo — and each one re-stitches
    /// the car and rebuilds every row. What the modder had arranged must survive that: the branch they
    /// folded stays folded, and the row they had selected is still the selected row.
    /// </summary>
    private static void CheckRestitch(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        sb.AppendLine("════ a re-stitch keeps what the modder arranged ════");
        if (!Stage(car, out ScenePanel? panel, out D3DImageHost? host) || panel == null || host == null) return;
        ComponentTreeViewModel components = panel.Components;

        List<ComponentRowViewModel> all = [.. components.Roots.SelectMany(r => r.SelfAndDescendants())];
        ComponentRowViewModel? branch = all.FirstOrDefault(r => r.Children.Count > 0 && r.Parent != null);
        // Selected OUTSIDE the folded branch, on purpose. A row inside it would have to unfold it to be
        // visible at all — showing a selection is worth more than keeping a fold — so the two would not be
        // independent and the check would prove nothing.
        ComponentRowViewModel? chosen = branch == null
            ? null
            : all.Except(branch.SelfAndDescendants()).FirstOrDefault(r => !r.IsBroken);
        if (branch == null || chosen == null) { check("the car has a branch to fold", false, ""); return; }

        branch.IsExpanded = false;
        components.Select(chosen);
        ComponentId foldedId = branch.Id;
        ComponentId chosenId = chosen.Id;

        // What one scene change costs, because this runs on the dispatcher for every ordinary edit — a
        // duplicate, a delete, an undo — and a re-stitch nobody can feel is the difference between the tree
        // following the file and the tree being in the way.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) host.RaiseSceneChanged();
        clock.Stop();
        double perRestitch = clock.Elapsed.TotalMilliseconds / 10.0;
        sb.AppendLine($"  one scene change, re-stitch included: {perRestitch:F1} ms");
        check("a re-stitch is cheap enough to run on every scene change", perRestitch < 100,
            $"{perRestitch:F1} ms per scene change");

        ComponentRowViewModel? refolded = components.RowOf(foldedId);
        check("a branch folded away stays folded across a re-stitch",
            refolded is { IsExpanded: false },
            refolded == null ? "the row is gone" : $"{refolded.Name} expanded={refolded.IsExpanded}");
        check("…and the selected row is still the selected row",
            components.Selected?.Id == chosenId && components.Selected is { IsSelected: true },
            $"{components.Selected?.Name ?? "(nothing)"}");
        sb.AppendLine($"  folded \"{refolded?.Name}\", selected \"{components.Selected?.Name}\" — "
            + "both survived the re-stitch\n");
    }

    // ── a car the aggregate could only partly stitch ──

    /// <summary>
    /// The bone of one part is renamed out from under it, in the LIVE graph the panel is stitching against —
    /// which is what a rename made in Blender does. The car must still open and nothing may vanish.
    ///
    /// <para>
    /// Two rows come out of one, and both are right: the PART keeps its row and reads as broken, because its
    /// hash now resolves to nothing; and the bone, under its new name, is a bone carrying geometry that no
    /// part claims — which is the definition of a BARE component. The renamed panel is still a shootable
    /// part of the car, so showing it is the honest answer, not a leak.
    /// </para>
    /// <para>
    /// Naming the fault is ticket 06; what is asserted here is that the tree survives it.
    /// </para>
    /// </summary>
    private static void CheckBroken(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        sb.AppendLine("════ a car that only partly stitches still opens ════");
        if (!Stage(car, out ScenePanel? panel, out D3DImageHost? host)) return;
        if (panel!.Components.Car is not { } whole) { check("the car stitched", false, ""); return; }

        int was = panel.Components.Roots.SelectMany(r => r.SelfAndDescendants()).Count();
        CarComponent? part = whole.Components.FirstOrDefault(c =>
            !c.IsBare && c.BoneJoint >= 0 && c.PartType != 1);
        if (part == null) { check("the car has a part to break", false, ""); return; }

        FrameObjectModel? model = LiveModel(host!);
        if (model == null) { check("the staged car has a skinned model", false, ""); return; }
        HashName[] bones = model.GetSkeletonObject().BoneNames!;
        string original = bones[part.BoneJoint].String;
        bones[part.BoneJoint].String = original + "_gone";
        try
        {
            host!.RaiseSceneChanged();

            List<ComponentRowViewModel> rows =
                [.. panel.Components.Roots.SelectMany(r => r.SelfAndDescendants())];
            ComponentRowViewModel? still = rows.FirstOrDefault(r => r.Component.PartIndex == part.PartIndex);
            ComponentRowViewModel? renamed = rows.FirstOrDefault(r =>
                r.IsBare && r.Name == original + "_gone");

            check("a car whose bone was renamed out from under a part still opens",
                panel.Components.HasCar && rows.Count == was + 1,
                $"{rows.Count} rows, {was} before");
            check("…the part keeps its row, shown broken rather than dropped",
                still is { IsBroken: true },
                still == null ? "the row vanished" : $"{still.Name} broken={still.IsBroken}");
            // The other half of the same rename: the bone is still there, still carries geometry, and no part
            // claims it any more — so it is a bare component, which is the one thing it can honestly be.
            check("…and the bone under its new name is there as a bare component",
                renamed != null, renamed?.Name ?? $"no bare row named {original}_gone");

            // Selecting the broken row: there is no frame to hand the viewport, so the viewport's selection
            // is CLEARED rather than left where it was. The property tabs, the gizmo and Delete all act on
            // that selection, and leaving them pointed at the last frame while the tree says this row is
            // selected is how a modder deletes the wrong thing.
            if (renamed != null && still != null)
            {
                panel.Components.Select(renamed);
                panel.Components.Select(still);
                check("selecting a component whose bone is gone clears the viewport's selection",
                    host.SelectedNode == null && ReferenceEquals(panel.Components.Selected, still)
                    && still.IsSelected,
                    $"viewport={host.SelectedNode?.Name ?? "(nothing)"}, "
                    + $"tree={panel.Components.Selected?.Name ?? "(nothing)"}");
            }
            sb.AppendLine($"  renamed \"{original}\" ({part.Kind}) away: "
                + $"{panel.Components.Car?.Faults.Count ?? 0} faults, the part still listed as "
                + $"\"{still?.Name}\" and the bone as bare \"{renamed?.Name}\" — {rows.Count} rows");
        }
        finally
        {
            bones[part.BoneJoint].String = original;
            host!.RaiseSceneChanged();
        }
        sb.AppendLine();
    }

    // ── the diagnosis a car opens with ──

    /// <summary>
    /// What the aggregate could not stitch, on the panel: a strip above the tree that says how many faults
    /// there are and opens the list, and a mark on every component row that carries one.
    ///
    /// <para>
    /// Both are needed and neither replaces the other. A fault about a PREFAB ROW has no component to sit on
    /// and exists only in the list; a fault about a component is what a modder trips over on the row without
    /// having gone looking. The focus car has exactly one of the first kind, which is why it is the case this
    /// starts from.
    /// </para>
    /// </summary>
    private static void CheckFaults(
        FileInfo car, string folder, Action<string, bool, string> check, StringBuilder sb)
    {
        sb.AppendLine("════ the diagnosis a car opens with ════");
        if (!Stage(car, out ScenePanel? panel, out D3DImageHost? host)) return;
        if (panel!.Components.Car is not { } whole) { check("the car stitched", false, ""); return; }

        IReadOnlyList<FaultRowViewModel> rows = panel.Components.Faults;
        sb.AppendLine($"  {car.Name}: {rows.Count} fault(s) — "
            + string.Join("; ", rows.Select(r => $"{r.Title}: {r.What}")));

        check("a car with something wrong with it says so above the tree, without being asked",
            panel.FaultStrip.Visibility == Visibility.Visible && rows.Count == whole.Faults.Count
            && rows.Count > 0,
            $"{rows.Count} rows, strip {panel.FaultStrip.Visibility}");
        // The focus car ships with its one fault, so the strip must NOT accuse it of anything: 41 of the
        // corpus's faults are how cars are written, and a band that says "did not fully stitch" over a stock
        // archive is how the whole diagnosis learns to be ignored.
        check("…and a car that merely ships odd is called odd, not broken",
            Equals(panel.FaultToggle.Content, panel.Components.FaultSummary)
            && !panel.Components.HasBreak
            && panel.Components.FaultSummary.Contains("do not line up", StringComparison.Ordinal),
            panel.Components.FaultSummary);
        // Shut on open: the count IS the diagnosis being available, and a list that unfolds itself takes the
        // tree's room to repeat something already read.
        check("the list itself is shut until it is asked for",
            panel.FaultScroll.Visibility == Visibility.Collapsed && !panel.Components.FaultsOpen,
            $"scroll {panel.FaultScroll.Visibility}");

        panel.Components.FaultsOpen = true;
        check("…and opening it opens it, through the view-model rather than through the button",
            panel.FaultScroll.Visibility == Visibility.Visible
            && ReferenceEquals(panel.FaultList.ItemsSource, rows),
            $"scroll {panel.FaultScroll.Visibility}, {rows.Count} bound");

        // The focus car's own fault is about a BONE, not about a component: the split table still seats
        // deform_top_roof and no piece of it has a face left. Nothing in the tree could carry it.
        FaultRowViewModel? homeless = rows.FirstOrDefault(r => !r.HasComponent);
        check("a fault with no component of its own is in the list, which is the only place it can be",
            homeless != null && rows.All(r => r.HasComponent || r.Fault.Component == default),
            homeless?.What ?? "every fault had a component");

        // ── and the other kind: a fault ON a row ──
        CarComponent? part = whole.Components.FirstOrDefault(c =>
            !c.IsBare && c.BoneJoint >= 0 && c.PartType != 1);
        FrameObjectModel? model = LiveModel(host!);
        if (part == null || model == null) { check("the car has a part to break", false, ""); return; }
        HashName[] bones = model.GetSkeletonObject().BoneNames!;
        string original = bones[part.BoneJoint].String;
        bones[part.BoneJoint].String = original + "_gone";
        try
        {
            host!.RaiseSceneChanged();

            ComponentRowViewModel? broken = panel.Components.Roots.SelectMany(r => r.SelfAndDescendants())
                .FirstOrDefault(r => r.Component.PartIndex == part.PartIndex);
            FaultRowViewModel? about = panel.Components.Faults.FirstOrDefault(r =>
                r.Component != null && r.Component.Id == broken?.Id);

            check("a fault about a component is marked on that component's own row",
                broken is { HasFault: true }
                && broken.FaultTip.Contains("no bone of this car", StringComparison.Ordinal),
                broken == null ? "the row vanished" : broken.FaultTip);
            check("…and the line in the list leads to it, so reading and looking are one gesture",
                about != null && Select(panel, about) && ReferenceEquals(panel.Components.Selected, broken),
                about == null ? "no line named the component" : about.What);
            // MORE, not exactly one more: breaking a part whose bone a door or axle row also names raises a
            // second fault about that row, so which car is in focus decides whether it is one or two. What is
            // asserted is that the shipped fault survived and the new one joined it.
            check("the strip counts the new fault as well as the one the car shipped with",
                panel.Components.Faults.Count > rows.Count
                && panel.Components.Faults.Any(r => r.Fault.Kind == CarFaultKind.ComponentBoneUnresolved)
                && panel.FaultStrip.Visibility == Visibility.Visible,
                $"{panel.Components.Faults.Count} faults, {rows.Count} before");
            // …and the strip stops calling this car merely odd: a bone that does not resolve is a failure no
            // shipped car raises, so the line goes from a note to a fault.
            check("…and a break makes the strip read as damage rather than as a note",
                panel.Components.HasBreak
                && panel.Components.FaultSummary.Contains("did not fully stitch", StringComparison.Ordinal),
                panel.Components.FaultSummary);
            sb.AppendLine($"  after renaming \"{original}\" away: "
                + $"{panel.Components.Faults.Count} faults, marked on {panel.Components.Roots
                    .SelectMany(r => r.SelfAndDescendants()).Count(r => r.HasFault)} row(s)");
        }
        finally
        {
            bones[part.BoneJoint].String = original;
            host!.RaiseSceneChanged();
        }

        // An archive that is not a car has no faults and no strip — the panel it shares with every other
        // archive must not grow a band that says nothing.
        var plain = new FileInfo(Path.Combine(folder, "cars_universal.sds"));
        if (plain.Exists && Stage(plain, out ScenePanel? other, out _) && other != null)
        {
            check("an archive that is not a car has no strip at all",
                !other.Components.HasFaults && other.FaultStrip.Visibility == Visibility.Collapsed
                && other.FaultList.ItemsSource == null,
                $"strip {other.FaultStrip.Visibility}");
        }
        sb.AppendLine();
    }

    /// <summary>Selecting a line of the diagnosis, the way the panel's own click handler does.</summary>
    private static bool Select(ScenePanel panel, FaultRowViewModel fault)
    {
        panel.Components.Select(fault);
        return true;
    }

    // ── a picture of it ──

    /// <summary>
    /// A snapshot of the panel with the component tree in it. Everything above says the rows are RIGHT; none
    /// of it says a segmented switch, a bone name and a kind all fit across a panel this narrow without one
    /// of the three being cut off, and that is the only question a layout can fail on its own terms.
    /// </summary>
    private static void Render(FileInfo car, Action<string, bool, string> check, StringBuilder sb)
    {
        try
        {
            if (!Stage(car, out ScenePanel? panel, out _) || panel == null) return;
            // Open, because the shut strip is one line and cannot fail: what a picture is for here is whether
            // a fault's title, its line and the tree still share a panel 340 px wide.
            panel.Components.FaultsOpen = true;

            const double width = 340, height = 900;
            panel.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20));
            for (int i = 0; i < 3; i++)
            {
                panel.Measure(new Size(width, height));
                panel.Arrange(new Rect(0, 0, width, height));
                panel.UpdateLayout();
            }

            // The tree fills in through the dispatcher, and a snapshot taken before it has drawn catches an
            // empty box — let the clock run before the bitmap is taken.
            DateTime until = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < until)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.Render);
                Thread.Sleep(16);
            }
            panel.UpdateLayout();

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(panel);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            string path = Path.Combine(Path.GetTempPath(), "illusion_component_tree.png");
            using (FileStream file = File.Create(path)) encoder.Save(file);

            check("the component tree draws", new FileInfo(path).Length > 0, path);
            sb.AppendLine($"rendered {car.Name} -> {path}");
        }
        catch (Exception ex)
        {
            // A render that cannot run is not the layout being wrong — say so and leave the asserts above.
            sb.AppendLine("render skipped — " + ex.Message);
        }
    }

    // ── staging ──

    /// <summary>
    /// The real scene panel around one staged archive — the same shape the resource editor builds: an
    /// archive's frame roots under an SDS wrapper, and a panel attached to the viewport that holds them.
    /// No window is shown, so no D3D surface is ever created.
    /// </summary>
    private static bool Stage(FileInfo archive, out ScenePanel? panel, out D3DImageHost? host)
    {
        panel = null;
        host = null;
        if (!archive.Exists) return false;

        (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(archive);
        if (document == null || roots.Count == 0) return false;

        var viewport = new D3DImageHost { IsMapViewport = false };
        SceneNode folder = viewport.Tree.GetOrCreateFolder("probe");
        var sdsNode = new SceneNode(archive.Name, "Sds", true);
        var frameNode = new SceneNode("FrameResource", "FrameResource", true) { Source = document };
        var leaves = new List<SceneNode>();
        foreach (SdsFrameNode root in roots) frameNode.AddChild(Viewport.SceneTree.BuildSceneTree(root, leaves));
        sdsNode.AddChild(frameNode);
        folder.AddChild(sdsNode);
        viewport.Tree.RebuildStageRoots();

        var built = new ScenePanel();
        built.Attach(viewport);
        // The archive arriving is what the panel listens for; the probe builds the tree by hand, so it has to
        // say so itself.
        viewport.RaiseSceneChanged();

        panel = built;
        host = viewport;
        return true;
    }

    // Every bone row of the RAW tree, by name — the reading the component tree is checked against.
    private static Dictionary<string, SceneNode> BoneRows(D3DImageHost host)
    {
        var found = new Dictionary<string, SceneNode>(StringComparer.Ordinal);
        void Walk(SceneNode node)
        {
            if (node.Source is BoneNodeAdapter bone && bone.BoneName.Length > 0)
            {
                found.TryAdd(bone.BoneName, node);
            }
            foreach (SceneNode child in node.Children) Walk(child);
        }
        foreach (SceneNode root in host.Tree.Roots) Walk(root);
        return found;
    }

    // The row of the car's own skinned model — what a click on its geometry resolves to.
    private static SceneNode? ModelRow(D3DImageHost host)
    {
        SceneNode? found = null;
        void Walk(SceneNode node)
        {
            if (found != null) return;
            if (node.Source is FrameNodeAdapter { Frame: FrameObjectModel }) { found = node; return; }
            foreach (SceneNode child in node.Children) Walk(child);
        }
        foreach (SceneNode root in host.Tree.Roots) Walk(root);
        return found;
    }

    private static FrameObjectModel? LiveModel(D3DImageHost host) =>
        ModelRow(host)?.Source is FrameNodeAdapter { Frame: FrameObjectModel model } ? model : null;
}
