using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Domain.Properties;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.Properties;

/// <summary>
/// Property panel groups for one actor: what it is and what it is linked to. The transform is not here — the
/// adapter is an <c>IFrameNode</c>, so the standard Object tab and the gizmo edit it. The fields below stay
/// read-only: the names are length-coupled to the pack's offset tables, so changing one would shift every
/// entry after it.
/// </summary>
internal static class ActorPropertyCatalog
{
    public static IReadOnlyList<PropertyGroup> Build(ActorNodeAdapter node)
    {
        ActorEntry actor = node.Actor;
        FrameObjectBase? target = node.Target;

        var groups = new List<PropertyGroup>
        {
            new()
            {
                Title = "Actor",
                IsTypeSpecific = true,
                Properties = new[]
                {
                    ReadOnlyText("Actor.Entity", "Entity name", () => actor.EntityName),
                    ReadOnlyText("Actor.Type", "Entity type", () => node.TypeName),
                    IntDesc("Actor.TypeId", "Type id", () => (int)actor.TypeId,
                        tip: "The engine's E_EntityType value. A compressed pack stores this number; an "
                           + "uncompressed one stores the class name instead."),
                    ReadOnlyText("Actor.Category", "Category", () => ActorCategories.Label(node.Category)),
                    ReadOnlyText("Actor.Definition", "Definition", () => actor.LinkedDefinition),
                    ReadOnlyText("Actor.Sector", "Scene sector", () => actor.SceneSector),
                    ReadOnlyText("Actor.Name1", "Second name", () => actor.Name1),
                    BoolDesc("Actor.ActivateOnInit", "Active on load", () => actor.ActivateOnInit,
                        "Whether the game switches this actor on as soon as the pack streams in."),
                },
            },
            // Position / rotation / scale are edited through the standard Object tab — the adapter is an
            // IFrameNode, so the gizmo and the numeric fields already drive them.
            new()
            {
                Title = "Scene link",
                IsTypeSpecific = true,
                Properties = new[]
                {
                    ReadOnlyText("Actor.Frame", "Linked frame", () => actor.LinkedFrame),
                    HashDesc("Actor.FrameHash", "Frame hash", () => actor.FrameHash,
                        "What the link actually resolves through — the scene reference table is keyed by this, "
                        + "not by the name. Zero in an uncompressed pack, which derives it from the name."),
                    ReadOnlyText("Actor.Resolved", "Frame in this scene",
                        () => target != null ? "yes — " + (target.Name?.ToString() ?? "?") : "no (lives elsewhere)"),
                    ReadOnlyText("Actor.Drawn", "Has geometry", () => DrawnLabel(node)),
                    IntDesc("Actor.InitProp", "Init-props row", () => actor.InitPropId,
                        tip: "Row of the pack's entity-init property table, or -1 for none. Several actors can "
                           + "share one row. The contents are not typed yet."),
                    IntDesc("Actor.Index", "Item row", () => actor.Index,
                        tip: "Position in the pack's item table."),
                },
            },
        };

        return groups;
    }

    private static string DrawnLabel(ActorNodeAdapter node)
    {
        if (node.Target == null) return "no — nothing to draw, shown as a glyph";
        bool invisible = false;
        foreach (ActorEntry a in node.Placements.Invisible)
        {
            if (ReferenceEquals(a, node.Actor)) { invisible = true; break; }
        }
        return invisible ? "no — empty holder frame, shown as a glyph" : "yes — mesh under the placed frame";
    }

    private static PropertyDescriptor ReadOnlyText(string id, string label, Func<string> get) => new()
    {
        Id = id,
        Label = label,
        Kind = PropertyKind.Text,
        IsReadOnly = true,
        Get = () => get(),
    };

    private static PropertyDescriptor IntDesc(string id, string label, Func<int> get, string? tip = null) => new()
    {
        Id = id,
        Label = label,
        Kind = PropertyKind.Int,
        IsReadOnly = true,
        Tooltip = tip,
        Get = () => (long)get(),
    };

    private static PropertyDescriptor BoolDesc(string id, string label, Func<bool> get, string? tip = null) => new()
    {
        Id = id,
        Label = label,
        Kind = PropertyKind.Bool,
        IsReadOnly = true,
        Tooltip = tip,
        Get = () => get(),
    };

    private static PropertyDescriptor HashDesc(string id, string label, Func<ulong> get, string? tip = null) => new()
    {
        Id = id,
        Label = label,
        Kind = PropertyKind.UInt64Hex,
        IsReadOnly = true,
        Tooltip = tip,
        Get = () => get(),
    };
}
