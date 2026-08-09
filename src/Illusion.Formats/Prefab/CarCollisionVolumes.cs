using System.Numerics;

namespace Illusion.Formats.Prefab;

/// <summary>
/// One collision volume of a deformable part — the thing a bullet or a bumper actually meets.
///
/// <para>
/// A volume of type 5 names a physics shape the archive carries as an <c>ItemDesc</c> record, by that
/// record's DATA hash, and says where to put it; every other type describes itself with
/// <see cref="Size"/> and names nothing.
/// </para>
/// <para>
/// The transform is in the same axis order as the rest of the toolkit — the file's own order is the
/// reverse, and the conversion happens at this boundary so nothing above it has to know. WHICH SPACE it is
/// in depends on the type: a type-5 volume is written in its own part's bone space, and a self-describing
/// one in the space of the part its part hangs off (<see cref="CarDeformPart.ParentFrame"/>). Measured on
/// all 85 extracted cars — see <c>Illusion.Assets.Collisions.CarPhysicsVolumes.Load</c>, which is where the
/// two are resolved.
/// </para>
/// </summary>
/// <param name="Size">Full size of a self-describing volume, not half — measured on 1049 shipped volumes,
/// 63 of which would be bigger than their own car if these were half-sizes.</param>
/// <param name="ShapeHash">The ItemDesc record's data hash for type 5, otherwise 0.</param>
public sealed record CarPhysicsVolume(
    int Index, uint VolumeType, Matrix4x4 Transform, Vector3 Size, ulong ShapeHash)
{
    /// <summary>The volume type that names a physics shape instead of describing a box.</summary>
    public const uint ShapeVolumeType = 5;

    /// <summary>Whether this volume is a placed physics shape rather than a plain box.</summary>
    public bool NamesShape => VolumeType == ShapeVolumeType;
}

public sealed partial class PrefabFile
{
    /// <summary>The extents a shipped type-5 volume carries — it describes nothing, the shape does.</summary>
    private static readonly Vector3 PlacedShapeExtents = new(0.01f, 0.01f, 0.01f);

    /// <summary>Moves and resizes an existing volume. False when there is no such part or volume.</summary>
    public bool SetCarVolume(int part, int volume, Matrix4x4 transform, Vector3 size)
    {
        Native.Model.PrefabCollVolumeW? found = VolumeAt(part, volume);
        if (found == null) return false;
        found.Transform = FromMatrix(SwapAxes(transform));
        found.Extents = size;
        return true;
    }

    /// <summary>
    /// Changes what a volume IS — its type, its placement, its size and the shape it names, together.
    ///
    /// <para>
    /// All four at once because they are not independent: a type-5 volume names a shape and carries the 1 cm
    /// placeholder extents, a self-describing one names nothing and states its real size, and the two kinds
    /// are written in different spaces. Setting the type alone would leave a volume describing itself with a
    /// placeholder size, in the wrong space, still pointing at a shape.
    /// </para>
    /// </summary>
    /// <returns>False when there is no such part or volume, or the hash pair cannot hold the shape.</returns>
    public bool SetCarVolumeType(
        int part, int volume, uint volumeType, Matrix4x4 transform, Vector3 size, ulong shapeDataHash)
    {
        Native.Model.PrefabCollVolumeW? found = VolumeAt(part, volume);
        if (found == null) return false;

        found.VolumeType = volumeType;
        found.Transform = FromMatrix(SwapAxes(transform));
        found.Extents = size;

        // The hash pair is (0, shape) on all 1097 shipped volumes that have one, and absent on the rest.
        if (shapeDataHash != 0)
        {
            while (found.Unk4Hashes.Count < 2) found.Unk4Hashes.Add(0);
            found.Unk4Hashes[0] = 0;
            found.Unk4Hashes[1] = shapeDataHash;
        }
        else
        {
            found.Unk4Hashes.Clear();
        }
        return true;
    }

    /// <summary>Points a volume at a different physics shape, by that shape's ItemDesc DATA hash.</summary>
    /// <returns>False when there is no such volume, or when it is not the kind that names a shape.</returns>
    public bool SetCarVolumeShape(int part, int volume, ulong shapeDataHash)
    {
        Native.Model.PrefabCollVolumeW? found = VolumeAt(part, volume);
        if (found == null || found.Unk4Hashes.Count < 2) return false;
        // The pair is (0, shape) on all 1097 shipped volumes — the first slot is never used.
        found.Unk4Hashes[1] = shapeDataHash;
        return true;
    }

