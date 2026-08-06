using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using Illusion.Assets.Effects;
using Illusion.Views;

namespace Illusion.ViewModels;

/// <summary>
/// One editable number of an effect. Committed on losing focus, like every other numeric field in the
/// panel, and refused rather than clamped when the text is not a number — a birth rate of "fast" is a typo,
/// and silently making it zero would put the emitter out without saying so.
/// </summary>
public sealed class EffectValueRowViewModel : INotifyPropertyChanged
{
    private readonly Func<EffectValueRow, float, bool> _commit;
    private EffectValueRow _row;

    public EffectValueRowViewModel(EffectValueRow row, Func<EffectValueRow, float, bool> commit)
    {
        ArgumentNullException.ThrowIfNull(row);
        _row = row;
        _commit = commit;
    }

    public string Label => _row.Label;

    /// <summary>Where the value lives — what a bug report needs when a number turns out mislabelled.</summary>
    public string Where => $"offset {_row.Offset}";

    public string Number
    {
        get => _row.Value.ToString("0.######", CultureInfo.InvariantCulture);
        set
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                || !float.IsFinite(parsed))
            {
                Raise(nameof(Number));      // put the old text back
                return;
            }
            if (Math.Abs(parsed - _row.Value) < float.Epsilon) return;
            if (!_commit(_row, parsed)) { Raise(nameof(Number)); return; }

            _row = _row with { Value = parsed };
            Raise(nameof(Number));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One operator of a generation, as a card: its switch and its numbers. The switch is a byte in the file,
/// and turning it off is the quickest way to find out what an operator was contributing.
/// </summary>
public sealed class EffectOperatorRowsViewModel : INotifyPropertyChanged
{
    private readonly Func<int, bool, bool> _switch;
    private bool _enabled;

    public EffectOperatorRowsViewModel(
        EffectOperatorView op, IReadOnlyList<EffectValueRowViewModel> rows, Func<int, bool, bool> flip)
    {
        ArgumentNullException.ThrowIfNull(op);
        Title = op.Title;
        Detail = op.Parameters.Count == 0
            ? "nothing to set"
            : string.Join(" · ", op.Parameters.Select(p => p.Summary.Length > 0 ? $"{p.Title}: {p.Summary}" : p.Title));
        EnabledOffset = op.EnabledOffset;
        _enabled = op.Enabled;
        _switch = flip;
        Rows = rows;
    }

    public string Title { get; }

    /// <summary>What this operator sets, in one line — read when the card is skimmed rather than used.</summary>
    public string Detail { get; }

    public int EnabledOffset { get; }

    public IReadOnlyList<EffectValueRowViewModel> Rows { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            if (!_switch(EnabledOffset, value)) { Raise(nameof(Enabled)); return; }
            _enabled = value;
            Raise(nameof(Enabled));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One generation — one emitter — as a foldable band of operator cards.</summary>
public sealed class EffectGenerationRowsViewModel : INotifyPropertyChanged
{
    private bool _expanded;

    public EffectGenerationRowsViewModel(
        EffectGenerationView generation, IReadOnlyList<EffectOperatorRowsViewModel> operators, Brush accent)
    {
        ArgumentNullException.ThrowIfNull(generation);
        Title = generation.Title;
        Badge = generation.OperatorList;
        Accent = accent;
        Operators = operators;
    }

    public string Title { get; }

    /// <summary>"Birth, Position, Speed, …" beside the title, so a folded band still says what it is.</summary>
    public string Badge { get; }

    public Brush Accent { get; }

    public IReadOnlyList<EffectOperatorRowsViewModel> Operators { get; }

    /// <summary>Closed to start with: a car's fire is eight of these and sixty cards between them.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One effect of the archive: what it is, and the emitters it is made of.</summary>
public sealed class EffectRowsViewModel
{
    // Warm for the one that burns, cool for the one that rains, grey for an effect nothing names — the
    // same warm/cool reading the other tabs use.
    private static readonly Dictionary<string, Brush> Accents = new(StringComparer.Ordinal)
    {
        ["Fire"] = PaletteInk.Coral,
        ["Explosion"] = PaletteInk.Amber,
        ["Rain"] = PaletteInk.Sky,
        ["SmokeExhaust"] = PaletteInk.Slate,
    };

    public EffectRowsViewModel(EffectView effect, IReadOnlyList<EffectGenerationRowsViewModel> generations)
    {
        ArgumentNullException.ThrowIfNull(effect);
        Id = effect.Id;
        Title = effect.Title;
        Number = effect.Id.ToString(CultureInfo.InvariantCulture);
        Summary = effect.Summary;
        Status = $"{effect.ValueCount} value(s), written back in place";
        Generations = generations;
    }

    public uint Id { get; }

    public string Title { get; }

    /// <summary>The id, for the picker strip.</summary>
    public string Number { get; }

    public string Summary { get; }

    public string Status { get; }

    public IReadOnlyList<EffectGenerationRowsViewModel> Generations { get; }

    /// <summary>The accent an effect's bands wear, by what its own tuning calls it.</summary>
    public static Brush AccentFor(string role) =>
        Accents.TryGetValue(role, out Brush? brush) ? brush : PaletteInk.Pewter;
}
