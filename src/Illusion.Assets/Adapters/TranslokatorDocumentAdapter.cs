using System.Numerics;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Translokator;

namespace Illusion.Assets.Adapters;

/// <summary>
/// Adapts one loaded Translokator table — the placement list city_crash spawns its props from — into the Domain's
/// <see cref="ISceneDocument"/> save unit, the crash analog of <see cref="CollisionDocumentAdapter"/>. Slotting
/// into <c>ISceneDocument</c> is what makes crash edits ride the same Save/Build/backup pipeline as frame and
/// collision edits.
///
/// It also owns the seasonal pair. <c>city_crash.sds</c> and <c>city_crash_z.sds</c> ship the very same
/// placements — same objects, same instance IDs, same transforms, differing only in filler bytes — so a placement
/// exists in both seasons and an edit normally belongs in both. The twin table is loaded alongside this one and
/// mirrored into whenever the edited placement is <see cref="TranslokatorInstanceAdapter.SeasonLinked"/>; the
/// archive it came from rides along in <see cref="CompanionArchives"/> so a build repacks it too.
/// </summary>
public sealed class TranslokatorDocumentAdapter : ISceneDocument
{
    private readonly TranslokatorLoader _table;
    private readonly Dictionary<Instance, TranslokatorInstanceAdapter> _nodes = new();
    private readonly TranslokatorLoader? _twin;
    private readonly FileInfo? _twinArchive;
    private bool _twinEdited;

    public TranslokatorDocumentAdapter(TranslokatorLoader table, FileInfo sourceArchive,
        TranslokatorLoader? twin = null, FileInfo? twinArchive = null)
    {
        _table = table;
        SourceArchive = sourceArchive;
        _twin = twin;
        _twinArchive = twinArchive;
    }

    public FileInfo SourceArchive { get; }

    /// <summary>The wrapped placement table — what the edits mutate and <see cref="SaveWorkingCopy"/>
    /// serializes.</summary>
    public TranslokatorLoader Table => _table;

    /// <summary>The other season's table, or null when this archive has no seasonal twin (Sicily, or a winter
    /// archive that was never extracted). With no twin every placement is season-local.</summary>
    public TranslokatorLoader? Twin => _twin;

    /// <summary>Set when a placement changed so the instanced prop cloud on screen is stale; the streamer
    /// consumes it once per frame to re-upload the affected matrices (live during a gizmo drag). Not persistence
    /// — that is tracked separately by ScenePersistence.</summary>
    public bool RenderDirty { get; set; }

    // Which rows changed since the streamer last refreshed. A gizmo drag raises this every frame, and the crash
    // archive holds ~800 prototype meshes carrying 134 000 copies between them — rebuilding all of them per frame
    // is what a drag used to cost. Only the dragged row's meshes actually need re-uploading.
    private readonly HashSet<Formats.Translokator.Object> _dirtyRows = new();

    /// <summary>Marks a row's copies as stale on screen (an edit touched one of them).</summary>
    public void MarkRowDirty(Formats.Translokator.Object row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _dirtyRows.Add(row);
        RenderDirty = true;
    }

    /// <summary>Takes the rows whose copies changed since the last call, and clears the set.</summary>
    public IReadOnlyList<Formats.Translokator.Object> ConsumeDirtyRows()
    {
        var rows = new List<Formats.Translokator.Object>(_dirtyRows);
        _dirtyRows.Clear();
        return rows;
    }

    public int ObjectCount => PlacementCount(_table);
    public int GeometryCount => 0;
    public int MaterialCount => 0;
    public int SkeletonCount => 0;
    public int SceneCount => 0;

    public IReadOnlyList<FileInfo> CompanionArchives =>
        _twinEdited && _twinArchive != null ? [_twinArchive] : [];

    private static int PlacementCount(TranslokatorLoader table)
    {
        int n = 0;
        foreach (ObjectGroup group in table.ObjectGroups)
        {
            foreach (Formats.Translokator.Object obj in group.Objects) n += obj.Instances.Count;
        }
        return n;
    }

    /// <summary>The canonical adapter for a placement of this document — one per instance, cached by reference,
    /// so selection and editing key by identity the way frame objects do.</summary>
    public TranslokatorInstanceAdapter Node(Instance instance, Formats.Translokator.Object owner)
    {
        if (!_nodes.TryGetValue(instance, out TranslokatorInstanceAdapter? node))
        {
            _nodes[instance] = node = new TranslokatorInstanceAdapter(instance, owner, this);
        }
        return node;
    }

