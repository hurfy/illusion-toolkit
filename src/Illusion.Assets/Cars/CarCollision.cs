using System.Numerics;
using Illusion.Formats.ItemDesc;

namespace Illusion.Assets.Cars;

/// <summary>
/// What a collision volume IS to a modder, as opposed to the number the file stores.
///
/// <para>
/// The role is chosen; the stored type, the bone space the matrix is written in, whether the extents mean a
/// full size or half of one, and whether an ItemDesc record has to be minted are all DERIVED from it and are
/// never shown. The three are not variants of one thing — measured over 1698 deformable parts of 85 cars, a
/// part the engine calls a window carries type 0 and nothing else (527 of 527), a body carries type 5 and
/// nothing else (407 of 407), a motor carries type 6 (77 of 77).
/// </para>
/// </summary>
public enum CarCollisionRole
{
    /// <summary>The car's own solid: the body, the doors, the bumpers. Stored as type 5, which PLACES a shape
    /// the archive carries as an ItemDesc record and is written in the part's OWN bone space.</summary>
    Body,

    /// <summary>A pane of glass. Stored as type 0, describing itself with its own full size and naming no
    /// record at all — the type is the identity.</summary>
    Glass,

    /// <summary>A zone of the car: the engine bay, the snow volumes, a patch. Stored as type 6, the same
    /// self-describing box as glass.</summary>
    Zone,
}

/// <summary>
/// The form a collision takes. The four primitives are pure numbers and can be minted; a hull cannot.
/// </summary>
public enum CarCollisionShape
{
    Box,
    Sphere,

    /// <summary>A rod with rounded ends. Its axis is the shape's LOCAL Z — measured twice over the 251
    /// capsules of the shipped cars: the geometry a capsule wraps is longest along local Z on 232 of them,
    /// and Z is also the axis that agrees with the capsule's own radius and height.</summary>
    Capsule,

    Cylinder,

    /// <summary>A PhysX-cooked hull or triangle mesh. READ-ONLY: the vendored cooker knows exactly one verb,
    /// <c>-CookTriangleMesh</c>, so a hull cannot be re-cooked at a new size and resizing one would write a
    /// number nothing reads.</summary>
    Hull,
}

/// <summary>
/// One collision of a component, as the thing a modder authored: a role, a shape, a size and a position in
/// the component's OWN space.
///
/// <para>
/// Everything the file actually holds is derived and none of it appears here — not the stored type, not which
/// bone's space the matrix is written in (a self-describing volume is written in the bone space of the part
/// its part hangs off, and reading it in its own bone's space misses by 1.196 m), not the axis reversal
/// between the prefab copy and the frame stub, not that a self-describing volume's extents are a FULL size
/// while a placed shape states half of one, and not the mirror stub in the frame graph, which a car does not
/// read at all.
/// </para>
/// </summary>
public sealed class CarCollision
{
    internal CarCollision(
        CarCollisionRole role, CarCollisionShape shape, Vector3 size, Matrix4x4 placement,
        ComponentId component, int partIndex, int volumeIndex, string? readOnlyReason, ulong handle = 0)
    {
        Role = role;
        Shape = shape;
        Size = size;
        Placement = placement;
        Component = component;
        PartIndex = partIndex;
        VolumeIndex = volumeIndex;
        ReadOnlyReason = readOnlyReason;
        Handle = handle;
    }

    /// <summary>What this collision is: the car's solid, a pane of glass, or a zone.</summary>
    public CarCollisionRole Role { get; }

    /// <summary>The form it takes.</summary>
    public CarCollisionShape Shape { get; }

    /// <summary>
    /// Its FULL size in metres, along the shape's own axes — the whole box, never half of one, and never a
    /// radius the modder has to double in their head.
    ///
    /// <para>
    /// A sphere is the same number on all three. A capsule and a cylinder take their width from X and Y and
    /// their whole length from Z, the capsule's rounded caps included — which is what makes Z the axis, the
    /// way the shipped ones are written.
    /// </para>
    /// </summary>
    public Vector3 Size { get; }

    /// <summary>Where it sits, in the COMPONENT's own space — whatever space the file wrote it in.</summary>
    public Vector3 Position => Placement.Translation;

