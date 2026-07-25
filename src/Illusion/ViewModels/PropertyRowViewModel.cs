using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Illusion.Domain.Properties;
using PropertyDescriptor = Illusion.Domain.Properties.PropertyDescriptor;

namespace Illusion.ViewModels;

/// <summary>
/// One editable row of the property panel — a view over a single <see cref="PropertyDescriptor"/>. Exposes the
/// descriptor's value in the shape each editor template needs (text / bool / vector / hash / flags / lines) and,
/// on edit, routes a committed value back through the injected commit callback (which applies it, records undo
/// and marks the document dirty). <see cref="Refresh"/> re-reads the value in place after an external change
/// (undo/redo, or a second editor of the same field).
/// </summary>
public sealed class PropertyRowViewModel : INotifyPropertyChanged
{
    // (descriptor, before, after) — applies the edit through the viewport (undo + dirty tracking).
    private readonly PropertyDescriptor _d;
    private readonly Action<PropertyDescriptor, object?, object?>? _commit;

    public PropertyRowViewModel(PropertyDescriptor descriptor, Action<PropertyDescriptor, object?, object?>? commit)
    {
        _d = descriptor;
        _commit = commit;
        if (descriptor.Kind == PropertyKind.Flags && descriptor.FlagItems is { } items)
            Flags = items.Select(f => new FlagOptionViewModel(this, f.Name, f.Value)).ToList();
    }

    public string Label => _d.Label;
    public string? Tooltip => _d.Tooltip;
    public bool IsReadOnly => _d.Set == null;
    public PropertyKind Kind => _d.Kind;

    // ── Text-like value (Int / UInt64Hex / Float / Text; also the read-only display of Bool) ──
    public string Text
    {
        get => Format();
        set => CommitText(value);
    }

    // ── Bool ──
    public bool BoolValue
    {
        get => _d.Get() is bool b && b;
        set => CommitBoxed(value);
    }

    // ── Vector3 (bound to a Vector3Box as three doubles) ──
    public double VecX { get => Vec().X; set => CommitVec(0, value); }
    public double VecY { get => Vec().Y; set => CommitVec(1, value); }
    public double VecZ { get => Vec().Z; set => CommitVec(2, value); }

    // ── HashName ──
    public string HashText
    {
        get => Hash().Name;
        // Reject an empty name (it would keep the stale hash) and a no-op rename: the boxed hash is 0 here, so the
        // generic Equals guard in CommitBoxed can't tell an unchanged name from a real edit — check the name itself.
        set
        {
            if (string.IsNullOrEmpty(value) || value == Hash().Name) { Refresh(); return; }
            CommitBoxed(new HashNameValue(0, value));
        }
    }
    public string HashHex => "0x" + Hash().Hash.ToString("X", CultureInfo.InvariantCulture);

    // ── Flags ──
    public IReadOnlyList<FlagOptionViewModel>? Flags { get; }
    public string FlagsSummary
    {
        get
        {
            long v = CurrentFlagsValue;
            if (v == 0 || Flags is null) return "None";
            var names = Flags.Where(f => (v & f.Value) == f.Value).Select(f => f.Name).ToList();
            return names.Count == 0 ? "0x" + v.ToString("X", CultureInfo.InvariantCulture) : string.Join(" | ", names);
        }
    }
    internal long CurrentFlagsValue => _d.Get() is long l ? l : 0;

    // ── Matrix / StructList (read-only lines) ──
    public IReadOnlyList<string> Lines
    {
        get
        {
            if (_d.Get() is IReadOnlyList<string> lines) return lines;
            if (_d.Get() is Matrix4x4 m) return MatrixLines(m);
            return Array.Empty<string>();
        }
    }

    /// <summary>Re-reads every value from the descriptor (after an external change) without rebuilding the row.</summary>
    public void Refresh()
    {
        RaiseAll();
        if (Flags is not null) foreach (FlagOptionViewModel f in Flags) f.Refresh();
    }

    internal void ToggleFlag(long bit, bool on)
    {
        long cur = CurrentFlagsValue;
        long next = on ? cur | bit : cur & ~bit;
        CommitBoxed(next);
    }

    // ── Commit paths ──
    private void CommitText(string input)
    {
        object? after = Parse(input);
        if (after is null) { Refresh(); return; } // unparseable → revert the field to the current value
        CommitBoxed(after);
    }

    private void CommitVec(int axis, double value)
    {
        Vector3 v = Vec();
        var f = (float)value;
        Vector3 next = axis switch
        {
            0 => new Vector3(f, v.Y, v.Z),
            1 => new Vector3(v.X, f, v.Z),
            _ => new Vector3(v.X, v.Y, f),
        };
        CommitBoxed(next);
    }

    private void CommitBoxed(object? after)
    {
        if (_d.Set is null) return;
        object? before = _d.Get();
        if (Equals(before, after)) { Refresh(); return; } // phantom edit (focus-and-leave) — nothing to record
        _commit?.Invoke(_d, before, after);
        Refresh();
    }

    private object? Parse(string s) => _d.Kind switch
    {
        PropertyKind.Int => ParseInt(s),
        PropertyKind.Float => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : null,
        PropertyKind.UInt64Hex => ParseHex(s),
        PropertyKind.Text => s,
        _ => null,
    };

    private object? ParseInt(string s)
    {
        if (!long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) return null;
        return Math.Clamp(v, _d.Min, _d.Max);
    }

    private static object? ParseHex(string s)
    {
        string h = s.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        return ulong.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v) ? v : null;
    }

    private string Format() => _d.Kind switch
    {
        PropertyKind.Int => (_d.Get() is long l ? l : 0).ToString(CultureInfo.InvariantCulture),
        PropertyKind.Float => (_d.Get() is float f ? f : 0f).ToString(CultureInfo.InvariantCulture),
        PropertyKind.UInt64Hex => "0x" + (_d.Get() is ulong u ? u : 0).ToString("X", CultureInfo.InvariantCulture),
        PropertyKind.Bool => _d.Get() is true ? "Yes" : "No",
        PropertyKind.Text => _d.Get() as string ?? "",
        _ => _d.Get()?.ToString() ?? "",
    };

    private Vector3 Vec() => _d.Get() is Vector3 v ? v : default;
    private HashNameValue Hash() => _d.Get() is HashNameValue h ? h : default;

    private static string[] MatrixLines(Matrix4x4 m) => new[]
    {
        Row(m.M11, m.M12, m.M13, m.M14),
        Row(m.M21, m.M22, m.M23, m.M24),
        Row(m.M31, m.M32, m.M33, m.M34),
        Row(m.M41, m.M42, m.M43, m.M44),
    };

    private static string Row(float a, float b, float c, float d) =>
        $"{a,9:0.###}{b,10:0.###}{c,10:0.###}{d,10:0.###}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

/// <summary>One checkbox of a <see cref="PropertyKind.Flags"/> row — a single named bit.</summary>
public sealed class FlagOptionViewModel : INotifyPropertyChanged
{
    private readonly PropertyRowViewModel _row;

    public FlagOptionViewModel(PropertyRowViewModel row, string name, long value)
    {
        _row = row;
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public long Value { get; }

    public bool IsSet
    {
        get => (_row.CurrentFlagsValue & Value) == Value;
        set => _row.ToggleFlag(Value, value);
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSet)));

    public event PropertyChangedEventHandler? PropertyChanged;
}
