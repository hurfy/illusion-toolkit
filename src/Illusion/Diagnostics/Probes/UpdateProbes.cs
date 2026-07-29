using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Updates;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The update chain, end to end but offline: version ordering, what is read out of a GitHub release, the
/// checksum file, unpacking an archive into a staging folder, the file swap that follows, and the frozen
/// command line the two builds talk over. Needs no game data, no GPU and — apart from the opt-in live check —
/// no network: everything runs against fixtures and temporary folders, which is what makes it a gate rather
/// than a weather report.
/// <para>
/// The one thing it cannot exercise is the swap actually happening, since that ends with this process being
/// waited on and replaced. What it does instead is pin every piece the swap is made of, the argument shape
/// included: those arguments are WRITTEN by the old build and READ by the new one, so a rename here would only
/// ever be discovered by a user whose update silently did nothing.
/// </para>
/// Output: %TEMP%\illusion_update.txt
/// </summary>
internal static class UpdateProbes
{
    private delegate void Assert(string name, bool ok, string detail = "");

    private const string ArchiveName = "Illusion-Toolkit-9.9.9-win-x64.zip";

    // A release the way the API hands one over, cut down to the fields that are read. Two assets, because the
    // checksum has to be picked out from beside the archive rather than assumed to be second.
    private const string LatestFixture = """
        {
          "tag_name": "v9.9.9",
          "name": "Illusion Toolkit 9.9.9",
          "draft": false,
          "prerelease": false,
          "html_url": "https://github.com/hurfy/illusion-toolkit/releases/tag/v9.9.9",
          "assets": [
            { "name": "Illusion-Toolkit-9.9.9-win-x64.zip.sha256", "size": 82,
              "browser_download_url": "https://example.invalid/sums" },
            { "name": "Illusion-Toolkit-9.9.9-win-x64.zip", "size": 7506870,
              "browser_download_url": "https://example.invalid/archive" }
          ]
        }
        """;

    // Published, but with nothing on it a Windows machine could install.
    private const string NotesOnlyFixture = """
        {
          "tag_name": "v9.9.9",
          "assets": [
            { "name": "notes.txt", "size": 40, "browser_download_url": "https://example.invalid/notes" }
          ]
        }
        """;

    // An asset name that is a path rather than a file name. GitHub does not hand one of these out; the point
    // is that the archive's name is joined onto the staging folder, so it has to be checked, not trusted.
    private const string EscapingAssetFixture = """
        {
          "tag_name": "v9.9.9",
          "assets": [
            { "name": "../../win-x64.zip", "size": 10,
              "browser_download_url": "https://example.invalid/escape" }
          ]
        }
        """;

