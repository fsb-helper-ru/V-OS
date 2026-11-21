using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;
using Path = System.IO.Path;
using Microsoft.Win32;
using VOS.Core;
using VOS.Core.Ipc;
using VOS.Core.Settings;
using VOS.Core.Setup;
using VOS.Core.Updates;
using VOS.Platform.Desktop;
using VOS.Platform.Shell;
using VOS.Platform.Sys;
using VOS.UI;
using VOS.UI.Theme;

namespace VOS.Launcher;

/// <summary>Entry point of <c>VOS.exe --launcher</c>.</summary>
public static class LauncherWindow
{
    public static void Start(string[] args) => _ = new LauncherApp(args);
}

/// <summary>
/// V-OS Launcher: first-run completion after the bootstrapper installed V-OS, signed updates with rollback, repair,
/// recovery, diagnostics and uninstall. File swaps are done by <c>V-Launcher.exe</c>, which runs outside the app folder
/// (ADR-005, ADR-006); this window prepares and verifies everything first.
/// </summary>
internal sealed class LauncherApp
{
    private static readonly VosApp Identity = new("launcher", "V-OS Launcher", Glyphs.Apps, "#8EA2FF", "#4C5FD6", "Install, update and recover V-OS");
    private readonly AppFrame _frame;
    private readonly NavShell _nav = new(228);
    private readonly InstallLayout _layout = InstallLayout.Default;
    private readonly bool _installed;
    private readonly string _mode;
    private UpdateCheckResult? _check;
    private bool _busy;
    private string _updateText = "";
    private double? _updateProgress;
    private TextBlock? _updateStatus;
    private ProgressBar? _updateBar;

