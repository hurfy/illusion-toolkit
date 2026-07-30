using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Domain.Properties;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.Properties;

/// <summary>
/// Property panel groups for one actor: what it is, what it is linked to, and how it behaves. The transform is
/// not here — the adapter is an <c>IFrameNode</c>, so the standard Object tab and the gizmo edit it.
///
/// What is editable is what is fixed-size: the flags word, the property row an actor points at, and the fields of
/// that row. The names are length-coupled to the pack's offset tables and stay read-only until the structural
/// writer lands.
/// </summary>
internal static class ActorPropertyCatalog
{
    public static IReadOnlyList<PropertyGroup> Build(ActorNodeAdapter node)
    {
        ActorEntry actor = node.Actor;
        FrameObjectBase? target = node.Target;
        ActorsFile? pack = node.Placements.PackOf(actor);
        ActorPropertyRow? row = pack?.PropertiesOf(actor);

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
                    new PropertyDescriptor
                    {
                        Id = "Actor.ActivateOnInit",
                        Label = "Active on load",
                        Kind = PropertyKind.Bool,
                        Tooltip = "Whether the game switches this actor on as soon as the pack streams in. "
                                + "Bit 0 of the actor's flags word.",
                        Get = () => actor.ActivateOnInit,
                        Set = v => actor.ActivateOnInit = v is true,
                    },
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
                    InitPropDescriptor(actor, pack),
                    IntDesc("Actor.Index", "Item row", () => actor.Index,
                        tip: "Position in the pack's item table."),
                },
            },
        };

        AddBehaviour(groups, row);
        return groups;
    }

    // The behavior blob, split in two: the fields worth reading first, and — collapsed behind an expander — the
    // repeated array slots and the ones nobody has named yet. Both land on the per-type tab, beside "Actor".
    private static void AddBehaviour(List<PropertyGroup> groups, ActorPropertyRow? row)
    {
        if (row == null)
        {
            return;
        }
        if (row.Fields.Count == 0)
        {
            groups.Add(new PropertyGroup
            {
                Title = BehaviourTitle(row),
                IsTypeSpecific = true,
                Properties = new[]
                {
                    ReadOnlyText("Behaviour.Untyped", "Layout",
                        () => $"not decoded — {row.PayloadSize} bytes ride as they are"),
                },
            });
            return;
        }

        var named = new List<PropertyDescriptor>();
        var rest = new List<PropertyDescriptor>();
        foreach (ActorPropertyField field in row.Fields)
        {
            (IsDetail(field.Name) ? rest : named).Add(Describe(field));
        }

        if (named.Count > 0)
        {
            groups.Add(new PropertyGroup
            {
                Title = BehaviourTitle(row),
                IsTypeSpecific = true,
                Properties = named,
            });
        }
        if (rest.Count > 0)
        {
            groups.Add(new PropertyGroup
            {
                Title = "Behaviour — arrays and unnamed",
                IsTypeSpecific = true,
                IsUnknown = true, // renders collapsed
                Properties = rest,
            });
        }
    }

    // A row is shared by design — over half the shipped actors point at one another actor already uses — so the
    // header says so rather than letting an edit surprise someone.
    private static string BehaviourTitle(ActorPropertyRow row)
    {
        int sharers = row.SharerCount;
        string who = sharers <= 1 ? "this actor only" : $"shared by {sharers} actors";
        return $"Behaviour — {who}";
    }

    // Array slots ("Hit2.MinVol", "Mixer1.Near") and the unnamed leftovers of the reverse engineering. Neither is
    // wrong to edit, but neither is what someone opens the panel for.
    private static bool IsDetail(string name) =>
        name.Contains('.', StringComparison.Ordinal)
        || name.StartsWith("Unk", StringComparison.Ordinal);

    // One behavior field. The value shapes are fixed by the core's kind; each maps onto the panel editor that
    // fits it, with the integer editors bounded by what the field can actually hold.
    private static PropertyDescriptor Describe(ActorPropertyField field)
    {
        string id = "Behaviour." + field.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tip = $"offset {field.Offset}, {field.Capacity} byte(s)";
        return field.Kind switch
        {
            ActorPropertyKind.Bool => new PropertyDescriptor
            {
                Id = id, Label = field.Name, Kind = PropertyKind.Bool, Tooltip = tip,
                Get = () => field.Flag,
                Set = v => field.Flag = v is true,
            },
            ActorPropertyKind.Float => new PropertyDescriptor
            {
                Id = id, Label = field.Name, Kind = PropertyKind.Float, Tooltip = tip,
                Get = () => field.Single,
                Set = v => field.Single = v is float f ? f : 0f,
            },
            ActorPropertyKind.Vector3 => new PropertyDescriptor
            {
                Id = id, Label = field.Name, Kind = PropertyKind.Vector3, Tooltip = tip,
                Get = () => field.Vector,
                Set = v => field.Vector = v is System.Numerics.Vector3 vec ? vec : default,
            },
            ActorPropertyKind.Text => new PropertyDescriptor
            {
                Id = id, Label = field.Name, Kind = PropertyKind.Text,
                Tooltip = tip + $" — at most {Math.Max(0, field.Capacity - 1)} characters, the buffer is fixed",
                Get = () => field.Text,
                Set = v => field.Text = v as string ?? string.Empty,
            },
            ActorPropertyKind.Hash64 or ActorPropertyKind.UInt64 => new PropertyDescriptor
            {
                Id = id, Label = field.Name, Kind = PropertyKind.UInt64Hex, Tooltip = tip,
                Get = () => field.Hash,
                Set = v => field.Hash = v is ulong h ? h : 0ul,
            },
            _ => new PropertyDescriptor
            {
                Id = id, Label = field.Name, Kind = PropertyKind.Int, Tooltip = tip,
                Min = MinOf(field.Kind), Max = MaxOf(field.Kind),
                Get = () => field.Number,
                Set = v => field.Number = v is long n ? n : 0L,
            },
        };
    }

    private static long MinOf(ActorPropertyKind kind) => kind switch
    {
        ActorPropertyKind.Int8 => sbyte.MinValue,
        ActorPropertyKind.Int16 => short.MinValue,
        ActorPropertyKind.Int32 => int.MinValue,
        ActorPropertyKind.Int64 => long.MinValue,
        _ => 0,
    };

    private static long MaxOf(ActorPropertyKind kind) => kind switch
    {
        ActorPropertyKind.Int8 => sbyte.MaxValue,
        ActorPropertyKind.UInt8 => byte.MaxValue,
        ActorPropertyKind.Int16 => short.MaxValue,
        ActorPropertyKind.UInt16 => ushort.MaxValue,
        ActorPropertyKind.Int32 => int.MaxValue,
        ActorPropertyKind.UInt32 => uint.MaxValue,
        _ => long.MaxValue,
    };

    // Which behavior row the actor points at. Editable, but only between rows the engine would accept: a row
    // describes ONE entity type, and handing an actor a blob meant for another type is how a district stops
    // loading. -1 (no row) is always allowed.
    private static PropertyDescriptor InitPropDescriptor(ActorEntry actor, ActorsFile? pack)
    {
        IReadOnlyList<ActorPropertyRow> rows = pack?.PropertyRows ?? [];
        string tip = "Row of the pack's entity-init property table, or -1 for none. Several actors can share one "
                   + "row — editing its fields edits them for all of them. Only rows describing this actor's own "
                   + "entity type are accepted here.";
        if (pack == null)
        {
            return IntDesc("Actor.InitProp", "Init-props row", () => actor.InitPropId, tip);
        }
        return new PropertyDescriptor
        {
            Id = "Actor.InitProp",
            Label = "Init-props row",
            Kind = PropertyKind.Int,
            Min = -1,
            Max = Math.Max(-1, rows.Count - 1),
            Tooltip = tip,
            Get = () => (long)actor.InitPropId,
            Set = v =>
            {
                if (v is not long index) return;
                if (index < 0) { actor.InitPropId = -1; return; }
                if (index >= rows.Count) return;
                if (rows[(int)index].TypeId != (int)actor.TypeId) return;
                actor.InitPropId = (short)index;
            },
        };
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
