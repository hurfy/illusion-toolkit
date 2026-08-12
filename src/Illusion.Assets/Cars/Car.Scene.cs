using System.Numerics;
using Illusion.Formats;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.ItemDesc;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Cars;

/// <summary>
/// What a car's SCENE save has to carry through to the file — and the reason the editor's ordinary
/// "save the working copy" is the aggregate's business on a car rather than the frame writer's.
///
/// <para>
/// Three of the car's things are written down twice, once in the frame graph the modder drags and once in a
/// prefab row the game actually reads: a collision volume beside its mirror stub, a climb box beside its
/// Dummy, a seat beside the position its Dummy stands at. Dragging the frame and saving the frame resource
/// changes what the editor draws and nothing the game reads, and that is exactly how a newly added climb box
/// turned out to be unclimbable. So a car's save writes both halves, through the one seam.
/// </para>
/// <para>
/// Only the frames a modder actually MOVED are carried over. 18 of the 280 shipped climb-box rows already
/// disagree with their Dummy, and 37 of the 1097 collision pairs do; rewriting those from a frame nobody
/// touched would change cars nobody asked about.
/// </para>
/// </summary>
public sealed partial class Car
{
    /// <summary>
    /// Writes the placement of every stub a modder has DRAGGED through to the prefab volume that names its
    /// shape — the half of the archive the game reads.
    ///
    /// <para>
    /// A SCALE on the stub is folded into the shape's own dimensions first, because neither the volume nor a
    /// PhysX shape transform has anywhere to keep one: dropping it silently is what made a box drawn 0.41 m
    /// thick in the editor reach 0.10 m in the game, thin enough for an arm to pass through the part.
    /// </para>
    /// <para>
    /// A cooked hull cannot absorb one — rescaling it means re-cooking, which the vendored cooker cannot do
    /// for a convex shape. Its scale is therefore dropped from the volume and left on the frame, so the
    /// editor keeps drawing what the modder did while the game keeps the hull's own size. That is the
    /// shipped behaviour rather than a decision made here, and the row that says so is the collision's
    /// read-only reason on its component.
    /// </para>
    /// </summary>
    /// <returns>How many volumes were rewritten. Nothing reaches a file here; <see cref="Save"/> does that.</returns>
    public int SyncStubs(IReadOnlyCollection<FrameObjectCollision> moved)
    {
        ArgumentNullException.ThrowIfNull(moved);

        int written = 0;
        foreach (FrameObjectCollision stub in moved)
        {
            if (!_shapesByFile.TryGetValue(stub.Hash, out CarShapeRecord? record)
                || record.Shape.Element is not { } element)
            {
                continue;
            }

            Bake(stub, record);
            Matrix4x4 placement = Unscaled(stub.LocalTransform);
            foreach (CarDeformPart part in Prefab.CarDeformParts)
            {
                foreach (CarPhysicsVolume volume in part.Volumes)
                {
                    if (volume.ShapeHash != element.DataHash) continue;
                    if (Prefab.SetCarVolume(part.Index, volume.Index, placement, volume.Size)) written++;
                }
            }
        }
        return written;
    }

