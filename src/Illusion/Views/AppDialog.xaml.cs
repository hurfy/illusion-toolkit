using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Illusion.Views;

/// <summary>Icon shown beside the message (glyph + tint). <see cref="None"/> hides the icon column.</summary>
public enum DialogIcon { None, Info, Success, Warning, Error, Question }

/// <summary>Which buttons the dialog offers: a single OK (a notification) or a confirm/cancel pair (an action).</summary>
public enum DialogButtons { Ok, YesCancel }

/// <summary>
/// Everything the <see cref="AppDialog"/> shows. Every visual part is optional: leave a field null/empty and that
/// part collapses. A minimal notification is just <see cref="Text"/>; a full confirm sets <see cref="Heading"/>,
/// <see cref="Text"/>, a <see cref="CheckboxText"/> and <see cref="DialogButtons.YesCancel"/>.
/// </summary>
public sealed class DialogOptions
{
    /// <summary>Window title (title bar). Defaults to the app name.</summary>
    public string Title { get; init; } = "Illusion Toolkit";

    /// <summary>Optional bold heading line above the body text.</summary>
    public string? Heading { get; init; }

    /// <summary>Optional body text (wraps; may be multi-line).</summary>
    public string? Text { get; init; }

    /// <summary>Optional icon beside the message.</summary>
    public DialogIcon Icon { get; init; } = DialogIcon.None;

    /// <summary>OK (notification) or confirm/cancel (action). Defaults to OK.</summary>
    public DialogButtons Buttons { get; init; } = DialogButtons.Ok;

    /// <summary>Label for the confirm/OK button. Defaults to "OK" / "Yes".</summary>
    public string? ConfirmText { get; init; }

    /// <summary>Label for the cancel button (YesCancel only). Defaults to "Cancel".</summary>
    public string? CancelText { get; init; }

    /// <summary>Optional checkbox under the message; null/empty hides it.</summary>
    public string? CheckboxText { get; init; }

    /// <summary>Initial checkbox state.</summary>
    public bool CheckboxChecked { get; init; }
}

/// <summary>Result of an <see cref="AppDialog"/>: whether the confirm/OK button was pressed, and the final
/// checkbox state (false when there is no checkbox).</summary>
public readonly record struct DialogOutcome(bool Confirmed, bool Checked);

/// <summary>
/// A small, reusable modal dialog — the app's own styled stand-in for <c>MessageBox</c>. Built from a
/// <see cref="DialogOptions"/>: an optional icon, heading, body text and checkbox, plus either an OK button (a
/// notification) or a confirm/cancel pair (an action). Call <see cref="Show"/>; it returns a
/// <see cref="DialogOutcome"/>.
/// </summary>
public partial class AppDialog : Window
{
    // Segoe MDL2 Assets glyph code point + tint per icon kind (code points kept as ints so the source stays
    // plain ASCII). Tints read on both light and dark themes. 0 = no glyph.
    private static (int GlyphCode, Brush Brush) IconFor(DialogIcon icon) => icon switch
    {
        DialogIcon.Info => (0xE946, Frozen(0x2D, 0x7D, 0xD2)),     // Info        — accent blue
        DialogIcon.Success => (0xE930, Frozen(0x5F, 0xB6, 0x5F)),  // Completed   — green
        DialogIcon.Warning => (0xE7BA, Frozen(0xE8, 0xA3, 0x3D)),  // Warning     — amber
        DialogIcon.Error => (0xEA39, Frozen(0xE0, 0x73, 0x6B)),    // ErrorBadge  — red
        DialogIcon.Question => (0xE897, Frozen(0x2D, 0x7D, 0xD2)), // Help        — accent blue
        _ => (0, Brushes.Transparent),
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // Constructed via Show (or a probe in the same assembly); use Show for normal callers.
    internal AppDialog(DialogOptions options)
    {
        InitializeComponent();
        Apply(options);
    }

    private void Apply(DialogOptions o)
    {
        Title = o.Title;

        if (o.Icon == DialogIcon.None)
        {
            IconGlyph.Visibility = Visibility.Collapsed;
        }
        else
        {
            (int code, Brush brush) = IconFor(o.Icon);
            IconGlyph.Text = ((char)code).ToString();
            IconGlyph.Foreground = brush;
        }

        bool hasHeading = !string.IsNullOrEmpty(o.Heading);
        if (hasHeading)
            HeadingText.Text = o.Heading;
        else
            HeadingText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrEmpty(o.Text))
        {
            BodyText.Visibility = Visibility.Collapsed;
        }
        else
        {
            BodyText.Text = o.Text;
            // With no heading the body takes the heading's row so the gutter glyph stays centred on the first
            // line (an empty heading row would leave the glyph floating above nothing); the gap it leaves under
            // the heading goes too.
            if (!hasHeading)
            {
                Grid.SetRow(BodyText, 0);
                BodyText.Margin = default;
            }
        }

        if (string.IsNullOrEmpty(o.CheckboxText))
        {
            CheckBoxControl.Visibility = Visibility.Collapsed;
        }
        else
        {
            CheckBoxControl.Content = o.CheckboxText;
            CheckBoxControl.IsChecked = o.CheckboxChecked;
        }

        if (o.Buttons == DialogButtons.YesCancel)
        {
            SecondaryBtn.Content = o.CancelText ?? "Cancel";
            SecondaryBtn.IsCancel = true;      // Esc / close button → cancel
            PrimaryBtn.Content = o.ConfirmText ?? "Yes";
            PrimaryBtn.IsDefault = true;       // Enter → confirm
        }
        else // Ok — a single dismiss button that both Enter and Esc trigger
        {
            SecondaryBtn.Visibility = Visibility.Collapsed;
            PrimaryBtn.Content = o.ConfirmText ?? "OK";
            PrimaryBtn.IsDefault = true;
            PrimaryBtn.IsCancel = true;
        }
    }

    /// <summary>Whether the checkbox is currently checked (false when there is no checkbox).</summary>
    public bool Checked => CheckBoxControl.IsChecked == true;

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// Shows the dialog modally over <paramref name="owner"/> (centered on it; centered on screen when null) and
    /// returns the outcome — which button was pressed and the final checkbox state.
    /// </summary>
    public static DialogOutcome Show(Window? owner, DialogOptions options)
    {
        var dlg = new AppDialog(options);
        if (owner != null)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        bool confirmed = dlg.ShowDialog() == true;
        return new DialogOutcome(confirmed, dlg.Checked);
    }
}
