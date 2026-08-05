using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows.Data;
using Illusion.Assets.Adapters;
using Illusion.Assets.EntityData;
using Illusion.Assets.Prefabs;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Domain.Properties;
using Illusion.Formats.Prefab;
using Illusion.Rendering.Gizmos;
using Illusion.Scene;
using Illusion.Viewport;
using PropertyDescriptor = Illusion.Domain.Properties.PropertyDescriptor;

namespace Illusion.ViewModels;

/// <summary>
/// View-model behind the contextual property tabs. Holds the selected <see cref="SceneNode"/>, exposes the
/// booleans the tabs bind their visibility to, and — for a transformable frame object — the editable local
/// Position / Rotation (Euler degrees) / Scale. Field edits recompose the local transform and commit it through
/// the viewport (which re-syncs the GPU meshes); a gizmo drag calls <see cref="RefreshTransform"/> so the
/// fields track it live.
/// </summary>
public sealed class SelectionViewModel : INotifyPropertyChanged
{
    private readonly D3DImageHost _viewport;
    private bool _applyingField; // suppresses the self-refresh while a field edit commits

    public SelectionViewModel(D3DImageHost viewport) => _viewport = viewport;

    private SceneNode? _node;
    public SceneNode? Node => _node;

    public void SetNode(SceneNode? node)
    {
        // Re-selecting the SAME node (a reparent's reselect, undo/redo, a background mesh-attach) must
        // refresh values IN PLACE, never rebuild the parent picker: the reparent runs synchronously inside
        // the ListBox's mouse-DOWN push, and swapping the ItemsSource there yanks the filtered list out
        // from under the still-pressed click — the mouse-UP then lands on whatever row the UNFILTERED
        // list puts at that position and silently reparents to an arbitrary node.
        bool sameNode = node != null && ReferenceEquals(node, _node);
        _node = node;
        ReadTransform();
        if (sameNode)
        {
            RefreshPropertyValues();
            SyncSelectedParent();
        }
        else
        {
            BuildPropertyGroups();
            BuildMaterials();
            BuildParentCandidates();
            BuildPrefab();
            BuildTuning();
        }
        RaiseAll();
    }

    // ── Type flags (tab visibility) ──
    public bool HasTransform => _node?.Source is IFrameNode;
    public bool IsSds => _node?.Kind == "Sds";
    public bool IsFrameResource => _node?.Kind == "FrameResource";
    public bool IsScene => _node?.Kind == "Scene";
    public bool HasSelection => _node != null;

    // ── Header ──
    public string Title => _node?.Name ?? "—";
    public string ObjectType => _node == null ? "" : PrettyKind(_node.Kind);

    // ── Property groups (Object tab = common; per-type tab = type-specific) ──
    private IReadOnlyList<PropertyGroupViewModel> _commonGroups = Array.Empty<PropertyGroupViewModel>();
    private IReadOnlyList<PropertyGroupViewModel> _typeGroups = Array.Empty<PropertyGroupViewModel>();

    public IReadOnlyList<PropertyGroupViewModel> CommonGroups => _commonGroups;
    public IReadOnlyList<PropertyGroupViewModel> TypeGroups => _typeGroups;
    public bool HasTypeProperties => _typeGroups.Count > 0;

    /// <summary>Header/tooltip for the per-type property tab (e.g. "Light", "Single mesh").</summary>
    public string TypeTabTitle => ObjectType;

    private void BuildPropertyGroups()
    {
        var common = new List<PropertyGroupViewModel>();
        var type = new List<PropertyGroupViewModel>();
        if (_node is { Source: IPropertySource ps } node)
        {
            // Bind the commit to THIS node, not the live selection: a field that commits on lost-focus after the
            // selection already moved on must still edit the object the panel was built for.
            void Commit(PropertyDescriptor d, object? before, object? after) =>
                _viewport.CommitPropertyEdit(node, d, before, after);
            foreach (PropertyGroup g in ps.GetPropertyGroups())
                (g.IsTypeSpecific ? type : common).Add(new PropertyGroupViewModel(g, Commit));
        }
        _commonGroups = common;
        _typeGroups = type;
    }

    /// <summary>Re-reads every property row's value in place (after an undo/redo or a second-editor edit),
    /// keeping the panel's expander/scroll state. Also refreshes the header, so a rename shows immediately.</summary>
    public void RefreshPropertyValues()
    {
        Raise(nameof(Title));
        Raise(nameof(ObjectType));
        foreach (PropertyGroupViewModel g in _commonGroups) g.Refresh();
        foreach (PropertyGroupViewModel g in _typeGroups) g.Refresh();
    }

    // ── Prefab (archive root only) ──

    private PrefabAssembly? _prefab;
    private string? _prefabArchive;
    private int _prefabToken;

    /// <summary>Whether the open archive carries a PREFAB — the Prefab tab's visibility. False until the read
    /// finishes, so the tab appears rather than sitting empty (257 of 1324 archives carry one at all).</summary>
    public bool HasPrefab => _prefab != null;

    private IReadOnlyList<PrefabEntryRowsViewModel> _prefabRows = [];

    /// <summary>The prefab's entries as rows the panel can bind — each reference a picker over the archive's
    /// own frames.</summary>
    public IReadOnlyList<PrefabEntryRowsViewModel> PrefabEntries => _prefabRows;

    /// <summary>Raised when a prefab edit lands, so the host can record it and say so. The action undoes and
    /// redoes it; the string is the line for the notice banner.</summary>
    public event Action<FileInfo, string, IEditAction>? PrefabEdited;