    /// <summary>
    /// Hangs one more collision volume off a deformable part, so a piece of geometry that had nothing to be
    /// shot at now has something.
    /// <para>
    /// A volume that names a shape (<paramref name="shapeDataHash"/> non-zero) is written exactly as the
    /// shipped ones are: type 5, the placement, the throwaway 1 cm extents, and the pair (0, shape). One
    /// that names none is written with its own size instead.
    /// </para>
    /// </summary>
    /// <returns>The new volume's index in that part, or -1 when the part does not exist.</returns>
    /// <param name="volumeType">Which KIND of volume to write, or null to infer it from
    /// <paramref name="shapeDataHash"/>. The kinds are not interchangeable: a window carries type 0 on all
    /// 527 shipped ones, a body type 5 on all 407, a motor type 6 on all 77 — so this is the field that says
    /// what the volume IS, and until it was writable every box the toolkit made was body collision whatever
    /// part it was hung on.</param>
    public int AddCarVolume(
        int part, Matrix4x4 transform, Vector3 size, ulong shapeDataHash, uint? volumeType = null)
    {
        List<Native.Model.PrefabCollVolumeW>? list = VolumeList(part);
        if (list == null) return -1;

        var added = new Native.Model.PrefabCollVolumeW
        {
            VolumeType = volumeType ?? (shapeDataHash != 0 ? CarPhysicsVolume.ShapeVolumeType : 6),
            Transform = FromMatrix(SwapAxes(transform)),
            Extents = shapeDataHash != 0 ? PlacedShapeExtents : size,
        };
        if (shapeDataHash != 0)
        {
            added.Unk4Hashes.Add(0);
            added.Unk4Hashes.Add(shapeDataHash);
        }
        list.Add(added);
        return list.Count - 1;
    }

    /// <summary>Drops a volume and hands back its bytes, so an undo puts back exactly what was there.</summary>
    public byte[]? TakeCarVolume(int part, int volume)
    {
        List<Native.Model.PrefabCollVolumeW>? list = VolumeList(part);
        if (list == null || volume < 0 || volume >= list.Count) return null;
        byte[] bytes = Pack(w => list[volume].WriteTo(w));
        list.RemoveAt(volume);
        return bytes;
    }

    /// <summary>Puts a taken volume back where it was — the undo of <see cref="TakeCarVolume"/>.</summary>
    public bool PutCarVolume(int part, int volume, byte[] item)
    {
        ArgumentNullException.ThrowIfNull(item);
        List<Native.Model.PrefabCollVolumeW>? list = VolumeList(part);
        if (list == null || volume < 0 || volume > list.Count) return false;
        using var buffer = new MemoryStream(item, writable: false);
        list.Insert(volume, Native.Model.PrefabCollVolumeW.ReadFrom(new BinaryReader(buffer)));
        return true;
    }