    /// <summary>The whole placement in the component's own space, so a turned volume keeps its turn when its
    /// position is typed over.</summary>
    public Matrix4x4 Placement { get; }

    /// <summary>The component this hangs off.</summary>
    public ComponentId Component { get; }

    /// <summary>Where its deform part sits in the prefab's part list.</summary>
    public int PartIndex { get; }

    /// <summary>Which of that part's volumes this is.</summary>
    public int VolumeIndex { get; }

    /// <summary>Why this collision cannot be edited, or null when it can be.</summary>
    public string? ReadOnlyReason { get; }

    /// <summary>
    /// FNV64 of the frame that stands where this collision does — the handle a modder drags to place it — or
    /// 0 when it has none.
    ///
    /// <para>
    /// A SOLID has one: the mirror stub the toolkit mints beside its shape record. The modder is never told
    /// what it is, and never sees it as a frame of its own; it is handed to the viewport so that selecting a
    /// collision row puts the gizmo on the volume rather than on the whole part. Dragging it writes through to
    /// the prefab on the next save, which is the copy the game reads.
    /// </para>
    /// <para>
    /// Glass and zones have none, and cannot be given one: they describe themselves inside the prefab and
    /// name no record for a stub to mirror — every one of the 1049 shipped ones is written that way. Those are
    /// placed by their numbers instead.
    /// </para>
    /// </summary>
    public ulong Handle { get; }

    /// <summary>Whether the viewport has something to drag for this collision.</summary>
    public bool HasHandle => Handle != 0;

    /// <summary>Whether editing is refused — a cooked hull, or a component whose bone does not resolve.</summary>
    public bool IsReadOnly => ReadOnlyReason != null;

    /// <summary>The one line a row shows: what it is and what form it takes.</summary>
    public string Label => $"{RoleName(Role)} · {ShapeName(Shape)}";

    /// <summary>The role in the words the picker uses.</summary>
    public static string RoleName(CarCollisionRole role) => role switch
    {
        CarCollisionRole.Body => "Body",
        CarCollisionRole.Glass => "Glass",
        _ => "Zone",
    };

    /// <summary>The shape in the words the picker uses.</summary>
    public static string ShapeName(CarCollisionShape shape) => shape switch
    {
        CarCollisionShape.Box => "box",
        CarCollisionShape.Sphere => "sphere",
        CarCollisionShape.Capsule => "capsule",
        CarCollisionShape.Cylinder => "cylinder",
        _ => "hull",
    };

    public override string ToString() => Label;

    // ── role ⇄ stored type ──

    /// <summary>The volume type a role is written as. Never shown; this is the whole point of the role.</summary>
    public static uint TypeOfRole(CarCollisionRole role) => role switch
    {
        CarCollisionRole.Body => 5,
        CarCollisionRole.Glass => 0,
        _ => 6,
    };

    /// <summary>
    /// What a stored type MEANS. Type 2 — six shipped volumes, every one of them named <c>fish</c> — reads as
    /// a zone rather than as its own role: it behaves like one in every way the toolkit can observe, and
    /// offering a fourth role for six volumes on the whole corpus would be a menu entry nobody can use.
    /// </summary>
    public static CarCollisionRole RoleOf(uint volumeType) => volumeType switch
    {
        5 => CarCollisionRole.Body,
        0 => CarCollisionRole.Glass,
        _ => CarCollisionRole.Zone,
    };

    /// <summary>
    /// Which role a component's kind asks for, before the modder overrides it.
    ///
    /// <para>
    /// Overridable and expected to be overridden: a part carrying two kinds of volume is the normal case, not
    /// the exception. Doors run 0 ×21 / 5 ×228 and covers 0 ×16 / 5 ×231 over the corpus, so a door carrying
    /// both its own collision and its glass is simply how a car is built.
    /// </para>
    /// </summary>
    public static CarCollisionRole RoleFor(string kind) => kind switch
    {
        "window" => CarCollisionRole.Glass,
        "motor" or "snow" => CarCollisionRole.Zone,
        _ => CarCollisionRole.Body,
    };

    // ── shape ⇄ the ItemDesc record ──

