using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Illusion.Views;

/// <summary>
/// A reusable single-value field: a label, a dark input box and a copy button — the single-value sibling of
/// <see cref="Vector3Box"/>. Two-way <see cref="Value"/> (committed on Enter / lost focus); <see cref="ReadOnly"/>
/// shows the value dimmed and non-editable but still copyable. <see cref="ShowDelete"/> adds a remove button
/// to the label row (raising <see cref="DeleteClicked"/>) so the input keeps the full row width. Used for a
/// frame object's Name (editable), its auto-derived Hash (read-only) and the material editor's rows.
/// </summary>
public partial class CopyableTextField : UserControl
{
    public CopyableTextField()
    {
        InitializeComponent();
        UpdatePaste();
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(CopyableTextField), new PropertyMetadata(""));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(CopyableTextField),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ReadOnlyProperty = DependencyProperty.Register(
        nameof(ReadOnly), typeof(bool), typeof(CopyableTextField),
        new PropertyMetadata(false, (d, _) => ((CopyableTextField)d).UpdatePaste()));

    public static readonly DependencyProperty ShowDeleteProperty = DependencyProperty.Register(
        nameof(ShowDelete), typeof(bool), typeof(CopyableTextField),
        new PropertyMetadata(false, (d, _) => ((CopyableTextField)d).UpdateDelete()));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool ReadOnly { get => (bool)GetValue(ReadOnlyProperty); set => SetValue(ReadOnlyProperty, value); }
    public bool ShowDelete { get => (bool)GetValue(ShowDeleteProperty); set => SetValue(ShowDeleteProperty, value); }

    /// <summary>Raised by the optional remove button; the owner decides what removal means.</summary>
    public event RoutedEventHandler? DeleteClicked;

    // Paste only makes sense on an editable field.
    private void UpdatePaste()
    {
        if (PasteBtn != null) PasteBtn.Visibility = ReadOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateDelete()
    {
        if (DeleteBtn != null) DeleteBtn.Visibility = ShowDelete ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteClicked?.Invoke(this, e);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Value ?? ""); }
        catch { /* clipboard may be locked by another process — ignore */ }
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch { return; }
        // Single-line field: take the first line, trimmed. Setting Value pushes through the two-way binding
        // (commits the edit); the field text follows.
        text = text.Replace("\r", "").Split('\n')[0].Trim();
        if (text.Length == 0) return;
        Value = text;
    }

    // Enter commits the field and releases keyboard focus (so the fly camera's WASD works again).
    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb) return;
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }
}
