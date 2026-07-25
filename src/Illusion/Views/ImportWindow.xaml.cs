using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Illusion.Assets.Import;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Scene;
using Illusion.Viewport;
using Microsoft.Win32;

namespace Illusion.Views;

/// <summary>
/// File → Import…: reads a glTF file (.glb/.gltf) and lands every mesh it carries in one undoable step —
/// meshes named <c>COL_*</c> cook into collision hulls of the target archive's .col, everything else
/// becomes render meshes of the target document. The target is one of the SDS archives loaded in the
/// scene; everything spawns in front of the camera at the file's own scale. Material names bind to game
/// materials by name; missing ones are created on import (default diffuse preset, textures stay with the
/// modder) when the checkbox allows it. Success reports through the viewport's transient notice, refusals
/// through <see cref="AppDialog"/>.
/// </summary>
public partial class ImportWindow : Window
{
    private readonly D3DImageHost _viewport;
    private List<ImportItem> _items = new();

    private sealed record PreviewRow(string Name, string Type, string Materials, string Note);

    // One loaded SDS archive the import can land in: the archive file name over the document node the
    // pipeline anchors to (the node's own Name is the literal "FrameResource" — useless as a label).
    private sealed record TargetOption(string Display, SceneNode Node);

    public ImportWindow(D3DImageHost viewport)
    {
        _viewport = viewport;
        InitializeComponent();
        var targets = _viewport.FrameDocumentNodes()
            .Select(n => new TargetOption(
                (n.Source as ISceneDocument)?.SourceArchive.Name ?? n.Parent?.Name ?? n.Name, n))
            .ToList();
        TargetBox.ItemsSource = targets;
        TargetBox.SelectedItem = targets.FirstOrDefault();
    }

