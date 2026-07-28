using Illusion.Assets.Adapters;
using Illusion.Domain.Properties;
using Illusion.Formats.Translokator;

namespace Illusion.Assets.Properties;

/// <summary>
/// Builds the per-type property panel groups for one city_crash placement — the crash analog of
/// <see cref="CollisionPropertyCatalog"/>. Position, rotation and scale are edited through the standard Object
/// (transform) tab, since the placement adapter is an <c>IFrameNode</c>; this catalog contributes the season
/// switch and the placement's own table metadata.
/// </summary>
internal static class TranslokatorPropertyCatalog
{
    public static IReadOnlyList<PropertyGroup> Build(TranslokatorInstanceAdapter node)
    {
        Instance instance = node.Instance;
        Formats.Translokator.Object owner = node.Owner;
        TranslokatorDocumentAdapter document = node.Document;
        bool seasonal = document.Twin != null;

        return new[]
        {
            new PropertyGroup
            {
                Title = "Crash object",
                IsTypeSpecific = true,
                Properties = new[]
                {
                    ReadOnlyText("Crash.Object", "Object", () => owner.Name.String),
                    BoolDesc("Crash.AllSeasons", "In all seasons", () => node.SeasonLinked,
                        seasonal ? v => node.SeasonLinked = v : null,
                        seasonal
                            ? "On: moving, scaling or deleting this object does the same in the other season's "
                              + "archive, so it only has to be placed once. Off: it is edited in the season you "
                              + "are looking at and left alone in the other one. Everything the game shipped "
                              + "stands in both seasons, so it starts on."
                            : "This archive has no seasonal counterpart, so there is nothing to keep in step."),
                    ReadOnlyText("Crash.Season", "Season", () => SeasonLabel(document)),
                    IntDesc("Crash.Id", "Placement id", () => instance.ID, null,
                        tip: "The table-wide handle of this copy. Unique within the archive, and shared with the "
                           + "same copy in the other season — which is how the two are paired."),
                    FloatDesc("Crash.GridMax", "Draw distance", () => owner.GridMax, null,
                        "How far away the game still draws this object. Shared by every copy of it, and it also "
                        + "decides which streaming grid counts them, so it is not edited per copy."),
                    FloatDesc("Crash.GridMin", "Fade distance", () => owner.GridMin, null,
                        "The near distance the object fades in over. Shared by every copy of it."),
                    ReadOnlyText("Crash.ActorType", "Actor type", () => ActorLabel(document, owner)),
                },
            },
        };
    }

    private static string SeasonLabel(TranslokatorDocumentAdapter document)
    {
        string stem = Path.GetFileNameWithoutExtension(document.SourceArchive.Name);
        return stem.EndsWith("_z", StringComparison.OrdinalIgnoreCase) ? "winter" : "summer";
    }

    // The group an object sits in names what the engine treats it as (a prop, a script anchor, a light…).
    private static string ActorLabel(TranslokatorDocumentAdapter document, Formats.Translokator.Object owner)
    {
        foreach (ObjectGroup group in document.Table.ObjectGroups)
        {
            if (group.Objects.Contains(owner)) return group.ActorType.ToString();
        }
        return "?";
    }

    // Descriptor factories — mirror the private helpers of the frame and collision catalogs.
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

    private static PropertyDescriptor FloatDesc(string id, string label, Func<float> get, Action<float>? set,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.Float,
            IsReadOnly = set == null,
            Tooltip = tip,
            Get = () => get(),
            Set = set == null ? null : v => set((float)v!),
        };

    private static PropertyDescriptor BoolDesc(string id, string label, Func<bool> get, Action<bool>? set,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.Bool,
            IsReadOnly = set == null,
            Tooltip = tip,
            Get = () => get(),
            Set = set == null ? null : v => set((bool)v!),
        };

    private static PropertyDescriptor ReadOnlyText(string id, string label, Func<string> get) => new()
    {
        Id = id,
        Label = label,
        Kind = PropertyKind.Text,
        IsReadOnly = true,
        Get = () => get(),
    };
}
