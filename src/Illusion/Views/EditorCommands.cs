using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Settings;

namespace Illusion.Views;

/// <summary>
/// The editor's menu commands, shared by every window that edits a loaded archive — the map editor and the
/// resource editor. They live here rather than on one of those windows so neither has to reach into the
/// other: each window registers its own <c>CommandBinding</c> for the ones it offers, and a menu item or a
/// key press routes to whichever window is in front.
/// <para>
/// No command carries a <c>KeyGesture</c>. WPF refuses one for an unmodified non-function key, which rules
/// out half of what is rebindable here; the keys come from <see cref="HotkeyMap"/> and are dispatched by each
/// window's <c>PreviewKeyDown</c> — see <see cref="CommandHotkeys"/>.
/// </para>
/// </summary>
internal static class EditorCommands
{
    /// <summary>Undo — Edit menu; the key comes from <see cref="HotkeyId.Undo"/>.</summary>
    public static readonly RoutedUICommand Undo = new("Undo", "Undo", typeof(EditorCommands));

    /// <summary>Redo — Edit menu; the key comes from <see cref="HotkeyId.Redo"/>.</summary>
    public static readonly RoutedUICommand Redo = new("Redo", "Redo", typeof(EditorCommands));

    /// <summary>Delete selected objects — hierarchy context menu; the key comes from <see cref="HotkeyId.Delete"/>.</summary>
    public static readonly RoutedUICommand Delete = new("Delete", "Delete", typeof(EditorCommands));

    /// <summary>Duplicate the selection — hierarchy context menu; the key comes from <see cref="HotkeyId.Duplicate"/>.</summary>
    public static readonly RoutedUICommand Duplicate = new("Duplicate", "Duplicate", typeof(EditorCommands));

    /// <summary>Save edits to disk — File menu; the key comes from <see cref="HotkeyId.Save"/>.</summary>
    public static readonly RoutedUICommand Save = new("Save", "Save", typeof(EditorCommands));

    /// <summary>Import an external model — File menu; the key comes from <see cref="HotkeyId.Import"/>.</summary>
    public static readonly RoutedUICommand Import = new("Import", "Import", typeof(EditorCommands));

    /// <summary>Open the settings window — File menu; the key comes from <see cref="HotkeyId.OpenSettings"/>.</summary>
    public static readonly RoutedUICommand Settings = new("Settings", "Settings", typeof(EditorCommands));

    /// <summary>
    /// Which rebindable action fires which command. Everything else the keyboard does belongs to a window's
    /// own viewport handling, not here.
    /// </summary>
    public static readonly (HotkeyId Id, RoutedUICommand Command)[] CommandHotkeys =
    {
        (HotkeyId.Save, Save),
        (HotkeyId.Import, Import),
        (HotkeyId.OpenSettings, Settings),
        (HotkeyId.Undo, Undo),
        (HotkeyId.Redo, Redo),
        (HotkeyId.Delete, Delete),
        (HotkeyId.Duplicate, Duplicate),
    };

    /// <summary>
    /// Runs the command a key is bound to, if any, and if it is available right now. Asking CanExecute first is
    /// what keeps text fields working: Delete and Undo report "unavailable" while one has focus, so the key is
    /// left alone and reaches the field — the same gate the Edit menu greys itself out with, rather than a
    /// second set of rules that could disagree with it.
    /// </summary>
    public static bool Handle(Key key, ModifierKeys modifiers, IInputElement target)
    {
        foreach ((HotkeyId id, RoutedUICommand command) in CommandHotkeys)
        {
            if (!HotkeyMap.Current.Matches(id, key, modifiers)) continue;
            if (!command.CanExecute(null, target)) return false;
            command.Execute(null, target);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether UNDO should stand aside for the field that has the focus.
    ///
    /// <para>
    /// Not simply "a text box has the focus", which is the rule every other key uses. A numeric field commits
    /// on Enter and KEEPS the caret, so after an edit the value is already in the file and the box is merely
    /// still focused; refusing undo then refuses it at the exact moment the user wants the edit back, which
    /// is what "Ctrl+Z does not work for the prefab" was. A field with nothing pending is not being typed
    /// into, whatever the focus says.
    /// </para>
    /// <para>
    /// UNDO ONLY, deliberately. Delete and Duplicate keep the plain focus rule: a caret sitting in a field
    /// that shows its committed value is still a caret, and Delete there has to delete a character rather
    /// than the selected object.
    /// </para>
    /// </summary>
    public static bool IsTypingUncommitted()
    {
        if (Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase box) return false;

        // A field inside a Vector3Box can say whether its text is still uncommitted; anything else — a name,
        // a search, a free-text box — is taken at its word and keeps the key.
        for (DependencyObject? at = box; at != null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is Vector3Box vector) return vector.HasPendingEdit;
        }
        return true;
    }
}
