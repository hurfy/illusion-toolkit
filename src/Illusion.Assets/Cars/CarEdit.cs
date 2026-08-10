using System.Numerics;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Mathematics;

namespace Illusion.Assets.Cars;

/// <summary>
/// Everything the affected structures held before (or after) one component-level intent, kept as BYTES rather
/// than as a recipe.
///
/// <para>
/// Undo restores this; it never runs the derivation backwards. Derivation is lossy in places — the hit-box
/// rule reproduces only 65.3 % of the boxes a car ships with — so reversing it would leave a car that differs
/// from the one the modder started with, in fields they never touched.
/// </para>
/// </summary>
public sealed class CarState
{
    internal CarState(
        byte[] prefab, IReadOnlyList<(string Path, byte[]? Bytes)> shapes,
        IReadOnlyList<CarFrameState> frames, FrameObjectModel.HitBoxInfo[]? hitBoxes)
    {
        Prefab = prefab;
        Shapes = shapes;
        FrameStates = frames;
        HitBoxes = hitBoxes;
    }

    internal byte[] Prefab { get; }

    /// <summary>The ItemDesc files this intent touched, with the bytes they held — or null for a file that
    /// did not exist, which is how an undo of an add knows to take it away again.</summary>
    internal IReadOnlyList<(string Path, byte[]? Bytes)> Shapes { get; }

    /// <summary>The frames it touched — a collision's mirror stub, a marker's Dummy or Point — each with
    /// everything needed to put it back exactly.</summary>
    internal IReadOnlyList<CarFrameState> FrameStates { get; }

    /// <summary>The model's per-piece hit boxes as they were. The whole array — it is sixteen bytes a piece,
    /// and a snapshot of part of it could not be put back without knowing which part.</summary>
    internal FrameObjectModel.HitBoxInfo[]? HitBoxes { get; }

    /// <summary>
    /// The frames this state says the graph holds, and the ones it says are gone — what a view over the
    /// aggregate needs to keep its own rows in step.
    ///
    /// <para>
    /// A frame the graph holds and the scene tree does not is one the modder can neither see nor drag, and
    /// dragging it is the whole of placing a marker; a row left behind for a frame that has gone acts on
    /// nothing. The aggregate does not know about rows and must not — this is the fact it can state.
    /// </para>
    /// </summary>
    public IReadOnlyList<CarFrameRef> Frames =>
        [.. FrameStates.Select(f => new CarFrameRef(f.Frame, f.Joint, f.Present))];
}

/// <summary>One frame an edit minted or took away: which it is, which joint it hangs off, and whether the
/// graph holds it in the state this came from.</summary>
public readonly record struct CarFrameRef(FrameObjectBase Frame, int Joint, bool Present);

/// <summary>
/// One frame the aggregate minted or took away, as it stood: whether the graph held it, where, on which
/// joint, and — the part that is easy to forget — how it hung in the frame graph.
///
/// <para>
/// The two parent slots are recorded because taking a frame OUT clears them, and one put back without them is
/// an orphan: <c>ParentIndex1</c> cascades the transform and <c>ParentIndex2</c> anchors the frame to a scene,
/// and a frame anchored to nothing loads and is invisible. An undo has to give back the archive that was
/// there, not one that merely has the same rows.
/// </para>
/// </summary>
/// <param name="Order">Where it sat among the graph's frames. A frame resource is written in the order the
/// frames are held in, so one taken out and put back at the END is an archive whose every frame after it has
/// moved — the same car, and not the same bytes. An undo owes the bytes.</param>
/// <param name="Attached">And where its reference sat in the model's attachment list, which is written in
/// its own order for the same reason.</param>
/// <param name="Bounds">A Dummy's own box, when the frame is one. It is the other half of where a climb box
/// IS — typing new corners on the row resizes the Dummy as well as moving it — so a snapshot that recorded
/// only the transform would put the row back and leave the frame the new size, which is a car the editor
/// draws one way and the game climbs another.</param>
internal sealed record CarFrameState(
    FrameObjectBase Frame, bool Present, Matrix4x4 Local, int Joint,
    FrameObjectBase? Parent, FrameObjectBase? Root, int Order, int Attached,
    BoundingBox? Bounds = null);

/// <summary>
/// One component-level intent, as the two states it moved between — which is what makes it ONE undo step
/// however many structures it touched: a prefab volume, an ItemDesc record, a manifest entry, a frame and a
/// handful of hit boxes.
/// </summary>
/// <param name="What">The one line to tell the modder.</param>
public sealed record CarEdit(string What, CarState Before, CarState After);
