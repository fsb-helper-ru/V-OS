using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// BCL-only on purpose: the V-Launcher bootstrapper compiles this file too, so install, update, rollback and uninstall
// use one implementation of the layout (ADR-005, ADR-006).
namespace VOS.Core.Setup;

/// <summary>What is installed, kept in install.json in the install root.</summary>
public sealed class InstallState
{
    public string Version { get; set; } = "";
    public string? PreviousVersion { get; set; }
    public string Channel { get; set; } = "stable";
    public DateTimeOffset InstalledAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    /// <summary>File name of the verified package kept under packages\ for repair without a download.</summary>
    public string? PackageFile { get; set; }
    public string? PackageSha256 { get; set; }
    /// <summary>The uninstall entry and shortcuts have been written.</summary>
    public bool Registered { get; set; }
}

/// <summary>
/// The per-user installation: <c>app\</c> is the active version, <c>app.previous\</c> the rollback copy and
/// <c>app.staging\</c> a version being installed. Directory moves within one volume make the swap atomic.
/// </summary>
public sealed class InstallLayout(string root)
{
    /// <summary>Points the installation somewhere else (tests and verification runs never touch the real one).</summary>
    public const string RootOverrideVariable = "VOS_INSTALL_ROOT";

    public static InstallLayout Default { get; } = new(ResolveRoot());

    public static string ResolveRoot() =>
        Environment.GetEnvironmentVariable(RootOverrideVariable) is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "V-OS");

    /// <summary>True for an installation redirected with <see cref="RootOverrideVariable"/>: it registers nothing with Windows.</summary>
    public static bool IsRedirected => Environment.GetEnvironmentVariable(RootOverrideVariable) is { Length: > 0 };

    public string Root { get; } = Path.GetFullPath(root).TrimEnd('\\');
    public string App => Path.Combine(Root, "app");
    public string Previous => Path.Combine(Root, "app.previous");
    public string Staging => Path.Combine(Root, "app.staging");
    public string Packages => Path.Combine(Root, "packages");
    public string Bootstrapper => Path.Combine(Root, "V-Launcher.exe");
    public string AppExe => Path.Combine(App, "VOS.exe");
    public string StatePath => Path.Combine(Root, "install.json");
    public bool IsInstalled => File.Exists(AppExe);
    public bool HasRollback => File.Exists(Path.Combine(Previous, "VOS.exe"));

    /// <summary>True when <paramref name="directory"/> is this installation's active app folder.</summary>
    public bool IsActiveApp(string directory) => string.Equals(Path.GetFullPath(directory).TrimEnd('\\'), App, StringComparison.OrdinalIgnoreCase);

    public InstallState? ReadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize(File.ReadAllText(StatePath), SetupJsonContext.Default.InstallState) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void WriteState(InstallState state)
    {
        Directory.CreateDirectory(Root);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, SetupJsonContext.Default.InstallState), new UTF8Encoding(false));
        File.Move(temp, StatePath, overwrite: true);
    }

    /// <summary>Extracts a verified package into a cleared staging folder. Entries that would land outside it are rejected.</summary>
    public void ExtractToStaging(string packagePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        DeleteDirectory(Staging);
        Directory.CreateDirectory(Staging);
        var stagingRoot = Path.GetFullPath(Staging) + Path.DirectorySeparatorChar;
        using (var zip = ZipFile.OpenRead(packagePath))
        {
            long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(Staging, entry.FullName));
                if (!target.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The package contains an unsafe path: " + entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                done += entry.Length;
                progress?.Report((double)done / total);
            }
        }
        if (!File.Exists(Path.Combine(Staging, "VOS.exe"))) throw new InvalidDataException("The package does not contain VOS.exe.");
    }

    /// <summary>Makes the staged version active. The current version becomes the rollback copy, replacing the older
    /// one. If the second move fails, the current version is put back.</summary>
    public void PromoteStaging()
    {
        if (!File.Exists(Path.Combine(Staging, "VOS.exe"))) throw new InvalidOperationException("No version is staged.");
        DeleteDirectory(Previous);
        bool hadApp = Directory.Exists(App);
        if (hadApp) Move(App, Previous);
        try { Move(Staging, App); }
        catch
        {
            if (hadApp && !Directory.Exists(App) && Directory.Exists(Previous)) Move(Previous, App);
            throw;
        }
    }

    /// <summary>Directory move with short retries: antivirus scanners and indexers can hold freshly written files for a
    /// moment, which makes a move fail although nothing is really in use.</summary>
    private static void Move(string from, string to)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception ex) when (attempt < 6 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(250 * (attempt + 1)); }
        }
    }

    /// <summary>Puts the rollback copy back in place and discards the active version. False when there is no copy.</summary>
    public bool RollBack()
    {
        if (!HasRollback) return false;
        var failed = Path.Combine(Root, "app.failed");
        DeleteDirectory(failed);
        bool hadApp = Directory.Exists(App);
        if (hadApp) Move(App, failed);
        try { Move(Previous, App); }
        catch
        {
            // Never leave the installation without an app folder: put the active version back.
            if (hadApp && !Directory.Exists(App) && Directory.Exists(failed)) Move(failed, App);
            throw;
        }
        try { DeleteDirectory(failed); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>Runs <c>VOS.exe --health-check</c> in <paramref name="appDirectory"/>: the binaries load and the data folder works.</summary>
    public static bool HealthCheck(string appDirectory, TimeSpan timeout, out string detail)
    {
        var exe = Path.Combine(appDirectory, "VOS.exe");
        if (!File.Exists(exe)) { detail = "VOS.exe is missing"; return false; }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--health-check") { UseShellExecute = false, CreateNoWindow = true });
            if (p == null) { detail = "the health check did not start"; return false; }
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                detail = "the health check timed out";
                return false;
            }
            detail = $"exit code {p.ExitCode}";
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            detail = ex.Message;
            return false;
        }
    }

    public static string? FileVersion(string exe) => File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion : null;

    /// <summary>VOS.exe processes started from this installation.</summary>
    public List<Process> RunningProcesses()
    {
        var list = new List<Process>();
        var prefix = Root + Path.DirectorySeparatorChar;
        foreach (var p in Process.GetProcessesByName("VOS"))
        {
            try
            {
                if (p.MainModule?.FileName is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { list.Add(p); continue; }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            p.Dispose();
        }
        return list;
    }

    /// <summary>
    /// Closes V-OS politely, then firmly: asks the app hosts to close their windows (they save as they close) and the
    /// shell to exit (it restores the Windows desktop), waits, and ends whatever is left after the grace period.
    /// </summary>
    public bool StopRunning(TimeSpan grace)
    {
        var sid = CurrentUserId();
        foreach (var pipe in new[] { $"VOS.Apps.{sid}", $"VOS.App.browser.{sid}" }) SendIpc(pipe, "quit");
        SendIpc($"VOS.Shell.{sid}", "shutdown");
        var deadline = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < deadline)
        {
            var running = RunningProcesses();
            bool any = running.Count > 0;
            running.ForEach(p => p.Dispose());
            if (!any) return true;
            Thread.Sleep(200);
        }
        foreach (var p in RunningProcesses())
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
        var left = RunningProcesses();
        bool stopped = left.Count == 0;
        left.ForEach(p => p.Dispose());
        return stopped;
    }

    /// <summary>The same per-user suffix the V-OS pipes use (<c>IpcNames</c>).</summary>
    public static string CurrentUserId()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch { return Environment.UserName; }
    }

    /// <summary>Sends one JSON line to a V-OS pipe. Returns false when nothing is listening.</summary>
    public static bool SendIpc(string pipe, string type)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(800);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine("{\"type\":\"" + type + "\",\"id\":\"setup\"}");
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static long DirectorySize(string directory) =>
        Directory.Exists(directory) ? new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    /// <summary>Deletes a directory tree, retrying briefly while files are released by exiting processes.</summary>
    public static void DeleteDirectory(string path)
    {
        for (int attempt = 0; Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, recursive: true); return; }
            catch (Exception) when (attempt < 5) { Thread.Sleep(200 * (attempt + 1)); }
        }
    }
}

