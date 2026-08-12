using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using Illusion.Assets.EntityData;
using Illusion.Views;

namespace Illusion.ViewModels;

/// <summary>
/// One row of the Tuning tab: a named number of an entity-data table, in whichever shape it is stored —
/// a float, a whole number, a yes/no, three floats, or a fixed-width name buffer.
///
/// <para>
/// A write goes straight to the file, so the row also holds the value: the panel is not rebuilt after a
/// number is typed (that would replace the box under the caret), and without keeping it here the getter
/// would keep answering with what was read off disk.
/// </para>
/// </summary>
public sealed class TuningRowViewModel : INotifyPropertyChanged
{
    private TuningFieldView _row;
    private readonly Func<TuningFieldView, TuningEditing.TuningValue, bool> _commit;

    public TuningRowViewModel(
        TuningFieldView row, Func<TuningFieldView, TuningEditing.TuningValue, bool> commit)
    {
        ArgumentNullException.ThrowIfNull(row);
        _row = row;
        _commit = commit;
    }

    public string Label => _row.Label;

    /// <summary>The field's full name in the struct — what the search matches on, and what a bug report
    /// needs when a number turns out to be mislabelled.</summary>
    public string Name => _row.Name;

    public string Value => _row.Value;

    public bool IsNumber => _row.Kind == TuningFieldKind.Number;

    public bool IsInteger => _row.Kind == TuningFieldKind.Integer;

    public bool IsFlag => _row.Kind == TuningFieldKind.Flag;

    public bool IsVector => _row.Kind == TuningFieldKind.Vector;

    /// <summary>A plain name buffer — a text box with the slot's own width as its limit.</summary>
    public bool IsText => _row.Kind == TuningFieldKind.Text && !_row.HasChoices;

    /// <summary>A name buffer the toolkit knows the candidates for: the wheel prototype. Editable, because
    /// the list is what <c>cars_universal</c> happens to carry and a mod may name something else.</summary>
    public bool IsChoiceText => _row.Kind == TuningFieldKind.Text && _row.HasChoices;

    /// <summary>Whether the row draws its own name line. A vector does not — the vector box draws its own.</summary>
    public bool HasCaption => !IsVector;

    /// <summary>The widest the text may get. One byte of the buffer is the terminator.</summary>
    public int MaxLength => _row.Capacity > 0 ? (int)_row.Capacity - 1 : 0;

    public IReadOnlyList<string> Choices => _row.Choices ?? [];

    /// <summary>A float the game reads.</summary>
    public float Number
    {
        get => _row.X;
        set => Write(TuningEditing.TuningValue.Of(value, _row.Y, _row.Z), nameof(Number));
    }

    /// <summary>A whole number: a count, an index, a sound id.</summary>
    public long Integer
    {
        get => _row.Number;
        set => Write(TuningEditing.TuningValue.Of(value), nameof(Integer));
    }

    public bool Flag
    {
        get => _row.Number != 0;
        set => Write(TuningEditing.TuningValue.Of(value ? 1L : 0L), nameof(Flag));
    }

    public float X
    {
        get => _row.X;
        set => Write(TuningEditing.TuningValue.Of(value, _row.Y, _row.Z), nameof(X));
    }

    public float Y
    {
        get => _row.Y;
        set => Write(TuningEditing.TuningValue.Of(_row.X, value, _row.Z), nameof(Y));
    }

    public float Z
    {
        get => _row.Z;
        set => Write(TuningEditing.TuningValue.Of(_row.X, _row.Y, value), nameof(Z));
    }

    public string Text
    {
        get => _row.Text;
        set => Write(TuningEditing.TuningValue.Of(value ?? ""), nameof(Text));
    }

    private void Write(TuningEditing.TuningValue value, string property)
    {
        if (Unchanged(value)) return;
        if (!_commit(_row, value))
        {
            // Refused. Say so, so the box snaps back to what the file holds rather than showing a number
            // nothing has.
            Raise(property);
            return;
        }

        _row = _row with
        {
            X = value.X,
            Y = value.Y,
            Z = value.Z,
            Number = value.Number,
            Text = value.Text,
        };
        _row = _row with { Value = Render() };
        Raise(property);
        Raise(nameof(Value));
    }

    private bool Unchanged(TuningEditing.TuningValue value) => _row.Kind switch
    {
        TuningFieldKind.Number => Same(_row.X, value.X),
        TuningFieldKind.Vector => Same(_row.X, value.X) && Same(_row.Y, value.Y) && Same(_row.Z, value.Z),
        TuningFieldKind.Text => string.Equals(_row.Text, value.Text, StringComparison.Ordinal),
        _ => _row.Number == value.Number,
    };

    private static bool Same(float a, float b) => a.Equals(b);