    private void BuildPrefabRows()
    {
        // Which bands were open before. A pick, an add or a remove rewrites the file and rebuilds these
        // rows, and folding the band shut under the user's hands the moment they changed something in it is
        // the panel throwing away where they were.
        var wasOpen = _prefabRows
            .SelectMany(e => e.Groups)
            .Where(g => g.IsExpanded)
            .Select(g => g.Title)
            .ToHashSet(StringComparer.Ordinal);

        if (_prefab == null || _prefabArchive == null) { _prefabRows = []; return; }

        var archive = new FileInfo(_prefabArchive);
        var entries = new List<PrefabEntryRowsViewModel>();
        foreach (PrefabEntryView entry in _prefab.Entries)
        {
            var groups = new List<PrefabGroupRowsViewModel>();
            foreach (PrefabGroupView group in entry.Groups)
            {
                CarItemKind? adds = AddableIn(group.Title);
                var rows = new List<PrefabRowViewModel>();
                foreach (PrefabRefView row in group.Rows)
                {
                    // A row may bring its own list — the kinds a collision volume can be — instead of the
                    // archive's frames. Same dropdown, different meaning; the commit tells them apart.
                    var vm = new PrefabRowViewModel(row, row.Options ?? _prefab.FrameChoices,
                        (r, choice) => CommitPrefabPick(archive, r, choice))
                    {
                        Removable = RemovableFor(row),
                        // The PART's index, not the frame's: axles are listed one per row but stored two at a
                        // time, and the pair is the only unit the file can lose without desyncing the rest.
                        RemoveIndex = row.PartIndex,
                    };
                    if (vm.Removable != null) vm.OnRemove(r => CommitPrefabRemove(archive, r));
                    vm.OnSetValue((r, axis, value) => CommitPrefabValue(archive, r, axis, value));
                    rows.Add(vm);
                }
                var band = new PrefabGroupRowsViewModel(group, rows, adds,
                    adds == null ? null : g => CommitPrefabAdd(archive, g));
                band.IsExpanded = wasOpen.Contains(band.Title);
                groups.Add(band);
            }
            entries.Add(new PrefabEntryRowsViewModel(entry, groups));
        }
        _prefabRows = entries;

        // A rebuild (an edit, an undo) must not silently widen the tab back out from under the search.
        if (_prefabSearch.Trim().Length > 0)
        {
            foreach (PrefabGroupRowsViewModel group in entries.SelectMany(e => e.Groups))
            {
                group.Search(_prefabSearch.Trim());
            }
        }
    }

    // One picked frame, written straight into the working copy — a prefab edit has no in-memory stage of its
    // own, the same as a collision box. The host turns it into an undo entry and a pending build.
    private bool CommitPrefabPick(FileInfo archive, PrefabRefView row, FrameChoice choice)
    {
        // Changing a volume's KIND, which travels down the same dropdown and is nothing like pointing a slot
        // at a frame: the kinds live in different spaces, so this has to move the placement too.
        if (row.ChoosesVolumeType)
        {
            if (!_viewport.ChangeCollisionVolumeType(archive, row.Index, (uint)choice.Hash, out string? why))
            {
                _viewport.RaiseNotice("the volume was not changed: " + (why ?? "unknown reason"), true);
                return false;
            }
            RefreshPrefabAfterUndo();
            return true;
        }

        if (row.Slot is not { } slot) return false;

        PrefabEditing.Change? change = PrefabEditing.SetFrame(
            archive, slot, row.Index, choice.Hash, row.Label);
        if (change == null) return false;

        PrefabEdited?.Invoke(archive,
            $"{row.Label} now points at {choice.Name}.",
            new PrefabPickEdit(change, RefreshPrefabAfterUndo));
        return true;
    }

    // Which band gains a part when its + is pressed. The bands that are a fixed set of slots — a car has one
    // headlight and one rest bone — have none, and show no button rather than a dead one.
    private static CarItemKind? AddableIn(string group) => group switch
    {
        "Seats" => CarItemKind.Seat,
        "Doors" => CarItemKind.Door,
        "Windows" => CarItemKind.Window,
        "Axles" => CarItemKind.AxlePair,
        "Climb boxes" => CarItemKind.ClimbBox,
        "Driving wheels" => CarItemKind.DrivingWheel,
        "Fuel tanks" => CarItemKind.FuelTank,
        "Exhausts" => CarItemKind.Exhaust,
        "Wipers" => CarItemKind.Wiper,
        _ => null,
    };

    // Which rows stand for a whole part. A part made of several fields carries it on its HEADER — the line
    // that names it — while a bare list's row is the part itself. A brake drum belongs to its axle and a
    // seat's door is a reference the seat holds; neither is a thing that can be dropped on its own.
    private static CarItemKind? RemovableFor(PrefabRefView row) => row.Item ?? row.Slot switch
    {
        CarFrameSlot.Wiper => CarItemKind.Wiper,
        CarFrameSlot.DrivingWheel => CarItemKind.DrivingWheel,
        CarFrameSlot.FuelTank => CarItemKind.FuelTank,
        CarFrameSlot.Exhaust => CarItemKind.Exhaust,
        _ => null,
    };

    // Adding a part is two things: the prefab row, and the FRAME the row names.
    //
    // When the toolkit can mint that frame — a climb box, a fuel tank, a seat, an exhaust: measured to be a
    // Dummy or a Point on every shipped car — the whole part is made at once and lands on the selected bone,
    // ready to be dragged. When it cannot (a door, a window, an axle and a wiper are a BONE every time, and
    // growing a rig is a different job), the old behaviour stands: the row is written pointing at the first
    // frame in the archive and the user re-points it, because a row that exists is at least visible and
    // fixable, while no row at all is a dead button.
    private void CommitPrefabAdd(FileInfo archive, PrefabGroupRowsViewModel group)
    {
        if (group.Adds is not { } kind || _prefab is not { FrameChoices.Count: > 0 } prefab) return;

        if (CarPartBuilder.CanAdd(kind))
        {
            // The viewport owns this one: it holds the frame graph the new helper goes into, the scene tree
            // row for it and the undo stack that has to take BOTH halves back together.
            if (_viewport.AddCarPart(kind)) RefreshPrefabAfterUndo();
            return;
        }

        PrefabEditing.ItemChange? change = PrefabEditing.AddItem(
            archive, kind, prefab.FrameChoices[0].Hash, group.Title);
        if (change == null) return;

        PrefabEdited?.Invoke(archive,
            $"Added a {kind.ToString().ToLowerInvariant()} — point it at the right frame.",
            new PrefabItemEdit(change, added: true, RefreshPrefabAfterUndo));
        RefreshPrefabAfterUndo();
    }

