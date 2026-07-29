using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Illusion.Settings;

/// <summary>Every action the editor lets the user put on a key. The names are the persistence format
/// (<see cref="UserSettings.Hotkeys"/> is keyed by them) — renaming one drops that user's rebinding.</summary>
public enum HotkeyId
{
    Save,
    Import,
    OpenSettings,
    Undo,
    Redo,
    Delete,
    Duplicate,
    ToggleWalk,
    FrameSelection,
    FrameSelectionAlt,
    BridgeToggle,
    BridgeLeave,
    GizmoMove,
    GizmoRotate,
    GizmoScale,
    ModalCommit,
    ModalCommitAlt,
    ModalCancel,
    AxisX,
    AxisY,
    AxisZ,
    CameraForward,
    CameraBack,
    CameraLeft,
    CameraRight,
    CameraFast,
    CameraSlow,
}

/// <summary>
/// When an action's key is listened for — and therefore which other actions it can actually collide with.
/// Two bindings only conflict inside one scope: the editor deliberately ships <c>S</c> as Scale, as the
/// camera's "back", and as part of Ctrl+S, because no two of those are ever live at the same moment.
/// </summary>
public enum HotkeyScope
{
    /// <summary>The editor window's keys — menu commands and the keys that start something in the viewport.</summary>
    Editor,

    /// <summary>Only while a modal transform is running; it owns the keyboard until it ends.</summary>
    Modal,

    /// <summary>Only while walk mode flies the camera, where the letter keys mean movement.</summary>
    Camera,
}

/// <summary>One rebindable action: what it is called in the settings list, where it is listened for, and the
/// key it ships with.</summary>
/// <param name="Group">The heading it is listed under, and the order the headings appear in.</param>
/// <param name="ModifierOnly">The binding is a held modifier rather than a key (the camera speed keys), so
/// the settings window offers a short list of modifiers instead of a key capture.</param>
public sealed record HotkeyAction(
    HotkeyId Id,
    HotkeyScope Scope,
    string Group,
    string Label,
    string Description,
    Hotkey Default,
    bool ModifierOnly = false);

/// <summary>The table of rebindable actions, in the order the settings window lists them.</summary>
public static class HotkeyCatalog
{
    private const string GroupFile = "File";
    private const string GroupEdit = "Edit";
    private const string GroupViewport = "Viewport";
    private const string GroupBridge = "Blender bridge";
    private const string GroupTransform = "Transform";
    private const string GroupModal = "Transform — while dragging";
    private const string GroupCamera = "Camera — walk mode";