    /// <summary>What an ItemDesc record's own shape is, in the vocabulary the modder chooses from.</summary>
    internal static CarCollisionShape ShapeOf(RigidBodyShape shape) => shape switch
    {
        RigidBodyShape.Box => CarCollisionShape.Box,
        RigidBodyShape.Sphere => CarCollisionShape.Sphere,
        RigidBodyShape.Capsule => CarCollisionShape.Capsule,
        RigidBodyShape.Cylinder => CarCollisionShape.Cylinder,
        _ => CarCollisionShape.Hull,
    };

    /// <summary>The record's own shape id for a shape that can be minted; null for a hull, which cannot.</summary>
    internal static RigidBodyShape? RecordShapeOf(CarCollisionShape shape) => shape switch
    {
        CarCollisionShape.Box => RigidBodyShape.Box,
        CarCollisionShape.Sphere => RigidBodyShape.Sphere,
        CarCollisionShape.Capsule => RigidBodyShape.Capsule,
        CarCollisionShape.Cylinder => RigidBodyShape.Cylinder,
        _ => null,
    };

    /// <summary>
    /// The full size a record occupies — what a modder would have to type to cover the same space.
    ///
    /// <para>
    /// A hull answers through the bounds stored in its cooked blob, the same ones the overlay draws it by.
    /// Falling back to a token size here is what once made a converted body hull look like it had vanished:
    /// it was still there, still in the right place, and a fifth of a metre across on a five-metre car.
    /// </para>
    /// </summary>
    internal static Vector3 FullSizeOf(RigidBodyElement rigid)
    {
        switch (rigid.Shape)
        {
            case RigidBodyShape.Box:
                return rigid.BoxDimensions * 2f;
            case RigidBodyShape.Sphere:
                return new Vector3(rigid.Radius * 2f);
            case RigidBodyShape.Capsule:
                return new Vector3(rigid.Radius * 2f, rigid.Radius * 2f, rigid.Height + (rigid.Radius * 2f));
            case RigidBodyShape.Cylinder:
                return new Vector3(rigid.Radius * 2f, rigid.Radius * 2f, rigid.Height);
            default:
                return Collisions.CarCollisionShapes.TryReadCookedBounds(
                    rigid.CookedMesh, out Vector3 lo, out Vector3 hi)
                    ? Vector3.Abs(hi - lo)
                    : new Vector3(0.2f);
        }
    }

    /// <summary>
    /// The record's own numbers for a full size — the inverse of <see cref="FullSizeOf"/>, so a size typed in
    /// and read back is the size that was typed.
    /// </summary>
    /// <returns>False with a <paramref name="refusal"/> when the numbers cannot describe that shape.</returns>
    internal static bool Describe(
        CarCollisionShape shape, Vector3 fullSize,
        out Vector3 halfExtents, out float radius, out float height, out string? refusal)
    {
        halfExtents = default;
        radius = 0f;
        height = 0f;
        refusal = null;

        if (!(fullSize.X > 0f) || !(fullSize.Y > 0f) || !(fullSize.Z > 0f))
        {
            refusal = "a collision needs a size greater than zero on every axis";
            return false;
        }

        switch (shape)
        {
            case CarCollisionShape.Box:
                halfExtents = fullSize * 0.5f;
                return true;
            case CarCollisionShape.Sphere:
                // One number, three given. The largest is the safe reading, for the reason a baked scale takes
                // the same one: a shape that came out bigger than asked is visible, one that quietly did not
                // is a hole in the car.
                radius = MathF.Max(fullSize.X, MathF.Max(fullSize.Y, fullSize.Z)) * 0.5f;
                return true;
            case CarCollisionShape.Capsule:
                radius = MathF.Max(fullSize.X, fullSize.Y) * 0.5f;
                height = fullSize.Z - (radius * 2f);
                if (height <= 0f)
                {
                    refusal = "a capsule is longer than it is wide — its length runs along Z, and the rounded "
                        + "caps already take one width of it";
                    return false;
                }
                return true;
            case CarCollisionShape.Cylinder:
                radius = MathF.Max(fullSize.X, fullSize.Y) * 0.5f;
                height = fullSize.Z;
                return true;
            default:
                refusal = "a hull cannot be minted or resized — the cooker the toolkit ships can only cook "
                    + "triangle meshes, so new collision is always a primitive";
                return false;
        }
    }
}