    private void CommitPrefabRemove(FileInfo archive, PrefabRowViewModel row)
    {
        if (row.Removable is not { } kind) return;

        PrefabEditing.ItemChange? change = PrefabEditing.RemoveItem(
            archive, kind, row.RemoveIndex, row.Label);
        if (change == null) return;

        PrefabEdited?.Invoke(archive,
            $"Removed a {kind.ToString().ToLowerInvariant()}.",
            new PrefabItemEdit(change, added: false, RefreshPrefabAfterUndo));
        RefreshPrefabAfterUndo();
    }

    // One number typed into a field. Unlike a pick, this does NOT rebuild the panel: retyping a depth while
    // the rows are being replaced under the caret is how a field loses what is being typed into it.
    private bool CommitPrefabValue(FileInfo archive, PrefabRefView row, int axis, float value)
    {
        if (row.ValueSlot is not { } slot) return false;

        // A row shown in a converted space writes all THREE numbers, not one: the axis the user typed is an
        // axis of the car, and turning it back into the bone's space mixes it into every stored component.
        // The three go on the undo stack as one entry — the user made one edit.
        if (row.Space is { } space)
        {
            var wanted = new System.Numerics.Vector3(row.X, row.Y, row.Z);
            wanted = axis switch
            {
                0 => wanted with { X = value },
                1 => wanted with { Y = value },
                _ => wanted with { Z = value },
            };
            if (!System.Numerics.Matrix4x4.Invert(space, out System.Numerics.Matrix4x4 back)) return false;
            System.Numerics.Vector3 stored = System.Numerics.Vector3.Transform(wanted, back);

            var written = new List<IEditAction>();
            foreach ((int at, float number) in new[] { (0, stored.X), (1, stored.Y), (2, stored.Z) })
            {
                if (PrefabEditing.SetValue(archive, slot, row.Index, at, number, row.Label) is { } one)
                {
                    written.Add(new PrefabValueEdit(one, RefreshPrefabAfterUndo));
                }
            }
            if (written.Count == 0) return false;

            PrefabEdited?.Invoke(archive, $"{row.Label} set.", new CompositeEdit([.. written]));
            _viewport.RefreshCarCollisionOverlay();
            return true;
        }

        PrefabEditing.ValueChange? change = PrefabEditing.SetValue(
            archive, slot, row.Index, axis, value, row.Label);
        if (change == null) return false;

        PrefabEdited?.Invoke(archive, $"{row.Label} set.", new PrefabValueEdit(change, RefreshPrefabAfterUndo));
        // The panel is deliberately left alone (see above) — but the VIEWPORT is not the panel, and a
        // collision volume that moved has to move on screen or the number reads as having done nothing.
        _viewport.RefreshCarCollisionOverlay();
        return true;
    }

    // After an undo or redo the file has moved underneath the panel; re-read it rather than guess.
    private void RefreshPrefabAfterUndo()
    {
        _prefabArchive = null;      // defeat the same-archive cache — the file really did change
        BuildPrefab();
        // …and so has the car's physics, which is drawn from the same file and cached separately.
        _viewport.RefreshCarCollisionOverlay();
    }

    /// <summary>The headline over the entries: how many, and whether anything is broken.</summary>
    public string PrefabSummary
    {
        get
        {
            if (_prefab == null) return "";
            int entries = _prefab.Entries.Count;
            int dangling = _prefab.Entries.Sum(e => e.DanglingCount);
            string count = entries == 1 ? "1 entry" : $"{entries} entries";
            return dangling == 0 ? count : $"{count} · {dangling} dangling references";
        }
    }

    public bool PrefabHasDangling => _prefab?.HasDangling ?? false;

    private string _prefabSearch = "";

    /// <summary>
    /// Narrows the Prefab tab to what matches — by the name of a row or by the frame it names. A car's
    /// assembly is over two hundred lines and the question is usually "where is X", not "show me everything".
    /// </summary>
    public string PrefabSearch
    {
        get => _prefabSearch;
        set
        {
            _prefabSearch = value ?? "";
            foreach (PrefabGroupRowsViewModel group in _prefabRows.SelectMany(e => e.Groups))
            {
                group.Search(_prefabSearch.Trim());
            }
            Raise(nameof(PrefabSearch));
            Raise(nameof(PrefabNothingFound));
        }
    }

    /// <summary>Whether the search left nothing on screen — otherwise an empty tab reads as a broken one.</summary>
    public bool PrefabNothingFound =>
        _prefabSearch.Trim().Length > 0
        && _prefabRows.SelectMany(e => e.Groups).All(g => !g.IsVisible);

    // Reading one costs opening the frame resource to build the name table, so it runs off the UI thread and
    // the tab appears when the answer arrives. Token-guarded: selecting another archive mid-read must not be
    // overwritten by the first one landing late. Kept per ARCHIVE rather than per selection — clicking from
    // one door to the next inside a car must not re-read the file each time.
    /// <summary>
    /// Re-reads the staged archive's prefab. Called when the SCENE changes, not just the selection: the tab
    /// is about the archive, and it has to be there the moment a car opens.
    /// <para>
    /// It DEFEATS the same-archive cache, because the scene changing is exactly when the file behind that name
    /// can have become a different file. Restoring a backup swaps the .sds and re-extracts it under the same
    /// path, so a cached read left the tab showing parts and collision volumes the archive no longer has —
    /// the rollback looked like it had not worked.
    /// </para>
    /// </summary>
    public void RefreshPrefab()
    {
        _prefabArchive = null;
        BuildPrefab();
    }

