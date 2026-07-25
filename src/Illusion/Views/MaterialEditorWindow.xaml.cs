using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Illusion.Assets.Textures;
using Illusion.Domain.Materials;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The material editor window (non-modal, one instance reused by the main window): an MTL library browser
/// on the left, the live preview sphere in the widest center column (name/hash identity fields above it,
/// the assign-to-mesh-slot overlay when opened from a mesh tile), and editable texture slots + shader
/// parameters on the right — all inputs are project-style <see cref="CopyableTextField"/>s (copy/paste,
/// commit on Enter / focus loss). Committing the name field renames the material: the FNV64 hash
/// re-derives from the name and the hash field follows. Texture names resolve against the WHOLE resources
/// mirror (<see cref="TextureSearchIndex"/>), not only loaded districts. All mutations go through the
/// viewport facade, so they are undoable (Ctrl+Z here too) and persist via the common Save flow (Ctrl+S).
/// Refreshes itself on <see cref="D3DImageHost.MaterialsChanged"/>.
/// </summary>
public partial class MaterialEditorWindow : Window
{
    private readonly D3DImageHost _viewport;
    private readonly TextureThumbnailRenderer _texThumbs = new();
    private List<MaterialSummary> _summaries = new();
    private ListCollectionView? _view;
    private ulong _currentHash;
    private SceneNode? _contextNode;
    private int _contextSlot;
    private bool _syncing; // the window is changing the list/library itself — ignore the selection echoes

    private static readonly RoutedCommand UndoCmd = new();
    private static readonly RoutedCommand RedoCmd = new();
    private static readonly RoutedCommand SaveCmd = new();

    public MaterialEditorWindow(D3DImageHost viewport)
    {
        InitializeComponent();
        _viewport = viewport;

        LibraryCombo.ItemsSource = viewport.MaterialCatalog.Libraries;
        Preview.SetFolders(viewport.TextureFolders);
        if (viewport.Catalogs.SkyTexturePath is { } sky) Preview.LoadSky(sky); // the map's panorama

        _viewport.MaterialsChanged += OnMaterialsChanged;
        _viewport.SceneChanged += OnSceneChanged;
        Closed += (_, _) =>
        {
            _viewport.MaterialsChanged -= OnMaterialsChanged;
            _viewport.SceneChanged -= OnSceneChanged;
            _texThumbs.Dispose();
        };

        // The shared history works from this window too — a material edit undoes where it was made.
        CommandBindings.Add(new CommandBinding(UndoCmd, (_, _) => _viewport.Undo()));
        CommandBindings.Add(new CommandBinding(RedoCmd, (_, _) => _viewport.Redo()));
        CommandBindings.Add(new CommandBinding(SaveCmd, (_, _) => CommitAndSave()));
        InputBindings.Add(new KeyBinding(UndoCmd, new KeyGesture(Key.Z, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(RedoCmd, new KeyGesture(Key.Z, ModifierKeys.Control | ModifierKeys.Shift)));
        InputBindings.Add(new KeyBinding(SaveCmd, new KeyGesture(Key.S, ModifierKeys.Control)));

        LibraryCombo.SelectedItem = viewport.MaterialCatalog.Libraries.FirstOrDefault(); // fires Library_Changed → list
    }

    /// <summary>Focuses the editor on one material. <paramref name="contextNode"/>/<paramref name="slotIndex"/>
    /// carry the mesh slot the editor was opened from (null = plain browsing, no assign overlay).</summary>
    public void ShowMaterial(ulong hash, SceneNode? contextNode, int slotIndex)
    {
        _contextNode = contextNode is { Source: IMaterialSlotEditor } ? contextNode : null;
        _contextSlot = slotIndex;

        SearchBox.Text = ""; // the filter must not hide the requested material
        string? library = _viewport.MaterialCatalog.LibraryOf(hash);
        if (library != null && !Equals(LibraryCombo.SelectedItem, library))
            LibraryCombo.SelectedItem = library; // rebuilds the list via Library_Changed
        SelectInList(hash, scroll: true);
        LoadMaterial(hash);
        UpdateAssignPanel();
    }

    // ── Library / list / search ──

    private void Library_Changed(object sender, RoutedEventArgs e) => RebuildList();

    private void RebuildList()
    {
        _summaries = LibraryCombo.SelectedItem is string library
            ? _viewport.MaterialCatalog.GetMaterials(library)
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<MaterialSummary>();
        _view = new ListCollectionView(_summaries) { Filter = FilterMaterial };
        _syncing = true;
        MaterialList.ItemsSource = _view;
        _syncing = false;
        SelectInList(_currentHash, scroll: false); // keep the shown material selected when it is still here
    }

    private bool FilterMaterial(object o) =>
        SearchBox.Text.Length == 0 ||
        (o is MaterialSummary m && m.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));

    private void Search_Changed(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void MaterialList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || MaterialList.SelectedItem is not MaterialSummary m) return;
        LoadMaterial(m.Hash);
        UpdateAssignPanel();
    }

