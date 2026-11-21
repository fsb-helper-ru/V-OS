using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using VOS.Core.Setup;
using VOS.Core.Updates;

namespace VOS.Bootstrap;

/// <summary>A failure explained for the user. Anything else is shown with technical details.</summary>
internal sealed class SetupException(string message, string? details = null) : Exception(message)
{
    public string? Details { get; } = details;
}

/// <summary>Progress shared between a worker thread and its dialog.</summary>
internal sealed class Job
{
    private volatile string _text = "";
    private volatile int _permille = -1;
    public volatile bool CanCancel = true;
    public volatile bool Done;
    public string Text { get => _text; set => _text = value; }
    /// <summary>0..1, or null for an indeterminate bar.</summary>
    public double? Fraction { get => _permille < 0 ? null : _permille / 1000.0; set => _permille = value is double f ? (int)Math.Clamp(f * 1000, 0, 1000) : -1; }
    public void Phase(string text, double? fraction = null) { Text = text; Fraction = fraction; }
}

internal sealed record ReleaseOffer(ReleaseManifest Manifest, string? LocalPackage, Uri? PackageUri, string Source);

/// <summary>
/// V-Launcher.exe: installs V-OS for the current user (download or files next to it), applies staged updates with an
/// automatic rollback, rolls back on request, and uninstalls. Verbs: none (install or repair), --apply, --rollback,
/// --uninstall. It never needs administrator rights and changes nothing outside the user's profile.
/// </summary>
internal static class Program
{
    private const int InstallButton = 100, OpenButton = 101, RepairButton = 102, UninstallButton = 103, RetryButton = 104;
    private static readonly InstallLayout Layout = InstallLayout.Default;
    private static readonly HttpClient Http = CreateHttp();
    private static string ExePath => Environment.ProcessPath!;
    private static string ExeDirectory => Path.GetDirectoryName(ExePath)!;
    private static string SelfVersion => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

