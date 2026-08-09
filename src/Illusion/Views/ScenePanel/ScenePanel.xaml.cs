using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Adapters;
using Illusion.Scene;
using Illusion.ViewModels;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The right-hand half of an editor window: what is loaded (stats), how to find something in it (search), what
/// it is made of (the scene tree) and everything about the selected object (the property tabs). It knows
/// nothing about WHERE the content came from — a city district or one archive off the library shelf — so the
/// map editor and the resource editor host the same control and hand it their own viewport through
/// <see cref="Attach"/>.
/// <para>
/// The two halves are controls of their own — <see cref="SceneTreeView"/> and <see cref="ScenePropertyTabs"/>
/// — and what is left here is the panel's own furniture (the stats strip, the search box) plus the wiring
/// between the viewport, the halves and the host.
/// </para>
/// <para>
/// Two things it deliberately does NOT do, because they belong to a window rather than to a panel: opening the
/// material editor and rolling an archive back to a backup. Both are raised as requests
/// (<see cref="MaterialEditorRequested"/>, <see cref="RestoreBackupRequested"/>) for the host to answer.
/// </para>
/// </summary>
public partial class ScenePanel : UserControl
{
    /// <summary>Height of the host window's toolbar band, so the panel's stats strip lines up with it.</summary>
    public static readonly DependencyProperty HeaderHeightProperty = DependencyProperty.Register(
        nameof(HeaderHeight), typeof(double), typeof(ScenePanel), new PropertyMetadata(double.NaN));

    private D3DImageHost _viewport = null!;
    private SelectionViewModel _selection = null!;

    public ScenePanel() => InitializeComponent();

    /// <summary>A material tile was clicked — the host opens (or re-focuses) the material editor on it.</summary>
    public event Action<MaterialViewModel>? MaterialEditorRequested;

    /// <summary>The tree menu's rollback item was picked, scoped to the clicked row's archive (null = let the
    /// host ask which one).</summary>
    public event Action<FileInfo?>? RestoreBackupRequested;

    /// <summary>Raised after the selection changed and the panel has re-pointed itself at it — the host uses
    /// it for the chrome that lives outside this panel (the transform overlay, the Blender button).</summary>
    public event Action? SelectionShown;

    public double HeaderHeight
    {
        get => (double)GetValue(HeaderHeightProperty);
        set => SetValue(HeaderHeightProperty, value);
    }

    /// <summary>The view-model behind the property tabs. The host binds its own transform overlay to it.</summary>
    public SelectionViewModel Selection => _selection;

    /// <summary>
    /// Points the panel at the viewport whose content it reports on. Called once, by the host window's
    /// constructor — the panel is useless before it and every handler below assumes it has happened.
    /// </summary>
    public void Attach(D3DImageHost viewport)
    {
        _viewport = viewport;
        _selection = new SelectionViewModel(viewport);

        Tree.Attach(viewport);
        Tree.RestoreBackupRequested += sds => RestoreBackupRequested?.Invoke(sds);

        Tabs.Attach(viewport, _selection);
        Tabs.MaterialEditorRequested += vm => MaterialEditorRequested?.Invoke(vm);

        // A prefab pick writes the working copy the moment it is made, so the three things that follow are
        // the host's: it goes on the undo stack, the archive joins the build list, and the user is told.
        _selection.PrefabEdited += (archive, message, edit) =>
        {
            viewport.History.Push(edit);
            viewport.MarkArchiveModified(archive);
            viewport.RaiseNotice(message + " Build to write it into the archive.", isError: false);
        };

        // A tuning edit is the same deal: the number is already in the working copy, so the host records it,
        // adds the archive to the build list and says so.
        _selection.TuningEdited += (archive, message, edit) =>
        {
            viewport.History.Push(edit);
            viewport.MarkArchiveModified(archive);
            viewport.RaiseNotice(message + " Build to write it into the archive.", isError: false);
        };

        // And an effect edit — a birth rate, a colour key, a whole effect copied.
        _selection.EffectEdited += (archive, message, edit) =>
        {
            viewport.History.Push(edit);
            viewport.MarkArchiveModified(archive);
            viewport.RaiseNotice(message + " Build to write it into the archive.", isError: false);
        };

        viewport.SceneChanged += () => Dispatcher.Invoke(() =>
        {
            UpdateSceneStats();
            Tree.Refresh();
            // The Prefab tab describes the ARCHIVE, not the selection, so it has to follow what is staged —
            // otherwise it only appears once something has been clicked, and a car that has just opened
            // shows nothing at all.
            _selection.RefreshPrefab();
            _selection.RefreshTuning();
            _selection.RefreshEffects();
        });

        viewport.SelectionChanged += OnSelectionChanged;
        viewport.SelectionTransformChanged += _selection.RefreshTransform;
        viewport.SelectionPropertiesChanged += _selection.RefreshPropertyValues;

        // Any material edit — from either window, or an undo — rebuilds the tab's tiles in place.
        viewport.MaterialsChanged += _selection.RefreshMaterials;
    }

    /// <inheritdoc cref="SceneTreeView.ShowNothing"/>
    public void ShowNothing(string title, string hint, bool always = false) =>
        Tree.ShowNothing(title, hint, always);

    /// <inheritdoc cref="ScenePropertyTabs.ShowPrefab"/>
    public void ShowPrefab() => Tabs.ShowPrefab();

    /// <inheritdoc cref="ScenePropertyTabs.ShowTuning"/>
    public void ShowTuning() => Tabs.ShowTuning();

    /// <inheritdoc cref="RenderTabView.HideCityFilters"/>
    public void HideCityFilters() => Tabs.RenderFilters.HideCityFilters();

    /// <inheritdoc cref="RenderTabView.SetSnowFilter"/>
    public void SetSnowFilter(bool on) => Tabs.RenderFilters.SetSnowFilter(on);

    // ── Selection → property tabs ──

    // Selection changed (tree click or viewport pick): feed the property tabs and surface the type's tab.
    private void OnSelectionChanged()
    {
        _selection.SetNode(_viewport.SelectedNode);
        Tabs.SurfaceTabFor(_selection);

        // Selecting a mesh or an actor (viewport ray-pick or tree click) scrolls the hierarchy to its row —
        // an actor node carries no mesh, and without this a viewport pick left the tree where it was.
        if (_viewport.SelectedNode is { } picked && (picked.Mesh != null || picked.Source is ActorNodeAdapter))
        {
            Tree.BringNodeIntoView(picked);
        }

        SelectionShown?.Invoke();
    }

    // ── Search · stats ──

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility =
            string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        Tree.ApplySearch(SearchBox.Text ?? "");
    }

    // Scene panel header: loaded SDS files · meshes · polygons. Files = SDS nodes across all folders.
    private void UpdateSceneStats()
    {
        int files = 0;
        foreach (SceneNode f in _viewport.Roots) files += f.Children.Count;
        StatFiles.Text = files.ToString("N0", CultureInfo.InvariantCulture);
        StatMeshes.Text = _viewport.MeshCount.ToString("N0", CultureInfo.InvariantCulture);
        StatPolys.Text = FormatCompact(_viewport.TriangleCount);
    }

    // Compact number for the narrow stats cell: 1.2M / 45.6K / 8,900.
    internal static string FormatCompact(long n) =>
        n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M"
        : n >= 10_000 ? (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K"
        : n.ToString("N0", CultureInfo.InvariantCulture);
}