    // ── Field plumbing ──

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import model",
            Filter = "glTF (*.glb;*.gltf)|*.glb;*.gltf|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        FileBox.Text = dialog.FileName;
        LoadPreview();
    }

    private void Target_Changed(object sender, RoutedEventArgs e) => RefreshPreviewRows();

    private void Field_Changed(object sender, RoutedEventArgs e) => RefreshPreviewRows();

    private void LoadPreview()
    {
        _items = new List<ImportItem>();
        List<GltfMeshInstance>? meshes = GltfFile.TryLoad(FileBox.Text, out string? error);
        if (meshes == null)
        {
            PreviewList.ItemsSource = null;
            ImportBtn.IsEnabled = false;
            Fail(error ?? "the file could not be read");
            return;
        }
        _items = ModelImport.Plan(meshes);
        RefreshPreviewRows();
    }

    // Re-renders the preview rows for the current target/checkbox state and gates the Import button.
    private void RefreshPreviewRows()
    {
        if (_items.Count == 0) return;
        bool createMissing = CreateMaterialsCheck.IsChecked == true;
        bool targetHasCol = TargetBox.SelectedItem is TargetOption target && _viewport.HasCollisionLayer(target.Node);

        var rows = new List<PreviewRow>(_items.Count);
        int importable = 0;
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImportItem item in _items)
        {
            foreach (MaterialResolution m in item.Materials)
                if (m.State == MaterialState.Missing) missing.Add(m.Name);

            string note = Note(item, createMissing, targetHasCol, out bool ok);
            if (ok) importable++;
            rows.Add(new PreviewRow(
                item.Name,
                item.Kind == ImportKind.CollisionHull ? "Collision" : "Mesh",
                string.Join(", ", item.Materials.Select(Describe)),
                note));
        }

        PreviewList.ItemsSource = rows;
        if (missing.Count > 0)
        {
            CreateMaterialsCheck.Content = missing.Count == 1
                ? "Create 1 missing game material (in default.mtl, backup kept; textures are up to you)"
                : $"Create {missing.Count} missing game materials (in default.mtl, backup kept; textures are up to you)";
            CreateMaterialsCheck.Visibility = Visibility.Visible;
        }
        else
        {
            CreateMaterialsCheck.Visibility = Visibility.Collapsed;
        }
        ImportBtn.IsEnabled = importable > 0 && TargetBox.SelectedItem is TargetOption;
    }

    private static string Describe(MaterialResolution m) => m.State switch
    {
        MaterialState.Missing => m.Name + " (new)",
        MaterialState.UnknownSurface => m.Name + " (?)",
        _ => m.Name,
    };

    // One line saying whether (and why not) this row will import under the current dialog state.
    private static string Note(ImportItem item, bool createMissing, bool targetHasCol, out bool ok)
    {
        ok = false;
        if (item.Refusal != null) return item.Refusal;
        if (item.Kind == ImportKind.CollisionHull && !targetHasCol) return "target has no .col layer";
        if (item.HasMissingMaterials && !createMissing) return "needs the missing materials created";
        ok = true;
        return "";
    }

    // ── Import ──

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (TargetBox.SelectedItem is not TargetOption target) return;

        // 1) Materials first: created (and persisted, with a backup) before any payload is validated —
        // the creation pipeline verifies every hash against the loaded libraries.
        bool createMissing = CreateMaterialsCheck.IsChecked == true && CreateMaterialsCheck.IsVisible;
        int createdMaterials = 0;
        if (createMissing)
        {
            var names = _items.Where(i => i.Refusal == null)
                .SelectMany(i => i.Materials)
                .Where(m => m.State == MaterialState.Missing)
                .Select(m => m.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (names.Count > 0)
            {
                string? error = GameMaterialCreator.CreateMissing(names, out createdMaterials, out _);
                if (error != null)
                {
                    Fail(error);
                    return;
                }
            }
        }

        // 2) Payloads for every importable item under the final state.
        bool targetHasCol = _viewport.HasCollisionLayer(target.Node);
        var eligible = _items.Where(i =>
            i.Refusal == null
            && (i.Kind != ImportKind.CollisionHull || targetHasCol)
            && (!i.HasMissingMaterials || createMissing)).ToList();
        if (eligible.Count == 0)
        {
            Fail("Nothing in the file can be imported — see the Note column.");
            return;
        }

        // The whole file lands in front of the camera: its centroid moves to the drop point, the items
        // keep their relative offsets and the file's own scale.
        Vector3 offset = _viewport.ImportDropPoint() - ModelImport.PlacementCenter(eligible, 1f);
        var options = new ModelImport.Options(1f, offset);

        var meshes = new List<MeshObjectPayload>();
        var hulls = new List<CollisionObjectPayload>();
        foreach (ImportItem item in eligible)
        {
            if (item.Kind == ImportKind.CollisionHull) hulls.Add(ModelImport.ToCollisionPayload(item, options));
            else meshes.Add(ModelImport.ToMeshPayload(item, options));
        }

        D3DImageHost.ImportReport report;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait; // hull cooking runs a subprocess (seconds)
            report = _viewport.ImportBatch(target.Node, meshes, hulls);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (report.MeshesApplied == 0 && report.HullsApplied == 0)
        {
            Fail(report.Skipped.Count > 0 ? string.Join("\n", report.Skipped.Take(6)) : "Nothing was imported.");
            return;
        }

        var summary = new List<string>();
        if (report.MeshesApplied > 0) summary.Add($"{report.MeshesApplied} mesh(es)");
        if (report.HullsApplied > 0) summary.Add($"{report.HullsApplied} collision hull(s)");
        string message = "Imported " + string.Join(" and ", summary);
        if (createdMaterials > 0) message += $"; created {createdMaterials} material(s) in default.mtl";
        if (report.Skipped.Count > 0) message += $"; {report.Skipped.Count} skipped";
        _viewport.RaiseNotice(message + ". Ctrl+Z removes the imported objects.");
        DialogResult = true;
        Close();
    }

    private void Fail(string message) =>
        AppDialog.Show(this, new DialogOptions
        {
            Title = "Import",
            Icon = DialogIcon.Error,
            Heading = "Import failed",
            Text = message,
        });

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
