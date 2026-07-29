using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Illusion.Updates;

namespace Illusion.Views;

/// <summary>
/// The updater's one window. This is the DOWNLOADED build, started out of its staging folder with
/// <see cref="UpdateInstaller.ApplySwitch"/> by the copy that is exiting: it waits for that process, replaces
/// the files in the install folder and starts the toolkit again, then closes — which ends the process, since
/// nothing else is open.
/// <para>
/// A failure here is the one place the update can leave a half-written install folder behind, so it is never
/// swallowed: the window stays, says what went wrong, and offers the staged files so the copy can be finished
/// by hand.
/// </para>
/// </summary>
public partial class ApplyUpdateWindow : Window
{
    private readonly ApplyRequest _request;

    internal ApplyUpdateWindow(ApplyRequest request)
    {
        InitializeComponent();
        _request = request;
        Loaded += async (_, _) => await ApplyAsync();
    }

    private async Task ApplyAsync()
    {
        // Progress<T> was created on this thread, so its reports land back here while the work runs off it.
        var report = new Progress<string>(text => StatusText.Text = text);

        string? error = null;
        try
        {
            await Task.Run(() => UpdateInstaller.Apply(_request, ((IProgress<string>)report).Report));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException
                                       or InvalidOperationException or Win32Exception)
        {
            error = ex.Message;
        }

        if (error is null)
        {
            Application.Current.Shutdown();
            return;
        }

        HeadingText.Text = "The update could not be installed";
        StatusText.Text = error + "\n\nThe files that were downloaded are still here, so the folder can be " +
                          "copied over " + _request.TargetDirectory + " by hand.";
        Bar.Visibility = Visibility.Collapsed;
        Actions.Visibility = Visibility.Visible;
    }

    // The staged payload IS this process's own folder — the files to copy from.
    private void ShowFiles_Click(object sender, RoutedEventArgs e)
    {
        var start = new ProcessStartInfo("explorer.exe");
        start.ArgumentList.Add(AppVersion.InstallDirectory);
        try { Process.Start(start); }
        catch (Win32Exception) { /* no shell to open it with; the path is on screen either way */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