    /// <summary>Whether the other season holds the same placement (matched by the table-wide unique instance ID
    /// under the same object name). False when there is no twin table at all.</summary>
    public bool HasTwinOf(Instance instance, Formats.Translokator.Object owner) =>
        FindTwin(instance, owner) != null;

    // Name → row of the twin table, and per twin row an id → placement index. Both are built on first use and
    // maintained by the add/remove paths. A gizmo drag mirrors on every frame, and scanning the winter table for
    // the matching placement (57 648 of them, inside lists) per frame is what a drag used to spend its time on.
    private Dictionary<string, Formats.Translokator.Object>? _twinRows;
    private readonly Dictionary<Formats.Translokator.Object, Dictionary<ushort, Instance>> _twinById = new();

    // The twin of a placement: same object name, same instance ID. IDs are unique across the whole table in the
    // shipped data and the editor keeps them that way, so the pair is unambiguous.
    private (Formats.Translokator.Object Owner, Instance Instance)? FindTwin(
        Instance instance, Formats.Translokator.Object owner)
    {
        Formats.Translokator.Object? twinRow = TwinRowOf(owner);
        if (twinRow == null) return null;
        Dictionary<ushort, Instance> byId = TwinIndexOf(twinRow);
        return byId.TryGetValue(instance.ID, out Instance? other) ? (twinRow, other) : null;
    }

    /// <summary>The row of the twin table that matches <paramref name="owner"/> by name, or null.</summary>
    private Formats.Translokator.Object? TwinRowOf(Formats.Translokator.Object owner)
    {
        if (_twin == null) return null;
        if (_twinRows == null)
        {
            _twinRows = new Dictionary<string, Formats.Translokator.Object>(StringComparer.Ordinal);
            foreach (ObjectGroup group in _twin.ObjectGroups)
            {
                foreach (Formats.Translokator.Object candidate in group.Objects)
                {
                    _twinRows.TryAdd(candidate.Name.String, candidate);
                }
            }
        }
        return _twinRows.TryGetValue(owner.Name.String, out Formats.Translokator.Object? row) ? row : null;
    }

    private Dictionary<ushort, Instance> TwinIndexOf(Formats.Translokator.Object twinRow)
    {
        if (!_twinById.TryGetValue(twinRow, out Dictionary<ushort, Instance>? byId))
        {
            byId = new Dictionary<ushort, Instance>(twinRow.Instances.Count);
            foreach (Instance copy in twinRow.Instances) byId[copy.ID] = copy;
            _twinById[twinRow] = byId;
        }
        return byId;
    }

    /// <summary>
    /// Keeps the streaming grid's per-cell counts in step with a placement that moved. The table carries one grid
    /// per draw distance, whose cells count how many placements of that distance fall in them; the engine streams
    /// by those counts, so a moved prop that is still counted in its old cell is a table that lies about itself.
    /// Decrementing the old cell and incrementing the new one leaves the file untouched when the move stayed
    /// inside one cell, which is what keeps an unrelated edit byte-exact.
    /// </summary>
    public void MovePlacement(Formats.Translokator.Object owner, Vector3 from, Vector3 to)
    {
        AdjustGrid(_table, owner, from, -1);
        AdjustGrid(_table, owner, to, +1);
    }

    /// <summary>Copies the edited placement's transform onto its twin in the other season, when the placement is
    /// linked. A no-op without a twin table, without a linked flag, or when the twin is gone.</summary>
    public void MirrorTransform(TranslokatorInstanceAdapter node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.SeasonLinked) return;
        if (FindTwin(node.Instance, node.Owner) is not var (twinOwner, twin)) return;