    private void SelectInList(ulong hash, bool scroll)
    {
        MaterialSummary? target = _summaries.FirstOrDefault(m => m.Hash == hash);
        _syncing = true;
        MaterialList.SelectedItem = target;
        if (target != null && scroll) MaterialList.ScrollIntoView(target);
        _syncing = false;
    }

    // ── The shown material ──

    private void LoadMaterial(ulong hash)
    {
        _currentHash = hash;
        MaterialInfo? info = _viewport.MaterialCatalog.GetMaterial(hash);
        if (info == null)
        {
            ClearMaterialPanel();
            return;
        }

        NamePanel.DataContext = new NameEditRow(info.Name ?? "", hash, CommitRename);
        NamePanel.IsEnabled = true;

        IReadOnlyList<string> folders = _viewport.TextureFolders;
        Preview.SetFolders(folders);
        Preview.SetMaterial(hash, SlotOf(info, "S000"), SlotOf(info, "S001"), SlotOf(info, "S002"),
            MaterialPreviewViewport.LightingFor(info));

        var slotRows = new List<SlotEditRow>(info.TextureSlots.Count);
        foreach (MaterialSlotInfo slot in info.TextureSlots)
            slotRows.Add(new SlotEditRow(slot, ResolvesAnywhere(folders, slot.TextureName),
                _texThumbs.Render(slot.TextureName, folders), CommitSlotText));
        SlotsList.ItemsSource = slotRows;
        NoSlotsText.Visibility = slotRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Slots the material does not carry yet — the Add button's choices.
        var taken = new HashSet<string>(info.TextureSlots.Select(s => s.SlotId), StringComparer.Ordinal);
        var free = _viewport.MaterialCatalog.KnownSamplerSlots.Where(d => !taken.Contains(d.Id)).ToList();
        AddSlotList.ItemsSource = free;
        AddSlotBtn.IsEnabled = free.Count > 0;

        // Every known parameter is editable: the material's own first (file order), then the codes it
        // does not carry yet — empty rows whose commit ADDS the parameter.
        var paramRows = new List<ParamEditRow>(info.Parameters.Count);
        foreach (MaterialParamInfo p in info.Parameters)
            paramRows.Add(new ParamEditRow(p, CommitParamText));
        var carried = new HashSet<string>(info.Parameters.Select(p => p.ParamId), StringComparer.Ordinal);
        foreach (ParamDescriptor d in _viewport.MaterialCatalog.KnownParameters)
            if (!carried.Contains(d.Id))
                paramRows.Add(new ParamEditRow(d, CommitParamText));
        ParamsList.ItemsSource = paramRows;
        ParamsCaption.Text = $"PARAMETERS ({info.Parameters.Count}/{paramRows.Count})";
        NoParamsText.Visibility = paramRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        DeleteBtn.IsEnabled = true;
        AssignBtn.IsEnabled = _contextNode != null;
    }