    private static readonly HotkeyAction[] All =
    {
        new(HotkeyId.Save, HotkeyScope.Editor, GroupFile, "Save",
            "Write the edited scene back to the extracted folder.",
            new Hotkey(Key.S, ModifierKeys.Control)),
        new(HotkeyId.Import, HotkeyScope.Editor, GroupFile, "Import…",
            "Open the import dialog for an external model.",
            new Hotkey(Key.I, ModifierKeys.Control)),
        new(HotkeyId.OpenSettings, HotkeyScope.Editor, GroupFile, "Settings…",
            "Open this window.",
            new Hotkey(Key.OemComma, ModifierKeys.Control)),

        new(HotkeyId.Undo, HotkeyScope.Editor, GroupEdit, "Undo",
            "Step back through the edit history.",
            new Hotkey(Key.Z, ModifierKeys.Control)),
        new(HotkeyId.Redo, HotkeyScope.Editor, GroupEdit, "Redo",
            "Step forward again.",
            new Hotkey(Key.Z, ModifierKeys.Control | ModifierKeys.Shift)),
        new(HotkeyId.Delete, HotkeyScope.Editor, GroupEdit, "Delete",
            "Remove the selected objects. Still deletes characters while a text field has focus.",
            new Hotkey(Key.Delete, ModifierKeys.None)),
        new(HotkeyId.Duplicate, HotkeyScope.Editor, GroupEdit, "Duplicate",
            "Clone the selection in place.",
            new Hotkey(Key.D, ModifierKeys.Control)),

        new(HotkeyId.ToggleWalk, HotkeyScope.Editor, GroupViewport, "Walk mode",
            "Switch between the mouse-only orbit camera and WASD flying.",
            new Hotkey(Key.Space, ModifierKeys.None)),
        new(HotkeyId.FrameSelection, HotkeyScope.Editor, GroupViewport, "Frame selection",
            "Fly the camera to whatever is selected.",
            new Hotkey(Key.OemQuestion, ModifierKeys.None)),
        new(HotkeyId.FrameSelectionAlt, HotkeyScope.Editor, GroupViewport, "Frame selection (second key)",
            "A second key for the same thing — the numeric keypad is a separate set of keys.",
            new Hotkey(Key.Divide, ModifierKeys.None)),

        new(HotkeyId.BridgeToggle, HotkeyScope.Editor, GroupBridge, "Enter / leave Edit Mode",
            "Send the selection to Blender, or come back from it.",
            new Hotkey(Key.Tab, ModifierKeys.None)),
        new(HotkeyId.BridgeLeave, HotkeyScope.Editor, GroupBridge, "Leave Edit Mode",
            "Come back from Blender without toggling into it.",
            new Hotkey(Key.Escape, ModifierKeys.None)),

        new(HotkeyId.GizmoMove, HotkeyScope.Editor, GroupTransform, "Move",
            "Start a move that follows the pointer. Not available in walk mode, which spends the letter keys on flying.",
            new Hotkey(Key.G, ModifierKeys.None)),
        new(HotkeyId.GizmoRotate, HotkeyScope.Editor, GroupTransform, "Rotate",
            "Start a rotation that follows the pointer.",
            new Hotkey(Key.R, ModifierKeys.None)),
        new(HotkeyId.GizmoScale, HotkeyScope.Editor, GroupTransform, "Scale",
            "Start a scale that follows the pointer.",
            new Hotkey(Key.S, ModifierKeys.None)),

        new(HotkeyId.ModalCommit, HotkeyScope.Modal, GroupModal, "Confirm",
            "Keep the transform under way.",
            new Hotkey(Key.Return, ModifierKeys.None)),
        new(HotkeyId.ModalCommitAlt, HotkeyScope.Modal, GroupModal, "Confirm (second key)",
            "A second key for confirming.",
            new Hotkey(Key.Space, ModifierKeys.None)),
        new(HotkeyId.ModalCancel, HotkeyScope.Modal, GroupModal, "Cancel",
            "Put everything back exactly as it was; nothing is recorded.",
            new Hotkey(Key.Escape, ModifierKeys.None)),
        new(HotkeyId.AxisX, HotkeyScope.Modal, GroupModal, "Lock to X",
            "Pin the drag to the world X axis; with Shift, to the plane across it.",
            new Hotkey(Key.X, ModifierKeys.None)),
        new(HotkeyId.AxisY, HotkeyScope.Modal, GroupModal, "Lock to Y",
            "Pin the drag to the world Y axis; with Shift, to the plane across it.",
            new Hotkey(Key.Y, ModifierKeys.None)),
        new(HotkeyId.AxisZ, HotkeyScope.Modal, GroupModal, "Lock to Z",
            "Pin the drag to the world Z axis; with Shift, to the plane across it.",
            new Hotkey(Key.Z, ModifierKeys.None)),

        new(HotkeyId.CameraForward, HotkeyScope.Camera, GroupCamera, "Forward",
            "Fly along the look direction.",
            new Hotkey(Key.W, ModifierKeys.None)),
        new(HotkeyId.CameraBack, HotkeyScope.Camera, GroupCamera, "Back",
            "Fly against the look direction.",
            new Hotkey(Key.S, ModifierKeys.None)),
        new(HotkeyId.CameraLeft, HotkeyScope.Camera, GroupCamera, "Strafe left",
            "Slide left without turning.",
            new Hotkey(Key.A, ModifierKeys.None)),
        new(HotkeyId.CameraRight, HotkeyScope.Camera, GroupCamera, "Strafe right",
            "Slide right without turning.",
            new Hotkey(Key.D, ModifierKeys.None)),
        new(HotkeyId.CameraFast, HotkeyScope.Camera, GroupCamera, "Cover ground",
            "Held with a movement key: fly faster. Held with them it also stops that combination from "
            + "reaching the editor's shortcuts, so flying can never save or duplicate by accident.",
            new Hotkey(Key.None, ModifierKeys.Shift), ModifierOnly: true),
        new(HotkeyId.CameraSlow, HotkeyScope.Camera, GroupCamera, "Creep",
            "Held with a movement key: fly slower.",
            new Hotkey(Key.None, ModifierKeys.Control), ModifierOnly: true),
    };

    private static readonly Dictionary<HotkeyId, HotkeyAction> ById =
        All.ToDictionary(a => a.Id);

    /// <summary>Every rebindable action, in display order.</summary>
    public static ReadOnlyCollection<HotkeyAction> Actions { get; } = new(All);

    /// <summary>The group headings, in display order.</summary>
    public static IEnumerable<string> Groups => All.Select(a => a.Group).Distinct();

    public static HotkeyAction Get(HotkeyId id) => ById[id];

    /// <summary>The key the action ships with — what "Reset" puts back.</summary>
    public static Hotkey Default(HotkeyId id) => ById[id].Default;
}