    [STAThread]
    private static int Main(string[] args)
    {
        Log($"start {string.Join(' ', args)} (root {Layout.Root})");
        try
        {
            // Left behind when a previous run replaced itself (see CopySelfIntoInstallation).
            var stale = Layout.Bootstrapper + ".old";
            if (File.Exists(stale) && !string.Equals(Path.GetFullPath(ExePath), Path.GetFullPath(stale), StringComparison.OrdinalIgnoreCase))
                try { File.Delete(stale); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal)) switch
            {
                "--apply" => Apply(args.Contains("--restart-shell"), args.Contains("--repair")),
                "--rollback" => Rollback(args.Contains("--restart-shell")),
                "--uninstall" => Uninstall(args.Contains("--quiet")),
                _ => Install(),
            };
        }
        catch (Exception ex)
        {
            Log("fatal: " + ex);
            ShowError("V-OS setup stopped", ex);
            return 10;
        }
    }

    // ─────────────────────────────── install and repair ───────────────────────────────

    private static int Install()
    {
        while (true)
        {
            ReleaseOffer offer;
            try { offer = FindRelease(); }
            catch (Exception ex)
            {
                Log("find release: " + ex.Message);
                if (AskRetry("V-OS could not be prepared", ex)) continue;
                return 2;
            }

            var installed = InstallLayout.FileVersion(Layout.AppExe);
            SemVer.TryParse(offer.Manifest.Version, out var available);
            bool hasInstalled = installed != null && SemVer.TryParse(installed, out _);
            SemVer.TryParse(installed, out var current);
            int cmp = hasInstalled ? available.CompareTo(current) : 1;
            bool repair = false;
            if (hasInstalled && cmp == 0)
            {
                var (b, _) = TaskDialog.Show(new TaskPage
                {
                    Heading = $"V-OS {installed} is installed",
                    Content = "Open it, or repair the installation if something is not working. Repair replaces the program files and keeps your settings and data.",
                    Buttons = [(OpenButton, "Open V-OS"), (RepairButton, "Repair")],
                    DefaultButton = OpenButton,
                    CommonButtons = TaskDialog.CloseButton,
                });
                if (b == OpenButton) { StartApp("--shell"); return 0; }
                if (b != RepairButton) return 1;
                repair = true;
            }
            else if (hasInstalled && cmp < 0)
            {
                var (b, _) = TaskDialog.Show(new TaskPage
                {
                    Heading = "A newer V-OS is already installed",
                    Content = $"Version {installed} is installed. This installer has version {offer.Manifest.Version}.",
                    Buttons = [(OpenButton, "Open V-OS")],
                    CommonButtons = TaskDialog.CloseButton,
                });
                if (b == OpenButton) StartApp("--shell");
                return 0;
            }
            else
            {
                bool update = hasInstalled;
                var (b, _) = TaskDialog.Show(new TaskPage
                {
                    Heading = update ? $"Update V-OS to {offer.Manifest.Version}" : $"Install V-OS {offer.Manifest.Version}",
                    Content = update
                        ? $"Version {installed} is installed. Your settings and data are kept. V-OS closes during the update and starts again afterwards."
                        : $"V-OS is a desktop environment that runs on top of Windows.\n\nIt installs for your account only, in {Layout.Root}. No administrator rights are needed, and Windows security settings are not changed. You can remove it at any time from Windows Settings › Apps.",
                    Details = OfferDetails(offer),
                    Buttons = [(InstallButton, update ? "Update" : "Install")],
                    DefaultButton = InstallButton,
                    CommonButtons = TaskDialog.CancelButton,
                });
                if (b != InstallButton) return 1;
            }

            bool wasRunning = Layout.RunningProcesses() is { Count: > 0 } running && Dispose(running);
            var error = RunWithProgress(repair ? "Repairing V-OS" : hasInstalled ? $"Updating V-OS to {offer.Manifest.Version}" : $"Installing V-OS {offer.Manifest.Version}",
                (job, ct) => InstallRelease(offer, job, ct));
            if (error is OperationCanceledException) return 1;
            if (error != null)
            {
                Log("install: " + error);
                if (AskRetry("V-OS could not be installed", error)) continue;
                return 3;
            }

            // The WinUI launcher finishes: shortcuts, the Apps entry, start-up option and the first start.
            StartApp("--launcher", hasInstalled ? (repair ? "--repaired" : "--updated") : "--setup");
            if (hasInstalled && wasRunning) StartApp("--shell");
            return 0;
        }
    }

    private static void InstallRelease(ReleaseOffer offer, Job job, CancellationToken ct)
    {
        var m = offer.Manifest;
        Directory.CreateDirectory(Layout.Packages);
        var packageName = SafeFileName(m.Package.Url);
        var cached = Path.Combine(Layout.Packages, packageName);

        if (offer.LocalPackage != null)
        {
            job.Phase("Checking the package…");
            VerifyPackage(offer.LocalPackage, m);
            if (!string.Equals(Path.GetFullPath(offer.LocalPackage), Path.GetFullPath(cached), StringComparison.OrdinalIgnoreCase))
                File.Copy(offer.LocalPackage, cached, overwrite: true);
        }
        else if (!(File.Exists(cached) && Hash(cached).Equals(m.Package.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            Download(offer.PackageUri!, cached, m, job, ct);
        }
        ct.ThrowIfCancellationRequested();

        if (Layout.RunningProcesses() is { Count: > 0 } running)
        {
            Dispose(running);
            job.Phase("Closing V-OS…");
            job.CanCancel = false;
            if (!Layout.StopRunning(TimeSpan.FromSeconds(10)))
                throw new SetupException("V-OS could not be closed. Close it and try again.");
        }

        job.CanCancel = false;
        job.Phase("Unpacking…", 0);
        Layout.ExtractToStaging(cached, new Progress(f => job.Fraction = f));
        var previous = InstallLayout.FileVersion(Layout.AppExe);
        job.Phase("Installing…");
        Layout.PromoteStaging();
        CopySelfIntoInstallation();

        job.Phase("Checking that V-OS starts…");
        if (!InstallLayout.HealthCheck(Layout.App, TimeSpan.FromSeconds(60), out var detail))
        {
            Log("health check failed: " + detail);
            if (Layout.HasRollback) Layout.RollBack();
            else InstallLayout.DeleteDirectory(Layout.App);
            throw new SetupException(previous != null
                ? $"V-OS {m.Version} did not pass its start-up check, so version {previous} was kept."
                : $"V-OS {m.Version} did not pass its start-up check, so it was not installed.", detail);
        }

        var state = Layout.ReadState() ?? new InstallState { InstalledAt = DateTimeOffset.Now };
        if (previous != null && previous != m.Version) { state.PreviousVersion = previous; state.UpdatedAt = DateTimeOffset.Now; }
        state.Version = m.Version;
        state.Channel = m.Channel;
        state.PackageFile = packageName;
        state.PackageSha256 = m.Package.Sha256.ToUpperInvariant();
        Layout.WriteState(state);
        PruneOldPackages(packageName);
        job.Phase("Done", 1);
        Log($"installed {m.Version} (previous {previous ?? "none"})");
    }

    private static ReleaseOffer FindRelease()
    {
        // Offline: a manifest and its package next to this file (also how test builds are installed).
        var localManifest = Path.Combine(ExeDirectory, "vos-release.json");
        if (File.Exists(localManifest))
        {
            var m = ReleaseJson.TryParse(File.ReadAllText(localManifest)) ?? throw new SetupException("The vos-release.json file next to the installer is not valid.");
            Verify(m);
            var local = Path.Combine(ExeDirectory, SafeFileName(m.Package.Url));
            if (File.Exists(local)) return new(m, local, null, "the folder of this installer");
            if (Uri.TryCreate(m.Package.Url, UriKind.Absolute, out var abs) && abs.Scheme == Uri.UriSchemeHttps) return new(m, null, abs, abs.Host);
            throw new SetupException($"The package {SafeFileName(m.Package.Url)} is missing from the folder of this installer.");
        }
        var feed = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "VosUpdateFeed")?.Value;
        if (string.IsNullOrWhiteSpace(feed) || !Uri.TryCreate(feed, UriKind.Absolute, out var feedUri) || feedUri.Scheme != Uri.UriSchemeHttps)
            throw new SetupException("This installer has no download address. Put vos-release.json and the V-OS package in the same folder as V-Launcher.exe and run it again.");
        string json;
        try { json = Http.GetStringAsync(feedUri).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new SetupException($"The download server ({feedUri.Host}) could not be reached. Check the internet connection.", ex.Message);
        }
        var manifest = ReleaseJson.TryParse(json) ?? throw new SetupException("The release information from the server is not valid.");
        Verify(manifest);
        var package = new Uri(feedUri, manifest.Package.Url);
        if (package.Scheme != Uri.UriSchemeHttps) throw new SetupException("The package address is not secure (https).");
        return new(manifest, null, package, package.Host);
    }

    private static void Verify(ReleaseManifest m)
    {
        if (!ReleaseSigning.Verify(m, ReleaseKey.PublicKeyPem))
            throw new SetupException("The release signature is not valid, so nothing was installed. The files may have been altered or damaged.");
        if (m.Product != "V-OS" || !SemVer.TryParse(m.Version, out _)) throw new SetupException("The release information is not for V-OS.");
        if (int.TryParse(m.MinimumWindowsBuild, out var min) && Environment.OSVersion.Version.Build < min)
            throw new SetupException($"V-OS needs Windows 10 build {min} or later. This PC has build {Environment.OSVersion.Version.Build}.");
    }

    private static void VerifyPackage(string path, ReleaseManifest m)
    {
        var info = new FileInfo(path);
        if (info.Length != m.Package.Size || !Hash(path).Equals(m.Package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new SetupException("The package does not match its signed release information, so it was not used.", $"{info.Name}: {info.Length} bytes");
    }

    private static void Download(Uri uri, string destination, ReleaseManifest m, Job job, CancellationToken ct)
    {
        var partial = destination + ".partial";
        try
        {
            using var response = Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? m.Package.Size;
            using (var source = response.Content.ReadAsStream(ct))
            using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                var sw = Stopwatch.StartNew();
                while ((n = source.Read(buffer)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, n);
                    done += n;
                    var rate = done / Math.Max(0.5, sw.Elapsed.TotalSeconds);
                    job.Phase($"Downloading… {Mb(done)} of {Mb(total)}" + (rate > 0 && total > done ? $" · about {Math.Ceiling((total - done) / rate)} s left" : ""), total > 0 ? 0.95 * done / total : null);
                }
            }
            job.Phase("Checking the package…");
            VerifyPackage(partial, m);
            File.Move(partial, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && ex is not SetupException)
        {
            throw new SetupException("The download did not complete. Check the internet connection and try again.", ex.Message);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
        }
    }

    // ─────────────────────────────── apply, rollback ───────────────────────────────

    /// <summary>Called by the WinUI launcher after it has staged a verified version in app.staging.</summary>
    private static int Apply(bool restartShell, bool repair)
    {
        var staged = InstallLayout.FileVersion(Path.Combine(Layout.Staging, "VOS.exe"));
        if (staged == null) { ShowMessage("Nothing to update", "No new version is waiting to be installed.", TaskIcon.Information); return 1; }
        var previous = InstallLayout.FileVersion(Layout.AppExe);
        string? failure = null;
        var error = RunWithProgress(repair ? "Repairing V-OS" : $"Updating V-OS to {staged}", (job, ct) =>
        {
            job.CanCancel = false;
            job.Phase("Closing V-OS…");
            if (!Layout.StopRunning(TimeSpan.FromSeconds(10))) throw new SetupException("V-OS could not be closed, so the update was not installed. Close V-OS and try again from the V-OS Launcher.");
            job.Phase("Installing…");
            Layout.PromoteStaging();
            job.Phase("Checking that V-OS starts…");
            if (!InstallLayout.HealthCheck(Layout.App, TimeSpan.FromSeconds(60), out var detail))
            {
                failure = detail;
                Layout.RollBack();
                return;
            }
            var state = Layout.ReadState() ?? new InstallState { InstalledAt = DateTimeOffset.Now };
            state.PreviousVersion = previous;
            state.Version = staged;
            state.UpdatedAt = DateTimeOffset.Now;
            var package = Directory.Exists(Layout.Packages) ? Directory.GetFiles(Layout.Packages, $"V-OS-{staged}-*.zip").FirstOrDefault() : null;
            if (package != null) { state.PackageFile = Path.GetFileName(package); state.PackageSha256 = Hash(package); PruneOldPackages(state.PackageFile); }
            Layout.WriteState(state);
            CopySelfIntoInstallation(Path.Combine(Layout.App, "V-Launcher.exe"));
        });
        if (error != null) { ShowError("The update was not installed", error); if (restartShell) StartApp("--shell"); return 3; }
        if (failure != null)
        {
            Log($"update to {staged} failed its health check ({failure}); rolled back to {previous}");
            ShowMessage("The update could not start", $"V-OS {staged} did not pass its start-up check, so V-OS went back to version {previous}. Nothing was lost.", TaskIcon.Warning, failure);
            if (restartShell) StartApp("--shell");
            return 4;
        }
        Log($"updated {previous} → {staged}");
        if (restartShell) StartApp("--shell");
        StartApp("--launcher", repair ? "--repaired" : "--updated");
        return 0;
    }

    private static int Rollback(bool restartShell)
    {
        if (!Layout.HasRollback) { ShowMessage("Nothing to roll back to", "No previous version is kept on this PC.", TaskIcon.Information); return 1; }
        var current = InstallLayout.FileVersion(Layout.AppExe);
        var previous = InstallLayout.FileVersion(Path.Combine(Layout.Previous, "VOS.exe"));
        var error = RunWithProgress($"Returning to V-OS {previous}", (job, ct) =>
        {
            job.CanCancel = false;
            job.Phase("Closing V-OS…");
            if (!Layout.StopRunning(TimeSpan.FromSeconds(10))) throw new SetupException("V-OS could not be closed. Close it and try again.");
            job.Phase("Restoring the previous version…");
            Layout.RollBack();
            var state = Layout.ReadState() ?? new InstallState { InstalledAt = DateTimeOffset.Now };
            state.Version = previous ?? state.PreviousVersion ?? "";
            state.PreviousVersion = null;
            state.UpdatedAt = DateTimeOffset.Now;
            // The kept package belongs to the version that was just removed; repair must not reuse it.
            if (state.PackageFile != null && !state.PackageFile.Contains($"-{state.Version}-", StringComparison.Ordinal)) { state.PackageFile = null; state.PackageSha256 = null; }
            Layout.WriteState(state);
        });
        if (error != null) { ShowError("V-OS could not go back", error); return 3; }
        Log($"rolled back {current} → {previous}");
        if (restartShell) StartApp("--shell");
        StartApp("--launcher", "--rolled-back");
        return 0;
    }

    // ─────────────────────────────── uninstall ───────────────────────────────

    private static int Uninstall(bool quiet)
    {
        bool deleteData = false;
        if (!quiet)
        {
            var (b, check) = TaskDialog.Show(new TaskPage
            {
                Heading = "Uninstall V-OS?",
                Content = "V-OS closes, the Windows taskbar and desktop return to how they were, and V-OS is removed from this PC.",
                Checkbox = "Also delete my V-OS settings and data (notes, clipboard history, alarms, logs)",
                Buttons = [(UninstallButton, "Uninstall")],
                CommonButtons = TaskDialog.CancelButton,
                DefaultButton = TaskDialog.IdCancel,
                Icon = TaskIcon.Warning,
            });
            if (b != UninstallButton) return 1;
            deleteData = check;
        }
        var dataRoot = InstallRegistration.DataRoot;
        var error = RunWithProgress("Removing V-OS", (job, ct) =>
        {
            job.CanCancel = false;
            job.Phase("Closing V-OS…");
            Layout.StopRunning(TimeSpan.FromSeconds(10));
            if (File.Exists(Layout.AppExe))
            {
                job.Phase("Restoring the Windows desktop…");
                RunAndWait(Layout.AppExe, "--recover", TimeSpan.FromSeconds(20));
            }
            job.Phase("Removing V-OS…");
            InstallRegistration.RemoveAll(Layout);
            foreach (var dir in new[] { Layout.App, Layout.Previous, Layout.Staging, Path.Combine(Layout.Root, "app.failed"), Layout.Packages })
                InstallLayout.DeleteDirectory(dir);
            foreach (var file in new[] { Layout.StatePath, Path.Combine(Layout.Root, "setup.log") })
                if (File.Exists(file)) File.Delete(file);
            if (deleteData) { job.Phase("Deleting your V-OS data…"); InstallLayout.DeleteDirectory(dataRoot); }
        });
        if (error != null) { ShowError("V-OS could not be removed completely", error); return 3; }
        Log($"uninstalled (data {(deleteData ? "deleted" : "kept")})");
        if (!quiet)
            ShowMessage("V-OS was removed", deleteData || !Directory.Exists(dataRoot)
                ? "Thank you for trying V-OS."
                : $"Your settings and data were kept in {dataRoot}, in case you install V-OS again. Delete that folder to remove them.", TaskIcon.Information);
        ScheduleSelfDelete();
        return 0;
    }

    /// <summary>The running copy cannot delete itself. A small script waits until this process has exited (retrying for
    /// up to 20 seconds), removes it and the then-empty folder, and deletes itself.</summary>
    private static void ScheduleSelfDelete()
    {
        var inRoot = ExePath.StartsWith(Layout.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!inRoot) { TryRemoveEmpty(Layout.Root); return; }
        var script = Path.Combine(Path.GetTempPath(), $"V-OS-cleanup-{Environment.ProcessId}.cmd");
        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            "for /l %%i in (1,1,20) do (",
            $"  del /f /q \"{ExePath}\" \"{ExePath}.old\" >nul 2>&1",
            $"  if not exist \"{ExePath}\" goto removed",
            "  ping -n 2 127.0.0.1 >nul",
            ")",
            ":removed",
            $"rmdir \"{Layout.Root}\" >nul 2>&1",
            "del /f /q \"%~f0\" >nul 2>&1", ""));
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c \"\"{script}\"\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
        })?.Dispose();
    }

    // ─────────────────────────────── helpers ───────────────────────────────

    /// <summary>Runs work on a thread pool thread behind a progress dialog. Returns the failure, or null.</summary>
    private static Exception? RunWithProgress(string heading, Action<Job, CancellationToken> work)
    {
        var job = new Job { Text = "Preparing…" };
        using var cts = new CancellationTokenSource();
        Exception? error = null;
        var task = Task.Run(() =>
        {
            try { work(job, cts.Token); }
            catch (Exception ex) { error = ex; }
            finally { job.Done = true; }
        });
        string shown = "";
        bool? marquee = null;
        bool closing = false;
        TaskDialog.Show(new TaskPage
        {
            Heading = heading,
            Content = job.Text,
            Progress = true,
            CommonButtons = TaskDialog.CancelButton,
            Tick = h =>
            {
                if (job.Done && !closing) { closing = true; h.Click(TaskDialog.IdCancel); return; }
                var text = job.Text;
                if (text != shown) { shown = text; h.SetContent(text); }
                var fraction = job.Fraction;
                if (fraction == null && marquee != true) { marquee = true; h.SetMarquee(true); }
                else if (fraction is double f) { if (marquee != false) { marquee = false; h.SetMarquee(false); } h.SetProgress(f); }
                h.EnableButton(TaskDialog.IdCancel, job.CanCancel);
            },
            Clicked = id =>
            {
                if (job.Done) return true;
                if (job.CanCancel) { cts.Cancel(); job.Text = "Cancelling…"; }
                return false;
            },
        });
        task.Wait();
        return error;
    }

    private static bool AskRetry(string heading, Exception error)
    {
        var (b, _) = TaskDialog.Show(new TaskPage
        {
            Heading = heading,
            Content = error is SetupException ? error.Message : "Something unexpected happened.",
            Details = error is SetupException s ? s.Details : error.ToString(),
            Footer = $"Setup log: {LogPath}",
            Icon = TaskIcon.Error,
            Buttons = [(RetryButton, "Try again")],
            CommonButtons = TaskDialog.CloseButton,
        });
        return b == RetryButton;
    }

    private static void ShowError(string heading, Exception error) => TaskDialog.Show(new TaskPage
    {
        Heading = heading,
        Content = error is SetupException ? error.Message : "Something unexpected happened.",
        Details = error is SetupException s ? s.Details : error.ToString(),
        Footer = $"Setup log: {LogPath}",
        Icon = TaskIcon.Error,
        CommonButtons = TaskDialog.CloseButton,
    });

    private static void ShowMessage(string heading, string text, TaskIcon icon, string? details = null) => TaskDialog.Show(new TaskPage
    {
        Heading = heading, Content = text, Icon = icon, Details = details, CommonButtons = TaskDialog.CloseButton,
    });

    private static string OfferDetails(ReleaseOffer offer)
    {
        var m = offer.Manifest;
        return $"Source: {offer.Source}\nPackage: {SafeFileName(m.Package.Url)}, {Mb(m.Package.Size)}\nSHA-256: {m.Package.Sha256.ToUpperInvariant()}\nRelease signature: valid (ECDSA P-256)\nPublished: {m.Published.ToLocalTime():yyyy-MM-dd}";
    }

    private static void StartApp(params string[] args)
    {
        if (!File.Exists(Layout.AppExe)) return;
        try
        {
            var psi = new ProcessStartInfo(Layout.AppExe) { UseShellExecute = false, WorkingDirectory = Layout.App };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Log("start failed: " + ex.Message); }
    }

    private static void RunAndWait(string exe, string args, TimeSpan timeout)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            if (p != null && !p.WaitForExit(timeout)) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { Log($"{Path.GetFileName(exe)} {args}: {ex.Message}"); }
    }

    /// <summary>Keeps a copy of this bootstrapper in the install root: it applies updates and uninstalls.</summary>
    private static void CopySelfIntoInstallation(string? source = null)
    {
        source ??= ExePath;
        var target = Layout.Bootstrapper;
        if (!File.Exists(source) || string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            // A running executable cannot be overwritten but can be renamed: move this copy aside, then put the new one in place.
            if (File.Exists(target) && string.Equals(Path.GetFullPath(ExePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Move(target, target + ".old", overwrite: true);
            File.Copy(source, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("could not refresh V-Launcher.exe: " + ex.Message); }
    }

    private static void PruneOldPackages(string keep)
    {
        foreach (var f in Directory.GetFiles(Layout.Packages))
            if (!Path.GetFileName(f).Equals(keep, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(f); } catch (IOException) { }
    }

    private static void TryRemoveEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (IOException) { }
    }

    private static bool Dispose(List<Process> processes) { processes.ForEach(p => p.Dispose()); return true; }

    private static string SafeFileName(string url) => Path.GetFileName(url.Replace('\\', '/').Split('?')[0]);
    private static string Hash(string path) { using var fs = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(fs)); }
    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("V-OS-Setup", SelfVersion));
        return http;
    }

    private static string LogPath => Path.Combine(Path.GetTempPath(), "V-OS-setup.log");

    private static void Log(string line)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); } catch (IOException) { }
    }

    private sealed class Progress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
}
