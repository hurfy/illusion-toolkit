using System.Numerics;
using Illusion.Formats.Collisions;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Collisions;

/// <summary>Outcome of a mint: the hash a placement should point at, plus the mesh that has to be added
/// to the file (null when an identical hull already existed and was reused).</summary>
/// <param name="Hash">The hull hash to point placements at; 0 when <paramref name="SkipReason"/> is set.</param>
/// <param name="Added">The mesh to append to <c>CollisionFile.Meshes</c>, or null if it is already there.</param>
/// <param name="SkipReason">Human-readable reason the mint could not happen, or null on success.</param>
public readonly record struct MintedHull(ulong Hash, CollisionMesh? Added, string? SkipReason);

/// <summary>
/// Derives a new collision hull from an existing one and gives it a stable identity.
/// <para>
/// A <c>CollisionInstance</c> is 37 bytes of Position / Rotation / Hash / Unk4 / Group with nowhere to put a
/// scale, so a scaled placement can only be expressed as a placement pointing at a DIFFERENT hull. This is the
/// piece that mints that hull.
/// </para>
/// <para>
/// The derived hash is a pure function of (source hash, quantized scale), so scaling the same hull by the same
/// amount twice — in one session or across runs — resolves to ONE mesh instead of growing the .col on every
/// edit. Producing the cooked bytes is the caller's job (a delegate), which keeps this layer independent of how
/// the blob is derived: an in-place scale patch today, a full re-cook later.
/// </para>
/// </summary>
public static class CollisionMeshMinter
{
    /// <summary>Scale is quantized to this many ticks per unit before hashing, so float noise from a gizmo drag
    /// cannot mint a near-duplicate hull per frame.</summary>
    private const float ScaleTicks = 10000f;

    /// <summary>Scales within this distance of 1 are treated as "no scale at all".</summary>
    private const float UnitEpsilon = 1e-4f;

    /// <summary>Whether a scale is close enough to identity that no derived hull is needed.</summary>
    public static bool IsIdentityScale(Vector3 scale) =>
        MathF.Abs(scale.X - 1f) <= UnitEpsilon
        && MathF.Abs(scale.Y - 1f) <= UnitEpsilon
        && MathF.Abs(scale.Z - 1f) <= UnitEpsilon;

    /// <summary>
    /// Stable identity for the hull derived from <paramref name="sourceHash"/> by <paramref name="scale"/>.
    /// Deterministic across sessions — that is what makes dedup work — and distinct from the source's own hash.
    /// </summary>
    public static ulong DeriveHash(ulong sourceHash, Vector3 scale)
    {
        var key = new byte[8 + 12];
        BitConverter.TryWriteBytes(key.AsSpan(0), sourceHash);
        BitConverter.TryWriteBytes(key.AsSpan(8), Quantize(scale.X));
        BitConverter.TryWriteBytes(key.AsSpan(12), Quantize(scale.Y));
        BitConverter.TryWriteBytes(key.AsSpan(16), Quantize(scale.Z));
        return Fnv64.Hash(key, 0, key.Length);
    }

    /// <summary>
    /// Resolves the hull for <paramref name="sourceHash"/> scaled by <paramref name="scale"/>: reuses an existing
    /// derived hull, or builds one via <paramref name="derive"/>. The result is NOT added to
    /// <paramref name="file"/> — the caller adds it as part of an undoable edit, so undo can take it back out.
    /// </summary>
    /// <param name="derive">Produces the derived cooked blob from the source blob and the scale. Returning null,
    /// or throwing any of the deriving failures (<see cref="InvalidOperationException"/>,
    /// <see cref="NotSupportedException"/>, <see cref="ArgumentException"/>,
    /// <see cref="CollisionDecodeException"/>), is reported as a skip rather than propagated — one unsupported
    /// scale or malformed hull must not tear down the edit.</param>
    public static MintedHull Mint(
        CollisionFile file, ulong sourceHash, Vector3 scale, Func<byte[], Vector3, byte[]?> derive)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(derive);

        if (IsIdentityScale(scale)) return new MintedHull(sourceHash, null, null);

        ulong hash = DeriveHash(sourceHash, scale);

