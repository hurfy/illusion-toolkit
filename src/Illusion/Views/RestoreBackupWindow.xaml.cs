using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// File → Restore Backup… (and the tree/viewport context menus): pick one loaded SDS archive and one of
/// its versioned backups (written by every Build). Confirming hands the pair back through
/// <see cref="SelectedArchive"/>/<see cref="SelectedBackup"/> — MainWindow performs the actual restore
/// (scene stop → extracted-mirror delete → atomic archive swap → reload), keeping the destructive step
/// in one place. The dialog itself only browses and confirms.
/// </summary>
public partial class RestoreBackupWindow : Window
{
    // One loaded SDS archive the restore can target: its short path label over the archive file.
    private sealed record ArchiveOption(string Display, FileInfo Sds);

    // One row of the backup list: the parsed build stamp, the file size and the literal file name.
    private sealed record BackupRow(SdsWriter.BackupInfo Info)
    {
        public string StampText => Info.Stamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        public string SizeText => Info.File.Length >= 1 << 20
            ? $"{Info.File.Length / (1024.0 * 1024.0):0.0} MB"
            : $"{Info.File.Length / 1024.0:0.0} KB";
        public string FileName => Info.File.Name;
    }

    /// <summary>The archive the user confirmed a restore for; null until confirmed.</summary>
    public FileInfo? SelectedArchive { get; private set; }

    /// <summary>The backup version the user confirmed; null until confirmed.</summary>
    public FileInfo? SelectedBackup { get; private set; }

    public RestoreBackupWindow(D3DImageHost viewport, FileInfo? preselect)
    {
        InitializeComponent();

        // One option per distinct archive — several documents (frame + collision layers) share one .sds.
        var archives = viewport.FrameDocumentNodes()
            .Select(n => (n.Source as ISceneDocument)?.SourceArchive)
            .OfType<FileInfo>()
            .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ArchiveOption(MainWindow.DescribeArchive(f), f))
            .ToList();
        ArchiveBox.ItemsSource = archives;
        ArchiveBox.SelectedItem = (preselect == null
                ? null
                : archives.FirstOrDefault(a =>
                    string.Equals(a.Sds.FullName, preselect.FullName, StringComparison.OrdinalIgnoreCase)))
            ?? archives.FirstOrDefault();
        RefreshBackups(); // the programmatic select above also fires Archive_Changed — refreshing twice is harmless
    }

    private void Archive_Changed(object sender, RoutedEventArgs e) => RefreshBackups();

    private void RefreshBackups()
    {
        List<BackupRow> rows = ArchiveBox.SelectedItem is ArchiveOption a
            ? SdsWriter.ListBackups(a.Sds).Select(b => new BackupRow(b)).ToList()
            : new List<BackupRow>();
        BackupList.ItemsSource = rows;
        BackupList.SelectedItem = rows.FirstOrDefault(); // newest preselected — "undo the last build" is the common case
        EmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreBtn.IsEnabled = rows.Count > 0;
    }

    private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RestoreBtn.IsEnabled = BackupList.SelectedItem is BackupRow;

    // The point of no return is NOT here: this only records the confirmed pair and closes. MainWindow
    // stops the scene and swaps the files, so the dialog is gone before the world changes under it.
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (ArchiveBox.SelectedItem is not ArchiveOption a || BackupList.SelectedItem is not BackupRow b) return;

        DialogOutcome ok = AppDialog.Show(this, new DialogOptions
        {
            Title = "Restore Backup",
            Icon = DialogIcon.Warning,
            Buttons = DialogButtons.YesCancel,
            ConfirmText = "Restore",
            Heading = $"Restore {a.Sds.Name} from {b.StampText}?",
            Text = "The archive in the game folder and its extracted files are replaced with the backup, "
                 + "and the scene reloads.\n\nUnsaved edits are discarded. All backups are kept. "
                 + "Material libraries (.mtl) are not affected.",
        });
        if (!ok.Confirmed) return;

        SelectedArchive = a.Sds;
        SelectedBackup = b.Info.File;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