    /// <summary>Folds a dragged stub's scale into the shape it names and leaves the stub's matrix unscaled.</summary>
    private void Bake(FrameObjectCollision stub, CarShapeRecord record)
    {
        Matrix4x4 m = stub.LocalTransform;
        var scale = new Vector3(
            new Vector3(m.M11, m.M12, m.M13).Length(),
            new Vector3(m.M21, m.M22, m.M23).Length(),
            new Vector3(m.M31, m.M32, m.M33).Length());
        if (MathF.Abs(scale.X - 1f) < 1e-4f && MathF.Abs(scale.Y - 1f) < 1e-4f
            && MathF.Abs(scale.Z - 1f) < 1e-4f)
        {
            return;
        }
        if (scale.X <= 0f || scale.Y <= 0f || scale.Z <= 0f) return;
        if (record.Shape.Element is not RigidBodyElement rigid) return;

        switch (rigid.Shape)
        {
            case RigidBodyShape.Box:
                rigid.BoxDimensions *= scale;
                break;
            case RigidBodyShape.Sphere:
                // A sphere has one number, so only a uniform scale means anything; the largest is the safe
                // reading — a shape that grew is better than one that quietly did not.
                rigid.Radius *= MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
                break;
            case RigidBodyShape.Capsule:
            case RigidBodyShape.Cylinder:
                // The axis is local Z (measured over the 251 shipped capsules), so the height takes Z and the
                // radius the wider of the two across it.
                rigid.Radius *= MathF.Max(scale.X, scale.Y);
                rigid.Height *= scale.Z;
                break;
            default:
                return;     // a cooked hull cannot be rescaled without a cooker we do not have
        }

        _pendingShapes[record.File] = record.Shape.ToBytes();
        stub.LocalTransform = Unscaled(m);
    }