    private void ClearMaterialPanel()
    {
        _currentHash = 0;
        NamePanel.DataContext = null;
        NamePanel.IsEnabled = false;
        SlotsList.ItemsSource = null;
        NoSlotsText.Visibility = Visibility.Collapsed;
        AddSlotList.ItemsSource = null;
        AddSlotBtn.IsEnabled = false;
        AddSlotBtn.IsChecked = false; // the shown material vanished — close a still-open type popup
        ParamsList.ItemsSource = null;
        ParamsCaption.Text = "PARAMETERS";
        NoParamsText.Visibility = Visibility.Collapsed;
        Preview.SetMaterial(0, null, null, null, MaterialPreviewViewport.LightingFor(null));
        DeleteBtn.IsEnabled = false;
        AssignBtn.IsEnabled = false;
    }

    private static string? SlotOf(MaterialInfo info, string id)
    {
        foreach (MaterialSlotInfo s in info.TextureSlots)
            if (s.SlotId == id)
                return s.TextureName;
        return null;
    }

    // A texture resolves when any loaded district folder has it OR the whole-mirror index knows it —
    // the same scope the preview sphere and thumbnails actually sample from.
    private static bool ResolvesAnywhere(IReadOnlyList<string> folders, string? texture)
    {
        if (string.IsNullOrEmpty(texture)) return false;
        foreach (string folder in folders)
            if (File.Exists(Path.Combine(folder, texture)))
                return true;
        return TextureSearchIndex.FindPath(texture) != null;
    }

    // A scene reload (area/season switch, restore-from-backup) replaced the tree — the pinned assign
    // target went with it, and assigning to it could never change anything visible (its GpuMesh is
    // disposed). Drop the context so the Assign button disappears instead of offering a dead action.
    // May fire off the UI thread (streaming) — marshal when needed.
    private void OnSceneChanged()
    {
        if (Dispatcher.CheckAccess()) DropStaleAssignContext();
        else Dispatcher.BeginInvoke(DropStaleAssignContext);
    }

    private void DropStaleAssignContext()
    {
        if (_contextNode == null || _viewport.IsNodeInScene(_contextNode)) return;
        _contextNode = null;
        UpdateAssignPanel();
    }

    // Any material edit (from here, the main window, or an undo elsewhere): refresh the list and the panel.
    private void OnMaterialsChanged()
    {
        RebuildList();
        if (_currentHash != 0)
        {
            if (_viewport.MaterialCatalog.GetMaterial(_currentHash) != null) LoadMaterial(_currentHash);
            else ClearMaterialPanel(); // deleted (or its create was undone)
        }
        UpdateAssignPanel();
    }

    // ── Texture slot / parameter commits (called from the rows' Text setters) ──

    private void CommitSlotText(SlotEditRow row, string value)
    {
        if (_currentHash == 0) return;
        if (!_viewport.SetMaterialTexture(_currentHash, row.SlotId, value.Trim()))
            LoadMaterial(_currentHash); // unknown slot (stale row) — revert the field
        // success: MaterialsChanged already reloaded the panel
    }

