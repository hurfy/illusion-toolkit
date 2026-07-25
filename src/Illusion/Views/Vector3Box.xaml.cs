using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Illusion.Views;

/// <summary>
/// Reusable three-component (X/Y/Z) numeric editor with copy/paste. Styled to match the app; used for a frame
/// object's Position / Rotation / Scale and for the camera position. The three values are <see cref="X"/>,
/// <see cref="Y"/>, <see cref="Z"/> dependency properties (two-way by default), so it binds to a view-model or
/// is driven directly in code. <see cref="ValueCommitted"/> fires after the user edits a field or pastes.
///
/// Two layouts, chosen by <see cref="Compact"/>: compact = one row (label · X Y Z · paste · copy), for the
/// wide status bar; block (default) = label + paste/copy on top with the three fields spanning the full width
/// below, so a narrow side panel never clips a field. Both layouts share the same value logic.
///
/// Copy/paste format: three invariant-culture numbers separated by ", " (e.g. <c>-125.4, 33.9, 8.75</c>).
/// Paste is lenient — it pulls the first up-to-three numbers out of the clipboard text.
/// </summary>
public partial class Vector3Box : UserControl
{
    private static readonly Regex NumberRegex =
        new(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);

    private readonly (TextBox Box, DependencyProperty Prop)[] _fields;

    public Vector3Box()
    {
        InitializeComponent();
        _fields = new[]
        {
            (CompactX, XProperty), (CompactY, YProperty), (CompactZ, ZProperty),
            (BlockX, XProperty), (BlockY, YProperty), (BlockZ, ZProperty),
        };
        foreach ((TextBox box, DependencyProperty prop) in _fields) HookField(box, prop);
        CompactCopy.Click += (_, _) => Copy();
        BlockCopy.Click += (_, _) => Copy();
        CompactPaste.Click += (_, _) => Paste();
        BlockPaste.Click += (_, _) => Paste();

        ApplyCompact();
        ApplyShowActions();
        RefreshText();
        UpdateLabel();
    }

