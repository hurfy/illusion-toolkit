using System.Windows.Controls;
using System.Windows.Input;
using Illusion.Settings;

namespace Illusion.Views;

/// <summary>
/// The field the settings window records a key combination in: it shows the current one, and a click puts it
/// into recording, where the next keypress (with whatever modifiers are held) becomes the new value.
/// <para>
/// There is no key that cancels recording, on purpose — Esc is a combination the editor genuinely binds, and a
/// field that refused to record it would be a field that cannot express the defaults it ships with. Clicking
/// away leaves recording without changing anything, which is the way out.
/// </para>
/// </summary>
public partial class HotkeyBox : UserControl
{
    private Hotkey _value;

    public HotkeyBox()
    {
        InitializeComponent();
        Refresh();
    }

    /// <summary>Raised when a keypress was recorded — never for a programmatic <see cref="Value"/> change.</summary>
    public event Action<Hotkey>? Committed;

    /// <summary>The combination shown. Assigning it does not raise <see cref="Committed"/>.</summary>
    public Hotkey Value
    {
        get => _value;
        set
        {
            _value = value;
            Refresh();
        }
    }

    /// <summary>True while the next keypress would be recorded.</summary>
    public bool IsRecording => Btn.IsChecked == true;

    /// <summary>Leaves recording without changing the value.</summary>
    public void CancelRecording() => Btn.IsChecked = false;

    /// <summary>Records a combination as if it had been pressed — the probe's way in, and what the row's
    /// unbind button uses to clear the field.</summary>
    public void Commit(Hotkey hotkey)
    {
        Btn.IsChecked = false;
        if (hotkey == _value) return;
        Value = hotkey;
        Committed?.Invoke(hotkey);
    }

    private void Refresh()
    {
        // An unbound action reads as a dash rather than as an empty box, which looks like a rendering fault.
        Btn.Content = IsRecording ? "Press a key…" : _value.IsBound ? _value.ToString() : "—";
    }

    private void Btn_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        Refresh();
        Btn.Focus();   // recording reads the keyboard through this button
    }

    private void Btn_Unchecked(object sender, System.Windows.RoutedEventArgs e) => Refresh();

    private void Btn_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CancelRecording();

    private void Btn_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsRecording) return;

        // With Alt held, WPF puts Key.System in Key and the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // A modifier on its own is the first half of a combination, not a combination — keep waiting. So is
        // anything an input method swallowed on the way here.
        if (IsModifier(key) || key is Key.None or Key.ImeProcessed or Key.DeadCharProcessed) return;

        e.Handled = true;   // Tab must not traverse focus and Space must not toggle the button
        Commit(new Hotkey(key, Keyboard.Modifiers));
    }

    private static bool IsModifier(Key key) => key is
        Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