    private void CommitParamText(ParamEditRow row, string value)
    {
        if (_currentHash == 0) return;
        string[] pieces = value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var values = new List<float>(pieces.Length);
        foreach (string piece in pieces)
        {
            if (!float.TryParse(piece, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                LoadMaterial(_currentHash); // unparsable — revert the field
                return;
            }
            values.Add(f);
        }
        bool ok = row.IsNew
            ? values.Count > 0 && _viewport.AddMaterialParameter(_currentHash, row.ParamId, values)
            : _viewport.SetMaterialParameter(_currentHash, row.ParamId, values);
        if (!ok) LoadMaterial(_currentHash); // count mismatch (the payload length is fixed) — revert
    }

    private void CommitRename(NameEditRow row, string value)
    {
        if (_currentHash == 0) return;
        ulong shown = _currentHash;
        string name = value.Trim();
        if (name.Length == 0 || name == _viewport.MaterialCatalog.GetMaterial(shown)?.Name)
        {
            LoadMaterial(shown); // empty or unchanged — revert the field
            return;
        }
        ulong? renamed = _viewport.RenameMaterial(shown, name);
        if (renamed == null)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Rename material",
                Text = $"\"{name}\" already exists in the loaded libraries (or the name is invalid).",
                Icon = DialogIcon.Warning,
            });
            LoadMaterial(shown); // revert the field
            return;
        }
        // MaterialsChanged already rebuilt the list (and cleared the panel — the old hash is gone);
        // follow the material to its new hash identity.
        SelectInList(renamed.Value, scroll: true);
        LoadMaterial(renamed.Value);
        UpdateAssignPanel();
    }

    // ── Slot add / remove ──

    // Opening the Add popup: no selection to land on — clear it so any row registers as a fresh pick.
    private void AddSlotBtn_Checked(object sender, RoutedEventArgs e) => AddSlotList.SelectedItem = null;

    private void AddSlotList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AddSlotList.SelectedItem is not SlotDescriptor slot) return; // includes the programmatic clears
        AddSlotBtn.IsChecked = false; // close the popup before the panel reloads under it
        if (_currentHash != 0) _viewport.AddMaterialTextureSlot(_currentHash, slot.Id); // MaterialsChanged reloads
    }

    private void AddSlotPopup_Closed(object sender, EventArgs e) => AddSlotBtn.IsChecked = false;

    private void RemoveSlot_Click(object sender, RoutedEventArgs e)
    {
        if (_currentHash == 0 || (sender as FrameworkElement)?.DataContext is not SlotEditRow row) return;
        _viewport.RemoveMaterialTextureSlot(_currentHash, row.SlotId);
    }

    // ── Create / delete / assign ──

    private void NewNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        New_Click(sender, e);
        e.Handled = true;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        string name = NewNameBox.Text.Trim();
        if (name.Length == 0 || LibraryCombo.SelectedItem is not string library) return;
        ulong? hash = _viewport.CreateMaterial(library, name);
        if (hash == null)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "New material",
                Text = $"\"{name}\" already exists in the loaded libraries (or the name is invalid).",
                Icon = DialogIcon.Warning,
            });
            return;
        }
        NewNameBox.Text = "";
        SelectInList(hash.Value, scroll: true); // the list itself was rebuilt by MaterialsChanged
        LoadMaterial(hash.Value);
        UpdateAssignPanel();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentHash == 0) return;
        MaterialInfo? info = _viewport.MaterialCatalog.GetMaterial(_currentHash);
        if (info == null) return;
        int uses = _viewport.CountLoadedMaterialUses(_currentHash);
        DialogOutcome outcome = AppDialog.Show(this, new DialogOptions
        {
            Title = "Delete material",
            Heading = string.IsNullOrEmpty(info.Name) ? "(unnamed material)" : info.Name,
            Text = uses > 0
                ? $"This material is used by {uses} loaded mesh part(s). Those meshes keep the hash and will " +
                  "render with placeholder textures. Deletion is undoable until the next save."
                : "No loaded mesh uses this material. Deletion is undoable until the next save.",
            Icon = DialogIcon.Warning,
            Buttons = DialogButtons.YesCancel,
            ConfirmText = "Delete",
        });
        if (outcome.Confirmed) _viewport.DeleteMaterial(_currentHash); // MaterialsChanged clears the panel
    }

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (_contextNode == null || _currentHash == 0) return;
        if (!_viewport.AssignSlotMaterial(_contextNode, _contextSlot, _currentHash))
        {
            // Backstop for a stale target the SceneChanged sweep has not caught yet (or a slot that
            // vanished from the mesh): say so instead of silently doing nothing.
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Assign material",
                Icon = DialogIcon.Warning,
                Text = "The mesh this editor was opened from is no longer in the scene (the area was "
                     + "reloaded). Reopen the editor from the mesh's material tile and assign again.",
            });
            _contextNode = null;
        }
        UpdateAssignPanel();
    }

    private void UpdateAssignPanel()
    {
        if (_contextNode == null)
        {
            AssignBtn.Visibility = Visibility.Collapsed;
            return;
        }
        AssignBtn.Visibility = Visibility.Visible;
        AssignBtn.IsEnabled = _currentHash != 0;
        string current = "—";
        if (_contextNode.Source is IMaterialSlotEditor editor && editor.GetSlotMaterial(_contextSlot) is { } bound)
            current = _viewport.MaterialCatalog.GetMaterial(bound)?.Name ?? ("0x" + bound.ToString("X"));
        AssignBtn.ToolTip =
            $"Assign the selected material to slot {_contextSlot + 1} of \"{_contextNode.Name}\" (currently {current}).";
    }

    // ── Save ──

    private void CommitAndSave()
    {
        MaterialList.Focus(); // steal focus so an in-edit field commits via LostFocus first
        if (!_viewport.HasUnsavedEdits) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            _viewport.SaveEdits();
        }
        catch (Exception ex)
        {
            AppDialog.Show(this, new DialogOptions
            {
                Title = "Save",
                Text = ex.Message,
                Icon = DialogIcon.Error,
            });
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}