    /// <summary>Raised after the user commits a field edit or pastes — lets a non-binding host (camera) write back.</summary>
    public event EventHandler? ValueCommitted;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(Vector3Box),
        new PropertyMetadata("", (d, _) => ((Vector3Box)d).UpdateLabel()));

    public static readonly DependencyProperty XProperty = Register(nameof(X));
    public static readonly DependencyProperty YProperty = Register(nameof(Y));
    public static readonly DependencyProperty ZProperty = Register(nameof(Z));

    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(
        nameof(Decimals), typeof(int), typeof(Vector3Box),
        new PropertyMetadata(3, (d, _) => ((Vector3Box)d).RefreshText()));

    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.Register(
        nameof(LabelWidth), typeof(double), typeof(Vector3Box),
        new PropertyMetadata(double.NaN, (d, e) =>
        {
            var c = (Vector3Box)d;
            if (c.CompactLabel != null) c.CompactLabel.Width = (double)e.NewValue;
        }));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(Vector3Box),
        new PropertyMetadata(false, (d, _) => ((Vector3Box)d).ApplyCompact()));

    /// <summary>Whether to show the copy / paste buttons. Off for a minimal fields-only editor (viewport overlay).</summary>
    public static readonly DependencyProperty ShowActionsProperty = DependencyProperty.Register(
        nameof(ShowActions), typeof(bool), typeof(Vector3Box),
        new PropertyMetadata(true, (d, _) => ((Vector3Box)d).ApplyShowActions()));

    private static DependencyProperty Register(string name) => DependencyProperty.Register(
        name, typeof(double), typeof(Vector3Box),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((Vector3Box)d).RefreshText()));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double X { get => (double)GetValue(XProperty); set => SetValue(XProperty, value); }
    public double Y { get => (double)GetValue(YProperty); set => SetValue(YProperty, value); }
    public double Z { get => (double)GetValue(ZProperty); set => SetValue(ZProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public double LabelWidth { get => (double)GetValue(LabelWidthProperty); set => SetValue(LabelWidthProperty, value); }
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }
    public bool ShowActions { get => (bool)GetValue(ShowActionsProperty); set => SetValue(ShowActionsProperty, value); }

    private void ApplyCompact()
    {
        if (CompactRoot == null || BlockRoot == null) return;
        CompactRoot.Visibility = Compact ? Visibility.Visible : Visibility.Collapsed;
        BlockRoot.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyShowActions()
    {
        if (CompactCopy == null) return; // template not applied yet (ctor calls this after InitializeComponent)
        Visibility v = ShowActions ? Visibility.Visible : Visibility.Collapsed;
        CompactCopy.Visibility = v;
        CompactPaste.Visibility = v;
        BlockCopy.Visibility = v;
        BlockPaste.Visibility = v;
    }

    private void HookField(TextBox box, DependencyProperty prop)
    {
        box.LostFocus += (_, _) => CommitField(box, prop);
        // Enter commits, then releases keyboard focus so the app regains its keys (the fly camera's WASD is
        // suppressed while a TextBox holds focus). Mirrors the old camera-field / speed-field behaviour.
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitField(box, prop); Keyboard.ClearFocus(); } };
    }

    // Push the current DP values into every field, skipping any the user is editing.
    private void RefreshText()
    {
        if (_fields == null) return;
        foreach ((TextBox box, DependencyProperty prop) in _fields) SetIfIdle(box, (double)GetValue(prop));
    }

    private void SetIfIdle(TextBox box, double value)
    {
        if (box == null || box.IsKeyboardFocused) return;
        string s = value.ToString("F" + Math.Clamp(Decimals, 0, 8), CultureInfo.InvariantCulture);
        if (box.Text != s) box.Text = s;
    }

    private void CommitField(TextBox box, DependencyProperty prop)
    {
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            // Only write back a genuine edit: if the parsed value equals what the field already displays (the
            // current value rounded to Decimals), the user just focused and left — writing back would push a
            // phantom transform edit (an extra undo step + a silent sub-precision nudge from round-trip drift).
            int dec = Math.Clamp(Decimals, 0, 8);
            if (Math.Round(v, dec) != Math.Round((double)GetValue(prop), dec))
            {
                SetValue(prop, v);
                ValueCommitted?.Invoke(this, EventArgs.Empty);
            }
        }
        // Force the box to reflect the committed value (or revert unparseable input) — even if it still has
        // focus, e.g. on Enter, so bad input never lingers silently.
        string s = ((double)GetValue(prop)).ToString("F" + Math.Clamp(Decimals, 0, 8), CultureInfo.InvariantCulture);
        if (box.Text != s) box.Text = s;
    }

    private void Copy()
    {
        try { Clipboard.SetText(FormatTriple(X, Y, Z)); }
        catch { /* clipboard may be locked by another process — ignore */ }
    }

    private void Paste()
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch { return; }

        if (ParseTriple(text) is not { } t) return;
        X = t.X;
        Y = t.Y;
        Z = t.Z;
        RefreshText();
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copy format: three invariant-culture numbers at full precision separated by <c>", "</c>.</summary>
    internal static string FormatTriple(double x, double y, double z) => $"{Fmt(x)}, {Fmt(y)}, {Fmt(z)}";

    /// <summary>
    /// Lenient paste parse: pulls the first up-to-three numbers out of arbitrary text (brackets / labels /
    /// separators are ignored). Missing components keep 0. Null when the text holds no number at all.
    /// </summary>
    internal static (double X, double Y, double Z)? ParseTriple(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        MatchCollection matches = NumberRegex.Matches(text);
        if (matches.Count == 0) return null;

        var v = new double[3];
        int n = Math.Min(3, matches.Count);
        for (int i = 0; i < n; i++)
            double.TryParse(matches[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
        return (v[0], v[1], v[2]);
    }

    // Shortest round-trippable representation of the underlying float (the transform/camera values are float):
    // clean for round values ("33.9"), lossless for precise ones ("0.19853017") — not display-rounded.
    private static string Fmt(double v) => ((float)v).ToString(CultureInfo.InvariantCulture);

    private void UpdateLabel()
    {
        if (CompactLabel == null || BlockLabel == null) return;
        bool has = !string.IsNullOrEmpty(Label);
        Visibility vis = has ? Visibility.Visible : Visibility.Collapsed;
        CompactLabel.Text = Label;
        BlockLabel.Text = Label;
        CompactLabel.Visibility = vis;
        BlockLabel.Visibility = vis;
    }
}
