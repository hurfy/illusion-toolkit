using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Scene;
using Illusion.ViewModels;
using Illusion.Viewport;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Probe of the Object-tab parent picker against the WPF click sequence. The bug it locks down:
/// selection commits on mouse-DOWN, the reparent used to rebuild the candidate list (new ItemsSource,
/// search filter cleared) synchronously inside that push, and the mouse-UP then landed on whatever row
/// the UNFILTERED list put under the cursor — a second, unintended reparent to an arbitrary node.
/// The probe replays the exact push sequence at the binding seam.
/// </summary>
internal static class PickerProbes
{
    internal static void RunReparentPickerProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_reparent_picker.txt");
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
            MafiaMaterials.EnsureLoaded();
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            if (!sds.Exists) { sb.AppendLine($"no such district: {sds.FullName}"); return; }

            // The app's real tree shape: folder → Sds → FrameResource(document) → scenes → frames.
            (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(sds);
            if (document == null || roots.Count == 0) { sb.AppendLine("no document/roots"); return; }

            var host = new D3DImageHost();
            SceneNode folder = host.Tree.GetOrCreateFolder("probe");
            var sdsNode = new SceneNode(district + ".sds", "Sds", true);
            var frNode = new SceneNode("FrameResource", "FrameResource", true) { Source = document };
            var meshLeaves = new List<SceneNode>();
            foreach (SdsFrameNode r in roots) frNode.AddChild(SceneTree.BuildSceneTree(r, meshLeaves));
            sdsNode.AddChild(frNode);
            folder.AddChild(sdsNode);

            // The app's selection→panel wiring (MainWindow.OnSelectionChanged does exactly this).
            var vm = new SelectionViewModel(host);
            host.SelectionChanged += () => vm.SetNode(host.SelectedNode);

            // A frame object to reparent and a scene folder to send it to (any pair works — the bug is
            // in the picker plumbing, not the object).
            SceneNode? objectNode = meshLeaves.FirstOrDefault(l => l.Source is IFrameNode);
            if (objectNode == null) { sb.AppendLine("no mesh leaf"); return; }
            vm.SetNode(objectNode);
            Check("Picker is available for a frame object", vm.CanReparent);

            List<ParentOption> candidates = View(vm);
            ParentOption? target = candidates.FirstOrDefault(o =>
                o.Node.Source is IFrameScene && !ReferenceEquals(o.Node, objectNode.Parent));
            if (target == null) { sb.AppendLine("no scene folder candidate"); return; }
            string sceneName = target.Node.Name;
            sb.AppendLine($"object '{objectNode.Name}' → scene '{sceneName}'\n");

            // 1) The user types the scene's name — the list filters down.
            vm.ParentSearchText = sceneName;
            List<ParentOption> filtered = View(vm);
            int clickIndex = filtered.FindIndex(o => ReferenceEquals(o.Node, target.Node));
            Check("Search narrows the list to the scene", clickIndex >= 0, $"{filtered.Count} rows");

            // 2) Mouse-DOWN: WPF pushes the clicked option through SelectedParent → the reparent runs.
            object? viewBefore = vm.ParentCandidatesView;
            vm.SelectedParent = filtered[clickIndex];
            Check("Reparent lands on the picked scene", ReferenceEquals(objectNode.Parent, target.Node),
                objectNode.Parent?.Name ?? "null");

            // 3) THE RACE WINDOW: the candidate view must SURVIVE the click — swapping it here is what
            // put a different row under the still-pressed mouse button.
            Check("Candidate view is not swapped mid-click",
                ReferenceEquals(vm.ParentCandidatesView, viewBefore));
            Check("Search filter survives the click", vm.ParentSearchText == sceneName, vm.ParentSearchText);

            // 4) Mouse-UP: WPF re-commits whatever row sits at the SAME visual position now. With the
            // view intact that is the same row (benign); with the old bug it was an arbitrary node of
            // the unfiltered list — replay that worst case and require the parent to stay put.
            List<ParentOption> after = View(vm);
            ParentOption upRow = after[Math.Min(clickIndex, after.Count - 1)];
            vm.SelectedParent = upRow;
            Check("Mouse-up on the settled list does not re-reparent",
                ReferenceEquals(objectNode.Parent, target.Node), objectNode.Parent?.Name ?? "null");

            // A deliberate later choice must still be honored: the user clears the search (a fresh
            // gesture, not ItemsSource churn) and picks a different scene.
            vm.ParentSearchText = "";
            ParentOption? other = View(vm).FirstOrDefault(o =>
                !ReferenceEquals(o.Node, target.Node) && !ReferenceEquals(o.Node, objectNode.Parent)
                && o.Node.Source is IFrameScene);
            Check("Another scene is offered after clearing the search", other != null);
            if (other != null)
            {
                vm.SelectedParent = other;
                Check("A deliberate later pick still reparents", ReferenceEquals(objectNode.Parent, other.Node),
                    objectNode.Parent?.Name ?? "null");
            }

            // 5) The picker ends the interaction displaying the node's real parent.
            Check("Picker shows the real parent afterwards",
                vm.SelectedParent != null && ReferenceEquals(vm.SelectedParent.Node, objectNode.Parent));

            // 6) Undo restores the original parent and the picker resyncs without a view swap.
            object? viewBeforeUndo = vm.ParentCandidatesView;
            while (host.History.CanUndo) host.Undo();
            Check("Undo restores the original tree parent", !ReferenceEquals(objectNode.Parent, target.Node));
            Check("Undo resyncs the picker without swapping the view",
                ReferenceEquals(vm.ParentCandidatesView, viewBeforeUndo));

            sb.Insert(0, $"REPARENT PICKER PROBE ({district}): {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "REPARENT PICKER PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }

        static List<ParentOption> View(SelectionViewModel vm) =>
            vm.ParentCandidatesView?.Cast<ParentOption>().ToList() ?? new List<ParentOption>();
    }
}