/// <summary>The name/hash identity rows above the preview: the editable name commits through the two-way
/// <see cref="Text"/> binding (Enter / focus loss / paste) and renames the material; the hash is the
/// FNV64 of the name — read-only, re-derived by the rename itself.</summary>
public sealed class NameEditRow
{
    private readonly Action<NameEditRow, string> _commit;
    private string _text;

    public NameEditRow(string name, ulong hash, Action<NameEditRow, string> commit)
    {
        _text = name;
        HashHex = "0x" + hash.ToString("X");
        _commit = commit;
    }

    public string HashHex { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (value == _text) return; // binding re-pushes on every focus loss — only real changes commit
            _text = value;
            _commit(this, value);
        }
    }
}

/// <summary>One editable texture-slot row: the flat texture thumbnail beside the project-style copyable
/// field, which commits through the two-way <see cref="Text"/> binding (Enter / focus loss / paste) and
/// forwards to the window's commit.</summary>
public sealed class SlotEditRow
{
    private readonly Action<SlotEditRow, string> _commit;
    private string _text;

    public SlotEditRow(Domain.Materials.MaterialSlotInfo slot, bool resolves,
        System.Windows.Media.ImageSource? thumbnail, Action<SlotEditRow, string> commit)
    {
        SlotId = slot.SlotId;
        Label = slot.FriendlyName == slot.SlotId ? slot.SlotId : $"{slot.FriendlyName} · {slot.SlotId}";
        Resolves = resolves;
        Thumbnail = thumbnail;
        _text = slot.TextureName ?? "";
        _commit = commit;
        ResolveNote = _text.Length == 0 ? "No texture bound"
            : resolves ? _text
            : _text + " — .dds not found in the loaded scopes";
    }

    public string SlotId { get; }
    public string Label { get; }
    public bool Resolves { get; }
    public System.Windows.Media.ImageSource? Thumbnail { get; }
    public string ResolveNote { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (value == _text) return; // binding re-pushes on every focus loss — only real changes commit
            _text = value;
            _commit(this, value);
        }
    }
}

/// <summary>One editable shader-parameter row: floats as a comma-separated string; the payload length is
/// fixed by the format, so a wrong count/parse reverts the field. A row for a code the material does not
/// carry yet (<see cref="IsNew"/>) starts empty — committing floats adds the parameter.</summary>
public sealed class ParamEditRow
{
    private readonly Action<ParamEditRow, string> _commit;
    private string _text;

    public ParamEditRow(Domain.Materials.MaterialParamInfo p, Action<ParamEditRow, string> commit)
    {
        ParamId = p.ParamId;
        Label = p.FriendlyName == p.ParamId ? p.ParamId : $"{p.FriendlyName} · {p.ParamId}";
        _text = string.Join(", ", p.Values.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)));
        _commit = commit;
    }

    public ParamEditRow(Domain.Materials.ParamDescriptor d, Action<ParamEditRow, string> commit)
    {
        ParamId = d.Id;
        Label = d.Display == d.Id ? d.Id : $"{d.Display} · {d.Id}";
        IsNew = true;
        Hint = d.Length is int n
            ? $"Not on this material yet — enter {n} float(s) to add it"
            : "Not on this material yet — enter floats to add it";
        _text = "";
        _commit = commit;
    }

    public string ParamId { get; }
    public string Label { get; }
    public bool IsNew { get; }
    public string? Hint { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (value == _text) return;
            _text = value;
            _commit(this, value);
        }
    }
}
