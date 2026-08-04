using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Illusion.Assets.Prefabs;
using Illusion.Formats.Prefab;
using Illusion.Views;

namespace Illusion.ViewModels;

/// <summary>
/// One row of the Prefab tab. A reference row is a CHOICE among the archive's own frames — never a text
/// field: a prefab addresses a frame by the hash of its name, and a name that almost exists resolves to
/// nothing and takes the part with it, silently. Picking from a list is the only way that cannot happen.
/// </summary>
public sealed class PrefabRowViewModel : INotifyPropertyChanged
{
    // Not readonly: a written value has to land HERE as well as in the file. The row the panel was built
    // from is a snapshot of what was on disk when it was read, and the panel is not rebuilt after a number
    // is typed — doing that would replace the fields under the caret. So the row moves with the file.
    private PrefabRefView _row;
    private readonly Func<PrefabRefView, FrameChoice, bool> _commit;
    private FrameChoice? _selected;

    public PrefabRowViewModel(
        PrefabRefView row, IReadOnlyList<FrameChoice> choices, Func<PrefabRefView, FrameChoice, bool> commit)
    {
        ArgumentNullException.ThrowIfNull(row);
        _row = row;
        _commit = commit;
        Choices = choices;
        _selected = choices.FirstOrDefault(c => c.Name == row.Value);
    }

    /// <summary>Which shape this row is drawn in. One flag per shape rather than a converter, because the
    /// panel picks the widget with a plain Visibility binding, the way every other tab does.</summary>
    public bool IsHeader => _row.Kind == PrefabRefKind.Header;

    public bool IsPicker => _row.CanEdit;

    public bool IsNumber => _row.Kind == PrefabRefKind.Number;

    public bool IsFlag => _row.Kind == PrefabRefKind.Flag;

    public bool IsVector => _row.Kind == PrefabRefKind.Vector;

    /// <summary>A row with nothing to edit: a count, or a reference the archive cannot resolve.</summary>
    public bool IsText => !IsHeader && !IsPicker && !IsNumber && !IsFlag && !IsVector;

    /// <summary>
    /// Whether the row carries a dot. Only a frame reference does: the dot means "this name resolves to a
    /// frame in the archive", and there is no such question about a depth or a mass. It also keeps the width
    /// it costs on the rows where it says something — the panel is narrow and every column is paid for.
    /// </summary>
    public bool HasDot => _row.Kind is PrefabRefKind.Reference or PrefabRefKind.Dangling or PrefabRefKind.Unset;

    /// <summary>Whether the row draws its own name line. A position does not — the vector box draws its name
    /// and its copy/paste buttons itself, and a second name over it would be the same word twice.</summary>
    public bool HasCaption => !IsVector;

    /// <summary>A field OF a part rather than the part itself — indented under it, and with no gap above.</summary>
    public bool IsField => _row.Sub;

    /// <summary>
    /// What separates one part from the next: a gap, not an indent. Indenting the fields under a part would
    /// read as hierarchy, but this panel is a narrow column and every pixel spent on the left is taken off
    /// the field on the right — so the parts are told apart by air above them instead, and everything stays
    /// on one left edge.
    /// </summary>
    /// <summary>Room between the lines INSIDE an element. What separates one element from the next is now
    /// the gap between their plates, so a part's own row no longer has to open one itself.</summary>
    public Thickness Spacing => new(0, 0, 0, 10);

    private Func<PrefabRefView, int, float, bool>? _setValue;

    public void OnSetValue(Func<PrefabRefView, int, float, bool> set) => _setValue = set;

    /// <summary>A single number the game reads.</summary>
    public float Number
    {
        get => _row.X;
        set => Write(0, value, nameof(Number));
    }

    /// <summary>A yes/no the game reads.</summary>
    public bool Flag
    {
        get => _row.X != 0;
        set => Write(0, value ? 1 : 0, nameof(Flag));
    }

    public float X { get => _row.X; set => Write(0, value, nameof(X)); }

    public float Y { get => _row.Y; set => Write(1, value, nameof(Y)); }

    public float Z { get => _row.Z; set => Write(2, value, nameof(Z)); }