    /// <summary>Which deformable part is the given bone, by the FNV64 of its name. -1 when none is.</summary>
    public int FindCarPartByFrame(ulong frameHash)
    {
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        if (parts == null || frameHash == 0) return -1;
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Unk3.Contains(frameHash)) return i;
        }
        return -1;
    }

    // ── flat addressing, so the property panel can reach a volume the way it reaches everything else ──

    /// <summary>How many collision volumes the whole car has, counted across every deformable part.</summary>
    public int CarVolumeCount()
    {
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        return parts?.Sum(p => p.CollisionVolumes.Sum(c => c.Volumes.Count)) ?? 0;
    }

    /// <summary>Which part and which of its volumes a flat index means, or null when it is past the end.</summary>
    public (int Part, int Volume)? CarVolumeAt(int flat)
    {
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        if (parts == null || flat < 0) return null;
        int seen = 0;
        for (int part = 0; part < parts.Count; part++)
        {
            int here = parts[part].CollisionVolumes.Sum(c => c.Volumes.Count);
            if (flat < seen + here) return (part, flat - seen);
            seen += here;
        }
        return null;
    }

    /// <summary>The flat index of a part's volume — the inverse of <see cref="CarVolumeAt"/>.</summary>
    public int CarVolumeIndex(int part, int volume)
    {
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        if (parts == null || part < 0 || part >= parts.Count) return -1;
        int seen = 0;
        for (int i = 0; i < part; i++) seen += parts[i].CollisionVolumes.Sum(c => c.Volumes.Count);
        return seen + volume;
    }

    private float GetVolumeValue(CarValueSlot slot, int flat, int axis)
    {
        if (CarVolumeAt(flat) is not { } at) return float.NaN;
        Native.Model.PrefabCollVolumeW? found = VolumeAt(at.Part, at.Volume);
        if (found == null) return float.NaN;
        Vector3 value = slot == CarValueSlot.CollisionVolumeSize
            ? found.Extents
            : SwapAxes(ToMatrix(found.Transform)).Translation;
        return axis switch { 0 => value.X, 1 => value.Y, _ => value.Z };
    }

    private bool SetVolumeValue(CarValueSlot slot, int flat, int axis, float value)
    {
        if (CarVolumeAt(flat) is not { } at) return false;
        Native.Model.PrefabCollVolumeW? found = VolumeAt(at.Part, at.Volume);
        if (found == null) return false;

        if (slot == CarValueSlot.CollisionVolumeSize)
        {
            found.Extents = WithAxis(found.Extents, axis, value);
            return true;
        }

        Matrix4x4 placement = SwapAxes(ToMatrix(found.Transform));
        placement.Translation = WithAxis(placement.Translation, axis, value);
        found.Transform = FromMatrix(SwapAxes(placement));
        return true;
    }

    private byte[]? TakeCarVolumeFlat(int flat) =>
        CarVolumeAt(flat) is { } at ? TakeCarVolume(at.Part, at.Volume) : null;

    private bool PutCarVolumeFlat(int flat, byte[] item)
    {
        // A put lands where the take came from, so the SAME flat index has to resolve — but the volume it
        // named is gone, which makes the last part's list one short and a flat index at the very end resolve
        // to nothing. Walking back to the part is what keeps an undo of the last volume working.
        if (CarVolumeAt(flat) is { } at) return PutCarVolume(at.Part, at.Volume, item);

        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        if (parts == null || flat != CarVolumeCount()) return false;
        for (int part = parts.Count - 1; part >= 0; part--)
        {
            if (parts[part].CollisionVolumes.Count == 0) continue;
            return PutCarVolume(part, parts[part].CollisionVolumes[0].Volumes.Count, item);
        }
        return false;
    }

    private static Vector3 WithAxis(Vector3 v, int axis, float value) => axis switch
    {
        0 => v with { X = value },
        1 => v with { Y = value },
        _ => v with { Z = value },
    };

    // ── the wire model, reached the same way every car edit reaches it ──

    private List<Native.Model.PrefabDeformPartW>? DeformParts()
    {
        Native.Model.PrefabEntryW? entry = Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0);
        Native.Model.PrefabCarInitW? car = entry?.CarInit[0];
        return car is { Deformation.Count: > 0 } ? car.Deformation[0].DeformParts : null;
    }

    /// <summary>
    /// A part's volumes. Every one of the 1698 shipped parts keeps them in exactly ONE collection, so a part
    /// that has none yet gets one rather than the caller having to think about the grouping.
    /// </summary>
    private List<Native.Model.PrefabCollVolumeW>? VolumeList(int part)
    {
        List<Native.Model.PrefabDeformPartW>? parts = DeformParts();
        if (parts == null || part < 0 || part >= parts.Count) return null;
        Native.Model.PrefabDeformPartW found = parts[part];
        if (found.CollisionVolumes.Count == 0)
        {
            found.CollisionVolumes.Add(new Native.Model.PrefabCollVolumeCollectionW());
        }
        return found.CollisionVolumes[0].Volumes;
    }

    private static IEnumerable<Native.Model.PrefabCollVolumeW> Volumes(Native.Model.PrefabDeformPartW part) =>
        part.CollisionVolumes.SelectMany(c => c.Volumes);

    private Native.Model.PrefabCollVolumeW? VolumeAt(int part, int volume)
    {
        List<Native.Model.PrefabCollVolumeW>? list = VolumeList(part);
        return list != null && volume >= 0 && volume < list.Count ? list[volume] : null;
    }

    /// <summary>
    /// The prefab writes a transform with its axes in the opposite order to the frame graph: the same
    /// placement reads as <c>stub[i][j] == prefab[2-i][2-j]</c> on 1065 of 1097 shipped pairs (the rest are
    /// archives already edited by hand). Reversing both indices is its own inverse, so one function converts
    /// in both directions.
    /// </summary>
    private static Matrix4x4 SwapAxes(Matrix4x4 m) => new(
        m.M33, m.M32, m.M31, 0f,
        m.M23, m.M22, m.M21, 0f,
        m.M13, m.M12, m.M11, 0f,
        m.M43, m.M42, m.M41, 1f);


    private static Matrix4x4 ToMatrix(Native.Model.PrefabTransformW t) => new(
        t.Row0.X, t.Row0.Y, t.Row0.Z, 0f,
        t.Row1.X, t.Row1.Y, t.Row1.Z, 0f,
        t.Row2.X, t.Row2.Y, t.Row2.Z, 0f,
        t.Translation.X, t.Translation.Y, t.Translation.Z, 1f);

    private static Native.Model.PrefabTransformW FromMatrix(Matrix4x4 m) => new()
    {
        Translation = m.Translation,
        Row0 = new Vector3(m.M11, m.M12, m.M13),
        Row1 = new Vector3(m.M21, m.M22, m.M23),
        Row2 = new Vector3(m.M31, m.M32, m.M33),
    };
}
