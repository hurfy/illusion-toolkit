using Illusion.Formats.IO;

namespace Illusion.Formats.Navigation;

/// <summary>
/// AI navigation world (.nav / NAV_AIWORLD_DATA): a versioned container of pathfinding objects for one
/// world. This port types and validates the self-describing frame (size, magic 1005, world id, generation
/// name, footer magic 0x1214F001) and now also types the whole navpoint hierarchy — the ten navpoint types
/// nested under a C_AIWorldPart in MafiaToolkitV2 — flattened to preorder in <see cref="PathObjects"/>, so
/// the file round-trips byte-exact through the native codec. (The vendor reader ignores the generation-name
/// footer and its writer drops it; this port preserves it.)
/// </summary>
public sealed class AiWorldFile
{
    public const uint Magic = 1005;
    public const uint FooterMagic = 0x1214F001;

    public uint WorldId { get; set; }
    public uint PathObjectCount { get; set; }
    /// <summary>The navpoint hierarchy in preorder (each node carries its type tag, child count and the
    /// fields its type uses — see the native NavPointW). Internal until the property panel edits navpoints.</summary>
    internal List<Native.Model.NavPointW> PathObjects { get; set; } = new();
    /// <summary>The trailing generation name (a source path the tool stamps in).</summary>
    public string GenerationName { get; set; } = string.Empty;

    /// <summary>
    /// The path objects as box wireframe line segments — each object's volume (position ± half-extents) as a
    /// box, ready for a line-list overlay. These are the AI action/cover/waypoint markers (cover and
    /// vault-over spots, sidewalks, crossings, …). Unlike .nov, .nav is stored in the engine frame (Z-up), so
    /// coordinates are used verbatim — no Kynapse swap. Zero-extent objects collapse to a point (invisible);
    /// the ones with a real volume — cover and action markers — stand out.
    /// </summary>
    public IReadOnlyList<System.Numerics.Vector3> PathObjectBoxLines()
    {
        var lines = new List<System.Numerics.Vector3>();
        foreach (Native.Model.NavPointW po in PathObjects)
        {
            System.Numerics.Vector3 ext = po.HalfExtents;
            if (ext.LengthSquared() < 1e-6f) continue; // zero-volume waypoints (the dense graph) — nothing to show
            // Oriented by the object's facing (Direction), so cover boxes align with the wall they face
            // instead of the world axes. .nav is already engine Z-up, so Position/Direction are used raw.
            NavViewGeometry.AddOrientedBox(lines, po.Position, ext, po.Direction);
        }
        return lines;
    }

    /// <summary>Per-type path-object counts (the numeric NavPoint type id → how many). Used to break the
    /// world down into meaningful groups (cover, waypoints, pedestrian, …) for the scene tree.</summary>
    public IReadOnlyDictionary<int, int> PathObjectTypeCounts()
    {
        var counts = new Dictionary<int, int>();
        foreach (Native.Model.NavPointW po in PathObjects)
            counts[po.Type] = counts.GetValueOrDefault(po.Type) + 1;
        return counts;
    }

    public static AiWorldFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static AiWorldFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadAiWorld(bytes);
    }


    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        Write(stream);
        return stream.ToArray();
    }

    public void Write(Stream output)
    {
        output.WriteBytes(Native.Misc.NativeMiscFiles.AiWorldToBytes(this));
    }

}
