using System.Numerics;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Prefab;

namespace Illusion.Assets.Prefabs;

/// <summary>
/// Keeping a climb box's PREFAB ROW in step with the Dummy it names.
///
/// <para>
/// A climb box is written down twice, and only one copy is read — the same trap the collision volumes set.
/// The row carries a world-space Min/Max, the Dummy carries a box and a placement, and the game climbs the
/// ROW. Measured on the shipped cars (<c>--probe-car-items</c>): 265 of 281 rows are exactly the Dummy's own
/// extents moved to where the Dummy stands, and none of them are those extents left unplaced. The 16 that
/// disagree are archives somebody has already edited by hand.
/// </para>
/// <para>
/// That is why a minted climb box could not be climbed: the row was copied from the donor, so the new box
/// sat exactly on top of an existing one, and dragging or scaling its Dummy changed nothing the game reads.
/// </para>
/// </summary>
public static class CarClimbBoxes
{
    /// <summary>
    /// Rewrites every climb-box row from the Dummy it names, so the box the game climbs is the box the
    /// editor draws. Scale and rotation come along: the row is an axis-aligned box in the car's own space,
    /// and that is what the eight corners of a placed Dummy give.
    /// </summary>
    /// <returns>How many rows had to change; 0 leaves the file untouched.</returns>
    public static int SyncFromFrames(string extractedFolder, FrameResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        (PrefabFile Prefab, string Path)? opened = OpenCarPrefab(extractedFolder);
        if (opened == null) return 0;
        (PrefabFile prefab, string path) = opened.Value;
        CarPrefab? car = prefab.Car;
        if (car == null) return 0;

        var byHash = new Dictionary<ulong, FrameObjectDummy>();
        foreach (FrameObjectDummy dummy in
                 (resource.FrameObjects?.Values ?? Enumerable.Empty<object>()).OfType<FrameObjectDummy>())
        {
            if (dummy.Name.String is { Length: > 0 }) byHash.TryAdd(dummy.Name.Hash, dummy);
        }

        int written = 0;
        IReadOnlyList<CarPrefab.ClimbBox> boxes = car.ClimbBoxes;
        for (int i = 0; i < boxes.Count; i++)
        {
            if (!byHash.TryGetValue(boxes[i].Dummy, out FrameObjectDummy? dummy)) continue;
            (Vector3 min, Vector3 max) = PlacedBox(dummy);
            if (Near(min, boxes[i].Min) && Near(max, boxes[i].Max)) continue;

            bool ok = true;
            for (int axis = 0; axis < 3; axis++)
            {
                ok &= prefab.SetCarValue(CarValueSlot.ClimbBoxMin, i, axis, Axis(min, axis));
                ok &= prefab.SetCarValue(CarValueSlot.ClimbBoxMax, i, axis, Axis(max, axis));
            }
            if (ok) written++;
        }

        if (written > 0) AtomicFile.WriteAllBytes(path, prefab.ToBytes());
        return written;
    }

    /// <summary>The Dummy's own box where the Dummy stands — all eight corners through its world matrix, so
    /// a rotated or scaled one still yields the axis-aligned box the row wants.</summary>
    public static (Vector3 Min, Vector3 Max) PlacedBox(FrameObjectDummy dummy)
    {
        ArgumentNullException.ThrowIfNull(dummy);
        Matrix4x4 world = dummy.WorldTransform;
        Vector3 lo = dummy.Bounds.Min, hi = dummy.Bounds.Max;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3(
                (corner & 1) != 0 ? hi.X : lo.X,
                (corner & 2) != 0 ? hi.Y : lo.Y,
                (corner & 4) != 0 ? hi.Z : lo.Z);
            Vector3 at = Vector3.Transform(point, world);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        return (min, max);
    }

    private static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-4f;

    private static float Axis(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static (PrefabFile Prefab, string Path)? OpenCarPrefab(string extractedFolder)
    {
        IReadOnlyList<string> files;
        try { files = SdsManifest.Load(extractedFolder).GetFiles("PREFAB"); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return null; }

        foreach (string file in files)
        {
            try
            {
                if (PrefabFile.Load(file) is { Car: not null } prefab) return (prefab, file);
            }
            catch (Exception ex) when (ex is IOException or SdsFormatException) { /* try the next one */ }
        }
        return null;
    }
}
