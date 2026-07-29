using System.Windows.Input;

namespace Illusion.Settings;

/// <summary>
/// The live keyboard layout: <see cref="HotkeyCatalog"/>'s defaults with the user's rebindings on top.
/// Only the rebindings are held (and stored) — an action the user never touched follows whatever default
/// the build ships, so changing a default reaches everyone who kept it.
/// <para>
/// Windows read this map instead of naming keys themselves, and re-read it on <see cref="Changed"/>: a
/// rebinding takes effect while the editor is open, with nothing to restart.
/// </para>
/// </summary>
public sealed class HotkeyMap
{
    private readonly Dictionary<HotkeyId, Hotkey> _overrides = new();
    private readonly bool _persistent;

    private HotkeyMap(bool persistent) => _persistent = persistent;

    /// <summary>The one map the whole application binds against.</summary>
    public static HotkeyMap Current { get; } = FromSettings(UserSettings.Current, persistent: true);

    /// <summary>
    /// A map read from some other settings object, which does NOT write back. The probes load hand-built
    /// settings through it to check what a file with unknown or mangled entries turns into, without that
    /// question costing the user their real keymap.
    /// </summary>
    internal static HotkeyMap Detached(UserSettings settings) => FromSettings(settings, persistent: false);

    /// <summary>Raised after any rebinding, once the change is on disk.</summary>
    public event Action? Changed;

    /// <summary>The key an action is on right now.</summary>
    public Hotkey this[HotkeyId id] => _overrides.TryGetValue(id, out Hotkey hotkey)
        ? hotkey
        : HotkeyCatalog.Default(id);

    /// <summary>True while the action still carries the key it shipped with.</summary>
    public bool IsDefault(HotkeyId id) => !_overrides.ContainsKey(id);

    /// <summary>True when the map has nothing on top of the shipped defaults.</summary>
    public bool IsPristine => _overrides.Count == 0;

    /// <summary>Does a key-down event fire this action?</summary>
    public bool Matches(HotkeyId id, Key key, ModifierKeys modifiers) => this[id].Matches(key, modifiers);

    /// <summary>Puts an action on a combination. Setting it back to the shipped key drops the override, so
    /// the action follows the default again rather than pinning today's value forever.</summary>
    public void Set(HotkeyId id, Hotkey hotkey)
    {
        if (this[id] == hotkey) return;

        if (hotkey == HotkeyCatalog.Default(id)) _overrides.Remove(id);
        else _overrides[id] = hotkey;
        Persist();
    }

    /// <summary>Puts one action back on its shipped key.</summary>
    public void Reset(HotkeyId id)
    {
        if (_overrides.Remove(id)) Persist();
    }

    /// <summary>Puts every action back on its shipped key.</summary>
    public void ResetAll()
    {
        if (_overrides.Count == 0) return;
        _overrides.Clear();
        Persist();
    }

    /// <summary>
    /// The other actions sharing this one's combination — but only those listened for at the same moment
    /// (see <see cref="HotkeyScope"/>). Cross-scope sharing is normal and deliberate: <c>S</c> is Scale in
    /// the editor and "back" to the camera, and the two modes are never live together.
    /// </summary>
    public IReadOnlyList<HotkeyAction> ConflictsWith(HotkeyId id)
    {
        Hotkey mine = this[id];
        if (!mine.IsBound) return Array.Empty<HotkeyAction>();

        HotkeyScope scope = HotkeyCatalog.Get(id).Scope;
        List<HotkeyAction>? hits = null;
        foreach (HotkeyAction action in HotkeyCatalog.Actions)
        {
            if (action.Id == id || action.Scope != scope || this[action.Id] != mine) continue;
            (hits ??= new List<HotkeyAction>()).Add(action);
        }
        return (IReadOnlyList<HotkeyAction>?)hits ?? Array.Empty<HotkeyAction>();
    }

    /// <summary>Every combination that collides with another inside its own scope.</summary>
    public bool HasConflicts()
    {
        foreach (HotkeyAction action in HotkeyCatalog.Actions)
        {
            if (ConflictsWith(action.Id).Count > 0) return true;
        }
        return false;
    }

    /// <summary>The overrides this map would store — the exact shape that reaches settings.json.</summary>
    internal IReadOnlyDictionary<HotkeyId, Hotkey> Overrides => _overrides;

    private static HotkeyMap FromSettings(UserSettings settings, bool persistent)
    {
        var map = new HotkeyMap(persistent);
        foreach ((string name, string gesture) in settings.Hotkeys)
        {
            // A name from a newer build, or a gesture mangled by a hand edit: keep the default rather than
            // leaving the action unbound, which would look like the editor lost a key.
            if (!Enum.TryParse(name, out HotkeyId id) || !Enum.IsDefined(id)) continue;
            if (!Hotkey.TryParse(gesture, out Hotkey hotkey)) continue;
            if (hotkey == HotkeyCatalog.Default(id)) continue;
            map._overrides[id] = hotkey;
        }
        return map;
    }

    private void Persist()
    {
        if (_persistent)
        {
            UserSettings.Update(settings =>
            {
                settings.Hotkeys.Clear();
                foreach ((HotkeyId id, Hotkey hotkey) in _overrides)
                {
                    settings.Hotkeys[id.ToString()] = hotkey.ToString();
                }
            });
        }
        Changed?.Invoke();
    }
}