    private async void BuildPrefab()
    {
        FileInfo? archive = ContextArchive();
        if (archive != null && _prefabArchive != null
            && string.Equals(archive.FullName, _prefabArchive, StringComparison.OrdinalIgnoreCase))
        {
            return;     // same archive, already read — the answer cannot have changed
        }

        int token = ++_prefabToken;
        _prefab = null;
        _prefabArchive = archive?.FullName;
        RaisePrefab();
        if (archive == null) return;

        // The open graph's names go with the read: a Dummy minted for a new climb box exists only in memory
        // until Save, and without it the part it belongs to shows as a bare hash the moment it is made.
        IReadOnlyDictionary<ulong, string> live = _viewport.LiveFrameNames(archive);
        // …and where its bones stand, which is what turns a collision volume's stored position into the
        // car's own axes. Read on this thread: the graph is the UI's.
        IReadOnlyDictionary<ulong, System.Numerics.Matrix4x4> bones = _viewport.LiveBoneWorlds(archive);

        PrefabAssembly? read = null;
        try { read = await Task.Run(() => PrefabAssembly.Read(archive, live, bones)); }
        catch (Exception) { /* an archive the panel cannot read is a tab that does not appear */ }

        if (token != _prefabToken) return;
        _prefab = read;
        BuildPrefabRows();
        RaisePrefab();
    }

    private void RaisePrefab()
    {
        Raise(nameof(HasPrefab));
        Raise(nameof(PrefabEntries));
        Raise(nameof(PrefabSummary));
        Raise(nameof(PrefabHasDangling));
    }

    // ── Tuning (archive root only) ──
    //
    // The entity-data tables of the staged archive. Same shape as the Prefab tab above and for the same
    // reason: both describe the ARCHIVE rather than the selection, both write the working copy the moment a
    // value is typed, and both hand the host an undo entry to record.

    private CarTuning? _tuning;
    private string? _tuningArchive;
    private int _tuningToken;

    /// <summary>Whether the open archive carries an entity-data table the core has a layout for — the Tuning
    /// tab's visibility. A car does; most archives do not.</summary>
    public bool HasTuning => _tuning != null;

    private IReadOnlyList<TuningTableRowsViewModel> _tuningRows = [];
    private int _tuningIndex;

    /// <summary>The tables as rows the panel can bind — the picker's list.</summary>
    public IReadOnlyList<TuningTableRowsViewModel> TuningTables => _tuningRows;

    /// <summary>
    /// The table on screen. ONE at a time: a car ships six of them at 771 fields each, and stacking those
    /// end to end made the tab four thousand rows long — the picker is the difference between choosing a
    /// variant and scrolling past the other five.
    /// </summary>
    public TuningTableRowsViewModel? SelectedTuningTable
    {
        get => _tuningIndex < _tuningRows.Count ? _tuningRows[_tuningIndex] : null;
        set
        {
            int index = value == null ? 0 : IndexOfTable(value);
            if (index < 0 || index == _tuningIndex) return;

            // The bands that were open stay open across the switch, and so does the search: the questions
            // "what is the camber here" and "and in the tuned one" are the same question twice, and closing
            // everything in between is the panel making the user ask it again.
            var wasOpen = _tuningRows[_tuningIndex].Bands
                .Where(b => b.IsExpanded)
                .Select(b => b.Title)
                .ToHashSet(StringComparer.Ordinal);

            _tuningIndex = index;
            foreach (TuningBandRowsViewModel band in _tuningRows[index].Bands)
            {
                band.Search(_tuningSearch.Trim());
                if (_tuningSearch.Trim().Length == 0) band.IsExpanded = wasOpen.Contains(band.Title);
            }
            Raise(nameof(SelectedTuningTable));
            Raise(nameof(TuningNothingFound));
        }
    }

    /// <summary>Whether there is anything to switch BETWEEN — one table needs no picker.</summary>
    public bool HasManyTuningTables => _tuningRows.Count > 1;

    private int IndexOfTable(TuningTableRowsViewModel table)
    {
        for (int i = 0; i < _tuningRows.Count; i++)
        {
            if (ReferenceEquals(_tuningRows[i], table)) return i;
        }
        return -1;
    }

    /// <summary>Raised when a tuning edit lands, so the host can record it and say so.</summary>
    public event Action<FileInfo, string, IEditAction>? TuningEdited;

    private void BuildTuningRows()
    {
        // Which bands were open before: a rebuild that folds them shut is the panel throwing away where the
        // user was.
        var wasOpen = _tuningRows
            .SelectMany(t => t.Bands)
            .Where(b => b.IsExpanded)
            .Select(b => b.Title)
            .ToHashSet(StringComparer.Ordinal);

        if (_tuning == null || _tuningArchive == null) { _tuningRows = []; return; }

        var archive = new FileInfo(_tuningArchive);
        var tables = new List<TuningTableRowsViewModel>();
        foreach (TuningTableView table in _tuning.Tables)
        {
            var bands = new List<TuningBandRowsViewModel>();
            foreach (TuningBandView band in table.Bands)
            {
                var elements = new List<TuningElementRowsViewModel>();
                foreach (TuningElementView element in band.Elements)
                {
                    var rows = element.Rows
                        .Select(r => new TuningRowViewModel(
                            r, (field, value) => CommitTuningValue(archive, table, field, value)))
                        .ToList();
                    elements.Add(new TuningElementRowsViewModel(element.Title, rows));
                }
                var vm = new TuningBandRowsViewModel(band.Title, elements);
                vm.IsExpanded = wasOpen.Contains(vm.Title);
                bands.Add(vm);
            }
            tables.Add(new TuningTableRowsViewModel(table, bands));
        }
        _tuningRows = tables;
        // An edit rebuilds these rows; landing back on table 1 after retuning table 4 would be the panel
        // moving the user somewhere they did not ask to go.
        _tuningIndex = Math.Clamp(_tuningIndex, 0, Math.Max(0, tables.Count - 1));

        if (_tuningSearch.Trim().Length > 0 && SelectedTuningTable != null)
        {
            foreach (TuningBandRowsViewModel band in SelectedTuningTable.Bands)
            {
                band.Search(_tuningSearch.Trim());
            }
        }
    }

