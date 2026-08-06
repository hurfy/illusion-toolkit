using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Scene;

namespace Illusion.Viewport;

/// <summary>
/// The per-piece hit boxes of the skinned models on the stage — the volumes that decide whether a bullet is
/// tested against a piece's triangles at all. Draws them, and rebuilds them on demand.
///
/// <para>
/// <b>Why these are drawn as SPHERES.</b> The box on disk is turned, and the turn lives in a word no reading
/// has cracked, so no honest wireframe can show its faces. What can be shown truthfully is the region the
/// box vouches for: a sphere about its centre. For a box this toolkit wrote that is exact — the size is
/// written equal on all three axes precisely so the turn stops mattering. For a shipped one it is the
/// smallest sphere that certainly holds it. Either way the question a modder actually asks — "is the thing I
/// welded on inside one of these" — is answered without a single invented number.
/// </para>
/// </summary>
internal sealed class HitBoxController
{
    private readonly D3DImageHost _host;
    private bool _show;

    internal HitBoxController(D3DImageHost host) => _host = host;

    /// <summary>Metres per raw unit — the same quantum the builder writes with.</summary>
    private const float Quantum = 10f / 32768f;

    /// <summary>Turns the layer on or off and redraws.</summary>
    internal bool Show
    {
        get => _show;
        set
        {
            if (_show == value) return;
            _show = value;
            Redraw();
        }
    }