    /// <summary>
    /// The same placement with its basis renormalized. A frame may be scaled; neither a prefab volume nor a
    /// PhysX shape transform can carry one, so passing it on would write a matrix the engine cannot honour.
    /// </summary>
    private static Matrix4x4 Unscaled(Matrix4x4 m)
    {
        var x = new Vector3(m.M11, m.M12, m.M13);
        var y = new Vector3(m.M21, m.M22, m.M23);
        var z = new Vector3(m.M31, m.M32, m.M33);
        x = x.LengthSquared() > 1e-12f ? Vector3.Normalize(x) : Vector3.UnitX;
        y = y.LengthSquared() > 1e-12f ? Vector3.Normalize(y) : Vector3.UnitY;
        z = z.LengthSquared() > 1e-12f ? Vector3.Normalize(z) : Vector3.UnitZ;
        return new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            m.M41, m.M42, m.M43, 1f);
    }

    /// <summary>
    /// Saves the working copy of an archive the editor has open: the frame graph, its name table when a frame
    /// was added or removed, and the prefab rows that the frames it moved are written down in twice.
    ///
    /// <para>
    /// This is where a car's frame resource is written, and it is the only place: the generic scene writer
    /// serves every other kind of archive, and a car going through it would be a second path to the same two
    /// files. The car is stitched FRESH here rather than carried, because the graph the editor holds is the
    /// truth and the prefab on disk may have moved since the tree last read it.
    /// </para>
    /// </summary>
    /// <param name="movedMarkers">Marker frames the modder dragged — a climb box's Dummy, a seat's.</param>
    /// <param name="movedStubs">Collision stubs the modder dragged.</param>
    /// <param name="nameTable">Whether a frame was added or removed, which is when the name table has to be
    /// rebuilt. A frame missing from it loads and is invisible in game.</param>
    /// <param name="lost">What the save would not deliver, or null when it delivered everything. A save can be
    /// REFUSED — the prefab has to survive being written and read back, and it refuses outright if somebody
    /// else has written the file since — and a caller that threw this away would let a frame move in the
    /// editor and nowhere else, silently.</param>
    /// <returns>The car this wrote, or null when the archive carries none — in which case nothing was written
    /// and the caller falls back to the plain frame writer.</returns>
    public static Car? SaveScene(
        string extracted, FrameResource frames,
        IReadOnlyCollection<FrameObjectBase> movedMarkers,
        IReadOnlyCollection<FrameObjectCollision> movedStubs,
        bool nameTable, out string? lost)
    {
        ArgumentException.ThrowIfNullOrEmpty(extracted);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(movedMarkers);
        ArgumentNullException.ThrowIfNull(movedStubs);
        lost = null;

        Car? car = Open(extracted, frames);
        if (car == null) return null;

        // The stubs are LIVE frames of the editor's graph, and baking a scale changes them. Everything else
        // this touches belongs to the throwaway car stitched a line ago — so a refused save has exactly one
        // thing to put back, and it has to: the modder resized a box, the save said no, and a stub left
        // unscaled is that resize destroyed rather than held for the next save.
        var was = movedStubs.Select(s => (Stub: s, s.LocalTransform)).ToList();

        car.SyncMarkers(movedMarkers);
        car.SyncStubs(movedStubs);
        car.TouchFrames(nameTable);

        CarSave saved = car.Save();
        if (saved.Ok) return car;

        lost = string.Join("; ", saved.Lost);
        foreach ((FrameObjectCollision stub, Matrix4x4 local) in was) stub.LocalTransform = local;
        return car;
    }

    /// <summary>
    /// Drops the collision volume a stub places, so deleting the frame really deletes the collision.
    ///
    /// <para>
    /// A stub is not the collision — the prefab volume is, and the game reads only that. Removing the frame on
    /// its own leaves a car that still collides exactly as before while the editor shows nothing there, which
    /// is "I deleted it, it is gone from the tree, and it is still in the game".
    /// </para>
    /// <para>
    /// The SHAPE record is deliberately left behind. One nothing names is inert, and deleting it would break
    /// any other volume naming the same shape — the archive keeps a few bytes rather than risking that.
    /// </para>
    /// </summary>
    /// <returns>What was removed, so an undo puts back exactly those bytes; null when the stub places no
    /// volume, or when the save was refused, in which case <paramref name="lost"/> says why.</returns>
    public static CarStubVolume? DropVolumeOfStub(
        string extracted, FrameResource frames, FrameObjectCollision stub, out string? lost)
    {
        ArgumentNullException.ThrowIfNull(stub);
        lost = null;

        Car? car = Open(extracted, frames);
        if (car?.Prefab.Car == null) return null;
        if (!car._shapesByFile.TryGetValue(stub.Hash, out CarShapeRecord? record)
            || record.Shape.Element is not { } element)
        {
            return null;
        }

        foreach (CarDeformPart part in car.Prefab.CarDeformParts)
        {
            foreach (CarPhysicsVolume volume in part.Volumes)
            {
                if (volume.ShapeHash != element.DataHash) continue;
                if (car.Prefab.TakeCarVolume(part.Index, volume.Index) is not { } taken) return null;

                CarSave saved = car.Save();
                if (saved.Ok) return new CarStubVolume(part.Index, volume.Index, taken);
                lost = string.Join("; ", saved.Lost);
                return null;
            }
        }
        return null;
    }

    /// <summary>Puts a dropped volume back exactly where it was — the undo of <see cref="DropVolumeOfStub"/>.</summary>
    public static bool PutVolumeOfStub(
        string extracted, FrameResource frames, CarStubVolume removed, out string? lost)
    {
        ArgumentNullException.ThrowIfNull(removed);
        lost = null;

        Car? car = Open(extracted, frames);
        if (car == null) return false;
        if (!car.Prefab.PutCarVolume(removed.Part, removed.Volume, removed.Item)) return false;

        CarSave saved = car.Save();
        if (saved.Ok) return true;
        lost = string.Join("; ", saved.Lost);
        return false;
    }

    /// <summary>The car of a working copy, stitched against a frame graph the caller already holds. Null when
    /// the archive carries no car prefab, which is most archives.</summary>
    private static Car? Open(string extracted, FrameResource frames)
    {
        foreach (string file in PrefabFiles(extracted))
        {
            PrefabFile prefab;
            try { prefab = PrefabFile.Load(file); }
            catch (Exception ex) when (ex is IOException or SdsFormatException) { continue; }
            if (prefab.Car == null) continue;
            return Stitch(prefab, frames, lod: 0, previous: null, prefabPath: file, extracted: extracted);
        }
        return null;
    }
}

/// <summary>A collision volume a deleted stub took with it, kept whole so putting it back is exact.</summary>
/// <param name="Item">The volume's own bytes — an undo restores these rather than a look-alike.</param>
public sealed record CarStubVolume(int Part, int Volume, byte[] Item);