    public LauncherApp(string[] args)
    {
        _mode = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal))?.TrimStart('-') ?? "";
        _installed = _layout.IsActiveApp(AppContext.BaseDirectory);
        if (_installed)
        {
            EnsureBootstrapperCopy();
            Registration.RegisterEssentials(_layout);
        }
        _frame = new AppFrame(Identity, "V-OS Launcher", 760, 540, 940, 680);
        _nav.CachePages = false;
        _nav.Add(new NavShell.Item("overview", "Overview", Glyphs.Home));
        _nav.Add(new NavShell.Item("updates", "Updates", Glyphs.Download));
        _nav.Add(new NavShell.Item("repair", "Repair and recovery", Glyphs.Restart));
        _nav.Add(new NavShell.Item("diagnostics", "Diagnostics", Glyphs.Gauge));
        _nav.Add(new NavShell.Item("uninstall", "Uninstall", Glyphs.Delete), footer: true);
        _nav.PageFactory = Build;
        _frame.Body.Content = _nav;
        _nav.Select(_mode == "update" ? "updates" : "overview");
        _frame.Activate();
        if (_mode == "update") _ = CheckAsync();
    }

    private UIElement Build(string id) => Kit.Scroll(id switch
    {
        "updates" => Updates(),
        "repair" => Repair(),
        "diagnostics" => Diagnostics(),
        "uninstall" => Uninstall(),
        _ => Overview(),
    }, 820);

    // ─────────────────────────────── overview ───────────────────────────────

    private StackPanel Overview()
    {
        var page = Kit.Stack(4);
        var hero = new Grid { ColumnSpacing = 20, Margin = new Thickness(0, 8, 0, 12) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.Children.Add(VosMark(72));
        var words = Kit.Stack(2,
            new TextBlock { Text = "V-OS", FontFamily = Tokens.Instrument, FontWeight = FontWeights.Light, FontSize = 46, Foreground = Ui.B("Vos.TextPrimary") },
            Ui.Text($"Version {AppInfo.Version}", 14, "Vos.TextSecondary"));
        words.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(words, 1);
        hero.Children.Add(words);
        page.Children.Add(hero);

        switch (_mode)
        {
            case "setup": page.Children.Add(WelcomeCard()); break;
            case "updated": page.Children.Add(Kit.Info("Update installed", $"V-OS was updated to version {AppInfo.Version}. Your settings and data were kept.", InfoBarSeverity.Success)); break;
            case "repaired": page.Children.Add(Kit.Info("Repair finished", "The program files were replaced with a verified copy. Your settings and data were kept.", InfoBarSeverity.Success)); break;
            case "rolled-back": page.Children.Add(Kit.Info("Previous version restored", $"V-OS is back on version {AppInfo.Version}.", InfoBarSeverity.Success)); break;
        }
        if (!_installed) page.Children.Add(NotInstalledInfo());

        var state = _layout.ReadState();
        page.Children.Add(Kit.Section("Installation"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Location", _installed ? _layout.Root : AppContext.BaseDirectory.TrimEnd('\\'), Buttons.Filled("Open", () => ShellOps.Launch(_installed ? _layout.Root : AppContext.BaseDirectory)), Glyphs.Folder),
            Kit.Setting("Installed", state != null ? state.InstalledAt.ToLocalTime().ToString("d MMMM yyyy") + (state.UpdatedAt is { } u ? $" · updated {u.ToLocalTime():d MMMM yyyy}" : "") : "—"),
            Kit.Setting("Rollback copy", _layout.HasRollback ? $"Version {InstallLayout.FileVersion(Path.Combine(_layout.Previous, "VOS.exe"))} is kept and can be restored." : "None yet. The current version is kept automatically when you update."),
            Kit.Setting("Disk space", _installed ? Fmt.Bytes(InstallLayout.DirectorySize(_layout.Root)) + " for program files" : "—"),
            Kit.Setting("Your data", InstallRegistration.DataRoot, Buttons.Filled("Open", () => ShellOps.Launch(InstallRegistration.DataRoot)), Glyphs.Document)));
        page.Children.Add(Kit.Row(8,
            Buttons.Accent("Open V-OS", () => StartVos("--shell")),
            Buttons.Filled("Check for updates", () => { _nav.Select("updates"); _ = CheckAsync(); })));
        return page;
    }

    private Border WelcomeCard()
    {
        bool canRegister = Registration.CanRegister(_layout);
        var registered = canRegister
            ? "V-OS is in the Start menu and in Windows Settings › Apps, where you can also uninstall it."
            : "Test installation: nothing was registered with Windows.";
        var content = Kit.Stack(12,
            Ui.Text("V-OS is installed", 20, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold),
            Ui.Text($"{registered} When V-OS starts for the first time, it asks how it should run alongside Windows.", 13.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap));
        if (canRegister) content.Children.Add(Kit.Setting("Desktop shortcut", null, Kit.Toggle(Registration.HasDesktopShortcut, v => Registration.SetDesktopShortcut(_layout, v))));
        content.Children.Add(Kit.Row(8, Buttons.Accent("Start V-OS", () => { StartVos("--shell"); _frame.Window.Close(); })));
        var card = Kit.Card(content, new Thickness(24));
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    private InfoBar NotInstalledInfo() => Kit.Info("Not an installed copy",
        $"This V-OS runs from {AppContext.BaseDirectory.TrimEnd('\\')}. Updates, repair and uninstall apply to installed copies; run V-Launcher.exe to install V-OS.", InfoBarSeverity.Informational);

    // ─────────────────────────────── updates ───────────────────────────────

    private StackPanel Updates()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Updates",
            "Updates are signed with the V-OS release key. The package checksum is checked before anything is installed, your settings are saved in a snapshot, and the current version is kept for rollback."));
        if (!_installed) page.Children.Add(NotInstalledInfo());
        _updateStatus = Ui.Text(_updateText.Length > 0 ? _updateText : "Check for a new version of V-OS.", 13, "Vos.TextSecondary", wrap: TextWrapping.Wrap);
        _updateBar = new ProgressBar { Maximum = 1, Visibility = _updateProgress == null ? Visibility.Collapsed : Visibility.Visible, Value = _updateProgress ?? 0, Margin = new Thickness(0, 6, 0, 0) };
        var check = Buttons.Filled("Check now", () => _ = CheckAsync());
        check.IsEnabled = !_busy;
        var install = Buttons.Accent("Download and install", () => _ = InstallUpdateAsync());
        install.Visibility = _check?.Available == true ? Visibility.Visible : Visibility.Collapsed;
        install.IsEnabled = _installed && !_busy;
        var head = new Grid { ColumnSpacing = 16 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(VosMark(44));
        var info = Kit.Stack(2, Ui.Text($"V-OS {AppInfo.Version}", 18, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold), _updateStatus, _updateBar);
        Grid.SetColumn(info, 1);
        head.Children.Add(info);
        var buttons = Kit.Row(8, install, check);
        buttons.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(buttons, 2);
        head.Children.Add(buttons);
        page.Children.Add(Kit.Card(head, new Thickness(20)));
        if (_check is { Available: true, Manifest: { Notes.Length: > 0 } m })
        {
            page.Children.Add(Kit.Section($"What's new in {m.Version}"));
            page.Children.Add(Kit.Card(MarkdownView.Render(m.Notes, 13.5), new Thickness(20)));
        }

        page.Children.Add(Kit.Section("Previous version"));
        if (_layout.HasRollback && _installed)
        {
            var previous = InstallLayout.FileVersion(Path.Combine(_layout.Previous, "VOS.exe"));
            page.Children.Add(Kit.Group(Kit.Setting($"Return to version {previous}", "V-OS closes and starts again with the previous version. Your settings and data are kept.",
                Buttons.Filled("Roll back", () => _ = RollBackAsync()), Glyphs.History)));
        }
        else page.Children.Add(Kit.Group(Kit.Setting("No previous version", "After an update, the version you had before is kept here so you can return to it.", null, Glyphs.History)));

        var feed = UpdateClient.ResolveFeed(SettingsStore.Instance.Current.Updates.FeedUrl);
        page.Children.Add(Kit.Section("Source"));
        page.Children.Add(Kit.Group(Kit.Setting("Release feed", feed ?? "No feed is configured in this build. Set one in Settings › Updates.", null, Glyphs.Globe)));
        return page;
    }

    private void SetUpdate(string text, double? progress)
    {
        _updateText = text;
        _updateProgress = progress;
        if (_updateStatus != null) _updateStatus.Text = text;
        if (_updateBar != null)
        {
            _updateBar.Visibility = progress == null ? Visibility.Collapsed : Visibility.Visible;
            _updateBar.Value = progress ?? 0;
        }
    }

    private async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        SetUpdate("Checking…", null);
        try
        {
            var feed = UpdateClient.ResolveFeed(SettingsStore.Instance.Current.Updates.FeedUrl);
            _check = await new UpdateClient().CheckAsync(feed, AppInfo.Version, ReleaseKey.PublicKeyPem);
            SetUpdate(_check.Message, null);
            SettingsStore.Instance.Update(s => s.Updates.LastCheck = DateTimeOffset.Now);
        }
        catch (Exception ex) { SetUpdate("The check failed: " + ex.Message, null); }
        finally { _busy = false; }
        if (_nav.Current == "updates") _nav.Select("updates");
    }

    private async Task InstallUpdateAsync()
    {
        if (_busy || _check is not { Available: true, Manifest: { } m } check || !_installed) return;
        _busy = true;
        try
        {
            var package = await DownloadAsync(check, m);
            await StageAsync(package, 0.8);
            SetUpdate("Saving a snapshot of your settings…", 0.98);
            await Task.Run(() => new SnapshotService().Create($"Before updating to {m.Version}"));
            SetUpdate("V-OS restarts to finish the update…", 1);
            await HandOverAsync("--apply");
        }
        catch (Exception ex)
        {
            Log.Warn("Update preparation failed", ex);
            SetUpdate("The update could not be prepared: " + ex.Message, null);
            InstallLayout.DeleteDirectory(_layout.Staging);
        }
        finally { _busy = false; }
    }

    private async Task<string> DownloadAsync(UpdateCheckResult check, ReleaseManifest m)
    {
        Directory.CreateDirectory(_layout.Packages);
        var package = System.IO.Path.Combine(_layout.Packages, System.IO.Path.GetFileName(m.Package.Url.Replace('\\', '/').Split('?')[0]));
        if (File.Exists(package) && Hash(package).Equals(m.Package.Sha256, StringComparison.OrdinalIgnoreCase)) return package;
        SetUpdate("Downloading…", 0);
        await new UpdateClient().DownloadAsync(check, package, new Progress<double>(p => SetUpdate($"Downloading… {p:P0} of {Fmt.Bytes(m.Package.Size)}", p * 0.8)));
        return package;
    }

    private async Task StageAsync(string package, double start)
    {
        SetUpdate("Unpacking…", start);
        var progress = new Progress<double>(p => SetUpdate("Unpacking…", start + p * (0.98 - start)));
        await Task.Run(() => _layout.ExtractToStaging(package, progress));
    }

    private async Task RollBackAsync()
    {
        var previous = InstallLayout.FileVersion(Path.Combine(_layout.Previous, "VOS.exe"));
        if (!await Dialogs.ConfirmAsync(_frame.Window.Content.XamlRoot, $"Return to V-OS {previous}?",
            "V-OS closes and starts again with the previous version. Your settings and data are kept.", $"Return to {previous}")) return;
        await HandOverAsync("--rollback");
    }

    /// <summary>Starts V-Launcher.exe (outside the app folder, so it can swap it) and exits.</summary>
    private async Task HandOverAsync(string verb, params string[] extra)
    {
        bool shellRunning = await Task.Run(() => IpcClient.IsListening(IpcNames.Shell));
        var args = new List<string> { verb };
        args.AddRange(extra);
        if (shellRunning) args.Add("--restart-shell");
        EnsureBootstrapperCopy();
        if (!File.Exists(_layout.Bootstrapper)) throw new FileNotFoundException("V-Launcher.exe is missing from the installation. Run the V-OS installer again to repair it.");
        var psi = new ProcessStartInfo(_layout.Bootstrapper) { UseShellExecute = false, WorkingDirectory = _layout.Root };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
        Log.Info("Handed over to V-Launcher", new { verb });
        Log.Flush();
        Application.Current.Exit();
    }

    private void EnsureBootstrapperCopy()
    {
        var packaged = Path.Combine(_layout.App, "V-Launcher.exe");
        try
        {
            if (File.Exists(packaged) && (!File.Exists(_layout.Bootstrapper) || File.GetLastWriteTimeUtc(packaged) > File.GetLastWriteTimeUtc(_layout.Bootstrapper)))
                File.Copy(packaged, _layout.Bootstrapper, overwrite: true);
        }
        catch (IOException ex) { Log.Warn("Could not refresh V-Launcher.exe", ex); }
    }

    // ─────────────────────────────── repair and recovery ───────────────────────────────

    private StackPanel Repair()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Repair and recovery", "Fix a damaged installation or put Windows back to normal. Your settings and data are kept unless you choose otherwise."));
        if (!_installed) page.Children.Add(NotInstalledInfo());
        var repairStatus = Ui.Text("", 12.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap);
        var repair = Buttons.Filled("Repair", null);
        repair.IsEnabled = _installed;
        repair.Click += async (_, _) =>
        {
            repair.IsEnabled = false;
            try { await RepairAsync(t => repairStatus.Text = t); }
            catch (Exception ex) { repairStatus.Text = "Repair could not start: " + ex.Message; Log.Warn("Repair failed", ex); }
            finally { repair.IsEnabled = _installed; }
        };
        var desktopStatus = Ui.Text("", 12.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap);
        var cacheStatus = Ui.Text("", 12.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap);
        page.Children.Add(Kit.Section("Program files"));
        page.Children.Add(Kit.Group(Kit.Setting("Repair V-OS", "Replaces the program files with a verified copy of this version, then starts V-OS again.", Kit.Stack(4, repair, repairStatus), Glyphs.Restart)));
        page.Children.Add(Kit.Section("Recovery"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Restore the Windows desktop", "Shows the taskbar and desktop icons and gives the screen space back, as before V-OS. Also available with Ctrl+Alt+Shift+F12 while V-OS runs.",
                Kit.Stack(4, Buttons.Filled("Restore", () => desktopStatus.Text = DesktopMode.Restore() ? "Windows desktop restored." : "The Windows desktop is already in its normal state."), desktopStatus), Glyphs.Desktop),
            Kit.Setting("Restore a snapshot", "Return your whole V-OS setup to a saved state. A snapshot is saved before every update.",
                Buttons.Filled("Open snapshots", () => StartVos("--app", "settings", "section=snapshots")), Glyphs.History),
            Kit.Setting("Clear cached data", "Icons, thumbnails and forecasts are downloaded or rebuilt again when needed.",
                Kit.Stack(4, Buttons.Filled("Clear", () => cacheStatus.Text = ClearCache()), cacheStatus), Glyphs.Delete),
            Kit.Setting("Logs", "Local files that help to understand a problem. They never leave this PC unless you share them.",
                Buttons.Filled("Open folder", () => ShellOps.Launch(VosPaths.Logs)), Glyphs.Document)));
        return page;
    }

    private async Task RepairAsync(Action<string> status)
    {
        var state = _layout.ReadState();
        string? package = state?.PackageFile is { } f ? Path.Combine(_layout.Packages, f) : null;
        status("Checking the saved package…");
        bool usable = package != null && File.Exists(package) && state!.PackageSha256 is { } sha
            && await Task.Run(() => Hash(package)).ConfigureAwait(true) is var h && h.Equals(sha, StringComparison.OrdinalIgnoreCase)
            && InstallLayoutVersion(package) == AppInfo.Version;
        if (!usable)
        {
            status("Downloading a verified copy of this version…");
            var feed = UpdateClient.ResolveFeed(SettingsStore.Instance.Current.Updates.FeedUrl);
            var check = await new UpdateClient().CheckAsync(feed, "0.0.0", ReleaseKey.PublicKeyPem);
            if (check.Manifest is not { } m || m.Version != AppInfo.Version)
            {
                status(check.Manifest is { } other
                    ? $"The saved package is missing and the update server offers version {other.Version}. Install that update instead (Updates page)."
                    : "The saved package is missing and the update server could not be reached: " + check.Message);
                return;
            }
            package = await DownloadAsync(check, m);
        }
        if (!await Dialogs.ConfirmAsync(_frame.Window.Content.XamlRoot, "Repair V-OS?",
            "V-OS closes, its program files are replaced with a verified copy of this version, and it starts again. Your settings and data are kept.", "Repair now")) { status(""); return; }
        status("Unpacking…");
        await Task.Run(() => _layout.ExtractToStaging(package!));
        await HandOverAsync("--apply", "--repair");
    }

    /// <summary>The version a package was built for, from its file name (V-OS-1.2.3-win-x64.zip).</summary>
    private static string? InstallLayoutVersion(string package)
    {
        var name = Path.GetFileNameWithoutExtension(package);
        var parts = name.Split('-');
        return parts.Length >= 3 ? parts[2] : null;
    }

    private static string ClearCache()
    {
        long freed = 0;
        foreach (var f in new DirectoryInfo(VosPaths.Cache).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { long len = f.Length; f.Delete(); freed += len; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return $"Cleared {Fmt.Bytes(freed)}.";
    }

    // ─────────────────────────────── diagnostics ───────────────────────────────

    private StackPanel Diagnostics()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Diagnostics", "Information about this PC and V-OS. Nothing is sent anywhere; an export is a file you can share yourself."));
        var hw = HardwareProbe.Probe();
        var processes = Process.GetProcessesByName("VOS");
        long memory = processes.Sum(p => { try { return p.PrivateMemorySize64; } catch (InvalidOperationException) { return 0; } });
        int count = processes.Length;
        foreach (var p in processes) p.Dispose();
        page.Children.Add(Kit.Section("This PC"));
        page.Children.Add(Kit.Group(
            Kit.Setting("Windows", WindowsDescription(), null, Glyphs.Desktop),
            Kit.Setting("Processor", ProcessorName() + $" · {hw.LogicalCores} logical cores", null, Glyphs.Cpu),
            Kit.Setting("Memory", $"{hw.RamGb:0.#} GB", null, Glyphs.Memory),
            Kit.Setting("Graphics", string.IsNullOrEmpty(hw.GpuName) ? "Unknown" : $"{hw.GpuName}" + (hw.GpuDedicatedMb > 0 ? $" · {hw.GpuDedicatedMb / 1024.0:0.#} GB" : ""), null, Glyphs.Monitor),
            Kit.Setting("Displays", $"{hw.MonitorCount} · up to {hw.MaxRefreshHz} Hz", null, Glyphs.Window)));
        page.Children.Add(Kit.Section("V-OS"));
        var exportStatus = Ui.Text("", 12.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap);
        var export = Buttons.Filled("Export…", null);
        export.Click += async (_, _) =>
        {
            var path = await Pickers.SaveAsync(_frame.Hwnd, $"V-OS diagnostics {DateTime.Now:yyyy-MM-dd HHmm}", "Zip archive", ".zip");
            if (path == null) return;
            try { await Task.Run(() => ExportDiagnostics(path)); exportStatus.Text = "Saved to " + path; }
            catch (Exception ex) { exportStatus.Text = "Export failed: " + ex.Message; }
        };
        page.Children.Add(Kit.Group(
            Kit.Setting("Version", $"{AppInfo.Version} · .NET {Environment.Version}" + (AppInfo.BuildTime is { } bt ? $" · built {bt}" : ""), null, Glyphs.Info),
            Kit.Setting("WebView2 runtime", WebViewVersion(), null, Glyphs.Globe),
            Kit.Setting("Running processes", $"{count} · {Fmt.Bytes(memory)} private memory", null, Glyphs.Gauge),
            Kit.Setting("Data folder", $"{InstallRegistration.DataRoot} · {Fmt.Bytes(InstallLayout.DirectorySize(InstallRegistration.DataRoot))}", null, Glyphs.Folder),
            Kit.Setting("Export diagnostics", "Logs, crash reports, install information and settings with keys removed, in one zip file.", Kit.Stack(4, export, exportStatus), Glyphs.Download)));
        return page;
    }

    private static string WindowsDescription()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            var build = Environment.OSVersion.Version.Build;
            if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");
            return $"{name} {k?.GetValue("DisplayVersion")} · build {build}.{k?.GetValue("UBR")}";
        }
        catch (Exception) { return Environment.OSVersion.VersionString; }
    }

    private static string ProcessorName()
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"); return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown"; }
        catch (Exception) { return "Unknown"; }
    }

    private static string WebViewVersion()
    {
        try { return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "Not installed"; }
        catch (Exception) { return "Not installed"; }
    }

    private void ExportDiagnostics(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void AddFolder(string dir, string prefix)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc).Take(40))
            {
                try { zip.CreateEntryFromFile(f, prefix + "/" + Path.GetFileName(f), CompressionLevel.Optimal); } catch (IOException) { }
            }
        }
        AddFolder(VosPaths.Logs, "logs");
        AddFolder(VosPaths.Crashes, "crashes");
        if (File.Exists(VosPaths.SettingsFile))
        {
            var entry = zip.CreateEntry("settings.json");
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(VOS.Core.Security.Redaction.RedactJson(File.ReadAllText(VosPaths.SettingsFile)));
        }
        if (File.Exists(_layout.StatePath)) zip.CreateEntryFromFile(_layout.StatePath, "install.json");
        var system = zip.CreateEntry("system.txt");
        using (var w = new StreamWriter(system.Open(), new UTF8Encoding(false)))
        {
            var hw = HardwareProbe.Probe();
            w.WriteLine($"V-OS {AppInfo.Version} (.NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
            w.WriteLine($"Installed: {_installed} ({_layout.Root}), rollback copy: {_layout.HasRollback}");
            w.WriteLine($"Windows: {WindowsDescription()}");
            w.WriteLine($"CPU: {ProcessorName()} ({hw.LogicalCores} threads), RAM {hw.RamGb:0.#} GB");
            w.WriteLine($"GPU: {hw.GpuName} ({hw.GpuDedicatedMb} MB), software: {hw.GpuIsSoftware}");
            w.WriteLine($"Displays: {hw.MonitorCount}, {hw.TotalPixels} px total, {hw.MaxRefreshHz} Hz max; high contrast {hw.HighContrast}; animations {hw.WindowsAnimations}; transparency {hw.WindowsTransparency}");
            w.WriteLine($"WebView2: {WebViewVersion()}");
        }
    }


    // ─────────────────────────────── uninstall ───────────────────────────────

    private StackPanel Uninstall()
    {
        var page = Kit.Stack(4, Kit.PageHeader("Uninstall", "Remove V-OS from this PC."));
        if (!_installed) { page.Children.Add(NotInstalledInfo()); return page; }
        var button = Buttons.Filled("Uninstall V-OS…", () => _ = HandOverAsync("--uninstall"));
        page.Children.Add(Kit.Card(Kit.Stack(12,
            Ui.Text("What happens", 15, "Vos.TextPrimary", Tokens.Display, FontWeights.SemiBold),
            Ui.Text("V-OS closes and the Windows taskbar, desktop icons and screen space return to how they were. The program files, Start menu entries and the entry in Windows Settings › Apps are removed. You choose whether your V-OS settings and data are kept.", 13.5, "Vos.TextSecondary", wrap: TextWrapping.Wrap),
            Ui.Text("You can also uninstall from Windows Settings › Apps › Installed apps.", 12.5, "Vos.TextTertiary", wrap: TextWrapping.Wrap),
            button), new Thickness(24)));
        return page;
    }

    // ─────────────────────────────── helpers ───────────────────────────────

    private void StartVos(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "VOS.exe")) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Log.Warn("Could not start V-OS", ex); }
    }

    private static string Hash(string path) { using var fs = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(fs)); }

    /// <summary>The V-OS mark: the two-tone squircle with the white "V", drawn as vectors.</summary>
    internal static Grid VosMark(double size)
    {
        var g = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Top };
        g.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.27),
            Background = new LinearGradientBrush
            {
                StartPoint = new global::Windows.Foundation.Point(0.15, 0), EndPoint = new global::Windows.Foundation.Point(0.85, 1),
                GradientStops = { new GradientStop { Color = Colors2.Hex("#8EA2FF"), Offset = 0 }, new GradientStop { Color = Colors2.Hex("#4C5FD6"), Offset = 1 } },
            },
            BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(Math.Max(1, size / 64)),
        });
        g.Children.Add(new Polyline
        {
            Points = { new(size * 0.31, size * 0.33), new(size * 0.50, size * 0.69), new(size * 0.69, size * 0.33) },
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = size * 0.115,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        });
        return g;
    }
}
