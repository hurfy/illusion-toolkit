using Illusion.Assets.Adapters;
using Illusion.Domain.Properties;
using Illusion.Formats.Collisions;

namespace Illusion.Assets.Properties;

/// <summary>
/// Builds the per-type property panel groups for one collision <see cref="CollisionInstance"/> placement — the
/// collision analog of <see cref="FramePropertyCatalog"/>. Position + Rotation are edited through the standard
/// Object (transform) tab, since the instance adapter is an <c>IFrameNode</c>; this catalog contributes only the
/// collision-specific metadata (mesh hash, group, the preserved unknown). Every set delegate mutates the
/// underlying <c>CollisionInstance</c>, so the generic property-commit path persists it to the .col through the
/// owning <c>CollisionDocumentAdapter</c>.
/// </summary>
internal static class CollisionPropertyCatalog
{
    public static IReadOnlyList<PropertyGroup> Build(CollisionInstanceAdapter node)
    {
        CollisionInstance inst = node.Instance;
        CollisionDocumentAdapter document = node.Document;

        // Every setter repaints. Without this a Hash edit leaves the viewport drawing the previous hull, which
        // reads as "the edit did nothing" — and the same staleness makes an undo look like it was ignored.
        void Touch() => document.RenderDirty = true;

        return new[]
        {
            new PropertyGroup
            {
                Title = "Collision",
                IsTypeSpecific = true,
                Properties = new[]
                {
                    ULongHexDesc("Collision.Hash", "Mesh hash", () => inst.Hash,
                        v =>
                        {
                            // A hash with no mesh behind it makes the hull vanish from the viewport (both the
                            // scene builder and the ray-picker skip unresolvable placements) while saving a live
                            // dangling reference into the .col. Refuse it rather than corrupt the file silently.
                            if (document.MeshFor(v) is null) return;
                            inst.Hash = v;
                            Touch();
                        },
                        "FNV64 of the cooked collision mesh this placement instances. Must name a mesh that exists "
                        + "in this .col; the hull re-resolves after the district reloads."),
                    ByteDesc("Collision.Group", "Group", () => inst.Group, v => { inst.Group = v; Touch(); },
                        "Per-placement group byte. Its meaning is not known; shipped data uses 17 distinct values "
                        + "and never 0, but 128 covers 85% of all placements."),
                    IntDesc("Collision.Unk4", "Owner object", () => inst.Unk4, v => { inst.Unk4 = v; Touch(); },
                        min: -1,
                        tip: "Ordinal of the frame object this placement belongs to, or -1 for none. Measured over "
                           + "the whole game: it names the visible mesh standing at the placement (10113 of 10115 "
                           + "resolve to a single-mesh object, a median 0 m away). Whether the game validates the "
                           + "value has never been tested."),
                },
            },
        };
    }

    // Descriptor factories — mirror FramePropertyCatalog's private helpers (which are not visible here). Get/Set
    // box per PropertyKind; a null set makes the field read-only.
    private static PropertyDescriptor IntDesc(string id, string label, Func<int> get, Action<int>? set,
        long min = int.MinValue, long max = int.MaxValue, string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.Int,
            IsReadOnly = set == null,
            Tooltip = tip,
            Min = min,
            Max = max,
            Get = () => (long)get(),
            Set = set == null ? null : v => set((int)(long)v!),
        };

    private static PropertyDescriptor ByteDesc(string id, string label, Func<byte> get, Action<byte>? set,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.Int,
            IsReadOnly = set == null,
            Tooltip = tip,
            Min = 0,
            Max = 255,
            Get = () => (long)get(),
            Set = set == null ? null : v => set((byte)(long)v!),
        };

    private static PropertyDescriptor ULongHexDesc(string id, string label, Func<ulong> get, Action<ulong>? set,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.UInt64Hex,
            IsReadOnly = set == null,
            Tooltip = tip,
            Get = () => get(),
            Set = set == null ? null : v => set((ulong)v!),
        };
}