    // What the row reads as now, written the way the table's own listing writes it — so the search, which
    // matches on this text, keeps agreeing with what is on screen.
    private string Render() => _row.Kind switch
    {
        TuningFieldKind.Number => _row.X.ToString("0.######", CultureInfo.InvariantCulture),
        TuningFieldKind.Vector => string.Create(CultureInfo.InvariantCulture,
            $"{_row.X:0.###}, {_row.Y:0.###}, {_row.Z:0.###}"),
        TuningFieldKind.Flag => _row.Number != 0 ? "true" : "false",
        TuningFieldKind.Text => _row.Text,
        _ => _row.Number.ToString(CultureInfo.InvariantCulture),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One thing inside a band, on its own plate: a wheel and its twenty-three fields.</summary>
public sealed class TuningElementRowsViewModel
{
    public TuningElementRowsViewModel(string? title, IReadOnlyList<TuningRowViewModel> rows)
    {
        Title = title;
        Rows = rows;
    }

    public string? Title { get; }

    /// <summary>Whether the plate draws a caption. The loose rows of a band do not — they ARE the band.</summary>
    public bool HasTitle => !string.IsNullOrEmpty(Title);

    public IReadOnlyList<TuningRowViewModel> Rows { get; }
}

/// <summary>
/// One band of a table — the gearbox, the wheels, the sound tail. Closed to start with, the way the Prefab
/// tab's bands are: a car's tuning is 771 fields and opening all of them at once is a wall.
/// </summary>
public sealed class TuningBandRowsViewModel : INotifyPropertyChanged
{
    // Warm for what a driver feels (engine, gearbox, brakes), cool for the structure under it, grey for the
    // parts nobody has named — the same warm/cool split the resource icons use.
    private static readonly Dictionary<string, Brush> Accents = new(StringComparer.Ordinal)
    {
        ["Body"] = PaletteInk.Steel,
        ["Engine"] = PaletteInk.Coral,
        ["Fuel"] = PaletteInk.Amber,
        ["Gearbox"] = PaletteInk.Apricot,
        ["Steering"] = PaletteInk.Sky,
        ["Differential"] = PaletteInk.Lavender,
        ["Wheels"] = PaletteInk.Mint,
        ["Brakes"] = PaletteInk.Salmon,
        ["Aerodynamics"] = PaletteInk.Cyan,
        ["Driving assists"] = PaletteInk.Seafoam,
        ["Effects"] = PaletteInk.Peach,
        ["Engine sound"] = PaletteInk.Jade,
        ["Car sound"] = PaletteInk.Aqua,
        ["Surface sound"] = PaletteInk.Slate,
        ["Fields"] = PaletteInk.Ice,
        ["Unknown"] = PaletteInk.Pewter,
    };

    private readonly IReadOnlyList<TuningElementRowsViewModel> _all;

    public TuningBandRowsViewModel(string title, IReadOnlyList<TuningElementRowsViewModel> elements)
    {
        Title = title;
        _all = elements;
        Elements = elements;
        FieldCount = elements.Sum(e => e.Rows.Count);
    }

    public string Title { get; }

    public int FieldCount { get; }

    /// <summary>What the header says beside the name: how many fields are in it.</summary>
    public string Badge => FieldCount.ToString(CultureInfo.InvariantCulture);

    public Brush Accent => Accents.GetValueOrDefault(Title, PaletteInk.Ash);

    public IReadOnlyList<TuningElementRowsViewModel> Elements { get; private set; }

    public bool IsVisible { get; private set; } = true;

    private bool _isExpanded;

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
    /// Narrows the band to what matches — by the row's label, by its full name in the struct, and by the
    /// value it holds. "camber" and "wheel_civ09" are both questions this tab gets asked.
    /// </summary>
    public void Search(string query)
    {
        if (query.Length == 0)
        {
            Elements = _all;
            IsVisible = _all.Count > 0;
            IsExpanded = false;     // leaving them open would hand back a wall the moment the box is cleared
        }
        else if (Title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            Elements = _all;
            IsVisible = true;
            IsExpanded = true;
        }
        else
        {
            var kept = new List<TuningElementRowsViewModel>();
            foreach (TuningElementRowsViewModel element in _all)
            {
                if (element.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                {
                    kept.Add(element);
                    continue;
                }
                var rows = element.Rows.Where(r => Hit(r, query)).ToList();
                if (rows.Count > 0) kept.Add(new TuningElementRowsViewModel(element.Title, rows));
            }
            Elements = kept;
            IsVisible = kept.Count > 0;
            IsExpanded = IsVisible;     // a hit nobody can see is not a hit
        }
        Raise(nameof(Elements));
        Raise(nameof(IsVisible));
    }

    private static bool Hit(TuningRowViewModel row, string query) =>
        row.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Value.Contains(query, StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One entity-data table, with its bands already turned into rows.</summary>
public sealed class TuningTableRowsViewModel
{
    public TuningTableRowsViewModel(TuningTableView table, IReadOnlyList<TuningBandRowsViewModel> bands)
    {
        ArgumentNullException.ThrowIfNull(table);
        Title = table.Title;
        Number = (table.Index + 1).ToString(CultureInfo.InvariantCulture);
        Label = table.Label;
        HashText = table.HashText;
        SizeText = table.SizeText;
        TypeName = table.TypeName;
        Status = table.TwinOf ?? string.Create(CultureInfo.InvariantCulture,
            $"{table.FieldCount} fields, written back in place");
        Bands = bands;
    }

    public string Title { get; }

    /// <summary>What the strip puts on the button — nothing but the table's number, because six buttons of
    /// prose is not a strip.</summary>
    public string Number { get; }

    /// <summary>What tells this table from the next: the title plus the values that differ. Rides in the
    /// button's tooltip and over the table itself, so the numbers on the strip mean something.</summary>
    public string Label { get; }

    public override string ToString() => Label;

    public string TypeName { get; }

    /// <summary>The name hash the storage lists the table under. Raw on purpose — it names an actor
    /// definition that lives in another archive entirely.</summary>
    public string HashText { get; }

    public string SizeText { get; }

    public string Status { get; }

    public IReadOnlyList<TuningBandRowsViewModel> Bands { get; }
}