/// <summary>What an installation registers with Windows (all per-user), and its removal.</summary>
public static class InstallRegistration
{
    public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\V-OS";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName = "V-OS";

    public static string StartMenu => Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    public static string ShellShortcut => Path.Combine(StartMenu, "V-OS.lnk");
    public static string LauncherShortcut => Path.Combine(StartMenu, "V-OS Launcher.lnk");
    public static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "V-OS.lnk");

    /// <summary>The user data folder (settings, notes, logs), as <c>VosPaths.DataRoot</c> resolves it.</summary>
    public static string DataRoot =>
        Environment.GetEnvironmentVariable("VOS_DATA_ROOT") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "V-OS");

    /// <summary>Removes the uninstall entry, the sign-in entry (when it points into this installation) and the
    /// shortcuts. A redirected installation registered nothing, so nothing is removed for it.</summary>
    public static void RemoveAll(InstallLayout layout)
    {
        if (InstallLayout.IsRedirected) return;
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (run?.GetValue(RunValueName) is string value && value.Contains(layout.Root, StringComparison.OrdinalIgnoreCase))
                run.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        foreach (var shortcut in new[] { ShellShortcut, LauncherShortcut, DesktopShortcut })
        {
            try { if (File.Exists(shortcut)) File.Delete(shortcut); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(InstallState))]
internal sealed partial class SetupJsonContext : JsonSerializerContext;