        AdjustGrid(_twin!, twinOwner, twin.Position, -1);
        twin.Position = node.Instance.Position;
        twin.Rotation = node.Instance.Rotation;
        twin.Scale = node.Instance.Scale;
        AdjustGrid(_twin!, twinOwner, twin.Position, +1);
        _twinEdited = true;
    }

    /// <summary>
    /// Adds a placement to a table row at <paramref name="index"/> (append when out of range), updating the
    /// streaming grid. With <paramref name="mirror"/> the same placement is added to the other season, which is
    /// what "place it in both seasons" means at the file level.
    /// </summary>
    public void InsertPlacement(Formats.Translokator.Object owner, Instance instance, int index, bool mirror)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(instance);

        index = Math.Clamp(index, 0, owner.Instances.Count);
        owner.Instances.Insert(index, instance);
        AdjustGrid(_table, owner, instance.Position, +1);

        if (mirror && TwinRowOf(owner) is { } twinRow && FindTwin(instance, owner) == null)
        {
            Instance copy = Clone(instance);
            twinRow.Instances.Add(copy);
            TwinIndexOf(twinRow)[copy.ID] = copy;
            AdjustGrid(_twin!, twinRow, copy.Position, +1);
            _twinEdited = true;
        }
        MarkRowDirty(owner);
    }

    /// <summary>Removes a placement from its table row, updating the streaming grid. With
    /// <paramref name="mirror"/> the twin in the other season goes with it.</summary>
    public void RemovePlacement(Formats.Translokator.Object owner, Instance instance, bool mirror)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(instance);

        if (mirror && FindTwin(instance, owner) is var (twinOwner, twin))
        {
            twinOwner.Instances.Remove(twin);
            TwinIndexOf(twinOwner).Remove(twin.ID);
            AdjustGrid(_twin!, twinOwner, twin.Position, -1);
            _twinEdited = true;
        }

        if (owner.Instances.Remove(instance))
        {
            AdjustGrid(_table, owner, instance.Position, -1);
        }
        _nodes.Remove(instance);
        MarkRowDirty(owner);
    }

    /// <summary>A deep copy of a placement — a fresh record with the same transform, ready to be given its own
    /// ID and inserted.</summary>
    public static Instance Clone(Instance source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Instance
        {
            Position = source.Position,
            Rotation = source.Rotation,
            Scale = source.Scale,
            ID = source.ID,
            W0 = source.W0,
            W1 = source.W1,
            W2 = source.W2,
            D4 = source.D4,
            D5 = source.D5,
        };
    }

    /// <summary>
    /// An instance ID free in this table and, when there is one, in the twin — the record's ID is a table-wide
    /// 16-bit handle, so a duplicate would make two props indistinguishable to anything keying on it. The shipped
    /// city table already spends 57 648 of the 65 536 ids, so this can genuinely run out; it reports that instead
    /// of handing back a colliding id.
    /// </summary>
    public bool TryAllocateId(out ushort id)
    {
        var used = new HashSet<ushort>();
        Collect(_table, used);
        if (_twin != null) Collect(_twin, used);

        for (int candidate = 0; candidate <= ushort.MaxValue; candidate++)
        {
            if (used.Contains((ushort)candidate)) continue;
            id = (ushort)candidate;
            return true;
        }
        id = 0;
        return false;

        static void Collect(TranslokatorLoader table, HashSet<ushort> into)
        {
            foreach (ObjectGroup group in table.ObjectGroups)
            {
                foreach (Formats.Translokator.Object obj in group.Objects)
                {
                    foreach (Instance instance in obj.Instances) into.Add(instance.ID);
                }
            }
        }
    }

    // One grid per draw distance; a row is counted by the grid whose key equals its GridMax. Cells are row-major
    // over the map's X/Y extent (Z is up in this data), and a placement outside the extent clamps to the edge
    // cell — the same rule that reproduces every shipped grid.
    private static void AdjustGrid(TranslokatorLoader table, Formats.Translokator.Object owner,
        Vector3 position, int delta)
    {
        foreach (Grid grid in table.Grids)
        {
            if (grid.Key != (short)owner.GridMax) continue;
            if (grid.Width <= 0 || grid.Height <= 0 || grid.CellSize.X <= 0f || grid.CellSize.Y <= 0f) return;

            int cx = Math.Clamp((int)((position.X - grid.Origin.X) / grid.CellSize.X), 0, grid.Width - 1);
            int cy = Math.Clamp((int)((position.Y - grid.Origin.Y) / grid.CellSize.Y), 0, grid.Height - 1);
            int cell = cy * grid.Width + cx;
            if (cell < 0 || cell >= grid.Data.Length) return;

            grid.Data[cell] = (ushort)Math.Clamp(grid.Data[cell] + delta, 0, ushort.MaxValue);
            return;
        }
    }

    /// <summary>Rewrites the .tra in the extracted folder from the current placements, and the twin season's too
    /// when an edit reached it. Packing back to the .sds is a separate step (<c>ScenePersistence.BuildEdits</c> →
    /// <c>SdsWriter.PackSds</c>), shared with frame and collision edits.</summary>
    public string SaveWorkingCopy()
    {
        string written = SdsTranslokatorSaver.SaveWorkingCopy(_table, SourceArchive);
        if (_twinEdited && _twin != null && _twinArchive != null)
        {
            SdsTranslokatorSaver.SaveWorkingCopy(_twin, _twinArchive);
        }
        return written;
    }

    /// <summary>The crash table carries no FrameNameTable — nothing to flag.</summary>
    public void MarkNameTableDirty() { }

    /// <summary>Placements are a flat list with no parent hierarchy — they never reparent.</summary>
    public bool Reparent(IFrameNode child, ISceneSource? newParent) => false;
}
