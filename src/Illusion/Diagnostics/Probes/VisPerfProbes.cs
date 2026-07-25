using System.Diagnostics;
using System.IO;
using System.Text;
using Illusion.Scene;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Correctness and cost gate for SceneNode's cached visibility aggregate. Hiding/showing a big
/// container used to be O(n^2) — a recursive IsVisible getter plus a per-node PropertyChanged
/// bubble (measured ~154 ms freeze on arpatro's tree when the FrameResource eye was toggled).
/// This proves the aggregate is still correct after the cache, and that a container eye-toggle is
/// now near-linear. No game data or GPU needed — the tree is synthetic but shaped like a district's.
/// </summary>
internal static class VisPerfProbes
{
    internal static void RunVisPerfProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_visperf.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            // ── Correctness ─────────────────────────────────────────────────────────────
            // A scene folder → sub-groups → leaves, the layout a district FrameResource makes.
            (SceneNode root, List<SceneNode> leaves) = BuildTree(fanout: 8, depth: 3, leavesPerNode: 6);

            Check("a fresh tree is visible", root.IsVisible && leaves.TrueForAll(l => l.IsVisible),
                $"{leaves.Count} leaves");

            root.IsVisible = false;
            Check("hiding the root hides every leaf and the root aggregate",
                !root.IsVisible && leaves.TrueForAll(l => !l.IsVisible));

            root.IsVisible = true;
            Check("showing the root shows every leaf and the root aggregate",
                root.IsVisible && leaves.TrueForAll(l => l.IsVisible));

            // Partially-hidden container must still fully re-show — the trap the naive early-out hits.
            leaves[0].IsVisible = false;
            Check("hiding one leaf leaves the root aggregate visible (some descendant still on)",
                root.IsVisible);
            Check("a partially-hidden container reads visible", root.IsVisible);
            root.IsVisible = true;
            Check("re-showing a partially-hidden container restores every leaf",
                leaves.TrueForAll(l => l.IsVisible));

            // Hiding every leaf individually must flip the container aggregate to hidden.
            foreach (SceneNode leaf in leaves) leaf.IsVisible = false;
            Check("hiding all leaves flips the root aggregate to hidden", !root.IsVisible);
            leaves[0].IsVisible = true;
            Check("showing one leaf flips the root aggregate back to visible", root.IsVisible);

            // ── Cost: the getter must be O(1) in subtree size ───────────────────────────
            // Read the root eye many times on two trees of very different size; the per-read cost
            // must not scale with node count (a recursive getter would blow up on the big tree).
            (SceneNode small, _) = BuildTree(4, 3, 4);   // ~340 nodes
            (SceneNode big, _) = BuildTree(10, 4, 8);    // ~100k nodes
            double smallRead = TimeReads(small, 200_000);
            double bigRead = TimeReads(big, 200_000);
            double ratio = bigRead / Math.Max(smallRead, 1e-6);
            Check("the IsVisible getter is O(1) (big-tree read cost ~ small-tree)", ratio < 3.0,
                $"small {smallRead:F1} ns/read, big {bigRead:F1} ns/read, ratio {ratio:F2}");

            // ── Cost: a container toggle is near-linear, with the realized-binding re-read ──
            // Simulate what the WPF eye binding does: every node that raises IsVisible has its row
            // re-read. Sum the wall-clock of one hide+show over a district-sized tree.
            foreach (int nodes in new[] { 2545, 5225 })
            {
                (SceneNode tree, _) = BuildBalanced(nodes);
                int reReads = 0;
                Subscribe(tree, () => { bool _ = tree.IsVisible; reReads++; });
                var sw = Stopwatch.StartNew();
                tree.IsVisible = false;
                tree.IsVisible = true;
                sw.Stop();
                Check($"toggle on a ~{nodes}-node tree is fast", sw.Elapsed.TotalMilliseconds < 20,
                    $"{sw.Elapsed.TotalMilliseconds:F1} ms, {reReads} re-reads");
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"VISPERF PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    // A container tree: each internal node has `fanout` children down to `depth`, and every node
    // also carries `leavesPerNode` leaf children. Returns the root and the flat leaf list.
    private static (SceneNode Root, List<SceneNode> Leaves) BuildTree(int fanout, int depth, int leavesPerNode)
    {
        var leaves = new List<SceneNode>();
        SceneNode root = new("root", "FrameResource", isContainer: true);
        Grow(root, fanout, depth, leavesPerNode, leaves);
        return (root, leaves);
    }

    private static void Grow(SceneNode node, int fanout, int depth, int leavesPerNode, List<SceneNode> leaves)
    {
        for (int i = 0; i < leavesPerNode; i++)
        {
            var leaf = new SceneNode($"leaf{i}", "Mesh", isContainer: false);
            node.AddChild(leaf);
            leaves.Add(leaf);
        }
        if (depth <= 0) return;
        for (int i = 0; i < fanout; i++)
        {
            var child = new SceneNode($"grp{i}", "Frame", isContainer: true);
            node.AddChild(child);
            Grow(child, fanout, depth - 1, leavesPerNode, leaves);
        }
    }

    // A roughly balanced tree of ~target nodes (binary-ish), for a size-controlled toggle timing.
    private static (SceneNode Root, int Count) BuildBalanced(int target)
    {
        SceneNode root = new("root", "FrameResource", isContainer: true);
        int count = 1;
        var frontier = new Queue<SceneNode>();
        frontier.Enqueue(root);
        while (count < target)
        {
            SceneNode parent = frontier.Dequeue();
            for (int i = 0; i < 4 && count < target; i++)
            {
                bool container = count * 3 < target; // upper nodes are containers, tail is leaves
                var child = new SceneNode($"n{count}", container ? "Frame" : "Mesh", container);
                parent.AddChild(child);
                if (container) frontier.Enqueue(child);
                count++;
            }
            if (frontier.Count == 0) frontier.Enqueue(root);
        }
        return (root, count);
    }

    private static double TimeReads(SceneNode node, int iterations)
    {
        bool sink = false;
        for (int i = 0; i < 5000; i++) sink ^= node.IsVisible; // warm
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) sink ^= node.IsVisible;
        sw.Stop();
        GC.KeepAlive(sink);
        return sw.Elapsed.TotalMilliseconds * 1e6 / iterations; // ns per read
    }

    private static void Subscribe(SceneNode node, Action onVisibilityRaised)
    {
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SceneNode.IsVisible)) onVisibilityRaised();
        };
        foreach (SceneNode c in node.Children) Subscribe(c, onVisibilityRaised);
    }
}