    // One value typed into a field. Like a prefab number, this does NOT rebuild the panel: replacing the rows
    // under the caret is how a box loses what is being typed into it.
    private bool CommitTuningValue(
        FileInfo archive, TuningTableView table, TuningFieldView field, TuningEditing.TuningValue value)
    {
        TuningEditing.Change? change = TuningEditing.Set(
            table.Path, table.Index, field.Offset, value, field.Name);
        if (change == null) return false;

        TuningEdited?.Invoke(archive, $"{field.Name} set.", new TuningValueEdit(change, RefreshTuningAfterUndo));
        return true;
    }

    private void RefreshTuningAfterUndo()
    {
        _tuningArchive = null;      // defeat the same-archive cache — the file really did change
        BuildTuning();
    }

    /// <summary>The headline over the tables: how many, and how much of them is named.</summary>
    public string TuningSummary
    {
        get
        {
            if (_tuning == null) return "";
            int tables = _tuning.Tables.Count;
            string count = tables == 1 ? "1 table" : $"{tables} tables";
            return $"{count} · {_tuning.FieldCount} fields";
        }
    }

    private string _tuningSearch = "";

    /// <summary>Narrows the Tuning tab to what matches — a car's table is 771 fields, so the question is
    /// nearly always "where is X" rather than "show me everything".</summary>
    public string TuningSearch
    {
        get => _tuningSearch;
        set
        {
            _tuningSearch = value ?? "";
            // Only the table on screen: searching the five that are not showing would report hits nobody
            // can see and leave the tab saying nothing matches while a band below is full of matches.
            foreach (TuningBandRowsViewModel band in SelectedTuningTable?.Bands ?? [])
            {
                band.Search(_tuningSearch.Trim());
            }
            Raise(nameof(TuningSearch));
            Raise(nameof(TuningNothingFound));
        }
    }

    /// <summary>Whether the search left nothing on screen — an empty tab otherwise reads as a broken one.</summary>
    public bool TuningNothingFound =>
        _tuningSearch.Trim().Length > 0
        && (SelectedTuningTable?.Bands ?? []).All(b => !b.IsVisible);

    /// <summary>Re-reads the staged archive's entity data. Called when the SCENE changes, not just the
    /// selection: the tab is about the archive, and it has to be there the moment a car opens. Defeats the
    /// same-archive cache for the same reason the prefab tab does — a restored backup is a different file
    /// under the same name.</summary>
    public void RefreshTuning()
    {
        _tuningArchive = null;
        BuildTuning();
    }

    private async void BuildTuning()
    {
        FileInfo? archive = ContextArchive();
        if (archive != null && _tuningArchive != null
            && string.Equals(archive.FullName, _tuningArchive, StringComparison.OrdinalIgnoreCase))
        {
            return;     // same archive, already read — the answer cannot have changed
        }

        int token = ++_tuningToken;
        _tuning = null;
        _tuningArchive = archive?.FullName;
        RaiseTuning();
        if (archive == null) return;

        CarTuning? read = null;
        try { read = await Task.Run(() => CarTuning.Read(archive)); }
        catch (Exception) { /* an archive the panel cannot read is a tab that does not appear */ }

        if (token != _tuningToken) return;
        _tuning = read;
        BuildTuningRows();
        RaiseTuning();
    }

    private void RaiseTuning()
    {
        Raise(nameof(HasTuning));
        Raise(nameof(TuningTables));
        Raise(nameof(SelectedTuningTable));
        Raise(nameof(HasManyTuningTables));
        Raise(nameof(TuningSummary));
    }

