using Illusion.Formats.IO;

namespace Illusion.Formats.Navigation;

/// <summary>
/// AI object data (.nov / NAV_OBJ_DATA): a navigation sub-graph plus a Kynogon runtime nav-mesh, framed
/// by a header (size, magic 0xD6D6F0F0, generation name) and a footer (generation name, its length,
/// magic 0x1213F001). This port types and validates the whole file: the frame, the navigation sub-graph
/// (vertices and edges) and the Kynogon AI-mesh (grid, cells, sets and their box arrays) — all raw-preserved
/// for an exact round-trip. Only the trailing Obj5-8 records inside the mesh stay an opaque capsule.
/// </summary>
public sealed class ObjDataFile
{
    public const uint HeaderMagic = 0xD6D6F0F0;
    public const uint FooterMagic = 0x1213F001;

    /// <summary>The object's name bytes (as stored, length-prefixed in the header).</summary>
    public byte[] Name { get; set; } = Array.Empty<byte>();
    /// <summary>The trailing generation name (a source path the tool stamps in).</summary>
    public string GenerationName { get; set; } = string.Empty;

    // The navigation sub-graph and the Kynogon AI-mesh, both typed (raw-preserved). Internal until
    // the property panel / viewport consume them; the mesh's Obj5-8 tail rides opaque inside NavMeshW.
    internal uint GraphVersion { get; set; }
    internal uint GraphId { get; set; }
    internal uint GraphTag0 { get; set; }
    internal uint GraphTag1 { get; set; }
    internal List<Native.Model.NavGraphVertexW> GraphVertices { get; set; } = new();
    internal List<Native.Model.NavGraphEdgeW> GraphEdges { get; set; } = new();
    internal Native.Model.NavMeshW Aimesh { get; set; } = new();

    /// <summary>Number of navigation-graph vertices (nodes).</summary>
    public int GraphVertexCount => GraphVertices.Count;

    /// <summary>Number of navigation-graph edges.</summary>
    public int GraphEdgeCount => GraphEdges.Count;

    /// <summary>Number of AI-mesh cells (the Kynogon spatial grid cells).</summary>
    public int AiMeshCellCount => Aimesh.Cells.Count;

    /// <summary>
    /// The navigation sub-graph as line segments: each edge becomes an (A, B) pair of vertex positions,
    /// ready to feed a line-list overlay. Positions are converted from the file's Kynapse space (Y-up) to
    /// the engine/viewport space (Z-up) via (x, y, z) → (x, -z, y), so the graph aligns with the district
    /// meshes. Edges referencing an out-of-range vertex are skipped.
    /// </summary>
    public IReadOnlyList<System.Numerics.Vector3> GraphLineVertices()
    {
        List<Native.Model.NavGraphVertexW> verts = GraphVertices;
        var lines = new List<System.Numerics.Vector3>(GraphEdges.Count * 2);
        foreach (Native.Model.NavGraphEdgeW edge in GraphEdges)
        {
            if (edge.StartVertex >= (uint)verts.Count || edge.EndVertex >= (uint)verts.Count) continue;
            lines.Add(NavViewGeometry.ToViewSpace(verts[(int)edge.StartVertex].Position));
            lines.Add(NavViewGeometry.ToViewSpace(verts[(int)edge.EndVertex].Position));
        }
        return lines;
    }

    /// <summary>
    /// The Kynogon AI-mesh's bounding boxes (walkable-cell / link volumes) as wireframe line segments —
    /// every box expands to its 12 edges (24 line vertices), ready to feed a line-list overlay. Boxes come
    /// from all cells' sets (the cell/link/edge box arrays). Coordinates are converted from Kynapse space to
    /// the viewport frame, matching <see cref="GraphLineVertices"/>.
    /// </summary>
    public IReadOnlyList<System.Numerics.Vector3> AiMeshBoxLines()
    {
        var lines = new List<System.Numerics.Vector3>();
        foreach (Native.Model.NavMeshCellW cell in Aimesh.Cells)
        {
            foreach (Native.Model.NavMeshSetW set in cell.Sets)
            {
                foreach (Native.Model.NavMeshUnk10W b in set.Unk10) NavViewGeometry.AddBox(lines, b.BoxMin, b.BoxMax);
                foreach (Native.Model.NavMeshUnk12W b in set.Unk12) NavViewGeometry.AddBox(lines, b.BoxMin, b.BoxMax);
                foreach (Native.Model.NavMeshEdgeBoxW b in set.Edges) NavViewGeometry.AddBox(lines, b.BoxMin, b.BoxMax);
            }
        }
        return lines;
    }

    public static ObjDataFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static ObjDataFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadObjData(bytes);
    }


    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        Write(stream);
        return stream.ToArray();
    }

    public void Write(Stream output)
    {
        output.WriteBytes(Native.Misc.NativeMiscFiles.ObjDataToBytes(this));
    }

}