        // Already minted (this session or a previous run) — reuse it. Note this also absorbs the astronomically
        // unlikely case of an FNV64 collision with an unrelated hull: we would reuse that hull rather than
        // corrupt it, which is wrong-looking but not destructive.
        foreach (CollisionMesh existing in file.Meshes)
            if (existing.Hash == hash) return new MintedHull(hash, null, null);

        CollisionMesh? source = null;
        foreach (CollisionMesh m in file.Meshes)
            if (m.Hash == sourceHash) { source = m; break; }

        if (source?.CookedMesh is not { Length: > 0 })
            return new MintedHull(0, null, $"no cooked hull for hash 0x{sourceHash:X16}");

        byte[]? derived;
        try
        {
            derived = derive(source.CookedMesh, scale);
        }
        // CollisionDecodeException derives straight from Exception, so it is NOT covered by the three
        // framework types — and it is exactly what CookedMeshScaler throws for a truncated or malformed blob.
        // Leaving it out turned one bad hull in a district into an unhandled exception at gizmo drag-end.
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
            or ArgumentException or CollisionDecodeException)
        {
            return new MintedHull(0, null, ex.Message);
        }
        if (derived is not { Length: > 0 }) return new MintedHull(0, null, "the hull could not be derived");

        var minted = new CollisionMesh { Hash = hash, CookedMesh = derived };
        // Sections are triangle RANGES over the cooked triangle list. Scaling moves vertices without touching
        // topology or triangle order, so the ranges carry over verbatim.
        foreach (CollisionSection s in source.Sections)
            minted.Sections.Add(new CollisionSection
            {
                Start = s.Start,
                NumEdges = s.NumEdges,
                Material = s.Material,
                Unk2 = s.Unk2,
            });

        return new MintedHull(hash, minted, null);
    }

    /// <summary>
    /// Resolves the hull for a freshly COOKED blob, identified by its content rather than by what it was
    /// derived from.
    /// <para>
    /// Scaling can name its result — source hull plus factor — but a reshape cannot: the same hull edited two
    /// different ways has no such name, and a reshape at scale 1 would collide with the source itself. Hashing
    /// the cooked bytes gives an identity that says what the hull IS, which works because the cooker is
    /// byte-deterministic: pushing the same geometry twice resolves to one mesh instead of growing the .col on
    /// every push.
    /// </para>
    /// <para>Like <see cref="Mint"/>, nothing is added to <paramref name="file"/> — the undoable edit does that,
    /// because only it can also take it back out.</para>
    /// </summary>
    public static MintedHull MintCooked(
        CollisionFile file, byte[] cooked, IReadOnlyList<CollisionSection> sections)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(cooked);
        ArgumentNullException.ThrowIfNull(sections);
        if (cooked.Length == 0) return new MintedHull(0, null, "the cooked hull is empty");

        ulong hash = Fnv64.Hash(cooked, 0, cooked.Length);
        foreach (CollisionMesh existing in file.Meshes)
            if (existing.Hash == hash) return new MintedHull(hash, null, null);

        var minted = new CollisionMesh { Hash = hash, CookedMesh = cooked };
        foreach (CollisionSection s in sections) minted.Sections.Add(s);
        return new MintedHull(hash, minted, null);
    }

    /// <summary>Whether no placement in the file references this hull — i.e. it is dead weight in the .col.</summary>
    public static bool IsOrphan(CollisionFile file, ulong hash)
    {
        foreach (CollisionInstance inst in file.Instances)
            if (inst.Hash == hash) return false;
        return true;
    }

    /// <summary>Removes a hull from the file. Used to collect a minted hull that undo has just orphaned, so an
    /// edit-then-undo leaves the .col exactly as it was found.</summary>
    public static bool RemoveMesh(CollisionFile file, ulong hash)
    {
        for (int i = 0; i < file.Meshes.Count; i++)
        {
            if (file.Meshes[i].Hash != hash) continue;
            file.Meshes.RemoveAt(i);
            return true;
        }
        return false;
    }

    private static int Quantize(float v) => (int)MathF.Round(v * ScaleTicks);
}