    internal static void RunUpdateProbe(bool live)
    {
        string outTxt = Path.Combine(Path.GetTempPath(), "illusion_update.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string scratch = Path.Combine(Path.GetTempPath(), "illusion_update_probe");
        try
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            Directory.CreateDirectory(scratch);

            sb.AppendLine($"running {AppVersion.Current} from {AppVersion.InstallDirectory}");
            sb.AppendLine($"development build: {AppVersion.IsDevelopmentBuild}, staging: {UpdateDownloader.StagingRoot}");
            sb.AppendLine();

            CheckVersions(Check);
            CheckThisBuild(Check);
            CheckRelease(Check);
            CheckChecksums(Check);
            CheckStaging(Check, scratch);
            CheckSwap(Check, scratch);
            CheckApplyContract(Check);
            CheckWindows(Check, sb);
            if (live) CheckLive(Check, sb);

            sb.Insert(0, $"UPDATE PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch { }
            File.WriteAllText(outTxt, sb.ToString());
        }
    }

    // ── Reading and ordering versions ──

    private static void CheckVersions(Assert check)
    {
        (string Text, string Expected)[] readable =
        {
            ("v0.3.1", "0.3.1"),                    // a tag
            ("0.3.1", "0.3.1"),                     // the same without the v
            ("0.3.1+6b06d4b", "0.3.1"),             // an informational version: build metadata is not order
            ("0.3.0.0", "0.3.0"),                   // an assembly version: the fourth part is not order either
            ("0.4", "0.4.0"),
            (" 1.2.3 ", "1.2.3"),
            ("1.0.0-rc.1", "1.0.0-rc.1"),
        };
        foreach ((string text, string expected) in readable)
        {
            bool ok = UpdateVersion.TryParse(text, out UpdateVersion version) && version.ToString() == expected;
            check($"reads '{text}' as {expected}", ok, ok ? "" : "got " + Describe(text));
        }

        check("reads a hyphenated pre-release", UpdateVersion.TryParse("1.0.0-beta-2", out UpdateVersion beta)
            && beta.ToString() == "1.0.0-beta-2");

        // The last four are the ones that matter beyond tidiness: a version names a folder under the staging
        // root, so a suffix that walks out of it would put a download somewhere else entirely.
        foreach (string garbage in new[]
                 {
                     "", "  ", "v", "next", "1.2.3.4.5", "0..1", "1.2.-3", "1.2.3-",
                     @"1.2.3-..\..\x", "1.2.3-../../x", "1.2.3-..", "1.2.3-a/b",
                 })
        {
            check($"refuses '{garbage}'", !UpdateVersion.TryParse(garbage, out _));
        }

        (string Newer, string Older)[] ordered =
        {
            ("0.3.2", "0.3.1"),
            ("0.4.0", "0.3.99"),
            ("1.0.0", "0.99.99"),
            ("1.0.0", "1.0.0-rc.1"),     // a release supersedes its own pre-releases
            ("1.0.0-rc.2", "1.0.0-rc.1"),
        };
        foreach ((string newer, string older) in ordered)
        {
            UpdateVersion.TryParse(newer, out UpdateVersion a);
            UpdateVersion.TryParse(older, out UpdateVersion b);
            check($"{newer} supersedes {older}", a.IsNewerThan(b) && !b.IsNewerThan(a));
        }

        UpdateVersion.TryParse("0.3.1", out UpdateVersion same);
        UpdateVersion.TryParse("0.3.1+deadbeef", out UpdateVersion rebuilt);
        check("the same version rebuilt is not an update", !rebuilt.IsNewerThan(same) && !same.IsNewerThan(rebuilt));
    }

    private static string Describe(string text) =>
        UpdateVersion.TryParse(text, out UpdateVersion v) ? v.ToString() : "refused";

    private static void CheckThisBuild(Assert check)
    {
        check("this build knows its own version", !AppVersion.Current.IsEmpty, AppVersion.Current.ToString());
        check("this build knows where it runs from",
            Directory.Exists(AppVersion.InstallDirectory), AppVersion.InstallDirectory);
        check("the executable it would relaunch is beside it",
            File.Exists(Path.Combine(AppVersion.InstallDirectory, AppVersion.ExecutableName)));

        // The refusal that keeps a working tree from being overwritten by a download.
        check("a build folder reads as a development build",
            AppVersion.LooksLikeBuildOutput(@"F:\Code\illusion\illusion-toolkit\src\Illusion\bin\Debug\net10.0-windows")
            && AppVersion.LooksLikeBuildOutput(@"F:\x\src\App\bin\Release\net10.0-windows")
            && AppVersion.LooksLikeBuildOutput(@"F:\x\bin\Debug"));
        check("an unpacked release does not",
            !AppVersion.LooksLikeBuildOutput(@"C:\Tools\Illusion-Toolkit-0.3.1-win-x64")
            && !AppVersion.LooksLikeBuildOutput(@"C:\Program Files\Illusion")
            && !AppVersion.LooksLikeBuildOutput(@"C:\bindings\Debugger"));
    }

    // ── What is read out of a release ──

    private static void CheckRelease(Assert check)
    {
        bool parsed = ReleaseInfo.TryParse(LatestFixture, out ReleaseInfo? release, out string error);
        check("a published release is read", parsed && release is not null, error);
        if (release is not null)
        {
            check("its version comes from the tag", release.Version.ToString() == "9.9.9", release.Tag);
            check("the win-x64 archive is the one picked", release.AssetName == ArchiveName, release.AssetName);
            check("the archive's address is kept", release.AssetUrl == "https://example.invalid/archive");
            check("its size is read", release.AssetSize == 7506870 && release.AssetSizeText == "7.2 MB",
                release.AssetSizeText);
            check("the checksum beside it is found", release.ChecksumUrl == "https://example.invalid/sums",
                release.ChecksumName ?? "none");
            check("the release page is kept", release.PageUrl.EndsWith("/tag/v9.9.9", StringComparison.Ordinal));
        }

        check("a draft is not an update",
            !ReleaseInfo.TryParse(LatestFixture.Replace("\"draft\": false", "\"draft\": true", StringComparison.Ordinal),
                out _, out _));
        check("a pre-release is not an update",
            !ReleaseInfo.TryParse(
                LatestFixture.Replace("\"prerelease\": false", "\"prerelease\": true", StringComparison.Ordinal),
                out _, out _));

        check("a release with nothing attached is refused",
            !ReleaseInfo.TryParse("""{ "tag_name": "v9.9.9", "assets": [] }""", out _, out string empty)
            && empty.Length > 0, empty);
        check("a release whose only asset is not the archive is refused",
            !ReleaseInfo.TryParse(NotesOnlyFixture, out _, out _));
        check("an unversioned tag is refused",
            !ReleaseInfo.TryParse("""{ "tag_name": "nightly", "assets": [] }""", out _, out _));
        check("an archive whose name is a path is refused",
            !ReleaseInfo.TryParse(EscapingAssetFixture, out _, out string escaping)
            && escaping.Contains("file name", StringComparison.Ordinal), escaping);

        // The reply is a third party's: a broken one has to come back as a failure, never as an exception
        // travelling up through a startup check nobody is waiting on.
        foreach (string garbage in new[] { "", "{", "null", "[1,2,3]", "<html>rate limited</html>" })
        {
            check($"garbage ('{Trim(garbage)}') is refused without throwing",
                !ReleaseInfo.TryParse(garbage, out _, out _));
        }
    }

    private static string Trim(string text) => text.Length <= 12 ? text : text[..12] + "…";

    // ── The checksum published beside the archive ──

    private static void CheckChecksums(Assert check)
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string other = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        check("the sha256sum form is read",
            UpdateDownloader.ParseChecksum($"{hash}  {ArchiveName}\n", ArchiveName) == hash);
        check("binary mode's asterisk is not part of the name",
            UpdateDownloader.ParseChecksum($"{hash} *{ArchiveName}\n", ArchiveName) == hash);
        check("a bare hash on its own is read",
            UpdateDownloader.ParseChecksum(hash + "\n", ArchiveName) == hash);
        check("the right line is picked out of a sums file",
            UpdateDownloader.ParseChecksum($"{other}  something-else.zip\n{hash}  {ArchiveName}\n", ArchiveName)
            == hash);
        check("a sums file naming only other files hands over nothing",
            UpdateDownloader.ParseChecksum($"{other}  a.zip\n{hash}  b.zip\n", ArchiveName) is null);
        check("text that is not a checksum hands over nothing",
            UpdateDownloader.ParseChecksum("404: Not Found", ArchiveName) is null);
        check("a truncated hash is not a hash",
            UpdateDownloader.ParseChecksum($"{hash[..40]}  {ArchiveName}\n", ArchiveName) is null);
    }