    /// <summary>Rebuilds the wireframe from the models now on the stage. Cheap enough to call on any change:
    /// a car is under two hundred spheres and the whole layer is one buffer upload.</summary>
    internal void Redraw()
    {
        if (_host.Rnd is not { } renderer) return;
        renderer.ShowHitBoxes = _show;
        if (!_show) { renderer.SetHitBoxLines([], []); return; }

        var lines = new List<Vector3>();
        var colors = new List<Vector4>();
        Escapes = 0;
        foreach ((FrameObjectModel model, Matrix4x4 world) in Models())
        {
            DecodedMesh? mesh = SdsMeshLoader.DecodeLod(model, 0);
            int ordinal = 0;
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in Pieces(model))
            {
                FrameObjectModel.HitBoxInfo[] boxes = model.HitBoxes ?? [];
                if (ordinal >= boxes.Length) break;
                FrameObjectModel.HitBoxInfo box = boxes[ordinal++];

                Vector3 centre = new Vector3(
                    Signed(box.Position.S1), Signed(box.Position.S2), Signed(box.Position.S3)) * Quantum;
                // The sphere that CIRCUMSCRIBES the box: an oriented box of half-extents (a,b,c) reaches its
                // farthest corner at |(a,b,c)| from its centre, whichever way it is turned. Taking the largest
                // axis instead would draw a sphere smaller than the box, and the red test below would then
                // accuse pieces that are perfectly fine.
                float radius = new Vector3(box.Size.S1, box.Size.S2, box.Size.S3).Length() * Quantum;
                if (radius <= 0f) continue;

                // Whether this piece's OWN geometry escapes its OWN box — the only question the layer can
                // answer without knowing the turn, and the only one worth asking.
                //
                // The test is one-sided ON PURPOSE. A vertex outside the sphere that CIRCUMSCRIBES the box is
                // outside the box whichever way the box is turned, so red is a fact: those triangles cannot
                // be shot. A vertex inside the sphere proves nothing — the real box is flatter than the
                // sphere — so blue means "nothing provably wrong here", never "this is fine".
                bool escapes = mesh != null && Escaping(mesh, piece, centre, radius);
                if (escapes) Escapes++;

                int before = lines.Count;
                AppendSphere(lines, Vector3.Transform(centre, world), radius, world);
                Vector4 tint = escapes ? new Vector4(1f, 0.35f, 0.3f, 1f) : new Vector4(0.45f, 0.85f, 1f, 1f);
                for (int i = before; i < lines.Count; i++) colors.Add(tint);
            }
        }
        renderer.SetHitBoxLines(lines, colors);
    }

    /// <summary>Drops the wireframe — for a scene reset, where the models are going away.</summary>
    internal void Forget()
    {
        if (_host.Rnd is { } renderer) renderer.ClearHitBoxes();
    }

    /// <summary>
    /// How many skinned models the selection reaches — what "Rebuild hit boxes" would recompute. Zero
    /// disables the command, so a selection that holds no model says so in the menu instead of failing on
    /// the click.
    /// </summary>
    internal int RebuildTargetCount() => SelectedModels().Count;

    /// <summary>
    /// Recomputes the selected models' hit boxes from their own LOD 0 geometry, as one undoable edit.
    ///
    /// <para>
    /// A geometry push does this by itself, so this exists for the paths that do not go through one — a
    /// mesh imported, deleted or scaled by other means, and any car whose boxes were already stale when it
    /// was opened. Rebuilding a model that needed nothing is harmless: the result is the boxes its geometry
    /// implies either way.
    /// </para>
    /// </summary>
    internal void RebuildSelected()
    {
        List<(SceneNode Node, FrameObjectModel Model)> targets = SelectedModels();
        if (targets.Count == 0)
        {
            _host.RaiseNotice("select a skinned model first — hit boxes belong to the pieces of one");
            return;
        }

        var edits = new List<IEditAction>();
        int failed = 0;
        foreach ((SceneNode node, FrameObjectModel model) in targets)
        {
            FrameObjectModel.HitBoxInfo[] before = model.HitBoxes ?? [];
            FrameObjectModel.HitBoxInfo[]? built = HitBoxBuilder.Compute(model);
            // The count is an invariant of the file — one box per piece. A walk that disagrees is a walk
            // this model does not answer to, and writing it would mispair every piece after the first
            // difference. Same refusal HitBoxBuilder.Rebuild makes.
            if (built == null || built.Length != before.Length) { failed++; continue; }

            var edit = new HitBoxRebuildEdit(model, before, built);
            edit.Redo();
            edits.Add(edit);
            _host.Persistence.MarkFrameModified(node);
        }

        if (edits.Count == 0)
        {
            _host.RaiseNotice("hit boxes not rebuilt — this model's geometry would not decode", isError: true);
            return;
        }

        _host.Editing.History.Push(edits.Count == 1 ? edits[0] : new CompositeEdit([.. edits]));
        Redraw();
        _host.RaiseNotice(failed == 0
            ? $"rebuilt hit boxes on {edits.Count} model(s) — Ctrl+Z restores the originals"
            : $"rebuilt hit boxes on {edits.Count} model(s); {failed} would not decode");
    }

    // The models the selection reaches: the one on a selected node, plus every one beneath it, so selecting
    // a car's holder covers the car without hunting for the skinned row. Only models that already carry
    // boxes — nothing here mints a box array a model never had.
    private List<(SceneNode Node, FrameObjectModel Model)> SelectedModels()
    {
        var found = new List<(SceneNode, FrameObjectModel)>();
        var seen = new HashSet<FrameObjectModel>();
        foreach (SceneNode node in _host.Selection.Selected) Collect(node, seen, found);
        return found;
    }

    private static void Collect(
        SceneNode node, HashSet<FrameObjectModel> seen, List<(SceneNode, FrameObjectModel)> found)
    {
        if (node.Source is FrameNodeAdapter { Frame: FrameObjectModel model }
            && model.HitBoxes is { Length: > 0 } && seen.Add(model))
        {
            found.Add((node, model));
        }
        foreach (SceneNode child in node.Children) Collect(child, seen, found);
    }

    // Every skinned model on the stage, with the matrix that puts its geometry into the world. Walked from
    // the tree rather than from the documents, because the matrix is what the TREE knows: a car standing on
    // a district is placed by its row, not by anything in the archive.
    private IEnumerable<(FrameObjectModel Model, Matrix4x4 World)> Models()
    {
        var seen = new HashSet<FrameObjectModel>();
        foreach (SceneNode root in _host.Tree.Roots)
        {
            foreach ((FrameObjectModel model, Matrix4x4 world) in Under(root, seen)) yield return (model, world);
        }
    }

    private static IEnumerable<(FrameObjectModel Model, Matrix4x4 World)> Under(
        SceneNode node, HashSet<FrameObjectModel> seen)
    {
        if (node.Source is FrameNodeAdapter { Frame: FrameObjectModel model } adapter
            && model.HitBoxes is { Length: > 0 } && seen.Add(model))
        {
            yield return (model, adapter.WorldTransform);
        }
        foreach (SceneNode child in node.Children)
        {
            foreach ((FrameObjectModel found, Matrix4x4 world) in Under(child, seen)) yield return (found, world);
        }
    }

    /// <summary>How many pieces were drawn RED at the last redraw — pieces whose own geometry provably sits
    /// outside their own box, and so cannot be shot. Shown beside the switch, because a red sphere in a cloud
    /// of a hundred and eighty blue ones is not something anyone spots by looking.</summary>
    internal int Escapes { get; private set; }

    // The pieces of a model, in the flat split-then-piece order its boxes are stored in.
    private static IEnumerable<FrameObjectModel.BlendMeshSplitInfo> Pieces(FrameObjectModel model)
    {
        foreach (FrameObjectModel.WeightedByMeshSplit split in model.BlendMeshSplits ?? [])
        {
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? []) yield return piece;
        }
    }

    // Whether any vertex of this piece lies outside the sphere that circumscribes its box. One-sided: true
    // means those triangles are certainly untestable, false means only that nothing was proven.
    private static bool Escaping(
        DecodedMesh mesh, FrameObjectModel.BlendMeshSplitInfo piece, Vector3 centre, float radius)
    {
        float limit = radius + 1e-3f;
        foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
        {
            foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
            {
                int to = Math.Min(range.StartIndex + (range.NumFaces * 3), mesh.Indices.Length);
                for (int i = range.StartIndex; i < to; i++)
                {
                    uint v = mesh.Indices[i];
                    if (v < mesh.Positions.Length && (mesh.Positions[v] - centre).Length() > limit) return true;
                }
            }
        }
        return false;
    }

    private static float Signed(ushort raw) => raw >= 32768 ? raw - 65536 : raw;

    // Three rings rather than a shaded ball: a wireframe sphere has to stay readable when a hundred of them
    // overlap, and rings are what an editor's viewport draws for a radius everywhere else.
    private static void AppendSphere(List<Vector3> lines, Vector3 centre, float radius, Matrix4x4 world)
    {
        const int Segments = 16;
        // The rings follow the model's own axes, so a car's boxes read as belonging to the car rather than
        // to the world when it stands at an angle.
        Vector3 ax = Vector3.Normalize(new Vector3(world.M11, world.M12, world.M13)) * radius;
        Vector3 ay = Vector3.Normalize(new Vector3(world.M21, world.M22, world.M23)) * radius;
        Vector3 az = Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33)) * radius;

        foreach ((Vector3 u, Vector3 v) in new[] { (ax, ay), (ay, az), (az, ax) })
        {
            for (int i = 0; i < Segments; i++)
            {
                float a = i * MathF.Tau / Segments;
                float b = (i + 1) * MathF.Tau / Segments;
                lines.Add(centre + (u * MathF.Cos(a)) + (v * MathF.Sin(a)));
                lines.Add(centre + (u * MathF.Cos(b)) + (v * MathF.Sin(b)));
            }
        }
    }
}
