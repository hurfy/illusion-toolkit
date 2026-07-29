using System.Numerics;
using Illusion.Assets.Actors;
using Illusion.Assets.Adapters;
using Illusion.Domain.Properties;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.Properties;

/// <summary>
/// Property panel groups for one actor. Everything is read-only in this build: the transform lives in the .act
/// pack (which the toolkit reads but does not yet write back edits to), and the names are length-coupled to the
/// pack's offset tables, so changing one would shift every entry after it.
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
            new()
            {
                Title = "Spawn transform",
                IsTypeSpecific = true,
                Properties = new[]
                {
                    VectorDesc("Actor.Position", "Position", () => actor.Position,
                        "Where the game puts the actor — and, for an actor that places a frame object, where "
                        + "that object's whole subtree goes. The frame itself sits at the origin."),
                    VectorDesc("Actor.Rotation", "Rotation (deg)", () => ToEulerDegrees(actor.Rotation),
                        "The stored value is a quaternion; this shows it as degrees for reading."),
                    VectorDesc("Actor.Scale", "Scale", () => actor.Scale),
                },
            },
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

    // Quaternion → yaw/pitch/roll in degrees, for reading only (the panel never writes it back).
    private static Vector3 ToEulerDegrees(Quaternion q)
    {
        float sinrCosp = 2 * (q.W * q.X + q.Y * q.Z);
        float cosrCosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        float roll = MathF.Atan2(sinrCosp, cosrCosp);

        float sinp = 2 * (q.W * q.Y - q.Z * q.X);
        float pitch = MathF.Abs(sinp) >= 1 ? MathF.CopySign(MathF.PI / 2, sinp) : MathF.Asin(sinp);

        float sinyCosp = 2 * (q.W * q.Z + q.X * q.Y);
        float cosyCosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        float yaw = MathF.Atan2(sinyCosp, cosyCosp);

        const float ToDeg = 180f / MathF.PI;
        return new Vector3(roll * ToDeg, pitch * ToDeg, yaw * ToDeg);
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

    private static PropertyDescriptor VectorDesc(string id, string label, Func<Vector3> get, string? tip = null) => new()
    {
        Id = id,
        Label = label,
        Kind = PropertyKind.Vector3,
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