    private void Write(int axis, float value, string property)
    {
        if (_setValue == null || Math.Abs(Axis(axis) - value) < 1e-6f) return;
        if (!_setValue(_row, axis, value))
        {
            // Refused. Say so, so the field snaps back to what the file actually holds rather than sitting
            // there showing a number nothing has.
            Raise(property);
            return;
        }

        // The file took it, so the row holds it too. Without this the getter still answers with the value
        // that was read off disk, and the field the user just typed into resets itself the moment it
        // refreshes — the write having succeeded the whole time.
        _row = axis switch
        {
            0 => _row with { X = value },
            1 => _row with { Y = value },
            _ => _row with { Z = value },
        };
        _row = _row with { Value = Render() };
        Raise(property);
        Raise(nameof(Value));
    }

    // What the row reads as now, written the way it was written when the file was first read: a seat type has
    // no decimals, a mass has one. Re-deriving it keeps the search — which matches on this text — agreeing
    // with what is on screen.
    private string Render() => _row.Kind switch
    {
        PrefabRefKind.Number => _row.X.ToString(
            _row.Format ?? "F3", System.Globalization.CultureInfo.InvariantCulture),
        PrefabRefKind.Flag => _row.X != 0 ? "yes" : "no",
        PrefabRefKind.Vector => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"({_row.X:F2}, {_row.Y:F2}, {_row.Z:F2})"),
        _ => _row.Value,
    };

    private float Axis(int axis) => axis switch { 0 => _row.X, 1 => _row.Y, _ => _row.Z };

    public string Label => _row.Label;

    /// <summary>What the row reads as when it is not a choice — a fact, an empty slot, or a hash that names
    /// nothing.</summary>
    public string Value => _row.Value;

    public string? Detail => _row.Detail;

    public PrefabRefKind Kind => _row.Kind;

    /// <summary>Every frame in the archive. Shared by every row — one list, not one per row.</summary>
    public IReadOnlyList<FrameChoice> Choices { get; }

    /// <summary>Whether this row offers the picker. A fact does not; a reference does, dangling or not —
    /// a broken one is exactly the row that needs pointing somewhere else.</summary>
    public bool CanEdit => _row.CanEdit;

    /// <summary>True for a reference the archive cannot resolve. The picker shows nothing selected, because
    /// nothing IS selected — the hash names no frame here.</summary>
    public bool IsDangling => _row.Kind == PrefabRefKind.Dangling;

    /// <summary>The frame this slot points at. Setting it writes the prefab.</summary>
    public FrameChoice? SelectedFrame
    {
        get => _selected;
        set
        {
            if (value == null || ReferenceEquals(value, _selected)) return;
            FrameChoice? previous = _selected;
            _selected = value;
            Raise(nameof(SelectedFrame));
            // A refused write must not leave the picker showing a frame the file does not have.
            if (!_commit(_row, value))
            {
                _selected = previous;
                Raise(nameof(SelectedFrame));
            }
        }
    }

    /// <summary>Re-reads the picker off the file after an undo or a redo moved it underneath.</summary>
    public void Reselect(string frameName)
    {
        _selected = Choices.FirstOrDefault(c => c.Name == frameName);
        Raise(nameof(SelectedFrame));
    }

    /// <summary>Which part this row IS, when the row stands for a whole part that can be dropped. Null for
    /// the rows that are only a field of one — a brake drum is not a thing you remove, its axle is.</summary>
    public CarItemKind? Removable { get; init; }

    /// <summary>Index of that part in its list — the axle rows count in PAIRS, because that is the unit the
    /// file stores and the only one that can be removed without desyncing everything after it.</summary>
    public int RemoveIndex { get; init; }

    public bool CanRemove => Removable != null && _remove != null;

    /// <summary>What the button says it will do, so a row cannot be dropped by surprise.</summary>
    public string RemoveTip => Removable switch
    {
        CarItemKind.AxlePair => "Remove this axle pair — the file stores axles two at a time",
        CarItemKind.CollisionVolume =>
            "Remove this collision volume — the part stops being solid here. The shape file it named stays "
            + "in the archive, unused.",
        _ => $"Remove this {Removable?.ToString().ToLowerInvariant()}",
    };

    private Action<PrefabRowViewModel>? _remove;

    public void OnRemove(Action<PrefabRowViewModel> remove) => _remove = remove;

    public void Remove() => _remove?.Invoke(this);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One band of rows in the Prefab tab: a coloured bar, a title, how many, and — where the file allows it —
