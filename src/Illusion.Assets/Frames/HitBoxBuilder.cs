using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.Frames;

/// <summary>
/// The per-PIECE hit boxes a skinned model carries, rebuilt from its geometry.
///
/// <para>
/// These boxes are what lets a car be shot. Proven in game (2026-08-05) on an otherwise untouched car,
/// changing only this field: collapse every box to zero and the WHOLE car stops registering bullets — they
/// pass through it into the ground; open every box wide and a cube welded onto the hood, which had never
/// taken a single hit, starts taking them. It is a PRE-FILTER in front of a real per-triangle raycast, not
/// the hit volume itself: with the boxes wide open, shots into the air beside a panel still register
/// nothing, hits land where the metal is, and a bullet still punches through the bumper into the body
/// behind it. So a box that is too big costs a few extra triangle tests, and a box that is too small loses
/// hits outright — which is the whole reason this errs large.
/// </para>
/// <para>
/// Nothing in the toolkit rebuilt them, and <c>RebuildMeshSplits</c> deliberately keeps the pieces as they
/// ship and moves only the face RANGES. So geometry welded onto a car joined a piece whose box had been
/// computed before that geometry existed, fell outside it, and was never tested.
/// </para>
/// <para>
/// <b>How a box encodes</b>, solved rather than guessed (<c>--probe-hitbox-blowup … Fit</c>, 158 pieces of
/// ascot_baileys200_pha): pairing each box with the vertices of its own piece and fitting the raw word
/// against the true metre by least squares gives, per axis, <c>metre = raw × 0.0003038 / 0.0003044 /
/// 0.0003031</c> with R² <c>0.9988 / 0.9998 / 0.9966</c> and an intercept of zero — reading Position as a
/// SIGNED int16. That slope is <see cref="Quantum"/> to within half a percent. So <b>Position is the
/// piece's own bounding-box CENTRE, signed, in model space</b>.
/// </para>
/// <para>
/// <b>Size is not read per axis, and must not be written per axis.</b> The same fit against per-axis
/// half-extents collapses to R² 0.86 / 0.52 / 0.40 — the scatter of a box whose axes are not the model's.
/// The box is turned, and the turn lives in the opaque <c>Unk</c> word that no reading has cracked (the
/// best is "one of the 24 axis-aligned turns, chosen per piece" at 92.7 %, which is a fitted upper bound
/// rather than a formula; and the commonest value covers 6384 boxes across all 88 cars and even human
/// characters, so it is not a per-piece rotation code either). This builder therefore never needs it:
/// writing the piece's bounding-SPHERE radius on all three axes gives a box that contains the piece under
/// EVERY possible turn. The existing <c>Unk</c> is carried through untouched.
/// </para>
/// </summary>
public static class HitBoxBuilder
{
    /// <summary>Metres per raw unit, for both the signed centre and the unsigned size.</summary>
    private const float Quantum = 10f / 32768f;

    /// <summary>What a raw axis can hold — about 20 m, an order of magnitude more than any car piece.</summary>
    private const float RawCeiling = 32767f;

