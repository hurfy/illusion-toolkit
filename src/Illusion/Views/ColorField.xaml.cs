using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Illusion.Views;

/// <summary>
/// A shader parameter that is a colour, edited as one. The row keeps the raw numbers — they are what the
/// format stores and some of them are outside anything a picker can express — and puts a swatch beside them
/// that opens a hue / saturation-value picker.
/// <para>
/// The values are LINEAR (a car body's paint is <c>0.27 0.02 0.02</c>, a dark red). The swatch and the picker
/// work in the gamma-corrected space the viewport finally shows, so what you pick is what you see on the
/// car; the conversion happens on the way in and out and the stored numbers stay linear.
/// </para>
/// </summary>
public partial class ColorField : UserControl
{
    private const double Gamma = 2.2;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ColorField),
        new PropertyMetadata("", (d, e) => ((ColorField)d).LabelText.Text = (string)e.NewValue));

    /// <summary>The parameter's raw text — the same comma-separated floats the plain field shows. Two-way:
    /// the picker rewrites it, and typing rewrites the swatch.</summary>
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColorField),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((ColorField)d).OnValueChanged((string)e.NewValue)));

    public static readonly DependencyProperty SwatchProperty = DependencyProperty.Register(
        nameof(Swatch), typeof(Brush), typeof(ColorField), new PropertyMetadata(Brushes.Transparent));

    private double _h, _s, _v;      // the picker's own state, in display space
    private bool _syncing;          // guards the Value ⇄ picker round trip

    public ColorField()
    {
        InitializeComponent();
        SwatchBtn.Checked += (_, _) => SyncPickerFromValue();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>The colour the swatch paints — read by its template.</summary>
    public Brush Swatch
    {
        get => (Brush)GetValue(SwatchProperty);
        private set => SetValue(SwatchProperty, value);
    }

    // ── Value ⇄ colour ──

    private void OnValueChanged(string text)
    {
        if (!_syncing) ValueBox.Text = text;
        Swatch = TryParse(text, out Color colour) ? new SolidColorBrush(colour) : Brushes.Transparent;
    }

    /// <summary>The first three floats as a display colour. False when the text is not a colour at all —
    /// then the swatch shows nothing rather than lying about it.</summary>
    private static bool TryParse(string text, out Color colour)
    {
        colour = Colors.Transparent;
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;

        var channels = new byte[3];
        for (int i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return false;
            channels[i] = ToDisplay(v);
        }
        colour = Color.FromRgb(channels[0], channels[1], channels[2]);
        return true;
    }

    // Linear → what the eye is shown. Values above 1 (a material may carry them) clamp: the swatch is a hint,
    // not a measurement, and the numbers beside it remain the truth.
    private static byte ToDisplay(double linear) =>
        (byte)Math.Clamp(Math.Round(Math.Pow(Math.Clamp(linear, 0, 1), 1 / Gamma) * 255), 0, 255);

    private static double ToLinear(byte display) => Math.Pow(display / 255.0, Gamma);

    // Writes a picked colour back into the text, keeping any channels past the third (an alpha, usually)
    // exactly as they were — the picker has no opinion about them.
    private void CommitColour(Color colour)
    {
        string[] parts = ValueBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var floats = new List<string>
        {
            Fmt(ToLinear(colour.R)), Fmt(ToLinear(colour.G)), Fmt(ToLinear(colour.B)),
        };
        for (int i = 3; i < parts.Length; i++) floats.Add(parts[i]);

        _syncing = true;
        string text = string.Join(", ", floats);
        ValueBox.Text = text;
        Value = text;
        _syncing = false;
    }

    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    // ── The plain text field ──

    private void Value_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Value = ValueBox.Text; Keyboard.ClearFocus(); }
    }

    private void Value_LostFocus(object sender, RoutedEventArgs e) => Value = ValueBox.Text;

    // ── The picker ──

    private void SyncPickerFromValue()
    {
        if (!TryParse(ValueBox.Text, out Color colour)) colour = Colors.Black;
        (_h, _s, _v) = ToHsv(colour);
        RefreshPicker();
    }

    private void RefreshPicker()
    {
        HueFill.Background = new SolidColorBrush(FromHsv(_h, 1, 1));
        HexBox.Text = $"#{FromHsv(_h, _s, _v).R:X2}{FromHsv(_h, _s, _v).G:X2}{FromHsv(_h, _s, _v).B:X2}";

        // The thumbs are positioned by hand: the areas are plain Borders, so there is no track to bind to.
        SvThumb.Margin = new Thickness(_s * Math.Max(0, SvArea.ActualWidth) - 5.5,
            (1 - _v) * Math.Max(0, SvArea.ActualHeight) - 5.5, 0, 0);
        HueThumb.Margin = new Thickness(_h / 360.0 * Math.Max(0, HueArea.ActualWidth) - 2, 0, 0, 0);
    }

    private void Sv_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SvArea.CaptureMouse();
        SetSv(e.GetPosition(SvArea));
    }

    private void Sv_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && SvArea.IsMouseCaptured) SetSv(e.GetPosition(SvArea));
    }

    private void SetSv(Point p)
    {
        _s = Math.Clamp(p.X / Math.Max(1, SvArea.ActualWidth), 0, 1);
        _v = 1 - Math.Clamp(p.Y / Math.Max(1, SvArea.ActualHeight), 0, 1);
        RefreshPicker();
        Swatch = new SolidColorBrush(FromHsv(_h, _s, _v));   // live, without committing an edit per pixel
    }

    private void Hue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HueArea.CaptureMouse();
        SetHue(e.GetPosition(HueArea));
    }

    private void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && HueArea.IsMouseCaptured) SetHue(e.GetPosition(HueArea));
    }

    private void SetHue(Point p)
    {
        _h = Math.Clamp(p.X / Math.Max(1, HueArea.ActualWidth), 0, 1) * 360;
        RefreshPicker();
        Swatch = new SolidColorBrush(FromHsv(_h, _s, _v));
    }

    // One edit per drag, not one per pixel: dragging through a hundred shades must not leave a hundred
    // entries in the undo stack, and every one of them would rewrite the .mtl in memory.
    private void Picker_MouseUp(object sender, MouseButtonEventArgs e)
    {
        SvArea.ReleaseMouseCapture();
        HueArea.ReleaseMouseCapture();
        CommitColour(FromHsv(_h, _s, _v));
    }

    private void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyHex();
    }

    private void Hex_LostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    private void ApplyHex()
    {
        string text = HexBox.Text.Trim().TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            RefreshPicker();   // not a colour — put the field back rather than guess
            return;
        }
        var colour = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        (_h, _s, _v) = ToHsv(colour);
        RefreshPicker();
        CommitColour(colour);
    }

    // ── HSV ⇄ RGB ──

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;

        double h = 0;
        if (d > 1e-6)
        {
            if (max == r) h = 60 * (((g - b) / d + 6) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        return (h, max <= 1e-6 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = ((int)(h / 60) % 6) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