/// a way to add one more. The bar is the same idiom the content browser bands an archive's sections with, so
/// the two lists in the app read the same way; the colour is the band's own, which is what lets a car's
/// assembly be skimmed rather than read.
/// </summary>
public sealed class PrefabGroupRowsViewModel : INotifyPropertyChanged
{
    // What each band is drawn in. Warm for the things a person touches (seats, doors), cool for the
    // structure they hang on, red for damage — the same warm/cool split the resource icons use.
    private static readonly Dictionary<string, Brush> Accents = new(StringComparer.Ordinal)
    {
        ["Chassis"] = PaletteInk.Steel,
        ["Lights"] = PaletteInk.Sand,
        ["Driving wheels"] = PaletteInk.Mint,
        ["Fuel tanks"] = PaletteInk.Jade,
        ["Exhausts"] = PaletteInk.Slate,
        ["Wipers"] = PaletteInk.Cyan,
        ["Seats"] = PaletteInk.Apricot,
        ["Doors"] = PaletteInk.Sky,
        ["Windows"] = PaletteInk.Ice,
        ["Axles"] = PaletteInk.Lavender,
        ["Climb boxes"] = PaletteInk.Seafoam,
        ["Body"] = PaletteInk.Pewter,
        ["Wind emitters"] = PaletteInk.Aqua,
        ["Bus seats"] = PaletteInk.Tan,
        ["Bus entries"] = PaletteInk.Peach,
        ["Steering wheel grip"] = PaletteInk.Amber,
        ["Door damage"] = PaletteInk.Salmon,
        ["Damage"] = PaletteInk.Coral,
        ["Collision"] = PaletteInk.Rose,
    };

    private readonly Action<PrefabGroupRowsViewModel>? _add;

    public PrefabGroupRowsViewModel(
        PrefabGroupView group, IReadOnlyList<PrefabRowViewModel> rows,
        CarItemKind? adds, Action<PrefabGroupRowsViewModel>? add)
    {
        ArgumentNullException.ThrowIfNull(group);
        Title = group.Title;
        Badge = group.Badge;
        HasDangling = group.HasDangling;
        Rows = rows;
        Adds = adds;
        _add = add;
        Elements = Split(rows);
    }

    /// <summary>
    /// Splits a band's rows into the things they describe. A row that HAS fields under it opens a plate of
    /// its own and takes them with it — a door, a deform part, a steering wheel. Rows that stand alone are
    /// gathered onto one plate together, because a band of four plain slots (the chassis) as four boxes of
    /// one line each would be four boxes saying nothing.
    /// <para>
    /// Keyed on whether fields follow, NOT on whether the row can be removed: a deform part and a door-damage
    /// record are parts the toolkit cannot mint, and they still deserve a plate each.
    /// </para>
    /// </summary>
    private static List<PrefabElementViewModel> Split(IReadOnlyList<PrefabRowViewModel> rows)
    {
        var elements = new List<PrefabElementViewModel>();
        var current = new List<PrefabRowViewModel>();
        bool currentHasFields = false;

        for (int i = 0; i < rows.Count; i++)
        {
            PrefabRowViewModel row = rows[i];
            if (row.IsField)
            {
                current.Add(row);
                currentHasFields = true;
                continue;
            }

            bool opensOne = i + 1 < rows.Count && rows[i + 1].IsField;
            if (current.Count > 0 && (currentHasFields || opensOne))
            {
                elements.Add(new PrefabElementViewModel(current));
                current = [];
                currentHasFields = false;
            }
            current.Add(row);
        }
        if (current.Count > 0) elements.Add(new PrefabElementViewModel(current));
        return elements;
    }

    public string Title { get; }

    public string Badge { get; }

    public bool HasDangling { get; }

    public IReadOnlyList<PrefabRowViewModel> Rows { get; }

    /// <summary>
    /// The band's rows split into the things they describe, one plate each: a door with its handle and its
    /// lock is one element, and so is a chassis slot that is only itself. A single plate around a whole band
    /// makes eight doors one block of thirty rows; a plate per element is what makes them eight doors.
    /// </summary>
    public IReadOnlyList<PrefabElementViewModel> Elements { get; private set; }

    /// <summary>Whether the band has anything left to show under the current search.</summary>
    public bool IsVisible { get; private set; } = true;

    /// <summary>Whether the band holds nothing at all — drawn as a line saying so rather than as a header
    /// that opens onto blank space, which reads as the panel having failed.</summary>
    public bool IsEmpty => Elements.Count == 0;

