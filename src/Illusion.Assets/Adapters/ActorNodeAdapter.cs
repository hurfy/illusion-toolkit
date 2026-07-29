using Illusion.Assets.Actors;
using Illusion.Assets.Properties;
using Illusion.Domain.Properties;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.Adapters;

/// <summary>
/// Adapts one placed actor (<see cref="ActorEntry"/>) into an <see cref="IPropertySource"/>, so the scene tree
/// can list the scene's actors and the property panel can show what each one is: its type, the names that tie
/// it to a definition and to a frame object, its spawn transform, and whether this scene actually has the frame
/// it places.
///
/// Deliberately NOT an <c>IFrameNode</c>: that port is what puts a gizmo on an object and lets a drag write a
/// new transform, and an actor's transform belongs in the .act pack, which this build does not write yet.
/// Showing a gizmo that silently edits the wrong file would be worse than showing none.
/// </summary>
public sealed class ActorNodeAdapter : IPropertySource
{
    internal ActorNodeAdapter(ActorEntry actor, ActorPlacements placements)
    {
        Actor = actor;
        Placements = placements;
    }

    /// <summary>The wrapped actor — the property descriptors read it directly.</summary>
    public ActorEntry Actor { get; }

    internal ActorPlacements Placements { get; }

    /// <summary>The frame object this actor places, when this scene carries it; null for the ambient sounds and
    /// script hooks that name a frame living elsewhere.</summary>
    public FrameObjectBase? Target => Placements.TargetOf(Actor);

    /// <summary>Which coarse group the actor belongs to (drives the tree section and the glyph colour).</summary>
    public ActorCategory Category => ActorCategories.Of(Actor.Type);

    /// <summary>Whether the viewport represents this actor by a glyph (it places no geometry) rather than by the
    /// mesh it spawns.</summary>
    public bool HasGlyph => Placements.HasGlyph(Actor);

    /// <summary>Display name: the actor's own entity name, falling back to its type when unnamed.</summary>
    public string Name => Actor.EntityName.Length > 0 ? Actor.EntityName : TypeName;

    /// <summary>The engine class name where the pack stores one ("C_Door"), else the enum name of its type id.</summary>
    public string TypeName => Actor.TypeName.Length > 0 ? Actor.TypeName : Actor.Type.ToString();

    public IReadOnlyList<PropertyGroup> GetPropertyGroups() => ActorPropertyCatalog.Build(this);
}