    /// <summary>
    /// The boxes this model's own faces imply, one per split piece, in the flat split-then-piece order the
    /// file stores them in. Null when there is no split table or LOD 0 will not decode — a model with
    /// nothing to derive from keeps the boxes it has.
    /// </summary>
    public static FrameObjectModel.HitBoxInfo[]? Compute(FrameObjectModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        FrameObjectModel.WeightedByMeshSplit[] splits = model.BlendMeshSplits ?? [];
        if (splits.Length == 0) return null;

        // LOD 0 always, never the level a push happened to touch: there is one flat box array for the
        // model, and the face ranges it is walked through are LOD 0's index buffer.
        DecodedMesh? decoded = SdsMeshLoader.DecodeLod(model, 0);
        if (decoded?.Indices is not { Length: > 0 } indices) return null;
        Vector3[] positions = decoded.Positions;

        // Every box is built by COPYING the piece's own — which is how the opaque turn word survives, and
        // also the only way to make one at all (the empty constructors belong to the format assembly). A
        // model with no boxes has no pieces to give them to either: nothing here ever mints a piece.
        FrameObjectModel.HitBoxInfo[] existing = model.HitBoxes ?? [];
        if (existing.Length == 0) return null;
        var built = new List<FrameObjectModel.HitBoxInfo>(existing.Length);

        foreach (FrameObjectModel.WeightedByMeshSplit split in splits)
        {
            foreach (FrameObjectModel.BlendMeshSplitInfo piece in split.Data ?? [])
            {
                int ordinal = built.Count;
                if (ordinal >= existing.Length) return null; // more pieces than boxes — the walk is not this
                var box = new FrameObjectModel.HitBoxInfo(existing[ordinal]);

                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                bool any = false;
                foreach (FrameObjectModel.MiniMaterialBurst burst in piece.Data ?? [])
                {
                    foreach (FrameObjectModel.FacesBurst range in burst.Data ?? [])
                    {
                        int from = range.StartIndex;
                        int to = Math.Min(from + (range.NumFaces * 3), indices.Length);
                        for (int i = from; i < to; i++)
                        {
                            uint v = indices[i];
                            if (v >= positions.Length) continue;
                            min = Vector3.Min(min, positions[v]);
                            max = Vector3.Max(max, positions[v]);
                            any = true;
                        }
                    }
                }

                // A piece the rebuild left with no faces has nothing to derive from. Keeping what it had is
                // the same policy BoneBoundsBuilder uses for a bone no vertex touches — and it matters here,
                // because zeroing such a box would silently switch off whatever it still guards.
                if (!any)
                {
                    built.Add(box);
                    continue;
                }

                Vector3 centre = (min + max) * 0.5f;

                // The radius that contains the piece whichever way the box is turned. Half the diagonal, not
                // the largest half-extent: an oriented box of half-extent r on every axis contains the
                // sphere of radius r, and the sphere contains the piece.
                float radius = ((max - min) * 0.5f).Length();

                // Unk is left exactly as the copy brought it — the turn is what that field holds, we cannot
                // read it, and inventing one would aim the box somewhere nobody measured.
                Write(box.Position, centre.X, centre.Y, centre.Z);

                // Size is UNSIGNED — the same quantum, but the whole 16-bit range, so it is not written
                // through the signed path. Equal on all three axes: that is what makes the turn irrelevant.
                ushort r = (ushort)Math.Clamp(MathF.Round(radius / Quantum), 0f, ushort.MaxValue);
                box.Size.S1 = r;
                box.Size.S2 = r;
                box.Size.S3 = r;
                built.Add(box);
            }
        }

        return [.. built];
    }

    /// <summary>
    /// Recomputes the model's boxes in place. Silent about a model it cannot derive from — the caller is a
    /// geometry write, and refusing one because a box could not be rebuilt would be the wrong trade.
    /// </summary>
    public static bool Rebuild(FrameObjectModel model)
    {
        FrameObjectModel.HitBoxInfo[]? built = Compute(model);
        if (built == null) return false;

        // The count is an invariant of the file: one box per piece. A rebuild that produced a different
        // number would mean the walk disagrees with what shipped, and writing it would corrupt the pairing
        // for every piece after the first difference.
        if (model.HitBoxes is { Length: > 0 } had && had.Length != built.Length) return false;

        model.HitBoxes = built;
        // The file stores the block's SIZE beside it, sixteen bytes per piece, and the reader takes that as
        // the box count. Writing boxes without it would hand the reader a length that disagrees with them.
        model.RecomputeSplitCounters();
        return true;
    }

    /// <summary>Metres → the three raw words, signed and clamped, written into the triple in place. A
    /// coordinate past the field's reach is pinned rather than wrapped: a wrapped centre would put the box
    /// on the far side of the car.</summary>
    private static void Write(Short3 triple, float x, float y, float z)
    {
        triple.S1 = Raw(x);
        triple.S2 = Raw(y);
        triple.S3 = Raw(z);
    }

    private static ushort Raw(float metres)
    {
        float raw = MathF.Round(metres / Quantum);
        raw = Math.Clamp(raw, -RawCeiling, RawCeiling);
        return (ushort)(short)raw;
    }
}