    private bool _isExpanded;

    /// <summary>
    /// Whether the band is open. Closed to start with, the way Blender's panels are: fifteen bands and two
    /// hundred lines opened at once is a wall, and the first thing anyone does with a wall is scroll past it.
    /// A search opens the bands it matched, because a hit nobody can see is not a hit.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise(nameof(IsExpanded));
        }
    }

    /// <summary>
    /// Narrows the band to what matches, by row name AND by the frame a row names — a car's assembly is
    /// two hundred lines, and "which part is doorFL" is as common a question as "where is the mass".
    /// <para>
    /// A match on a FIELD keeps the part it belongs to as well: a lone "Mass 120" with no line saying which
    /// part it is would be an answer to nothing. A match on the part keeps all of its fields.
    /// </para>
    /// </summary>
    public void Search(string query)
    {
        if (query.Length == 0)
        {
            Elements = Split(Rows);
            // An empty band that can be added to still stands: its "+" is the only way back from removing
            // the last thing in it.
            IsVisible = Rows.Count > 0 || CanAdd;
            // Back to closed: the search opened these, and leaving them open would hand back a wall the
            // moment the box is cleared.
            IsExpanded = false;
        }
        else if (Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            // The band itself is what was asked for — show all of it, including an empty one, which no row
            // could ever match on behalf of.
            Elements = Split(Rows);
            IsVisible = true;
            IsExpanded = true;
        }
        else
        {
            var kept = new List<PrefabElementViewModel>();
            foreach (PrefabElementViewModel element in Split(Rows))
            {
                bool headMatches = element.Rows.Count > 0 && Hit(element.Rows[0], query);
                if (headMatches)
                {
                    kept.Add(element);
                    continue;
                }
                var rows = element.Rows.Where(r => Hit(r, query)).ToList();
                if (rows.Count == 0) continue;
                // Keep the part's own line at the top so the match has something to belong to — a lone
                // "Mass 120" with no line saying which part it is answers nothing. Keyed on the line being
                // the part's own, not on it being removable: a deform part is a part the toolkit cannot mint.
                if (!element.Rows[0].IsField && !rows.Contains(element.Rows[0]))
                {
                    rows.Insert(0, element.Rows[0]);
                }
                kept.Add(new PrefabElementViewModel(rows));
            }
            Elements = kept;
            IsVisible = kept.Count > 0;
            IsExpanded = IsVisible;     // a hit nobody can see is not a hit
        }
        Raise(nameof(Elements));
        Raise(nameof(IsVisible));
        Raise(nameof(IsEmpty));
    }

    private static bool Hit(PrefabRowViewModel row, string query) =>
        row.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Value.Contains(query, StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>What "add one" means here, or null for a band that is a fixed set of slots — a car has a
    /// headlight and a rest bone, it does not get a second one.</summary>
    public CarItemKind? Adds { get; }

    public bool CanAdd => Adds != null && _add != null;

    public string AddTip => Adds == CarItemKind.AxlePair
        ? "Add an axle pair — the file stores axles two at a time"
        : $"Add a {Adds?.ToString().ToLowerInvariant()}";

    public Brush Accent => Accents.GetValueOrDefault(Title, PaletteInk.Ash);

    public void Add() => _add?.Invoke(this);
}

/// <summary>One thing in a band, on its own plate: a door and everything that belongs to it.</summary>
public sealed class PrefabElementViewModel
{
    public PrefabElementViewModel(IReadOnlyList<PrefabRowViewModel> rows) => Rows = rows;

    public IReadOnlyList<PrefabRowViewModel> Rows { get; }
}

/// <summary>One prefab entry, with its rows already turned into pickers.</summary>
public sealed class PrefabEntryRowsViewModel
{
    public PrefabEntryRowsViewModel(PrefabEntryView entry, IReadOnlyList<PrefabGroupRowsViewModel> groups)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Owner = entry.Owner;
        TypeName = entry.TypeName;
        SizeText = entry.SizeText;
        Status = entry.Status;
        HasDangling = entry.HasDangling;
        Groups = groups;
    }

    public string Owner { get; }

    public string TypeName { get; }

    public string SizeText { get; }

    public string Status { get; }

    public bool HasDangling { get; }

    public IReadOnlyList<PrefabGroupRowsViewModel> Groups { get; }
}
