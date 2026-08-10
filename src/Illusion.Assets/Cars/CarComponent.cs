using System.Globalization;
using System.Numerics;

namespace Illusion.Assets.Cars;

/// <summary>
/// A component's identity — the TOOLKIT's own, minted for this editing session and written to no file.
///
/// <para>
/// It is deliberately not the bone's name hash, although that hash is unique within a car on 85 of 85. An
/// identity read out of one of the four parallel lists a car is scattered across breaks precisely when
/// another one of them is edited: rename the bone in Blender and every reference keyed on its hash names a
/// different component, while undo and the bridge still believe they are talking about the same door. The
/// bridge learned this once already and moved to <c>illusion_id</c> after RefID proved unstable.
/// </para>
/// <para>
/// Nothing on disk can carry it, so it does not survive closing the archive: the resolver rebuilds the
/// stitching on every open, and on every bridge push, because a push can change the very bones the stitching
/// keys on. What it does survive is a rename, because it is carried across a re-stitch by the component's
/// position in the rig, not by its name.
/// </para>
/// </summary>
public readonly record struct ComponentId(long Value)
{
    private static long _minted;

    /// <summary>No component. Never minted, so it can never collide with a real one.</summary>
    public static ComponentId None => default;

    /// <summary>Whether this names a component at all.</summary>
    public bool IsSet => Value != 0;

    /// <summary>The next identity this session has not used. Unique across every car opened in it.</summary>
    internal static ComponentId Mint() => new(Interlocked.Increment(ref _minted));

    public override string ToString() =>
        Value == 0 ? "—" : Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One real thing of a car — a door, a bumper, a licence plate — reassembled from the parallel lists the
/// prefab scatters it across.
///
/// <para>
/// A component is sourced EITHER from a deform part OR from a bone that carries geometry and that no deform
/// part claims. Nothing else mints one: a deform handle is a list on its component and never a component of
/// its own, and a bone with no geometry and no part — the rig roots, the hinge bones of tracked vehicles —
/// is not a thing of the car at all.
/// </para>
/// <para>
/// Its name is the bone it names. The prefab's part struct carries no name field, which is why the Prefab
/// tab could only ever label a row <c>Part 1</c>.
/// </para>
/// </summary>
public sealed class CarComponent
{
    private readonly List<CarComponent> _children = [];
    private readonly List<CarMarker> _markers = [];
    private readonly List<CarCollision> _collisions = [];
    private readonly List<CarComponentRow> _rows = [];

    internal CarComponent(
        ComponentId id, string name, ulong boneHash, int boneJoint, bool boneResolves,
        int partIndex, uint partType, string kind, int pieces,
        IReadOnlyList<CarHandle> handles, CarDamage? damage)
    {
        Id = id;
        Name = name;
        BoneHash = boneHash;
        BoneJoint = boneJoint;
        BoneResolves = boneResolves;
        PartIndex = partIndex;
        PartType = partType;
        Kind = kind;
        Pieces = pieces;
        Handles = handles;
        Damage = damage;
    }

    /// <summary>Who this is, for as long as the archive is open. See <see cref="ComponentId"/>.</summary>
    public ComponentId Id { get; }

    /// <summary>The name of the bone this component is — or the bare hash when nothing resolves to it.</summary>
    public string Name { get; }

    /// <summary>FNV64 of that bone's name. Zero only on a part that names no bone at all.</summary>
    public ulong BoneHash { get; }

    /// <summary>Where the bone sits in the rig, or -1 when it is not in this car's rig. This — not the
    /// name — is what carries the identity across a rename.</summary>
    public int BoneJoint { get; }

    /// <summary>Whether the bone the part names is actually in this car. False is the signature of a rename
    /// made in Blender, and the component is shown broken rather than hidden.</summary>
    public bool BoneResolves { get; }

    /// <summary>Where the deform part sits in the prefab's part list, or -1 when there is none.</summary>
    public int PartIndex { get; }

    /// <summary>Whether this component has no deform part — a licence plate, a light, a wiper. It still
    /// takes bullets: every one of the 2587 shipped bare bones carries a live hit box.</summary>
    public bool IsBare => PartIndex < 0;

    /// <summary>The engine's own part kind (1 body, 4 door, 5 window, 6 cover, 13 motor, …), 0 when bare.</summary>
    public uint PartType { get; }

    /// <summary>That kind in words — <c>door</c>, <c>cover</c>, <c>bare</c>. Shown, never inferred from the
    /// bone name: a cover names <c>doorBL</c> on seven shipped cars.</summary>
    public string Kind { get; }

    /// <summary>How many split pieces this component's bone carries at the LOD the car was read at. Zero
    /// means it is not drawn there — for a part, the ordinary case at the far level.</summary>
    public int Pieces { get; }

    /// <summary>Whether the component has geometry at the LOD the car was read at.</summary>
    public bool HasGeometry => Pieces > 0;

    /// <summary>The bones this component crumples around, with how far and how hard. Never components.</summary>
    public IReadOnlyList<CarHandle> Handles { get; }

    /// <summary>What it takes to move this component, or null when it has no deform part.</summary>
    public CarDamage? Damage { get; }

    /// <summary>The component this one hangs off — a window's door, a patch's bonnet.</summary>
    public CarComponent? Parent { get; internal set; }

    /// <summary>The components hanging off this one, in the prefab's own order.</summary>
    public IReadOnlyList<CarComponent> Children => _children;

    /// <summary>The seats, climb boxes, tanks, emitters and lights that hang off this component's bone.</summary>
    public IReadOnlyList<CarMarker> Markers => _markers;

    /// <summary>
    /// The prefab rows that name this component's OWN bone — its door points, its window record, its axle.
    ///
    /// <para>
    /// A component is written down more than once, and only the deform part is this component's own struct:
    /// the door row beside it says where the handle and the lock are, the window row how deep the pane sits
    /// and whether it rolls down. Nothing in the format ties them together, so the toolkit does, and they are
    /// shown here rather than in a list the modder would have to count rows in.
    /// </para>
    /// </summary>
    public IReadOnlyList<CarComponentRow> Rows => _rows;

    /// <summary>
    /// What this component is solid with — its own collision, its glass, its zones — by role and shape, with
    /// every size and position stated in the component's OWN space.
    ///
    /// <para>
    /// A bare component has none and can be given none: a collision volume hangs off a deform part, and a
    /// bone that no part claims has nothing to hang one off until it is given a part of its own.
    /// </para>
    /// </summary>
    public IReadOnlyList<CarCollision> Collisions => _collisions;

    internal void AddChild(CarComponent child) => _children.Add(child);

    internal void AddMarker(CarMarker marker) => _markers.Add(marker);

    internal void AddRow(CarComponentRow row) => _rows.Add(row);

    internal void AddCollision(CarCollision collision) => _collisions.Add(collision);

    /// <summary>Whether <paramref name="other"/> is this component or anything above it — the check that
    /// keeps a broken parent link from closing a loop the tree would walk forever.</summary>
    internal bool IsAtOrAbove(CarComponent other)
    {
        for (CarComponent? at = other; at != null; at = at.Parent)
        {
            if (ReferenceEquals(at, this)) return true;
        }
        return false;
    }

    public override string ToString() => $"{Name} ({Kind})";
}

/// <summary>
/// One deform handle on a component: a bone the panel crumples AROUND when it is hit, with how far it may
/// travel, how hard it resists and over what radius the panel follows it.
///
/// <para>
/// A handle is not a component and must never be shown as one — the modder sees <c>deform_doorFL</c> beside
/// <c>doorFL</c> and reads two doors. 1093 of 1698 shipped parts carry no handle at all, 356 carry one and
/// 249 carry two or more; a handle is never also some part's own bone (0 of 1402).
/// </para>
/// </summary>
public sealed record CarHandle(int Index, ulong BoneHash, string Name, Vector3 Range, float Intensity,
    float Radius);

/// <summary>
/// A component's own damage parameters — the damage model's, per deform part.
///
/// <para>
/// <see cref="Mass"/> and <see cref="CentreOfMass"/> are the PART's. The car class carries a mass and a
/// centre of mass of its own in the EDS record, exposed by the Tuning tab; they are different quantities on
/// the same car and must not be shown under the same bare label.
/// </para>
/// </summary>
/// <param name="EffectGroup">Which particle a hit on this component throws. Components sharing the number
/// behave alike — proven in game.</param>
public sealed record CarDamage(
    float Mass, float Resistance, float SpeedMin, float SpeedMax, float EnergyStart, float EnergyDrop,
    Vector3 CentreOfMass, byte EffectGroup, uint Flags);