    // ── Unpacking a downloaded archive ──

    private static void CheckStaging(Assert check, string scratch)
    {
        UpdateVersion.TryParse("9.9.9", out UpdateVersion version);

        // The release workflow's shape: everything inside one folder named after the release.
        string payload = Path.Combine(scratch, "payload", "Illusion-Toolkit-9.9.9-win-x64");
        Directory.CreateDirectory(Path.Combine(payload, "tools", "M2PhysX"));
        File.WriteAllText(Path.Combine(payload, AppVersion.ExecutableName), "new exe");
        File.WriteAllText(Path.Combine(payload, "Mafia.Formats.dll"), "new core");
        File.WriteAllText(Path.Combine(payload, "tools", "M2PhysX", "M2PhysX.exe"), "cooker");

        string wrapped = Path.Combine(scratch, "wrapped.zip");
        ZipFile.CreateFromDirectory(payload, wrapped, CompressionLevel.Fastest, includeBaseDirectory: true);

        StagedUpdate staged = UpdateDownloader.Stage(wrapped, Path.Combine(scratch, "stage-wrapped"), version);
        check("the archive's wrapper folder is seen through",
            Path.GetFileName(staged.PayloadDirectory) == "Illusion-Toolkit-9.9.9-win-x64",
            staged.PayloadDirectory);
        check("the staged executable is found", File.Exists(staged.ExecutablePath));
        check("the whole tree comes with it",
            File.Exists(Path.Combine(staged.PayloadDirectory, "tools", "M2PhysX", "M2PhysX.exe")));
        check("the staged version is the release's", staged.Version.ToString() == "9.9.9");

        string flat = Path.Combine(scratch, "flat.zip");
        ZipFile.CreateFromDirectory(payload, flat, CompressionLevel.Fastest, includeBaseDirectory: false);
        StagedUpdate flatStaged = UpdateDownloader.Stage(flat, Path.Combine(scratch, "stage-flat"), version);
        check("an archive packed flat stages just as well", File.Exists(flatStaged.ExecutablePath));

        // Anything else is not a release of this toolkit, and must be refused before a file is replaced.
        string stranger = Path.Combine(scratch, "stranger");
        Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "readme.txt"), "not a toolkit");
        string strangerZip = Path.Combine(scratch, "stranger.zip");
        ZipFile.CreateFromDirectory(stranger, strangerZip, CompressionLevel.Fastest, includeBaseDirectory: true);
        bool refused = false;
        try { UpdateDownloader.Stage(strangerZip, Path.Combine(scratch, "stage-stranger"), version); }
        catch (InvalidDataException) { refused = true; }
        check("an archive that is not a toolkit release is refused", refused);

        // Re-staging over a previous attempt has to start clean rather than mix two builds.
        string reused = Path.Combine(scratch, "stage-reused");
        Directory.CreateDirectory(reused);
        File.WriteAllText(Path.Combine(reused, "leftover.dll"), "from a previous try");
        UpdateDownloader.Stage(wrapped, reused, version);
        check("staging clears whatever a previous attempt left",
            !File.Exists(Path.Combine(reused, "leftover.dll")));

        CheckVerification(check, scratch);
    }

    private static void CheckVerification(Assert check, string scratch)
    {
        string file = Path.Combine(scratch, "verify.bin");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5 });
        string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

        check("a matching checksum passes", UpdateDownloader.Verify(file, expected));
        check("case does not matter", UpdateDownloader.Verify(file, expected.ToLowerInvariant()));
        check("a corrupted download fails",
            !UpdateDownloader.Verify(file, expected.Replace('0', '1').Replace('A', 'B')));
    }

    // ── Replacing the files ──

    private static void CheckSwap(Assert check, string scratch)
    {
        string source = Path.Combine(scratch, "swap-source");
        string target = Path.Combine(scratch, "swap-target");
        Directory.CreateDirectory(Path.Combine(source, "tools", "M2PhysX"));
        File.WriteAllText(Path.Combine(source, AppVersion.ExecutableName), "0.4.0");
        File.WriteAllText(Path.Combine(source, "Mafia.Formats.dll"), "core 0.4.0");
        File.WriteAllText(Path.Combine(source, "tools", "M2PhysX", "M2PhysX.exe"), "cooker");

        Directory.CreateDirectory(Path.Combine(target, "backups"));
        File.WriteAllText(Path.Combine(target, AppVersion.ExecutableName), "0.3.1");
        File.WriteAllText(Path.Combine(target, "backups", "eastside_20260101.sds"), "the user's own");

        int copied = UpdateInstaller.CopyTree(source, target);

        check("every file of the new build is written", copied == 3, copied + " files");
        check("the executable is replaced",
            File.ReadAllText(Path.Combine(target, AppVersion.ExecutableName)) == "0.4.0");
        check("folders the old install did not have are created",
            File.Exists(Path.Combine(target, "tools", "M2PhysX", "M2PhysX.exe")));
        check("what the user put in the folder is left alone",
            File.ReadAllText(Path.Combine(target, "backups", "eastside_20260101.sds")) == "the user's own");

        // Running it twice is what a retried update does; it must be as if it ran once.
        int again = UpdateInstaller.CopyTree(source, target);
        check("a repeated swap changes nothing",
            again == copied && File.ReadAllText(Path.Combine(target, AppVersion.ExecutableName)) == "0.4.0");

        string fresh = Path.Combine(scratch, "swap-fresh");
        check("a target folder that does not exist yet is made",
            UpdateInstaller.CopyTree(source, fresh) == 3 && File.Exists(Path.Combine(fresh, "Mafia.Formats.dll")));
    }

    // ── The command line the two builds talk over ──

    private static void CheckApplyContract(Assert check)
    {
        check("the switch is the one older builds write", UpdateInstaller.ApplySwitch == "--apply-update");

        string[] arguments = UpdateInstaller.BuildApplyArguments(@"C:\Tools\Illusion", 4321);
        check("the command line is switch, folder, process id",
            arguments.Length == 3 && arguments[0] == "--apply-update"
            && arguments[1] == @"C:\Tools\Illusion" && arguments[2] == "4321",
            string.Join(" ", arguments));

        bool read = UpdateInstaller.TryReadApplyRequest(arguments, out ApplyRequest? request);
        check("and it reads back the same",
            read && request is { ProcessId: 4321 } && request.TargetDirectory == @"C:\Tools\Illusion");

        check("a normal start is not an update",
            !UpdateInstaller.TryReadApplyRequest(Array.Empty<string>(), out _)
            && !UpdateInstaller.TryReadApplyRequest(new[] { "--probe-update" }, out _));
        check("a malformed apply is refused rather than half-run",
            !UpdateInstaller.TryReadApplyRequest(new[] { "--apply-update" }, out _)
            && !UpdateInstaller.TryReadApplyRequest(new[] { "--apply-update", @"C:\x" }, out _)
            && !UpdateInstaller.TryReadApplyRequest(new[] { "--apply-update", "", "1" }, out _)
            && !UpdateInstaller.TryReadApplyRequest(new[] { "--apply-update", @"C:\x", "later" }, out _)
            && !UpdateInstaller.TryReadApplyRequest(new[] { "--apply-update", @"C:\x", "-1" }, out _));
    }

    /// <summary>A picture of the launcher with the update button showing, so the one thing an assertion cannot
    /// judge — whether a green arrow beside the gear reads as an offer rather than as a fault — can be looked
    /// at. Output: %TEMP%\illusion_update_launcher.png</summary>
    private static void Render(FrameworkElement body, double width, StringBuilder sb)
    {
        try
        {
            // The window's own chrome is not in this tree, so without a fill the picture comes out on nothing
            // and the dark theme's white text lands on white. Painted BEFORE the layout pass — a background
            // set after one has already run does not reach the render.
            if (body is Panel root) root.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            body.Measure(new Size(width, double.PositiveInfinity));
            double height = body.DesiredSize.Height;
            body.Arrange(new Rect(0, 0, width, height));
            body.UpdateLayout();

            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(body);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(Path.GetTempPath(), "illusion_update_launcher.png");
            using FileStream file = File.Create(path);
            encoder.Save(file);
            sb.AppendLine($"rendered the launcher with an update waiting -> {path}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("launcher render skipped — " + ex.Message);
        }
    }

    // ── What the two windows do with a result ──

    private static void CheckWindows(Assert check, StringBuilder sb)
    {
        // A probe run never reaches the line in App that pins the dark theme, and a picture of the launcher in
        // WPF's default light one would say nothing about whether a green arrow reads right on the surface it
        // is actually drawn on. Set here rather than for every probe: this is the only one that looks.
        if (Application.Current is { } app) app.ThemeMode = ThemeMode.Dark;

        ReleaseInfo.TryParse(LatestFixture, out ReleaseInfo? release, out _);
        var available = new UpdateCheckResult(UpdateStatus.UpdateAvailable, release, "");
        var current = new UpdateCheckResult(UpdateStatus.UpToDate, release, "");
        var failed = new UpdateCheckResult(UpdateStatus.Failed, null, "Could not reach GitHub: no such host.");

        var launcher = new LauncherWindow();
        launcher.ShowUpdate(current);
        check("the launcher shows no button when there is nothing to install",
            launcher.UpdateBtn.Visibility != Visibility.Visible);

        launcher.ShowUpdate(failed);
        check("a failed check stays out of the launcher's way",
            launcher.UpdateBtn.Visibility != Visibility.Visible);

        launcher.ShowUpdate(available);
        check("a found update puts the button in the corner",
            launcher.UpdateBtn.Visibility == Visibility.Visible);
        check("and says which version it would install",
            launcher.UpdateBtn.ToolTip?.ToString()?.Contains("9.9.9", StringComparison.Ordinal) == true,
            launcher.UpdateBtn.ToolTip?.ToString() ?? "no tooltip");

        // The corner grows by a button when there is something to install. --probe-layout measures the same
        // corner but can only ever see it in its one-button state, so the crowded one is measured here: a
        // second button must not push the pair over the title or off the window's fixed 600px width.
        launcher.ShowUpdate(available);
        var body = (FrameworkElement)launcher.Content;
        const double width = 600;
        body.Measure(new Size(width, double.PositiveInfinity));
        body.Arrange(new Rect(0, 0, width, body.DesiredSize.Height));
        body.UpdateLayout();

        Rect gear = LayoutProbes.BoundsIn(launcher.SettingsBtn, body);
        Rect download = LayoutProbes.BoundsIn(launcher.UpdateBtn, body);
        Rect title = LayoutProbes.BoundsIn(launcher.TitleBlock, body);
        check("the download button takes the corner beside the gear, not over it",
            download.Right <= gear.Left + 0.5 && gear.Right <= width + 0.5 && download.Width > 0,
            $"download [{download.Left:F0}..{download.Right:F0}], gear [{gear.Left:F0}..{gear.Right:F0}]");
        check("and the two together still clear the title",
            download.Left >= title.Right - 0.5,
            $"title ends at {title.Right:F0}, download starts at {download.Left:F0}");

        Render(body, width, sb);

        launcher.ShowUpdate(current);
        check("and takes it away again once there is not", launcher.UpdateBtn.Visibility != Visibility.Visible);

        var settings = new SettingsWindow();
        check("the settings window has an Updates section",
            Enum.IsDefined(SettingsSection.Updates) && settings.Sections.Items.Count == 5);
        settings.SelectSection(SettingsSection.Updates);
        check("which is the one the rail opens on",
            settings.Sections.SelectedIndex == (int)SettingsSection.Updates);
        check("it says which version is running",
            settings.VersionText.Text.Length > 0 && settings.BuildText.Text.Length > 0,
            $"{settings.VersionText.Text} · {settings.BuildText.Text}");

        settings.ShowCheckResult(available);
        check("an available update offers the install button",
            settings.InstallUpdateBtn.Visibility == Visibility.Visible
            && settings.UpdateStatusText.Text.Contains("9.9.9", StringComparison.Ordinal),
            settings.UpdateStatusText.Text);

        settings.ShowCheckResult(current);
        check("an up-to-date answer offers nothing to press",
            settings.InstallUpdateBtn.Visibility != Visibility.Visible, settings.UpdateStatusText.Text);

        settings.ShowCheckResult(failed);
        check("a failed check is reported here rather than swallowed",
            settings.UpdateStatusText.Text.Contains("no such host", StringComparison.Ordinal),
            settings.UpdateStatusText.Text);

        settings.Close();
        launcher.Close();
    }

    // ── The real releases page (opt-in: --probe-update live) ──

    private static void CheckLive(Assert check, StringBuilder sb)
    {
        // Off the dispatcher: the probe blocks on the answer, and a continuation posted back to a message loop
        // that is not running would wait for one another forever.
        UpdateCheckResult result = Task.Run(() => UpdateChecker.CheckAsync(force: true)).GetAwaiter().GetResult();

        sb.AppendLine();
        sb.AppendLine($"live check: {result.Status} {result.Error}");

        if (result.Status == UpdateStatus.Failed)
        {
            check("live: SKIPPED — GitHub could not be reached", true, result.Error);
            return;
        }

        ReleaseInfo release = result.Release!;
        sb.AppendLine($"live release: {release.Tag} · {release.AssetName} ({release.AssetSizeText}) · " +
                      $"checksum: {release.ChecksumName ?? "none published"}");

        check("live: the newest release reads as a version", !release.Version.IsEmpty, release.Tag);
        check("live: it has a win-x64 archive to download",
            release.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && release.AssetUrl.StartsWith("https://", StringComparison.Ordinal),
            release.AssetName);
        check("live: the verdict follows from the two versions",
            result.HasUpdate == release.Version.IsNewerThan(AppVersion.Current),
            $"{AppVersion.Current} vs {release.Version}");
    }
}
