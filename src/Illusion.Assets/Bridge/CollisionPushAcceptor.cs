using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Collisions;
using Illusion.Bridge.Payload;
using Illusion.Formats.Collisions;

namespace Illusion.Assets.Bridge;

/// <summary>
/// Turns a hull reshaped in Blender into a cooked collision mesh the game can load.
/// <para>
/// This is the whole Edit Mode path in one place: resolve which physics surface each face was painted with,
/// drop the triangles PhysX would choke on, group them by surface and write the <c>.col</c> sections, cook,
/// and mint the result under an identity derived from the cooked bytes. Nothing is written to the file — the
/// caller folds the minted hull into an undoable edit, because only that can take it back out again.
/// </para>
/// <para>
/// Every failure comes back as a sentence naming what to fix, never an exception: one refused hull in a push
/// must not cost the modder the other nineteen.
/// </para>
/// </summary>
public static class CollisionPushAcceptor
{
    /// <summary>Outcome of accepting one reshaped hull.</summary>
    /// <param name="Minted">The hull to point the placement at, or null when it was refused.</param>
    /// <param name="Refusal">Why it was refused, or null on success.</param>
    public readonly record struct Result(MintedHull? Minted, string? Refusal);

    /// <summary>
    /// Cooks and mints the pushed geometry for a placement of <paramref name="document"/>.
    /// </summary>
    public static Result TryAccept(CollisionDocumentAdapter document, CollisionObjectPayload pushed)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pushed);

        if (pushed.Positions.Length < 3 || pushed.LoopVertexIndices.Length < 3)
            return Refuse("the pushed hull has no geometry");

        ushort[]? surfaces = ResolveSurfaces(pushed, out string? surfaceRefusal);
        if (surfaces == null) return Refuse(surfaceRefusal!);

        (int[] indices, ushort[] perTriangle, string? filterRefusal) = DropUnusableTriangles(pushed, surfaces);
        if (filterRefusal != null) return Refuse(filterRefusal);

        CollisionSectionPlan? plan = CollisionSectionBuilder.TryBuild(indices, perTriangle, out string? planRefusal);
        if (plan == null) return Refuse(planRefusal!);

        CookResult cooked = PhysXCooker.Cook(pushed.Positions, plan.Value.TriangleIndices, plan.Value.SurfaceIds);
        if (cooked.Cooked == null) return Refuse(cooked.Refusal ?? "the hull could not be cooked");

        MintedHull minted = CollisionMeshMinter.MintCooked(document.Collision, cooked.Cooked, plan.Value.Sections);
        return minted.SkipReason != null ? Refuse(minted.SkipReason) : new Result(minted, null);
    }

    private static Result Refuse(string reason) => new(null, reason);

    /// <summary>
    /// Maps each face's material SLOT back to the raw PhysX surface id it was painted with.
    /// <para>
    /// A face whose slot names no known surface is refused rather than defaulted. Guessing would produce a hull
    /// that looks right and behaves wrong — the wrong footfall, the wrong impact, the wrong bullet decal — and
    /// nothing would surface it until someone walked there.
    /// </para>
    /// </summary>
    private static ushort[]? ResolveSurfaces(CollisionObjectPayload pushed, out string? refusal)
    {
        refusal = null;
        int triangles = pushed.LoopVertexIndices.Length / 3;

        if (pushed.FaceMaterials.Length == 0)
        {
            refusal = "the pushed hull carries no per-face surfaces";
            return null;
        }
        if (pushed.FaceMaterials.Length != triangles)
        {
            refusal = $"the pushed hull has {triangles} triangles but {pushed.FaceMaterials.Length} face surfaces";
            return null;
        }

        var surfaces = new ushort[triangles];
        for (int t = 0; t < triangles; t++)
        {
            int slot = pushed.FaceMaterials[t];
            if (slot < 0 || slot >= pushed.Materials.Count)
            {
                refusal = $"a face uses material slot {slot}, which the push did not describe";
                return null;
            }

            CollisionMaterialInfo material = pushed.Materials[slot];
            if (material.RawId < CollisionSectionBuilder.SectionMaterialBias)
            {
                refusal = $"material \"{material.Name ?? "slot " + slot}\" is not a collision surface — "
                    + "assign one of the COL materials to those faces";
                return null;
            }
            surfaces[t] = (ushort)material.RawId;
        }
        return surfaces;
    }

    /// <summary>
    /// Drops triangles with no area and exact duplicates, keeping the surface array in step.
    /// <para>
    /// The cooker refuses a mesh made entirely of these, and its way of refusing is a zero-byte file. Filtering
    /// here mirrors what the exporter already does on the way out, so a hull that round-trips untouched is not
    /// altered by passing through this path.
    /// </para>
    /// </summary>
    private static (int[] Indices, ushort[] Surfaces, string? Refusal) DropUnusableTriangles(
        CollisionObjectPayload pushed, ushort[] surfaces)
    {
        int triangles = surfaces.Length;
        var keptIndices = new List<int>(triangles * 3);
        var keptSurfaces = new List<ushort>(triangles);
        var seen = new HashSet<(int, int, int)>();

        for (int t = 0; t < triangles; t++)
        {
            int a = (int)pushed.LoopVertexIndices[t * 3];
            int b = (int)pushed.LoopVertexIndices[t * 3 + 1];
            int c = (int)pushed.LoopVertexIndices[t * 3 + 2];
            if (a < 0 || b < 0 || c < 0
                || a >= pushed.Positions.Length || b >= pushed.Positions.Length || c >= pushed.Positions.Length)
            {
                return (Array.Empty<int>(), Array.Empty<ushort>(),
                    $"triangle {t} refers to a vertex the push did not send");
            }
            if (a == b || b == c || a == c) continue;

            Vector3 pa = pushed.Positions[a], pb = pushed.Positions[b], pc = pushed.Positions[c];
            if (Vector3.Cross(pb - pa, pc - pa).LengthSquared() <= 0f) continue;

            // Order-insensitive key: the same three corners wound either way is the same face to PhysX.
            (int, int, int) key = Sorted(a, b, c);
            if (!seen.Add(key)) continue;

            keptIndices.Add(a);
            keptIndices.Add(b);
            keptIndices.Add(c);
            keptSurfaces.Add(surfaces[t]);
        }

        return keptIndices.Count == 0
            ? (Array.Empty<int>(), Array.Empty<ushort>(),
                "every triangle in the pushed hull has no area — nothing to cook")
            : (keptIndices.ToArray(), keptSurfaces.ToArray(), null);
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }
}
