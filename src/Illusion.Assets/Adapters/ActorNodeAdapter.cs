using System.Numerics;
using Illusion.Assets.Actors;
using Illusion.Assets.Properties;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Formats.Actors;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.Adapters;

/// <summary>
/// Adapts one placed actor (<see cref="ActorEntry"/>) into an <see cref="IFrameNode"/> (so the standard
/// selection + gizmo + undo pipeline moves it) and an <see cref="IPropertySource"/> (so the property panel
/// shows what it is).
///
/// An actor is a bare world placement: it has no parent frame, so <see cref="ParentWorldTransform"/> is
/// identity and <see cref="WorldTransform"/> equals <see cref="LocalTransform"/> — the same shape
/// <see cref="TranslokatorInstanceAdapter"/> has, and what makes a world-space drag land as-is. Moving it
/// re-places the whole subtree it spawns, because that geometry is only its prototype.
/// </summary>
public sealed class ActorNodeAdapter : IFrameNode, IPropertySource
{
    internal ActorNodeAdapter(ActorEntry actor, ActorPlacements placements)
    {
        Actor = actor;
        Placements = placements;
    }

    /// <summary>The actor's spawn transform. Setting it moves the actor — and with it every frame object it
    /// places. Marking the pack as edited is the caller's job, through the same persistence path every other
    /// edit uses (the "Actors" tree node carries an <see cref="ActorDocumentAdapter"/> to save into). Rotation
    /// and scale come back out of the matrix in the same rotation·scale convention frame matrices use.</summary>
    public Matrix4x4 LocalTransform
    {
        get => Actor.Transform;
        set
        {
            TransformMath.TryDecompose(value, out Vector3 scale, out Quaternion rotation, out Vector3 position);
            Actor.Position = position;
            // Compose→decompose is not bit-exact for the rotation and the scale (the quaternion comes back out
            // of a normalized basis), so a pure drag would rewrite them by ~1e-7 and change bytes that nobody
            // asked to change. Keep the stored values unless the edit actually turned or resized the actor.
            if (MathF.Abs(Quaternion.Dot(rotation, Actor.Rotation)) < 1f - 1e-6f) Actor.Rotation = rotation;
            if ((scale - Actor.Scale).LengthSquared() > 1e-12f) Actor.Scale = scale;
            Placements.Refresh(Actor);
        }
    }

    /// <summary>An actor stands in the world, not under a frame — its world IS its local.</summary>
    public Matrix4x4 WorldTransform => Actor.Transform;

    /// <summary>Identity: there is no parent frame to re-localize a world-space edit against.</summary>
    public Matrix4x4 ParentWorldTransform => Matrix4x4.Identity;

    public IFrameNode? Parent => null;

    /// <summary>Actors are not frame-name-table entries; the flags there classify geometry, not placements.</summary>
    public bool IsOnNameTable => false;

    public int NameTableFlags => 0;

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