    /// <summary>
    /// The archive the Prefab tab is describing. The selection decides it when the selection belongs to one —
    /// that is what makes the tab right in the map editor, where a dozen archives are loaded at once. With
    /// nothing selected it falls back to the SCENE: a stage holding exactly one archive is unambiguous, and
    /// waiting for a click before saying what the car is made of would be waiting for nothing.
    /// </summary>
    private FileInfo? ContextArchive()
    {
        if (ArchiveOf(_node) is { } selected) return selected;

        FileInfo? only = null;
        foreach (SceneNode root in _viewport.Tree.Roots)
        {
            foreach (FileInfo archive in ArchivesUnder(root))
            {
                if (only == null) { only = archive; continue; }
                if (!string.Equals(only.FullName, archive.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    return null;    // more than one staged: nothing to point at without a selection
                }
            }
        }
        return only;
    }

    private static IEnumerable<FileInfo> ArchivesUnder(SceneNode node)
    {
        if (node.Source is ISceneDocument document) yield return document.SourceArchive;
        foreach (SceneNode child in node.Children)
        {
            foreach (FileInfo found in ArchivesUnder(child)) yield return found;
        }
    }

    /// <summary>
    /// The .sds the selected node belongs to — looked for UPWARD first and downward second, because which
    /// direction works depends on the window. The map editor's tree shows the real chain (folder → archive →
    /// FrameResource → frames), so a selected object finds its document by walking up. The resource editor
    /// shows a FLATTENED tree (<c>SceneTree.RebuildStageRoots</c>) whose rows start at the frame roots — the
    /// archive and frame-resource rows exist as parents but are never displayed, so there is nothing there to
    /// click and the tab has to hang off whatever the user CAN select. Hence up, then down.
    /// </summary>
    private static FileInfo? ArchiveOf(SceneNode? node)
    {
        if (node == null) return null;
        if (node.OwningDocumentNode()?.Source is ISceneDocument owner) return owner.SourceArchive;
        return Below(node);

        static FileInfo? Below(SceneNode node)
        {
            if (node.Source is ISceneDocument own) return own.SourceArchive;
            foreach (SceneNode child in node.Children)
            {
                if (Below(child) is { } found) return found;
            }
            return null;
        }
    }

    // ── Materials (mesh only) ──
    private IReadOnlyList<MaterialViewModel> _materials = Array.Empty<MaterialViewModel>();
    public IReadOnlyList<MaterialViewModel> Materials => _materials;
    public bool HasMaterials => _materials.Count > 0;

    private void BuildMaterials()
    {
        if (_node?.Source is not IMaterialListSource src)
        {
            _materials = Array.Empty<MaterialViewModel>();
            return;
        }
        IReadOnlyList<MaterialInfo> infos = src.GetMaterials();
        var list = new List<MaterialViewModel>(infos.Count);
        for (int i = 0; i < infos.Count; i++)
            list.Add(new MaterialViewModel(infos[i], i, _viewport.RenderMaterialThumbnail(infos[i])));
        _materials = list;
    }

    /// <summary>Rebuilds the material tiles in place (after a material edit or its undo/redo) — the
    /// thumbnails and slot bindings may have changed while the selected node stayed the same.</summary>
    public void RefreshMaterials()
    {
        BuildMaterials();
        Raise(nameof(Materials));
        Raise(nameof(HasMaterials));
    }

    // ── Hierarchy (parent picker) ──
    private IReadOnlyList<ParentOption> _parentCandidates = Array.Empty<ParentOption>();
    private ICollectionView? _parentCandidatesView;
    private ParentOption? _selectedParent;
    private string _parentSearchText = "";
    // Re-entry guard while a reparent/rebuild runs. A COUNTER, not a bool: the reparent's reselect
    // re-enters SetNode inside the SelectedParent setter, and a nested guard cleared by the inner
    // finally would re-open the outer one to WPF's binding echoes mid-flight.
    private int _applyingParent;
    private bool _parentPickerSynced;  // false until the picker displays the node's real parent — see SelectedParent

    /// <summary>Filtered view of the parent candidates the picker list binds to (see <see cref="ParentSearchText"/>).</summary>
    public ICollectionView? ParentCandidatesView => _parentCandidatesView;
    public bool CanReparent => _node?.Source is IFrameNode && _parentCandidates.Count > 0;

    /// <summary>Search text that filters the parent candidate list (case-insensitive substring of the label).</summary>
    public string ParentSearchText
    {
        get => _parentSearchText;
        set { if (_parentSearchText != value) { _parentSearchText = value; _parentCandidatesView?.Refresh(); } }
    }

    private bool FilterParent(object o) =>
        _parentSearchText.Length == 0 ||
        (o is ParentOption p && p.Display.Contains(_parentSearchText, StringComparison.OrdinalIgnoreCase));

    /// <summary>The chosen parent for the selected object; setting it reparents (an undoable edit).</summary>
    public ParentOption? SelectedParent
    {
        get => _selectedParent;
        set
        {
            // Ignore a deselect (the filter hiding the current item) and re-entry while a reparent rebuilds.
            if (value == null || _applyingParent > 0 || _node == null) return;
            if (ReferenceEquals(value.Node, _node.Parent))
            {
                _selectedParent = value;
                _parentPickerSynced = true; // the picker now agrees with reality — later pushes are the user's
                return;
            }

            // Until the picker has been shown sitting on the node's ACTUAL parent, any value arriving here came
            // from WPF, not from the user: swapping the ItemsSource makes the ComboBox publish the collection
            // view's current item, and that push can arrive a dispatcher tick after the rebuild finished. Acting
            // on it silently reparents the object, which persists to the FrameResource and crashes the game.
            // Selecting an object in the tree must never modify the scene.
            if (!_parentPickerSynced) return;

            _applyingParent++;
            try
            {
                _selectedParent = value;
                // The frame's HIERARCHY row, which is not always the selected one: a helper frame also has a
                // row under the bone it hangs on, and moving THAT row would move an attachment rather than a
                // parent link. Same object either way — the row is only how the edit is addressed.
                _viewport.Reparent(HierarchyRowOf(_node), value.Node); // reselects → SetNode → resyncs in place
            }
            finally { _applyingParent--; }
        }
    }

    private void BuildParentCandidates()
    {
        // Hold the re-entry guard for the WHOLE rebuild. Swapping the ItemsSource makes the ComboBox push the
        // collection view's current item back through SelectedParent, and that push is indistinguishable from a
        // user choice — without this guard it silently reparents the object, which persists to the FrameResource
        // and can crash the game. Selecting an object must never mutate the scene.
        _applyingParent++;
        try
        {
            var list = new List<ParentOption>();
            // Only frame-resource documents support reparenting; a collision placement has no hierarchy (its
            // document is a CollisionDocumentAdapter whose Reparent is a no-op), so it gets no parent picker.
            if (_node?.Source is IFrameNode && _node.OwningDocumentNode() is { Source: SceneDocumentAdapter } docNode)
            {
                list.Add(new ParentOption("(root)", docNode));
                CollectCandidates(docNode, _node, list, 0);
            }
            _parentCandidates = list;
            _parentSearchText = "";
            _parentCandidatesView = new ListCollectionView(list) { Filter = FilterParent };
            _selectedParent = CurrentParentOption();

            // The picker may only act once it is showing the node's real parent. When the parent is not among the
            // candidates the combo has nothing truthful to display, so a push from it would be pure noise.
            _parentPickerSynced = _selectedParent != null;
        }
        finally { _applyingParent--; }
    }

    // Re-points the picker at the node's CURRENT parent without touching the candidate list, the view or
    // the search text — the in-place half of a same-node refresh. The list may go cosmetically stale (the
    // node's own row still shows its old spot); it rebuilds on the next real selection change. Falls back
    // to a full rebuild only when the parent is not among the candidates at all (cannot display the truth).
    private void SyncSelectedParent()
    {
        _applyingParent++;
        try
        {
            if (CurrentParentOption() is not { } current)
            {
                BuildParentCandidates();
                return;
            }
            _selectedParent = current;
            _parentPickerSynced = true;
        }
        finally { _applyingParent--; }
    }

    /// <summary>
    /// The candidate the picker must be showing before it is allowed to act: the selected object's CURRENT
    /// parent.
    ///
    /// <para>
    /// Resolved from the FRAME first and from the tree row only as a fallback. A helper frame — a climb box,
    /// a seat, a fuel tank — has two rows in the tree: one under the bone it hangs on, and one in the
    /// hierarchy. The row under the bone has a BONE for a tree parent, and a bone is never a reparent
    /// candidate, so the picker never synced and every click on it was silently ignored: a control that was
    /// visible, enabled and inert. Which of the two rows happens to be selected must not decide whether
    /// reparenting works at all.
    /// </para>
    /// </summary>
    private ParentOption? CurrentParentOption()
    {
        foreach (ParentOption o in _parentCandidates)
        {
            if (ReferenceEquals(o.Node, _node?.Parent)) return o;
        }
        // The frame's OTHER row. A helper appears twice and only the hierarchy copy sits under its real
        // parent, so the candidate to show is the one that already holds a row for this same object.
        // Matched on the row's Source rather than on the frame's own Parent property, because a frame whose
        // parent is a SCENE folder reports that parent as a different kind of adapter and would never match.
        return _node == null ? null : HolderOf(_node);
    }

    /// <summary>The candidate that already holds a row for this object — its hierarchy parent.</summary>
    private ParentOption? HolderOf(SceneNode node)
    {
        if (node.Source is not { } source) return null;
        foreach (ParentOption o in _parentCandidates)
        {
            foreach (SceneNode child in o.Node.Children)
            {
                if (ReferenceEquals(child.Source, source) && !ReferenceEquals(child, node)) return o;
            }
        }
        return null;
    }

    /// <summary>
    /// The row that stands for this object in the HIERARCHY. Usually the node itself; for the copy that
    /// hangs under a bone it is the other row of the same object, which is the one a parent link belongs to.
    /// </summary>
    private SceneNode HierarchyRowOf(SceneNode node)
    {
        if (HolderOf(node) is not { } holder) return node;
        foreach (SceneNode child in holder.Node.Children)
        {
            if (ReferenceEquals(child.Source, node.Source) && !ReferenceEquals(child, node)) return child;
        }
        return node;
    }

    // Flattens the document subtree into parent options (scene folders + frame objects), skipping the node itself
    // and its whole subtree so a cycle can't be chosen. Indented by depth; scene folders marked with a caret.
    private static void CollectCandidates(SceneNode node, SceneNode exclude, List<ParentOption> list, int depth)
    {
        foreach (SceneNode c in node.Children)
        {
            if (ReferenceEquals(c, exclude)) continue;
            if (c.Source is IFrameScene || c.Source is IFrameNode)
            {
                string indent = new string(' ', depth * 3);
                string mark = c.Source is IFrameScene ? "▸ " : "";
                list.Add(new ParentOption(indent + mark + c.Name, c));
                CollectCandidates(c, exclude, list, depth + 1);
            }
        }
    }

    // ── Metadata (computed on selection change) ──
    public string MeshCountText => CountMeshes(out _).ToString("N0", CultureInfo.InvariantCulture);
    public string TriangleCountText { get { CountMeshes(out long tris); return tris.ToString("N0", CultureInfo.InvariantCulture); } }
    public string ChildCountText => (_node?.Children.Count ?? 0).ToString("N0", CultureInfo.InvariantCulture);
    public string SceneCategory => _node?.Category ?? "";

    public string FrObjects => Doc()?.ObjectCount.ToString("N0", CultureInfo.InvariantCulture) ?? "0";
    public string FrGeometries => Doc()?.GeometryCount.ToString("N0", CultureInfo.InvariantCulture) ?? "0";
    public string FrMaterials => Doc()?.MaterialCount.ToString("N0", CultureInfo.InvariantCulture) ?? "0";
    public string FrSkeletons => Doc()?.SkeletonCount.ToString("N0", CultureInfo.InvariantCulture) ?? "0";
    public string FrScenes => Doc()?.SceneCount.ToString("N0", CultureInfo.InvariantCulture) ?? "0";

    private ISceneDocument? Doc() => _node?.Source as ISceneDocument;

    private int CountMeshes(out long triangles)
    {
        int meshes = 0;
        triangles = 0;
        if (_node != null)
            foreach (SceneNode leaf in _node.DescendantMeshLeaves())
                if (leaf.Mesh != null) { meshes++; triangles += leaf.Mesh.TriangleCount; }
        return meshes;
    }

    // ── Transform (local) ──
    private Vector3 _pos, _rotDeg, _scale = Vector3.One;

    public float PosX { get => _pos.X; set => SetPos(0, value); }
    public float PosY { get => _pos.Y; set => SetPos(1, value); }
    public float PosZ { get => _pos.Z; set => SetPos(2, value); }
    public float RotX { get => _rotDeg.X; set => SetRot(0, value); }
    public float RotY { get => _rotDeg.Y; set => SetRot(1, value); }
    public float RotZ { get => _rotDeg.Z; set => SetRot(2, value); }
    public float ScaleX { get => _scale.X; set => SetScale(0, value); }
    public float ScaleY { get => _scale.Y; set => SetScale(1, value); }
    public float ScaleZ { get => _scale.Z; set => SetScale(2, value); }

    private void SetPos(int axis, float v) { _pos = With(_pos, axis, v); ApplyTransform(); }
    private void SetRot(int axis, float v) { _rotDeg = With(_rotDeg, axis, v); ApplyTransform(); }
    private void SetScale(int axis, float v) { _scale = With(_scale, axis, v); ApplyTransform(); }

    private static Vector3 With(Vector3 v, int axis, float value) =>
        axis == 0 ? new Vector3(value, v.Y, v.Z) : axis == 1 ? new Vector3(v.X, value, v.Z) : new Vector3(v.X, v.Y, value);

    // Rebuilds the frame's local transform from the fields, commits it (re-syncs its GPU meshes) and records
    // it as one undoable edit.
    private void ApplyTransform()
    {
        if (_node?.Source is not IFrameNode fn) return;
        _applyingField = true;
        try
        {
            Matrix4x4 before = fn.LocalTransform;
            fn.LocalTransform = TransformMath.Compose(TransformOps.EulerDegToQuat(_rotDeg), _scale, _pos);
            _viewport.CommitNodeTransform(_node);
            _viewport.RecordTransform(_node, before, fn.LocalTransform);
        }
        finally { _applyingField = false; }

        // Notify the fields from the cached values (no lossy re-read) so a second editor bound to the same
        // SelectionViewModel — the Object tab and the viewport overlay panel are both on screen — stays in sync.
        RaiseTransformFields();
    }

    private void RaiseTransformFields()
    {
        Raise(nameof(PosX)); Raise(nameof(PosY)); Raise(nameof(PosZ));
        Raise(nameof(RotX)); Raise(nameof(RotY)); Raise(nameof(RotZ));
        Raise(nameof(ScaleX)); Raise(nameof(ScaleY)); Raise(nameof(ScaleZ));
        Raise(nameof(DeltaX)); Raise(nameof(DeltaY)); Raise(nameof(DeltaZ));
    }

    // ── The change the last gizmo transform made (the viewport overlay) ──

    // The overlay answers "how much did that just change it by", which only a fixed starting point can
    // answer — the object's own current values cannot, and reading them back is what made the overlay show
    // an absolute position after a resize.
    private GizmoMode _deltaMode = GizmoMode.None;
    private Vector3 _basePos, _baseRotDeg, _baseScale = Vector3.One;

    /// <summary>Which transform the overlay is reporting; <see cref="GizmoMode.None"/> when it has nothing to say.</summary>
    public GizmoMode DeltaMode => _deltaMode;

    /// <summary>
    /// Starts reporting changes against where the object stood before the transform. Called when a gizmo drag
    /// first moves something, with the pre-drag state — never with the live one, or the change would measure
    /// itself and always read as nothing.
    /// </summary>
    public void BeginDelta(GizmoMode mode, Vector3 position, Vector3 rotationDeg, Vector3 scale)
    {
        _deltaMode = mode;
        _basePos = position;
        _baseRotDeg = rotationDeg;
        _baseScale = scale;
        RaiseTransformFields();
    }

    /// <summary>Stops reporting (a different object is a different story).</summary>
    public void ClearDelta()
    {
        if (_deltaMode == GizmoMode.None) return;
        _deltaMode = GizmoMode.None;
        RaiseTransformFields();
    }

    /// <summary>
    /// How much the last transform changed each axis by — see <see cref="TransformDelta"/> for what that means
    /// per transform. Assigning re-applies against the same starting point, so typing 2 into a scale always
    /// means "twice the original", however many times it is typed.
    /// </summary>
    public float DeltaX { get => Delta(0); set => SetDelta(0, value); }
    public float DeltaY { get => Delta(1); set => SetDelta(1, value); }
    public float DeltaZ { get => Delta(2); set => SetDelta(2, value); }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    // Which pair of vectors the change is measured between — the live one and the one captured before the drag.
    private (Vector3 Base, Vector3 Current) DeltaPair => _deltaMode switch
    {
        GizmoMode.Move => (_basePos, _pos),
        GizmoMode.Rotate => (_baseRotDeg, _rotDeg),
        GizmoMode.Scale => (_baseScale, _scale),
        _ => (Vector3.Zero, Vector3.Zero),
    };

    private float Delta(int axis)
    {
        (Vector3 baseline, Vector3 current) = DeltaPair;
        return TransformDelta.Measure(_deltaMode, Axis(baseline, axis), Axis(current, axis));
    }

    private void SetDelta(int axis, float value)
    {
        if (_deltaMode == GizmoMode.None) return;
        (Vector3 baseline, _) = DeltaPair;
        float applied = TransformDelta.Apply(_deltaMode, Axis(baseline, axis), value);
        switch (_deltaMode)
        {
            case GizmoMode.Move: _pos = With(_pos, axis, applied); break;
            case GizmoMode.Rotate: _rotDeg = With(_rotDeg, axis, applied); break;
            default: _scale = With(_scale, axis, applied); break;
        }
        ApplyTransform();
    }

    /// <summary>Re-reads the transform fields from the frame (after a gizmo drag). Ignored while a field edit commits.</summary>
    public void RefreshTransform()
    {
        if (_applyingField) return;
        ReadTransform();
        RaiseTransformFields();
    }

    private void ReadTransform()
    {
        if (_node?.Source is IFrameNode fn &&
            TransformMath.TryDecompose(fn.LocalTransform, out Vector3 scale, out Quaternion rot, out Vector3 pos))
        {
            _pos = pos;
            _scale = scale;
            _rotDeg = TransformOps.QuatToEulerDeg(rot);
        }
        else
        {
            _pos = Vector3.Zero;
            _scale = Vector3.One;
            _rotDeg = Vector3.Zero;
        }
    }

    private static string PrettyKind(string kind) => kind switch
    {
        "Sds" => "SDS archive",
        "FrameResource" => "Frame resource",
        "Scene" => "Scene folder",
        "Folder" => "Folder",
        "Mesh" => "Single mesh",
        "Collision" => "Collision layer",
        "CollisionInstance" => "Collision placement",
        _ => kind,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

/// <summary>One entry of the parent picker: a display label (indented by depth) and the tree node it targets.</summary>
public sealed record ParentOption(string Display, SceneNode Node);
